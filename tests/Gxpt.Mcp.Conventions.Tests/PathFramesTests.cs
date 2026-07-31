using System.IO;
using Gxpt.Mcp.Conventions;
using Xunit;

namespace Gxpt.Mcp.Conventions.Tests
{
    // The doubling-signature heuristic: a path whose leading segments re-state the current
    // directory's own trailing segments (root ...\.worktrees\x with ".worktrees/x/f.cs") is almost
    // always a workspace-root-relative path issued while cd'd — resolved, it nests a copy of the
    // tree under itself.
    public class PathFramesTests
    {
        private static string Root(params string[] segs)
        {
            string p = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            foreach (string s in segs) p += Path.DirectorySeparatorChar + s;
            return p;
        }

        [Fact]
        public void Detects_single_segment_restatement()
        {
            Assert.Equal("sub", PathFrames.RestatedRootTail(Root("proj", "sub"), "sub/file.txt"));
        }

        [Fact]
        public void Detects_multi_segment_restatement_longest_first()
        {
            string root = Root("proj", ".worktrees", "feat");
            Assert.Equal(".worktrees/feat", PathFrames.RestatedRootTail(root, ".worktrees/feat/src/a.cs"));
        }

        [Fact]
        public void Accepts_backslash_separators_in_the_path()
        {
            string root = Root("proj", ".worktrees", "feat");
            Assert.Equal(".worktrees/feat", PathFrames.RestatedRootTail(root, ".worktrees\\feat\\a.cs"));
        }

        [Fact]
        public void Match_is_case_insensitive()
        {
            Assert.Equal("Sub", PathFrames.RestatedRootTail(Root("proj", "sub"), "Sub/file.txt"));
        }

        [Fact]
        public void No_match_when_segments_differ()
        {
            Assert.Null(PathFrames.RestatedRootTail(Root("proj", "sub"), "other/file.txt"));
        }

        [Fact]
        public void No_match_for_a_single_segment_restatement()
        {
            // "sub" alone from ...\sub is an ordinary name collision (a file/dir named like the
            // cwd), not the doubling signature.
            Assert.Null(PathFrames.RestatedRootTail(Root("proj", "sub"), "sub"));
        }

        [Fact]
        public void Full_multi_segment_restatement_matches()
        {
            // ".worktrees/feat" issued from ...\.worktrees\feat — the exact observed worktree-add
            // disaster (it would carve ...\.worktrees\feat\.worktrees\feat). No trailing segment
            // needed for the multi-segment form.
            string root = Root("proj", ".worktrees", "feat");
            Assert.Equal(".worktrees/feat", PathFrames.RestatedRootTail(root, ".worktrees/feat"));
        }

        [Fact]
        public void No_match_for_single_segment_paths_or_empty_inputs()
        {
            Assert.Null(PathFrames.RestatedRootTail(Root("proj", "sub"), "file.txt"));
            Assert.Null(PathFrames.RestatedRootTail(Root("proj", "sub"), null));
            Assert.Null(PathFrames.RestatedRootTail(Root("proj", "sub"), ""));
            Assert.Null(PathFrames.RestatedRootTail(null, "sub/file.txt"));
        }

        [Fact]
        public void Partial_segment_names_do_not_match()
        {
            // root ends with "subdir"; path starts with "sub" - a segment-boundary check, not a
            // substring check (the separator prepended to each candidate enforces the boundary).
            Assert.Null(PathFrames.RestatedRootTail(Root("proj", "subdir"), "dir/file.txt"));
            Assert.Null(PathFrames.RestatedRootTail(Root("proj", "subdir"), "sub/file.txt"));
        }
    }
}
