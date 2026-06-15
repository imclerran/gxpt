using System;

namespace GxPT
{
    // Command-layer rule for path arguments. Deliberately STRICTER than the shared PathSandbox
    // (Mcp35.Core.Security.PathSandbox): paths must be relative to the working folder, and parent
    // traversal ("..") is refused outright rather than allowed-while-within-root. A workdir-rooted path
    // never needs to climb out, so this is purely lexical -- no GetFullPath / boundary math required.
    //
    // The one-directional guarantee that keeps the two layers consistent: anything this helper accepts,
    // PathSandbox also accepts (relative + no colon + no ".." always resolves within root). So every
    // completion the autocomplete offers, and every expanded path command, is valid at the server.
    internal static class WorkspacePath
    {
        // Returns null when rel is an acceptable workspace-relative path; otherwise a user-facing reason.
        // An empty/whitespace argument is treated as "no path supplied" and is the caller's concern, so
        // it is accepted here (returns null).
        public static string Validate(string rel)
        {
            if (rel == null) return null;
            string p = rel.Trim();
            if (p.Length == 0) return null;

            if (p.IndexOf(':') >= 0)
                return "Paths are relative to the working folder (drive letters are not allowed).";

            if (IsRooted(p))
                return "Paths are relative to the working folder (absolute paths are not allowed).";

            if (HasParentSegment(p))
                return "Paths cannot contain \"..\".";

            return null;
        }

        public static bool IsValid(string rel)
        {
            return Validate(rel) == null;
        }

        // True if the path is absolute or drive/UNC-rooted. Checks both separators directly rather than
        // calling Path.IsPathRooted so the rule is identical across platforms (tests run on net48/Linux).
        private static bool IsRooted(string p)
        {
            char c0 = p[0];
            if (c0 == '/' || c0 == '\\') return true;
            // Drive-letter forms ("C:", "C:\foo") are caught by the colon check, but guard anyway.
            if (p.Length >= 2 && p[1] == ':') return true;
            return false;
        }

        // True if any segment (split on either separator) is exactly "..".
        private static bool HasParentSegment(string p)
        {
            string[] parts = p.Split('/', '\\');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "..") return true;
            }
            return false;
        }
    }
}
