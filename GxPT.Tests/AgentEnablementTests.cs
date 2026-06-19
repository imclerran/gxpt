using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentEnablementTests
    {
        [Theory]
        [InlineData(null, false, false)]   // no override, global off  -> off (the default)
        [InlineData(null, true, true)]     // no override, global on   -> on
        [InlineData(true, false, true)]    // override on  beats global off
        [InlineData(false, true, false)]   // override off beats global on
        public void FeatureEnabled_OverrideBeatsGlobal(bool? convOverride, bool global, bool expected)
        {
            Assert.Equal(expected, AgentEnablement.FeatureEnabled(convOverride, global));
        }

        [Fact]
        public void GlobalDefault_IsOff()
        {
            Assert.False(AgentEnablement.GlobalDefault);
            Assert.False(AgentEnablement.FeatureEnabled(null, AgentEnablement.GlobalDefault));
        }
    }
}
