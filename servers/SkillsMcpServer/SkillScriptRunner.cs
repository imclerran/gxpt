using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Security;
using Mcp35.Server.Process;

namespace SkillsMcpServer
{
    /// <summary>A run_skill_script setup failure (relayed to the model as isError), not an exception.</summary>
    internal sealed class SkillScriptException : Exception
    {
        public SkillScriptException(string message) : base(message) { }
    }

    /// <summary>The located, validated entry point of a skill script (design S11).</summary>
    internal sealed class SkillScriptTarget
    {
        public string Slug;       // normalized slug
        public string SkillDir;   // the skill's root folder (absolute) - GXPT_SKILL_DIR for the script
        public string BatPath;    // the .bat/.cmd to run (absolute), inside SkillDir
    }

    /// <summary>
    /// Runs a skill's declared batch entry (design S9/S11-S15). The model names a (slug, relpath); the
    /// server resolves the slug to a skill folder across all roots (project shadows user shadows bundled),
    /// confines relpath to that folder, allows only .bat/.cmd, runs it via cmd.exe with cwd = the
    /// workspace and the skill folder reachable through %~dp0 / GXPT_SKILL_DIR (for reading its bundled
    /// assets - by convention; nothing actually prevents the script from writing there). Args are passed
    /// as literal, individually-quoted tokens - no shell metacharacters from the model are honored. A
    /// workspace is required (like command__run). The real safety boundary is the host's Destructive,
    /// always-confirm gate, NOT a sandbox: the script body is author-written and can do anything the user
    /// can, so the user approves each run.
    ///
    /// Resolution + quoting are pure and unit-tested; only Run() spawns a process (Windows-only).
    /// </summary>
    internal sealed class SkillScriptRunner
    {
        private readonly SkillsConfig _config;
        private readonly ProcessRunner _runner;

        public SkillScriptRunner(SkillsConfig config)
        {
            _config = config;
            _runner = new ProcessRunner(null);
        }

        // ---- pure: resolution ----

        // slug + relpath -> a validated .bat/.cmd inside the skill folder. Throws SkillScriptException for
        // any locking failure (no workspace, unknown skill, escape, wrong extension, missing file).
        public SkillScriptTarget Resolve(string slugIn, string relpath)
        {
            if (string.IsNullOrEmpty(_config.WorkDir))
                throw new SkillScriptException("no workspace folder is set for this conversation; "
                    + "skill scripts run against a workspace");

            string slug = SkillSlug.Make(slugIn);
            if (string.IsNullOrEmpty(slug)) throw new SkillScriptException("a valid slug is required");

            string skillDir = FindSkillDir(slug);
            if (skillDir == null)
                throw new SkillScriptException("skill '" + slug + "' was not found in any skills root");

            string full;
            try { full = new PathSandbox(skillDir, "skill folder").Resolve(relpath); }
            catch (SandboxException ex) { throw new SkillScriptException(ex.Message); }

            string ext = (Path.GetExtension(full) ?? string.Empty).ToLowerInvariant();
            if (ext != ".bat" && ext != ".cmd")
                throw new SkillScriptException("only .bat/.cmd scripts can be run (got '" + relpath + "')");
            if (!File.Exists(full))
                throw new SkillScriptException(relpath + " does not exist in skill '" + slug + "'");

            SkillScriptTarget t = new SkillScriptTarget();
            t.Slug = slug;
            t.SkillDir = Path.GetFullPath(skillDir);
            t.BatPath = full;
            return t;
        }

        // First root (most specific first: project, then user, then bundled) that holds <root>/<slug>/SKILL.md.
        private string FindSkillDir(string slug)
        {
            string[] roots = new string[] { _config.ProjectRoot, _config.UserRoot, _config.BundledRoot };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string dir = Path.Combine(root, slug);
                if (File.Exists(Path.Combine(dir, "SKILL.md"))) return dir;
            }
            return null;
        }

        // ---- pure: argument quoting ----

        // The inner command line cmd.exe parses: "batPath" "arg1" "arg2" ... Each token is wrapped in one
        // pair of double quotes so spaces and cmd operators (& | < > ^) are literal inside it. Characters
        // that can't be made literal that way are rejected rather than guessed at: a double quote (would
        // close the quote) and a percent (cmd still expands %VAR% inside quotes), plus control chars.
        public static string BuildCommandLine(string batPath, IList<string> args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Quote(batPath));
            if (args != null)
            {
                foreach (string a in args)
                {
                    sb.Append(' ');
                    sb.Append(Quote(a != null ? a : string.Empty));
                }
            }
            return sb.ToString();
        }

        private static string Quote(string token)
        {
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c == '"')
                    throw new SkillScriptException("a script argument may not contain a double quote");
                if (c == '%')
                    throw new SkillScriptException("a script argument may not contain '%'");
                if (c < ' ')
                    throw new SkillScriptException("a script argument may not contain control characters");
            }
            return "\"" + token + "\"";
        }

        // ---- run (Windows process spawn) ----

        public ProcessResult RunResolved(SkillScriptTarget target, IList<string> args, int timeoutMs)
        {
            ProcessRequest req = new ProcessRequest();
            req.FileName = _config.Shell;
            // cmd /s /c "<inner>": /s + one outer quote pair makes cmd strip exactly those outer quotes
            // and parse the rest as written (same wrapping as CommandMcpServer, avoiding cmd's legacy
            // first/last-quote stripping).
            req.Arguments = "/s /c \"" + BuildCommandLine(target.BatPath, args) + "\"";
            req.WorkingDirectory = _config.WorkDir;       // cwd = the workspace (S14)
            req.Environment = BuildEnvironment(target);   // skill folder reachable for its bundled assets
            req.TimeoutMs = timeoutMs;
            return _runner.Run(req);
        }

        private IDictionary<string, string> BuildEnvironment(SkillScriptTarget target)
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            env["GXPT_SKILL_DIR"] = target.SkillDir;      // the skill's root folder (read its bundled assets; write to cwd)
            env["GXPT_SKILL_SLUG"] = target.Slug;
            env["GXPT_WORKDIR"] = _config.WorkDir;
            return env;
        }
    }
}
