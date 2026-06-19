using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Covers the <agents> section added to the orchestrator's ephemeral context tail (phase 3): it sits
    // after <skills> and before <available_tools>, and is omitted when empty.
    public sealed class OrchestratorEphemeralTailTests
    {
        [Fact]
        public void AgentsSection_OrderedAfterSkills_BeforeTools()
        {
            string tail = McpChatOrchestrator.BuildEphemeralContextText("mem", "sk", "ag", "tools");

            int mem = tail.IndexOf("<memory>");
            int sk = tail.IndexOf("<skills>");
            int ag = tail.IndexOf("<agents>");
            int tl = tail.IndexOf("<available_tools>");

            Assert.True(mem >= 0 && sk >= 0 && ag >= 0 && tl >= 0);
            Assert.True(mem < sk && sk < ag && ag < tl);
            Assert.Contains("<agents>\nag\n</agents>", tail);
        }

        [Fact]
        public void AgentsSection_OmittedWhenEmpty()
        {
            string tail = McpChatOrchestrator.BuildEphemeralContextText(null, null, null, "tools");

            Assert.DoesNotContain("<agents>", tail);
            Assert.Contains("<available_tools>", tail);
        }

        [Fact]
        public void OnlyAgents_StillProducesTail()
        {
            string tail = McpChatOrchestrator.BuildEphemeralContextText(null, null, "ag", null);

            Assert.Contains("<agents>\nag\n</agents>", tail);
        }

        [Fact]
        public void AllEmpty_ReturnsNull()
        {
            Assert.Null(McpChatOrchestrator.BuildEphemeralContextText(null, null, null, null));
        }
    }
}
