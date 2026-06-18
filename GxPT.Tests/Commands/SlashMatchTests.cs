using System.Collections.Generic;
using System.Linq;
using GxPT;
using Xunit;

namespace GxPT.Tests.Commands
{
    public class SlashMatchTests
    {
        // ---- SlashMatch.HyphenPrefix ----

        [Theory]
        // Start-of-name prefix (the classic case).
        [InlineData("something", "some", true)]
        [InlineData("toggle-skill", "tog", true)]
        // Anchored after a hyphen: the query matches a later segment.
        [InlineData("do-something", "some", true)]
        [InlineData("some-setting-status", "some", true)]
        [InlineData("toggle-some-setting", "some", true)]
        [InlineData("toggle-skill", "skill", true)]
        // A typed hyphen forces the run to continue past it, so single-segment names drop out.
        [InlineData("something", "some-", false)]
        [InlineData("do-something", "some-", false)]
        [InlineData("some-setting-status", "some-", true)]
        [InlineData("toggle-some-setting", "some-", true)]
        // Case-insensitive; empty prefix matches everything; non-boundary substrings do not match.
        [InlineData("Toggle-Skill", "skill", true)]
        [InlineData("toggle-skill", "", true)]
        [InlineData("toggle-skill", "oggle", false)]
        [InlineData("toggle-skill", "kill", false)]
        public void HyphenPrefix_matches_at_segment_boundaries(string name, string query, bool expected)
        {
            Assert.Equal(expected, SlashMatch.HyphenPrefix(name, query));
        }

        // ---- SlashCommandRegistry.Match (the user-facing example) ----

        private static ISlashCommand Cmd(string name)
        {
            return new PromptCommand(name, "", "t", "", false, null, null, null);
        }

        private static List<string> MatchNames(SlashCommandRegistry reg, string prefix)
        {
            return reg.Match(prefix).Select(c => c.Name).ToList();
        }

        [Fact]
        public void Match_surfaces_every_segment_that_starts_with_the_prefix()
        {
            var reg = new SlashCommandRegistry(new[]
            {
                Cmd("something"), Cmd("do-something"),
                Cmd("some-setting-status"), Cmd("toggle-some-setting"),
                Cmd("unrelated")
            });

            var some = MatchNames(reg, "some");
            Assert.Equal(
                new[] { "something", "do-something", "some-setting-status", "toggle-some-setting" },
                some.ToArray());

            // Typing the hyphen narrows to the segment runs that continue past it.
            var someDash = MatchNames(reg, "some-");
            Assert.Equal(new[] { "some-setting-status", "toggle-some-setting" }, someDash.ToArray());
        }

        [Fact]
        public void Match_empty_prefix_returns_all_in_registration_order()
        {
            var reg = new SlashCommandRegistry(new[] { Cmd("alpha"), Cmd("beta") });
            Assert.Equal(new[] { "alpha", "beta" }, MatchNames(reg, "").ToArray());
        }
    }
}
