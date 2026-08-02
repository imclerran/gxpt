using System.IO;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Covers the orchestrator's ephemeral context tail sections: <current_directory> (host `cd`
    // awareness, 2026-07-31 design amendment) leads, then <memory>, <skills>, <agents>,
    // <available_tools>; each is omitted when empty.
    public sealed class OrchestratorEphemeralTailTests
    {
        [Fact]
        public void Sections_Ordered_CwdFirst_ThenMemorySkillsAgentsTools()
        {
            string tail = McpChatOrchestrator.BuildEphemeralContextText("cwd", "mem", "sk", "ag", "tools");

            int cd = tail.IndexOf("<current_directory>");
            int mem = tail.IndexOf("<memory>");
            int sk = tail.IndexOf("<skills>");
            int ag = tail.IndexOf("<agents>");
            int tl = tail.IndexOf("<available_tools>");

            Assert.True(cd >= 0 && mem >= 0 && sk >= 0 && ag >= 0 && tl >= 0);
            Assert.True(cd < mem && mem < sk && sk < ag && ag < tl);
            Assert.Contains("<current_directory>\ncwd\n</current_directory>", tail);
            Assert.Contains("<agents>\nag\n</agents>", tail);
        }

        [Fact]
        public void EmptySections_Omitted()
        {
            string tail = McpChatOrchestrator.BuildEphemeralContextText(null, null, null, null, "tools");

            Assert.DoesNotContain("<current_directory>", tail);
            Assert.DoesNotContain("<agents>", tail);
            Assert.Contains("<available_tools>", tail);
        }

        [Fact]
        public void OnlyAgents_StillProducesTail()
        {
            string tail = McpChatOrchestrator.BuildEphemeralContextText(null, null, null, "ag", null);

            Assert.Contains("<agents>\nag\n</agents>", tail);
        }

        [Fact]
        public void OnlyCwd_StillProducesTail()
        {
            // A workspace turn with no memory/skills/agents/tools still gets a tail: the
            // current-directory line must ride every workspace request to stay authoritative.
            string tail = McpChatOrchestrator.BuildEphemeralContextText("cwd", null, null, null, null);

            Assert.Contains("<current_directory>\ncwd\n</current_directory>", tail);
        }

        [Fact]
        public void AllEmpty_ReturnsNull()
        {
            Assert.Null(McpChatOrchestrator.BuildEphemeralContextText(null, null, null, null, null));
        }

        // ---- the <current_directory> block's text (CurrentDirContextBlock) ----

        [Fact]
        public void CurrentDirBlock_NullWithoutWorkspace()
        {
            // No workspace: `cd` doesn't exist there, so no line at all.
            Assert.Null(McpChatOrchestrator.CurrentDirContextBlock(null, null));
            Assert.Null(McpChatOrchestrator.CurrentDirContextBlock("", "anything"));
        }

        [Fact]
        public void CurrentDirBlock_AtAnchor_SaysWorkspaceRoot()
        {
            // Present even at the anchor: its job is to correct a model whose transcript claims a
            // subdir the host no longer honors, and that is exactly the at-anchor case.
            string anchor = Path.Combine(Path.GetTempPath(), "ws");
            string block = McpChatOrchestrator.CurrentDirContextBlock(anchor, null);

            Assert.Contains("current working directory is the workspace root", block);
            Assert.Contains("authoritative", block);
        }

        [Fact]
        public void CurrentDirBlock_Scoped_ShowsAnchorRelativePath()
        {
            string anchor = Path.Combine(Path.GetTempPath(), "ws");
            string sub = Path.Combine(anchor, Path.Combine("src", "app"));
            string block = McpChatOrchestrator.CurrentDirContextBlock(anchor, sub);

            Assert.Contains("`src/app` (relative to the workspace root)", block);
        }

        [Fact]
        public void CurrentDirBlock_OutOfAnchorValue_FallsBackToRootWording()
        {
            // An out-of-anchor current dir is an upstream bug; the block never echoes the path
            // (the servers reject the injected dir anyway) and falls back to the floor wording.
            string anchor = Path.Combine(Path.GetTempPath(), "ws");
            string outside = Path.Combine(Path.GetTempPath(), "elsewhere");
            string block = McpChatOrchestrator.CurrentDirContextBlock(anchor, outside);

            Assert.Contains("the workspace root", block);
            Assert.DoesNotContain("elsewhere", block);
        }
    }
}
