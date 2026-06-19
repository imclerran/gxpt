using System;
using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentToolResolverTests
    {
        // A fixed tier table for the tests (private helper; internal ToolTier is fine in a private member).
        private static ToolTier TierOf(string name)
        {
            switch (name)
            {
                case "files__read":
                case "files__list":
                case "git__status":
                    return ToolTier.ReadOnly;
                case "files__write":
                case "files__edit":
                case "git__commit":
                    return ToolTier.Write;
                case "files__delete":
                case "git__push":
                    return ToolTier.Destructive;
                default:
                    return ToolTier.Write;
            }
        }

        private static readonly string[] Parent = new string[] {
            "files__read", "files__list", "files__write", "files__edit", "files__delete",
            "git__status", "git__commit", "git__push"
        };

        private static List<string> Resolve(string[] tools, AgentMaxTier tier)
        {
            return AgentToolResolver.Resolve(tools, tier, Parent, TierOf);
        }

        [Fact]
        public void NullTools_InheritsReadOnlyOnly_RegardlessOfCeiling()
        {
            // Omitted allowlist => only ReadOnly tools, even with a Write ceiling (fail-safe default A5).
            List<string> eff = Resolve(null, AgentMaxTier.Write);
            Assert.Equal(new string[] { "files__read", "files__list", "git__status" }, eff.ToArray());
        }

        [Fact]
        public void StarTools_WithWriteCeiling_IncludesReadOnlyAndWrite_NotDestructive()
        {
            List<string> eff = Resolve(new string[] { "*" }, AgentMaxTier.Write);
            Assert.Equal(new string[] {
                "files__read", "files__list", "files__write", "files__edit",
                "git__status", "git__commit"
            }, eff.ToArray());
            Assert.DoesNotContain("files__delete", eff);
            Assert.DoesNotContain("git__push", eff);
        }

        [Fact]
        public void StarTools_WithDestructiveCeiling_IncludesEverything_OrderPreserved()
        {
            List<string> eff = Resolve(new string[] { "*" }, AgentMaxTier.Destructive);
            Assert.Equal(Parent, eff.ToArray());
        }

        [Fact]
        public void StarTools_WithReadOnlyCeiling_CapsToReadOnly()
        {
            List<string> eff = Resolve(new string[] { "*" }, AgentMaxTier.ReadOnly);
            Assert.Equal(new string[] { "files__read", "files__list", "git__status" }, eff.ToArray());
        }

        [Fact]
        public void PrefixGlob_MatchesServerFamily_TierCapped()
        {
            List<string> eff = Resolve(new string[] { "files__*" }, AgentMaxTier.Write);
            Assert.Equal(new string[] { "files__read", "files__list", "files__write", "files__edit" }, eff.ToArray());
            Assert.DoesNotContain("files__delete", eff);   // Destructive, over the Write ceiling
            Assert.DoesNotContain("git__status", eff);     // different family
        }

        [Fact]
        public void SuffixGlob_Matches()
        {
            List<string> eff = Resolve(new string[] { "*__read" }, AgentMaxTier.Write);
            Assert.Equal(new string[] { "files__read" }, eff.ToArray());
        }

        [Fact]
        public void ExactAndMultiplePatterns()
        {
            List<string> eff = Resolve(new string[] { "files__read", "git__commit" }, AgentMaxTier.Write);
            Assert.Equal(new string[] { "files__read", "git__commit" }, eff.ToArray());
        }

        [Fact]
        public void AllowlistedToolNotAvailableToParent_IsExcluded_NoEscalation()
        {
            // The agent lists a tool the parent can't call here -> it is simply absent (never granted).
            List<string> eff = Resolve(new string[] { "files__read", "web__search" }, AgentMaxTier.Write);
            Assert.Equal(new string[] { "files__read" }, eff.ToArray());
        }

        [Fact]
        public void DispatchAgent_NeverIncluded_EvenWithStar()
        {
            string[] parent = new string[] { "files__read", "dispatch_agent" };
            List<string> eff = AgentToolResolver.Resolve(new string[] { "*" }, AgentMaxTier.Destructive, parent,
                delegate(string n) { return ToolTier.ReadOnly; });
            Assert.Equal(new string[] { "files__read" }, eff.ToArray());
        }

        [Fact]
        public void Hidden_IsParentMinusEffective()
        {
            List<string> hidden = AgentToolResolver.Hidden(new string[] { "files__*" }, AgentMaxTier.Write, Parent, TierOf);
            Assert.Equal(new string[] { "files__delete", "git__status", "git__commit", "git__push" }, hidden.ToArray());
        }

        [Fact]
        public void Resolve_DedupesParent_PreservesFirstOrder()
        {
            string[] parent = new string[] { "files__read", "files__read", "files__list" };
            List<string> eff = AgentToolResolver.Resolve(new string[] { "*" }, AgentMaxTier.ReadOnly, parent, TierOf);
            Assert.Equal(new string[] { "files__read", "files__list" }, eff.ToArray());
        }

        [Fact]
        public void NullTierOf_TreatsEveryToolAsWrite()
        {
            string[] parent = new string[] { "x__a", "x__b" };
            Assert.Empty(AgentToolResolver.Resolve(new string[] { "*" }, AgentMaxTier.ReadOnly, parent, null));
            Assert.Equal(parent,
                AgentToolResolver.Resolve(new string[] { "*" }, AgentMaxTier.Write, parent, null).ToArray());
        }

        // ---- WildcardMatch unit cases (all-public signature; no internal types) ----

        [Theory]
        [InlineData("*", "anything", true)]
        [InlineData("files__read", "files__read", true)]
        [InlineData("files__read", "files__write", false)]
        [InlineData("FILES__READ", "files__read", true)]      // case-insensitive
        [InlineData("files__*", "files__read", true)]
        [InlineData("files__*", "git__status", false)]
        [InlineData("*__read", "files__read", true)]
        [InlineData("*__read", "files__readme", false)]       // suffix must be at the end
        [InlineData("a*c", "abc", true)]
        [InlineData("a*c", "ac", true)]                       // '*' matches empty
        [InlineData("a*c", "axyc", true)]
        [InlineData("a*c", "abd", false)]
        [InlineData("", "x", false)]                          // empty pattern matches nothing
        public void WildcardMatch_Cases(string pattern, string name, bool expected)
        {
            Assert.Equal(expected, AgentToolResolver.WildcardMatch(pattern, name));
        }
    }
}
