using System;
using System.IO;

namespace Gxpt.Mcp.Conventions
{
    /// <summary>
    /// Heuristic for the one recurring path-frame mistake GxPT's <c>cd</c> model invites: a
    /// workspace-ROOT-relative path passed while the conversation has cd'd into a subfolder, where
    /// every path argument actually resolves relative to the CURRENT directory. The signature is a
    /// path whose leading segments re-state the current directory's own trailing segments (current
    /// dir <c>...\.worktrees\x</c> with path <c>".worktrees/x/src/f.cs"</c>): resolved, it nests a
    /// copy of the tree under itself (<c>...\.worktrees\x\.worktrees\x\src\f.cs</c>) — observed in
    /// the wild as an agent doom loop that created doubled directory trees via <c>create_dirs</c>
    /// and reported "successful" writes into them.
    /// </summary>
    public static class PathFrames
    {
        /// <summary>
        /// When <paramref name="rel"/>'s leading segments re-state the trailing segments of
        /// <paramref name="root"/>, returns that repeated prefix in forward-slash notation (e.g.
        /// <c>".worktrees/x"</c>); otherwise null. A FULL multi-segment re-statement counts too —
        /// <c>".worktrees/x"</c> issued from <c>...\.worktrees\x</c> is exactly the observed
        /// worktree-doubling disaster. Only single-segment paths are exempt (a file named like its
        /// parent directory is an ordinary name collision, not the doubling signature).
        /// </summary>
        public static string RestatedRootTail(string root, string rel)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(rel)) return null;
            string[] segs = rel.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 2) return null;
            string rootTrim = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Longest candidate first, so ".worktrees/x/f.cs" against ...\.worktrees\x reports the
            // full ".worktrees/x" rather than stopping at a shorter accidental suffix match.
            for (int n = segs.Length; n >= 1; n--)
            {
                string tail = string.Empty;
                for (int i = 0; i < n; i++) tail += Path.DirectorySeparatorChar + segs[i];
                if (rootTrim.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    return string.Join("/", segs, 0, n);
            }
            return null;
        }
    }
}
