using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class ConversationUsageTests
    {
        [Fact]
        public void RecordUsage_SubAgent_AddsToTotals_ButNotContextGauge()
        {
            var convo = new Conversation(null);

            var parent = new ResponseUsage(); parent.PromptTokens = 1000; parent.Cost = 0.01m;
            convo.RecordUsage(parent);              // parent's own request - moves the gauge
            Assert.Equal(1000, convo.LastPromptTokens);

            var child = new ResponseUsage(); child.PromptTokens = 9000; child.Cost = 0.05m;
            convo.RecordUsage(child, false);        // sub-agent usage - totals only, no gauge move

            Assert.Equal(1000, convo.LastPromptTokens);     // gauge unchanged (still the parent's size)
            Assert.Equal(10000L, convo.TotalPromptTokens);  // totals include the child
            Assert.Equal(0.06m, convo.TotalCost);           // cost includes the child
        }

        [Fact]
        public void RecordUsage_Default_MovesContextGauge()
        {
            var convo = new Conversation(null);
            var u = new ResponseUsage(); u.PromptTokens = 4242;
            convo.RecordUsage(u);
            Assert.Equal(4242, convo.LastPromptTokens);
        }
    }
}
