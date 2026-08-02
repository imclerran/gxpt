using System;
using System.IO;
using Mcp35.Core.Security;
using Xunit;

namespace Mcp35.Core.Tests.Security
{
    // The canonical containment primitive (issue #118). These tests pin the security-critical
    // behaviour that every consumer (Files/Memory/Skills servers + host SkillTools) now inherits:
    // the directory-boundary check, and the absolute/drive/'..'-escape rejections.
    public class PathSandboxTests : IDisposable
    {
        private readonly string _root;

        public PathSandboxTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sandbox_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // ---- Directory-boundary check: "/root" must not match "/root-evil" ----

        [Fact]
        public void Sibling_root_prefix_is_not_within()
        {
            string sibling = _root + "-evil";
            var sandbox = new PathSandbox(_root);
            Assert.False(sandbox.IsWithin(Path.Combine(sibling, "secret.txt")));
        }

        [Fact]
        public void Root_itself_and_descendants_are_within()
        {
            var sandbox = new PathSandbox(_root);
            Assert.True(sandbox.IsWithin(_root));
            Assert.True(sandbox.IsWithin(_root + Path.DirectorySeparatorChar)); // trailing sep
            Assert.True(sandbox.IsWithin(Path.Combine(_root, "sub", "file.txt")));
        }

        // ---- Resolve: happy path collapses . and .. inside the root ----

        [Fact]
        public void Resolve_returns_full_path_inside_root()
        {
            var sandbox = new PathSandbox(_root);
            string full = sandbox.Resolve(Path.Combine("sub", "a.txt"));
            Assert.Equal(Path.Combine(_root, "sub", "a.txt"), full);
            Assert.True(sandbox.IsWithin(full));
        }

        [Fact]
        public void Resolve_collapses_interior_dotdot_that_stays_within()
        {
            var sandbox = new PathSandbox(_root);
            string full = sandbox.Resolve(Path.Combine("sub", "..", "b.txt"));
            Assert.Equal(Path.Combine(_root, "b.txt"), full);
        }

        // ---- Resolve: escape rejections ----

        [Theory]
        [InlineData("../escape.txt")]
        [InlineData("../../etc/passwd")]
        [InlineData("subdir/../../outside.txt")]
        public void Resolve_rejects_parent_traversal(string rel)
        {
            var sandbox = new PathSandbox(_root);
            var ex = Assert.Throws<SandboxException>(() => sandbox.Resolve(rel));
            Assert.Contains("escape", ex.Message);
        }

        [Fact]
        public void Resolve_rejects_absolute_paths()
        {
            var sandbox = new PathSandbox(_root);
            string abs = Path.Combine(_root, "x.txt"); // a rooted path
            var ex = Assert.Throws<SandboxException>(() => sandbox.Resolve(abs));
            Assert.Contains("absolute", ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Resolve_rejects_empty_path(string rel)
        {
            var sandbox = new PathSandbox(_root);
            var ex = Assert.Throws<SandboxException>(() => sandbox.Resolve(rel));
            Assert.Contains("required", ex.Message);
        }

        [Fact]
        public void Resolve_rejects_drive_or_ads_colon()
        {
            var sandbox = new PathSandbox(_root);
            // A colon signals a drive-relative path or NTFS alternate data stream - rejected outright.
            var ex = Assert.Throws<SandboxException>(() => sandbox.Resolve("file.txt:stream"));
            Assert.Contains("invalid", ex.Message);
        }

        // ---- The label parameter customizes only the escape-message text (per-consumer wording) ----

        [Fact]
        public void Label_is_woven_into_the_escape_message()
        {
            var workspace = new PathSandbox(_root, "workspace root");
            var skill = new PathSandbox(_root, "skill folder");
            Assert.Contains("workspace root", Assert.Throws<SandboxException>(() => workspace.Resolve("../x")).Message);
            Assert.Contains("skill folder", Assert.Throws<SandboxException>(() => skill.Resolve("../x")).Message);
        }

        [Fact]
        public void Default_label_is_used_when_unspecified()
        {
            var sandbox = new PathSandbox(_root);
            Assert.Contains("sandbox root", Assert.Throws<SandboxException>(() => sandbox.Resolve("../x")).Message);
        }

        // ---- Constructor guards ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Constructor_requires_a_root(string root)
        {
            Assert.Throws<ArgumentException>(() => new PathSandbox(root));
        }

        // ---- ToRelative round-trips against Resolve ----

        [Fact]
        public void ToRelative_strips_the_root_prefix()
        {
            var sandbox = new PathSandbox(_root);
            string full = sandbox.Resolve(Path.Combine("sub", "a.txt"));
            Assert.Equal(Path.Combine("sub", "a.txt"), sandbox.ToRelative(full));
            Assert.Equal(string.Empty, sandbox.ToRelative(sandbox.Root));
        }

        // ---- RelativeSubdirOrNull: the shared "is this a real subdir, and what is it called"
        // primitive the host's current-dir serialize / revalidate / display paths route through. ----

        [Fact]
        public void RelativeSubdirOrNull_returns_relative_and_canonical_for_a_subdir()
        {
            var sandbox = new PathSandbox(_root);
            string abs = Path.Combine(_root, Path.Combine("sub", "app"));
            string canonical;
            string rel = sandbox.RelativeSubdirOrNull(abs, out canonical);

            Assert.Equal(Path.Combine("sub", "app"), rel);
            Assert.Equal(Path.GetFullPath(abs), canonical);
        }

        [Fact]
        public void RelativeSubdirOrNull_collapses_interior_dotdot_within_root()
        {
            var sandbox = new PathSandbox(_root);
            string abs = Path.Combine(_root, Path.Combine("sub", "..", "app"));
            string canonical;
            string rel = sandbox.RelativeSubdirOrNull(abs, out canonical);

            Assert.Equal("app", rel);
            Assert.Equal(Path.Combine(_root, "app"), canonical);
        }

        [Fact]
        public void RelativeSubdirOrNull_returns_null_at_the_root_itself()
        {
            var sandbox = new PathSandbox(_root);
            string canonical;
            Assert.Null(sandbox.RelativeSubdirOrNull(_root, out canonical));
            Assert.Null(canonical);
            // A trailing separator still resolves to the root, so still "at anchor" -> null.
            Assert.Null(sandbox.RelativeSubdirOrNull(_root + Path.DirectorySeparatorChar, out canonical));
            Assert.Null(canonical);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void RelativeSubdirOrNull_returns_null_for_empty_input(string abs)
        {
            var sandbox = new PathSandbox(_root);
            string canonical;
            Assert.Null(sandbox.RelativeSubdirOrNull(abs, out canonical));
            Assert.Null(canonical);
        }

        [Fact]
        public void RelativeSubdirOrNull_returns_null_for_an_out_of_root_path()
        {
            var sandbox = new PathSandbox(_root);
            string canonical;
            // A sibling that shares the root's prefix must NOT read as within (the boundary check).
            Assert.Null(sandbox.RelativeSubdirOrNull(_root + "-evil", out canonical));
            Assert.Null(canonical);
            // And an interior path that escapes via '..'.
            string escape = Path.Combine(_root, Path.Combine("sub", "..", "..", "outside"));
            Assert.Null(sandbox.RelativeSubdirOrNull(escape, out canonical));
            Assert.Null(canonical);
        }
    }
}
