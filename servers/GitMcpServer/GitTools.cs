using System;
using System.Collections.Generic;
using System.IO;
using Mcp35.Core.Protocol;
using Mcp35.Core.Security;
using Mcp35.Server;
using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace GitMcpServer
{
    /// <summary>
    /// The Git server's tools, all run against GXPT_WORKDIR via ProcessRunner driving GXPT_GIT_PATH.
    /// Read tools (status/diff/log/fetch) inspect or sync without touching the working tree; write
    /// tools (commit/add/branch/stash) stage or record; the rest (push/pull/checkout/restore/merge/
    /// rebase/reset/rm/cherry-pick) can change or discard work and are gated by the host. The server's
    /// job is safe construction (servers-spec §4):
    ///   - arguments are built as discrete tokens and quoted once via ArgvQuoter — never
    ///     concatenated raw, so a model value can't be misread as a flag or split;
    ///   - the commit message goes via stdin (git commit -F -), not argv, removing message
    ///     content as an injection surface entirely;
    ///   - pathspecs sit after "--" so they can't be parsed as options;
    ///   - merge/cherry-pick pass --no-edit so git never blocks on an editor.
    ///
    /// Worktrees: the <c>worktree</c> tool creates linked working trees as subdirectories of the
    /// workspace root (confined by PathSandbox), and every tool accepts an optional <c>cwd</c>
    /// argument naming the subdirectory git should run in. Together these let the model carve out a
    /// worktree and then drive its whole git workflow inside it — still inside the sandbox, since a
    /// <c>cwd</c> that resolved outside GXPT_WORKDIR is rejected (servers-spec §2).
    /// </summary>
    internal static class GitTools
    {
        private const int DefaultLogMax = 20;
        private const int MaxLogMax = 200;
        private const int GitTimeoutMs = 30000;
        private const int OutputCap = 100000; // chars; trims runaway diffs/logs

        private const string CwdHelp =
            "Run git in this subdirectory of the workspace (e.g. a worktree created with the worktree "
            + "tool), relative to the workspace root. Defaults to the workspace root.";

        public static void Register(McpServer server, GitConfig config)
        {
            ProcessRunner runner = new ProcessRunner(null);
            PathSandbox sandbox = new PathSandbox(config.WorkDir, "workspace root");

            server.AddTool("status", "Show the working tree status (porcelain).",
                SchemaBuilder.Object()
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Status(config, runner, sandbox, ctx); });

            server.AddTool("diff", "Show changes; optionally only staged changes or a single path.",
                SchemaBuilder.Object()
                    .Bool("staged", false, "Show staged (cached) changes instead of the working tree")
                    .Str("path", false, "Limit the diff to this path")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Diff(config, runner, sandbox, ctx); });

            server.AddTool("log", "Show recent commit history.",
                SchemaBuilder.Object()
                    .Int("max", false, "Maximum commits to show (default 20)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Log(config, runner, sandbox, ctx); });

            server.AddTool("commit", "Stage and commit changes with a message.",
                SchemaBuilder.Object()
                    .Str("message", true, "The commit message")
                    .Bool("all", false, "Stage all changes (git add -A) before committing")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx) { return Commit(config, runner, sandbox, ctx); });

            server.AddTool("push", "Push commits to a remote.",
                SchemaBuilder.Object()
                    .Str("remote", false, "Remote name (e.g. origin)")
                    .Str("branch", false, "Branch name")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Push(config, runner, sandbox, ctx); });

            // ---- remote sync ----

            server.AddTool("fetch", "Download objects and refs from a remote (updates remote-tracking branches; does not touch the working tree).",
                SchemaBuilder.Object()
                    .Str("remote", false, "Remote name (default: the configured/default remote)")
                    .Bool("prune", false, "Remove remote-tracking refs that no longer exist on the remote")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Fetch(config, runner, sandbox, ctx); });

            server.AddTool("pull", "Fetch from and integrate with a remote (merge by default, or rebase).",
                SchemaBuilder.Object()
                    .Str("remote", false, "Remote name (e.g. origin)")
                    .Str("branch", false, "Branch name")
                    .Bool("rebase", false, "Rebase the current branch onto the upstream instead of merging")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Pull(config, runner, sandbox, ctx); });

            // ---- branches / working tree ----

            server.AddTool("checkout", "Switch to a branch or commit, optionally creating a new branch.",
                SchemaBuilder.Object()
                    .Str("ref", true, "Branch name or commit to switch to (the new branch name when create=true)")
                    .Bool("create", false, "Create a new branch from the current HEAD (git checkout -b)")
                    .Str("start_point", false, "When create=true, the commit/branch to start the new branch from")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Checkout(config, runner, sandbox, ctx); });

            server.AddTool("restore", "Restore working-tree files (discard local changes) or unstage them.",
                SchemaBuilder.Object()
                    .Arr("paths", "string", true, "Files/pathspecs to restore")
                    .Bool("staged", false, "Restore the staged content (unstage) instead of the working tree")
                    .Str("source", false, "Restore the files' content from this commit/ref")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Restore(config, runner, sandbox, ctx); });

            server.AddTool("branch", "List, create, delete, or rename branches.",
                SchemaBuilder.Object()
                    .Str("action", false, "list | create | delete | rename (default: list)")
                    .Str("name", false, "Branch name (the target for create/delete; the source for rename)")
                    .Str("new_name", false, "New name (for rename)")
                    .Bool("all", false, "Include remote-tracking branches when listing")
                    .Bool("force", false, "Force the operation (e.g. delete an unmerged branch)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx) { return Branch(config, runner, sandbox, ctx); });

            server.AddTool("worktree", "Manage linked working trees (git worktree): list, add, remove, or prune. Added worktrees live in a subdirectory of the workspace root.",
                SchemaBuilder.Object()
                    .Str("action", false, "list | add | remove | prune (default: list)")
                    .Str("path", false, "Worktree directory, relative to the workspace root (required for add/remove)")
                    .Str("ref", false, "Commit/branch to check out in the new worktree (action=add)")
                    .Str("branch", false, "Create a new branch with this name in the new worktree (action=add, git worktree add -b)")
                    .Bool("force", false, "Force the operation (action=add/remove)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Worktree(config, runner, sandbox, ctx); });

            // ---- integrate ----

            server.AddTool("merge", "Merge a branch into the current branch.",
                SchemaBuilder.Object()
                    .Str("branch", true, "Branch or commit to merge into the current branch")
                    .Bool("no_ff", false, "Always create a merge commit (--no-ff)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Merge(config, runner, sandbox, ctx); });

            server.AddTool("rebase", "Rebase the current branch, or continue/abort/skip an in-progress rebase.",
                SchemaBuilder.Object()
                    .Str("action", false, "start | continue | abort | skip (default: start)")
                    .Str("onto", false, "Upstream branch/commit to rebase onto (required when action=start)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Rebase(config, runner, sandbox, ctx); });

            server.AddTool("cherry_pick", "Apply the changes of an existing commit onto the current branch.",
                SchemaBuilder.Object()
                    .Str("commit", true, "Commit (or range) to cherry-pick")
                    .Bool("no_commit", false, "Apply the changes without committing (-n)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return CherryPick(config, runner, sandbox, ctx); });

            // ---- staging / stash ----

            server.AddTool("add", "Stage file changes for the next commit.",
                SchemaBuilder.Object()
                    .Arr("paths", "string", false, "Files/pathspecs to stage")
                    .Bool("all", false, "Stage all changes (git add -A)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx) { return Add(config, runner, sandbox, ctx); });

            server.AddTool("reset", "Reset the current HEAD, or unstage specific paths.",
                SchemaBuilder.Object()
                    .Str("mode", false, "soft | mixed | hard (default: mixed). Ignored when paths are given. hard discards working-tree changes.")
                    .Str("target", false, "Commit/ref to reset to (default: HEAD)")
                    .Arr("paths", "string", false, "Unstage only these paths (leaves working tree and HEAD untouched)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Reset(config, runner, sandbox, ctx); });

            server.AddTool("rm", "Remove files from the working tree and the index.",
                SchemaBuilder.Object()
                    .Arr("paths", "string", true, "Files/pathspecs to remove")
                    .Bool("cached", false, "Remove only from the index, keeping the working-tree file (--cached)")
                    .Bool("recursive", false, "Allow recursive removal of directories (-r)")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Rm(config, runner, sandbox, ctx); });

            server.AddTool("stash", "Save, restore, list, or drop stashed changes.",
                SchemaBuilder.Object()
                    .Str("action", false, "push | pop | apply | list | drop (default: push)")
                    .Str("message", false, "Description for the stash (action=push)")
                    .Int("index", false, "Stash index N (refers to stash@{N}) for pop/apply/drop")
                    .Str("cwd", false, CwdHelp)
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx) { return Stash(config, runner, sandbox, ctx); });
        }

        // ---- read-only ----

        private static CallToolResult Status(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("status");
            args.Add("--porcelain=v1");
            args.Add("-b");
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Diff(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("diff");
            if (BoolArg(ctx, "staged", false)) args.Add("--staged");

            string path = ctx.Arguments.Value<string>("path");
            if (!string.IsNullOrEmpty(path))
            {
                args.Add("--");   // everything after this is a pathspec, never a flag
                args.Add(path);
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Log(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            int max = IntArg(ctx, "max", DefaultLogMax, 1, MaxLogMax);
            List<string> args = new List<string>();
            args.Add("log");
            args.Add("-n");
            args.Add(max.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--pretty=format:%h %an %ad %s");
            args.Add("--date=short");
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        // ---- write / destructive ----

        private static CallToolResult Commit(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string message = ctx.Arguments.Value<string>("message");
            if (string.IsNullOrEmpty(message)) return ToolResults.Error("commit message is required");

            if (BoolArg(ctx, "all", false))
            {
                List<string> add = new List<string>();
                add.Add("add");
                add.Add("-A");
                CallToolResult staged = RunGit(config, runner, sandbox, ctx, add, null);
                if (staged.IsError) return staged;
            }

            // Message via stdin (git commit -F -): never an argv token, so its content can't be
            // misparsed or injected regardless of what characters it contains.
            List<string> args = new List<string>();
            args.Add("commit");
            args.Add("-F");
            args.Add("-");
            return RunGit(config, runner, sandbox, ctx, args, message);
        }

        private static CallToolResult Push(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("push");

            string remote = ctx.Arguments.Value<string>("remote");
            string branch = ctx.Arguments.Value<string>("branch");
            if (!string.IsNullOrEmpty(remote))
            {
                args.Add(remote);
                if (!string.IsNullOrEmpty(branch)) args.Add(branch);
            }
            else if (!string.IsNullOrEmpty(branch))
            {
                // A branch with no remote isn't a valid push target on its own.
                return ToolResults.Error("branch specified without a remote");
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        // ---- remote sync ----

        private static CallToolResult Fetch(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("fetch");
            if (BoolArg(ctx, "prune", false)) args.Add("--prune");
            string remote = ctx.Arguments.Value<string>("remote");
            if (!string.IsNullOrEmpty(remote)) args.Add(remote);
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Pull(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("pull");
            if (BoolArg(ctx, "rebase", false)) args.Add("--rebase");

            string remote = ctx.Arguments.Value<string>("remote");
            string branch = ctx.Arguments.Value<string>("branch");
            if (!string.IsNullOrEmpty(remote))
            {
                args.Add(remote);
                if (!string.IsNullOrEmpty(branch)) args.Add(branch);
            }
            else if (!string.IsNullOrEmpty(branch))
            {
                return ToolResults.Error("branch specified without a remote");
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        // ---- branches / working tree ----

        private static CallToolResult Checkout(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string refName = ctx.Arguments.Value<string>("ref");
            if (string.IsNullOrEmpty(refName)) return ToolResults.Error("ref is required");

            List<string> args = new List<string>();
            args.Add("checkout");
            if (BoolArg(ctx, "create", false)) args.Add("-b");
            args.Add(refName);
            string start = ctx.Arguments.Value<string>("start_point");
            if (BoolArg(ctx, "create", false) && !string.IsNullOrEmpty(start)) args.Add(start);
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Restore(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> paths = StrArrayArg(ctx, "paths");
            if (paths.Count == 0) return ToolResults.Error("at least one path is required");

            List<string> args = new List<string>();
            args.Add("restore");
            if (BoolArg(ctx, "staged", false)) args.Add("--staged");
            string source = ctx.Arguments.Value<string>("source");
            if (!string.IsNullOrEmpty(source)) { args.Add("--source"); args.Add(source); }
            args.Add("--");   // everything after this is a pathspec, never a flag
            for (int i = 0; i < paths.Count; i++) args.Add(paths[i]);
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Branch(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string action = StrArg(ctx, "action", "list").ToLowerInvariant();
            string name = ctx.Arguments.Value<string>("name");

            List<string> args = new List<string>();
            args.Add("branch");
            switch (action)
            {
                case "list":
                    if (BoolArg(ctx, "all", false)) args.Add("-a");
                    args.Add("--list");
                    break;
                case "create":
                    if (string.IsNullOrEmpty(name)) return ToolResults.Error("name is required to create a branch");
                    args.Add(name);
                    break;
                case "delete":
                    if (string.IsNullOrEmpty(name)) return ToolResults.Error("name is required to delete a branch");
                    args.Add(BoolArg(ctx, "force", false) ? "-D" : "-d");
                    args.Add(name);
                    break;
                case "rename":
                    string newName = ctx.Arguments.Value<string>("new_name");
                    if (string.IsNullOrEmpty(newName)) return ToolResults.Error("new_name is required to rename a branch");
                    args.Add(BoolArg(ctx, "force", false) ? "-M" : "-m");
                    if (!string.IsNullOrEmpty(name)) args.Add(name); // omitted => rename the current branch
                    args.Add(newName);
                    break;
                default:
                    return ToolResults.Error("unknown action '" + action + "' (use list, create, delete, or rename)");
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        // A linked working tree (git worktree). `add` carves one out as a subdirectory of the
        // workspace root — its location is resolved through the sandbox so it can't land outside
        // GXPT_WORKDIR, the same containment every other path argument gets (servers-spec §2). Once
        // it exists, the model drives git inside it by passing that same directory as `cwd`.
        private static CallToolResult Worktree(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string action = StrArg(ctx, "action", "list").ToLowerInvariant();

            List<string> args = new List<string>();
            args.Add("worktree");
            switch (action)
            {
                case "list":
                    args.Add("list");
                    args.Add("--porcelain");
                    break;
                case "add":
                {
                    string addPath;
                    CallToolResult err = ResolveWorktreePath(sandbox, ctx, out addPath);
                    if (err != null) return err;
                    args.Add("add");
                    if (BoolArg(ctx, "force", false)) args.Add("--force");
                    string branch = ctx.Arguments.Value<string>("branch");
                    if (!string.IsNullOrEmpty(branch)) { args.Add("-b"); args.Add(branch); }
                    args.Add(addPath);
                    string refName = ctx.Arguments.Value<string>("ref");
                    if (!string.IsNullOrEmpty(refName)) args.Add(refName);
                    break;
                }
                case "remove":
                {
                    string rmPath;
                    CallToolResult err = ResolveWorktreePath(sandbox, ctx, out rmPath);
                    if (err != null) return err;
                    args.Add("remove");
                    if (BoolArg(ctx, "force", false)) args.Add("--force");
                    args.Add(rmPath);
                    break;
                }
                case "prune":
                    args.Add("prune");
                    break;
                default:
                    return ToolResults.Error("unknown action '" + action + "' (use list, add, remove, or prune)");
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        // Resolve the `path` argument of a worktree add/remove to an absolute directory confined to
        // the workspace root.
        private static CallToolResult ResolveWorktreePath(PathSandbox sandbox, ToolCallContext ctx, out string full)
        {
            full = null;
            string rel = ctx.Arguments.Value<string>("path");
            if (string.IsNullOrEmpty(rel)) return ToolResults.Error("path is required for this worktree action");
            try { full = sandbox.Resolve(rel); }
            catch (SandboxException ex) { return ToolResults.Error(ex.Message); }
            return null;
        }

        // ---- integrate ----

        private static CallToolResult Merge(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string branch = ctx.Arguments.Value<string>("branch");
            if (string.IsNullOrEmpty(branch)) return ToolResults.Error("branch is required");

            List<string> args = new List<string>();
            args.Add("merge");
            args.Add("--no-edit");   // use the default message; never open an editor (would hang)
            if (BoolArg(ctx, "no_ff", false)) args.Add("--no-ff");
            args.Add(branch);
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Rebase(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string action = StrArg(ctx, "action", "start").ToLowerInvariant();
            List<string> args = new List<string>();
            args.Add("rebase");
            switch (action)
            {
                case "start":
                    string onto = ctx.Arguments.Value<string>("onto");
                    if (string.IsNullOrEmpty(onto)) return ToolResults.Error("onto is required to start a rebase");
                    args.Add(onto);
                    break;
                case "continue": args.Add("--continue"); break;
                case "abort": args.Add("--abort"); break;
                case "skip": args.Add("--skip"); break;
                default:
                    return ToolResults.Error("unknown action '" + action + "' (use start, continue, abort, or skip)");
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult CherryPick(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string commit = ctx.Arguments.Value<string>("commit");
            if (string.IsNullOrEmpty(commit)) return ToolResults.Error("commit is required");

            List<string> args = new List<string>();
            args.Add("cherry-pick");
            if (BoolArg(ctx, "no_commit", false)) args.Add("-n");
            else args.Add("--no-edit");
            args.Add(commit);
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        // ---- staging / stash ----

        private static CallToolResult Add(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> paths = StrArrayArg(ctx, "paths");
            bool all = BoolArg(ctx, "all", false);
            if (!all && paths.Count == 0) return ToolResults.Error("specify paths or set all=true");

            List<string> args = new List<string>();
            args.Add("add");
            if (all) { args.Add("-A"); }
            else
            {
                args.Add("--");
                for (int i = 0; i < paths.Count; i++) args.Add(paths[i]);
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Reset(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> paths = StrArrayArg(ctx, "paths");
            string target = ctx.Arguments.Value<string>("target");

            List<string> args = new List<string>();
            args.Add("reset");

            if (paths.Count > 0)
            {
                // Path reset only unstages; mode flags don't apply.
                if (!string.IsNullOrEmpty(target)) args.Add(target);
                args.Add("--");
                for (int i = 0; i < paths.Count; i++) args.Add(paths[i]);
                return RunGit(config, runner, sandbox, ctx, args, null);
            }

            string mode = StrArg(ctx, "mode", "mixed").ToLowerInvariant();
            switch (mode)
            {
                case "soft": args.Add("--soft"); break;
                case "mixed": args.Add("--mixed"); break;
                case "hard": args.Add("--hard"); break;
                default: return ToolResults.Error("unknown mode '" + mode + "' (use soft, mixed, or hard)");
            }
            if (!string.IsNullOrEmpty(target)) args.Add(target);
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Rm(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            List<string> paths = StrArrayArg(ctx, "paths");
            if (paths.Count == 0) return ToolResults.Error("at least one path is required");

            List<string> args = new List<string>();
            args.Add("rm");
            if (BoolArg(ctx, "cached", false)) args.Add("--cached");
            if (BoolArg(ctx, "recursive", false)) args.Add("-r");
            args.Add("--");
            for (int i = 0; i < paths.Count; i++) args.Add(paths[i]);
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        private static CallToolResult Stash(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx)
        {
            string action = StrArg(ctx, "action", "push").ToLowerInvariant();
            List<string> args = new List<string>();
            args.Add("stash");
            switch (action)
            {
                case "push":
                    args.Add("push");
                    string message = ctx.Arguments.Value<string>("message");
                    if (!string.IsNullOrEmpty(message)) { args.Add("-m"); args.Add(message); }
                    break;
                case "list":
                    args.Add("list");
                    break;
                case "pop":
                case "apply":
                case "drop":
                    args.Add(action);
                    string entry = StashEntry(ctx);
                    if (entry != null) args.Add(entry);
                    break;
                default:
                    return ToolResults.Error("unknown action '" + action + "' (use push, pop, apply, list, or drop)");
            }
            return RunGit(config, runner, sandbox, ctx, args, null);
        }

        // stash@{N} for a supplied index, or null to act on the latest stash.
        private static string StashEntry(ToolCallContext ctx)
        {
            JToken t = ctx.Arguments["index"];
            if (t == null || t.Type == JTokenType.Null) return null;
            int n;
            try { n = t.Value<int>(); }
            catch { return null; }
            if (n < 0) n = 0;
            return "stash@{" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        // ---- shared execution ----

        public static CallToolResult RunGit(GitConfig config, ProcessRunner runner, PathSandbox sandbox, ToolCallContext ctx, IList<string> args, string stdin)
        {
            string workDir;
            CallToolResult cwdErr = ResolveCwd(sandbox, ctx, out workDir);
            if (cwdErr != null) return cwdErr;

            ProcessRequest req = new ProcessRequest();
            req.FileName = config.GitPath;
            req.Arguments = ArgvQuoter.Join(args);   // single Windows-correct quoter (§4)
            req.WorkingDirectory = workDir;
            req.TimeoutMs = GitTimeoutMs;
            req.StdinText = stdin;

            ProcessResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The git executable couldn't be launched at all (not installed / not on PATH).
                return ToolResults.Error("git was not found. Install Git and ensure it is on your PATH (or set GXPT_GIT_PATH).");
            }
            catch (Exception ex)
            {
                return ToolResults.Error("failed to run git: " + ex.Message);
            }

            if (result.TimedOut)
                return ToolResults.Error("git timed out after " + GitTimeoutMs + " ms");

            if (result.ExitCode != 0)
            {
                string err = Trim(result.StdErr);
                if (string.IsNullOrEmpty(err)) err = Trim(result.StdOut);
                return ToolResults.Error("git exited " + result.ExitCode + ": " + err);
            }

            JObject outp = new JObject();
            outp["exitCode"] = result.ExitCode;
            outp["stdout"] = Cap(result.StdOut);
            string stderr = Trim(result.StdErr);
            if (!string.IsNullOrEmpty(stderr)) outp["stderr"] = Cap(stderr);
            return ToolResults.Json(outp);
        }

        // Resolve the optional `cwd` argument to an absolute directory git should run in. Absent or
        // empty selects the workspace root itself; anything else is confined to the root by the
        // sandbox (so a worktree subdir resolves, but "../escape" or an absolute path is rejected).
        private static CallToolResult ResolveCwd(PathSandbox sandbox, ToolCallContext ctx, out string workDir)
        {
            workDir = sandbox.Root;
            string rel = ctx.Arguments.Value<string>("cwd");
            if (string.IsNullOrEmpty(rel)) return null;

            string full;
            try { full = sandbox.Resolve(rel); }
            catch (SandboxException ex) { return ToolResults.Error(ex.Message); }
            if (!Directory.Exists(full)) return ToolResults.Error("cwd not found: " + rel);
            workDir = full;
            return null;
        }

        // ---- helpers ----

        private static bool BoolArg(ToolCallContext ctx, string name, bool fallback)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<bool>(); }
            catch { return fallback; }
        }

        private static int IntArg(ToolCallContext ctx, string name, int fallback, int min, int max)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        private static string StrArg(ToolCallContext ctx, string name, string fallback)
        {
            string s = ctx.Arguments.Value<string>(name);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        // Read a string[] argument; also accepts a lone string for convenience. Skips null/empty.
        private static List<string> StrArrayArg(ToolCallContext ctx, string name)
        {
            List<string> result = new List<string>();
            JToken t = ctx.Arguments[name];
            if (t == null) return result;
            if (t.Type == JTokenType.Array)
            {
                foreach (JToken item in (JArray)t)
                {
                    if (item == null || item.Type == JTokenType.Null) continue;
                    string s;
                    try { s = item.Value<string>(); }
                    catch { continue; }
                    if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }
            else if (t.Type == JTokenType.String)
            {
                string s = t.Value<string>();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            return result;
        }

        private static string Trim(string s)
        {
            return s == null ? null : s.Trim();
        }

        private static string Cap(string s)
        {
            if (s == null) return null;
            if (s.Length <= OutputCap) return s;
            return s.Substring(0, OutputCap) + "\n…[truncated]";
        }
    }
}
