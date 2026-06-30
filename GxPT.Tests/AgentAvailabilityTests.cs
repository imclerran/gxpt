using System;
using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public class AgentAvailabilityTests
    {
        // Real first-party tiers for the tools used here.
        private static ToolTier TierOf(string name)
        {
            switch (name)
            {
                case "files__read":
                case "files__list":
                case "files__search":
                case "web__search":
                case "web__extract":
                    return ToolTier.ReadOnly;
                case "files__edit":
                    return ToolTier.Write;
                case "command__run":
                    return ToolTier.Destructive;
                default:
                    return ToolTier.Write;
            }
        }

        private static Agent ReadOnlyAgent(string slug, string[] tools)
        {
            return new Agent(slug, slug, "d", tools, AgentMaxTier.ReadOnly,
                null, AgentEffort.Unset, 0, null, AgentSource.Bundled);
        }

        [Fact]
        public void Available_WhenAtLeastOneToolPresent()
        {
            Agent a = ReadOnlyAgent("explore", new string[] { "files__read", "files__search" });
            string[] parent = new string[] { "files__read", "files__list" };
            Assert.True(AgentAvailability.IsAvailable(a, parent, TierOf));
        }

        [Fact]
        public void Unavailable_WhenNoRequiredToolPresent()
        {
            Agent a = ReadOnlyAgent("web-research", new string[] { "web__search", "web__extract" });
            string[] parent = new string[] { "files__read", "files__list" };   // no web tools
            Assert.False(AgentAvailability.IsAvailable(a, parent, TierOf));

            List<string> missing = AgentAvailability.MissingTools(a, parent);
            Assert.Contains("web__search", missing);
            Assert.Contains("web__extract", missing);
        }

        [Fact]
        public void MissingTools_ListsOnlyTheUnavailableOnes()
        {
            Agent a = ReadOnlyAgent("explore", new string[] { "files__read", "web__search" });
            string[] parent = new string[] { "files__read" };   // web missing, files present
            List<string> missing = AgentAvailability.MissingTools(a, parent);
            Assert.Contains("web__search", missing);
            Assert.DoesNotContain("files__read", missing);
        }

        [Fact]
        public void Unavailable_WhenToolsExceedTierCeiling()
        {
            // A read-only agent that only lists a write tool resolves to nothing under the readonly ceiling.
            Agent a = ReadOnlyAgent("ro", new string[] { "files__edit" });
            string[] parent = new string[] { "files__edit" };
            Assert.False(AgentAvailability.IsAvailable(a, parent, TierOf));
        }
    }
}
