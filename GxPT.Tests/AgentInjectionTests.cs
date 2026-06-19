using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentInjectionTests
    {
        private static Agent A(string slug, string desc)
        {
            return new Agent(slug, slug, desc, null, AgentMaxTier.Write, AgentAutonomy.Gated, null,
                             0, slug + ".md", AgentSource.Bundled);
        }

        [Fact]
        public void BuildManifestMessage_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(AgentInjection.BuildManifestMessage(null));
            Assert.Null(AgentInjection.BuildManifestMessage(new List<Agent>()));
        }

        [Fact]
        public void BuildManifestMessage_HasFramingAndManifestLines()
        {
            List<Agent> agents = new List<Agent>();
            agents.Add(A("code-explorer", "Search the codebase."));
            agents.Add(A("verify", "Run tests."));

            string msg = AgentInjection.BuildManifestMessage(agents);

            Assert.Contains("# Agents", msg);
            Assert.Contains("dispatch_agent", msg);
            Assert.Contains("- code-explorer - Search the codebase.", msg);
            Assert.Contains("- verify - Run tests.", msg);
        }
    }
}
