using System;
using System.IO;
using Mcp35.Core.Security;
using Mcp35.Server;

namespace Gxpt.Mcp.Conventions
{
    /// <summary>
    /// Resolves the effective working directory (and a matching <see cref="PathSandbox"/>) for one
    /// workdir-scoped tool call, honoring GxPT's host-injected current directory
    /// (<c>params._meta["gxpt.cwd"]</c>, see <see cref="GxptMeta.CwdKey"/>).
    /// <para>
    /// This is GxPT policy, not protocol: it lives here rather than in the generic server framework so
    /// <c>Mcp35.Server</c> stays agnostic to which <c>_meta</c> fields a consumer uses. The server reads
    /// the raw bag via <see cref="ToolCallContext.MetaString"/>; the interpretation lives here.
    /// </para>
    /// <para>
    /// The server is launched once per <b>anchor</b> (its <c>GXPT_WORKDIR</c>); the host injects the
    /// conversation's <b>current</b> directory per call. Two roles that <c>GXPT_WORKDIR</c> used to play
    /// at once now split: the anchor stays the immovable <i>floor</i> the server validates against, while
    /// the current dir becomes the per-request working directory. Because the host and the server never
    /// trust each other to be the only defense (servers-spec §9), the server <b>re-validates</b> the
    /// injected current dir against its own launch anchor here — a current dir outside the anchor, or one
    /// that no longer exists (e.g. a removed worktree), is rejected rather than silently falling back to
    /// the anchor (silent fallback would re-expose the clobber the design prevents).
    /// </para>
    /// <para>
    /// Both returned values are per-call locals — no shared mutable state — so concurrent calls on the
    /// same pooled server can carry different current directories without racing.
    /// </para>
    /// </summary>
    public static class CwdScope
    {
        /// <summary>
        /// Resolve the effective working directory and a sandbox rooted there. When the call carries no
        /// current dir, both fall back to the anchor (today's behavior, the safe floor). On success
        /// <paramref name="workDir"/> is the absolute directory the call runs in and
        /// <paramref name="sandbox"/> is a fresh sandbox rooted at it (used by D2 servers that re-root
        /// their path arguments at the current dir). On failure <paramref name="error"/> holds a
        /// model-readable message and the call must be rejected.
        /// </summary>
        public static bool TryResolve(ToolCallContext ctx, string anchorRoot, string label,
                                      out string workDir, out PathSandbox sandbox, out string error)
        {
            string lbl = string.IsNullOrEmpty(label) ? "workspace root" : label;
            PathSandbox anchor = new PathSandbox(anchorRoot, lbl);
            workDir = anchor.Root;
            sandbox = anchor;
            error = null;

            string cwd = ctx != null ? ctx.MetaString(GxptMeta.CwdKey) : null;
            if (string.IsNullOrEmpty(cwd)) return true;   // absent => the anchor (default floor)

            string full;
            try { full = Path.GetFullPath(cwd); }
            catch (Exception) { error = "invalid current directory"; return false; }

            // Re-validate the host-supplied current dir against this server's launch anchor.
            if (!anchor.IsWithin(full))
            {
                error = "current directory escapes the " + lbl;
                return false;
            }
            // A current dir that has since vanished (worktree removed/pruned) is an error, not a silent
            // fallback to the anchor — surface it so the model deliberately steps back out.
            if (!Directory.Exists(full))
            {
                error = "current directory no longer exists";
                return false;
            }

            workDir = full;
            sandbox = new PathSandbox(full, lbl);
            return true;
        }

        /// <summary>
        /// Working-dir-only variant for D1 servers (command/msbuild) that move the child's working
        /// directory to the current dir but do not re-root their path arguments at it. Same validation
        /// as <see cref="TryResolve"/>; the path sandbox is simply discarded.
        /// </summary>
        public static bool TryResolveWorkingDir(ToolCallContext ctx, string anchorRoot, string label,
                                                out string workDir, out string error)
        {
            PathSandbox ignored;
            return TryResolve(ctx, anchorRoot, label, out workDir, out ignored, out error);
        }
    }
}
