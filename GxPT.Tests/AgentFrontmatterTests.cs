using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentFrontmatterTests
    {
        [Fact]
        public void Parse_ReadsAllKeysAndBody()
        {
            string text =
                "---\n" +
                "name: Code Explorer\n" +
                "description: Use to search the codebase. Read-only.\n" +
                "tools: [files__read, files__list, files__search]\n" +
                "max_tier: readonly\n" +
                "model: anthropic/claude-sonnet-4-6\n" +
                "---\n" +
                "\n" +
                "You are a code-exploration specialist.\n";

            AgentFrontmatter fm = AgentFrontmatter.Parse(text);

            Assert.True(fm.HasFrontmatter);
            Assert.Equal("Code Explorer", fm.Name);
            Assert.Equal("Use to search the codebase. Read-only.", fm.Description);
            Assert.Equal(new string[] { "files__read", "files__list", "files__search" }, fm.Tools);
            Assert.Equal(AgentMaxTier.ReadOnly, fm.MaxTier);
            Assert.Equal("anthropic/claude-sonnet-4-6", fm.Model);
            Assert.Equal("You are a code-exploration specialist.", fm.Body);
        }

        [Fact]
        public void Parse_DefaultsWhenKeysOmitted()
        {
            string text =
                "---\n" +
                "name: Plain\n" +
                "description: Minimal agent.\n" +
                "---\n" +
                "body\n";

            AgentFrontmatter fm = AgentFrontmatter.Parse(text);

            Assert.Null(fm.Tools);                          // absent => null (resolved to ReadOnly default later)
            Assert.Equal(AgentMaxTier.Write, fm.MaxTier);   // default ceiling
            Assert.Null(fm.Model);
        }

        [Fact]
        public void ParseToolList_Variants()
        {
            Assert.Equal(new string[] { "a", "b" }, AgentFrontmatter.ParseToolList("[a, b]"));
            Assert.Equal(new string[] { "*" }, AgentFrontmatter.ParseToolList("[*]"));
            Assert.Equal(new string[] { "files__read" }, AgentFrontmatter.ParseToolList("files__read")); // bare single
            Assert.Equal(new string[] { "a", "b" }, AgentFrontmatter.ParseToolList("[\"a\", 'b']"));      // quotes stripped
            Assert.Empty(AgentFrontmatter.ParseToolList("[]"));     // explicit empty => zero-length, not null
            Assert.Null(AgentFrontmatter.ParseToolList(""));        // blank => null (not specified)
            Assert.Null(AgentFrontmatter.ParseToolList(null));
        }

        [Fact]
        public void Parse_EmptyToolsListIsDistinctFromAbsent()
        {
            AgentFrontmatter present = AgentFrontmatter.Parse("---\ndescription: d\ntools: []\n---\nb\n");
            Assert.NotNull(present.Tools);
            Assert.Empty(present.Tools);

            AgentFrontmatter absent = AgentFrontmatter.Parse("---\ndescription: d\n---\nb\n");
            Assert.Null(absent.Tools);
        }

        // Expected is the enum's name (a string), not the enum itself: a public xUnit test method can't
        // take an `internal` enum parameter (CS0051). The body compares against fm.MaxTier.ToString().
        [Theory]
        [InlineData("readonly", "ReadOnly")]
        [InlineData("read-only", "ReadOnly")]
        [InlineData("WRITE", "Write")]
        [InlineData("destructive", "Destructive")]
        [InlineData("nonsense", "Write")]   // invalid => default Write
        public void Parse_MaxTier(string value, string expected)
        {
            AgentFrontmatter fm = AgentFrontmatter.Parse("---\ndescription: d\nmax_tier: " + value + "\n---\nb\n");
            Assert.Equal(expected, fm.MaxTier.ToString());
        }

        [Fact]
        public void Parse_UnknownKeysIgnored_FirstWins()
        {
            string text =
                "---\n" +
                "description: First.\n" +
                "description: Second.\n" +   // duplicate ignored (first wins)
                "max_tier: readonly\n" +
                "max_tier: destructive\n" +  // duplicate ignored (first wins)
                "color: blue\n" +            // unknown key ignored
                "---\n" +
                "body\n";

            AgentFrontmatter fm = AgentFrontmatter.Parse(text);

            Assert.Equal("First.", fm.Description);
            Assert.Equal(AgentMaxTier.ReadOnly, fm.MaxTier);
        }

        [Fact]
        public void Parse_IsCrlfAgnosticAndStripsBom()
        {
            string text = "\uFEFF---\r\ndescription: D\r\ntools: [a]\r\n---\r\nbody line\r\n";

            AgentFrontmatter fm = AgentFrontmatter.Parse(text);

            Assert.True(fm.HasFrontmatter);
            Assert.Equal("D", fm.Description);
            Assert.Equal(new string[] { "a" }, fm.Tools);
            Assert.Equal("body line", fm.Body);
        }

        [Theory]
        [InlineData("max_turns: 50", 50)]
        [InlineData("max_turns: 0", 0)]        // 0 stays unset
        [InlineData("max_turns: -5", 0)]       // negative ignored -> unset
        [InlineData("max_turns: abc", 0)]      // non-numeric ignored -> unset
        public void Parse_MaxTurns(string line, int expected)
        {
            AgentFrontmatter fm = AgentFrontmatter.Parse("---\ndescription: d\n" + line + "\n---\nb\n");
            Assert.Equal(expected, fm.MaxTurns);
        }

        [Fact]
        public void Parse_MaxTurns_DefaultsToZeroWhenAbsent()
        {
            AgentFrontmatter fm = AgentFrontmatter.Parse("---\ndescription: d\n---\nb\n");
            Assert.Equal(0, fm.MaxTurns);
        }

        [Fact]
        public void Parse_NoFrontmatter_WholeTextIsBody()
        {
            AgentFrontmatter fm = AgentFrontmatter.Parse("just a body, no frontmatter");

            Assert.False(fm.HasFrontmatter);
            Assert.Null(fm.Description);
            Assert.Equal("just a body, no frontmatter", fm.Body);
        }
    }
}
