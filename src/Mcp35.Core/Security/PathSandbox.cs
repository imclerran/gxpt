using System;
using System.IO;

namespace Mcp35.Core.Security
{
    /// <summary>Thrown when a requested path would escape the sandbox root.</summary>
    public sealed class SandboxException : Exception
    {
        public SandboxException(string message) : base(message) { }
    }

    /// <summary>
    /// The canonical path-containment primitive shared by every server and the host. Confines a
    /// caller-supplied relative path to a single root (servers-spec sec.2): resolve the relative path
    /// against the root, canonicalize <c>.</c>/<c>..</c>, and verify the result is inside the root with
    /// a <b>directory-boundary</b> check - so "/root" does not match "/root-evil". Absolute and
    /// drive-relative inputs are rejected outright.
    /// <para>
    /// This is a security-critical primitive: it lives in <c>Mcp35.Core</c> so a hardening fix lands in
    /// one place and reaches all consumers, instead of being copied per server (the divergence here is a
    /// sandbox-escape class bug). The only per-consumer difference is the label woven into the
    /// "escapes the ..." error message, supplied via the constructor.
    /// </para>
    /// </summary>
    public sealed class PathSandbox
    {
        private readonly string _root;          // canonical, no trailing separator
        private readonly string _rootWithSep;   // canonical + separator, for boundary checks
        private readonly string _label;         // woven into the escape message, e.g. "workspace root"

        /// <summary>Creates a sandbox with the generic "sandbox root" label.</summary>
        public PathSandbox(string root) : this(root, "sandbox root") { }

        /// <summary>
        /// Creates a sandbox whose escape message reads "path escapes the &lt;label&gt;" (e.g.
        /// <c>"workspace root"</c> or <c>"skill folder"</c>).
        /// </summary>
        public PathSandbox(string root, string label)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("root is required", "root");
            // Canonicalize the root itself and strip any trailing separator for a stable boundary.
            string full = Path.GetFullPath(root);
            _root = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _rootWithSep = _root + Path.DirectorySeparatorChar;
            _label = string.IsNullOrEmpty(label) ? "sandbox root" : label;
        }

        public string Root { get { return _root; } }

        /// <summary>Resolve a caller-supplied relative path to a full path guaranteed inside the root.</summary>
        public string Resolve(string rel)
        {
            if (rel == null) throw new SandboxException("path is required");
            if (rel.Length == 0) throw new SandboxException("path is required");
            if (Path.IsPathRooted(rel)) throw new SandboxException("absolute paths are not allowed");

            // Reject explicit drive-relative, UNC-ish, or alternate-data-stream forms early (defense in depth).
            if (rel.IndexOf(':') >= 0) throw new SandboxException("invalid path");

            string combined = Path.Combine(_root, rel);
            string full = Path.GetFullPath(combined); // collapses . and ..

            if (!IsWithin(full)) throw new SandboxException("path escapes the " + _label);
            return full;
        }

        /// <summary>True if <paramref name="full"/> is the root itself or a descendant of it.</summary>
        public bool IsWithin(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Equal to root, or under root + separator. Windows paths are case-insensitive.
            if (string.Equals(trimmed, _root, StringComparison.OrdinalIgnoreCase)) return true;
            return full.StartsWith(_rootWithSep, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Path relative to the root (for display in listings); falls back to the full path.</summary>
        public string ToRelative(string full)
        {
            if (full.StartsWith(_rootWithSep, StringComparison.OrdinalIgnoreCase))
                return full.Substring(_rootWithSep.Length);
            if (string.Equals(full, _root, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return full;
        }
    }
}
