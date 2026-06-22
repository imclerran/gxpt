using System;
using System.IO;
using ExtensionsMcpServer;
using Xunit;

namespace ExtensionsMcpServer.Tests
{
    public sealed class AgentWriterTests : IDisposable
    {
        private readonly string _root;       // stand-in workspace root
        private readonly string _project;    // the project agents root the writer targets
        private readonly AgentWriter _writer;

        public AgentWriterTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_agentwriter_" + Guid.NewGuid().ToString("N"));
            _project = Path.Combine(_root, "agents");
            Directory.CreateDirectory(_project);
            _writer = new AgentWriter(_project, null, "project");   // user-global root not wired
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private string AgentFile(string slug) { return Path.Combine(_project, slug + ".md"); }

        [Fact]
        public void CreateAgent_WritesValidAgentMd()
        {
            _writer.CreateAgent(null, "explorer", "Explorer", "Use this agent to explore code.",
                new[] { "files__read", "git__status" }, "readonly", "deepseek/x", 25, "You are an explorer.");

            string text = File.ReadAllText(AgentFile("explorer"));
            Assert.Contains("name: Explorer", text);
            Assert.Contains("description: Use this agent to explore code.", text);
            Assert.Contains("tools: [files__read, git__status]", text);
            Assert.Contains("max_tier: readonly", text);
            Assert.Contains("model: deepseek/x", text);
            Assert.Contains("max_turns: 25", text);
            Assert.Contains("You are an explorer.", text);

            // Round-trips through the reader.
            AgentFrontmatter fm = AgentFrontmatter.Parse(text);
            Assert.Equal("Explorer", fm.Name);
            Assert.Equal("Use this agent to explore code.", fm.Description);
            Assert.Equal("[files__read, git__status]", fm.ToolsRaw);
            Assert.Equal("readonly", fm.MaxTierRaw);
            Assert.Equal("25", fm.MaxTurnsRaw);
            Assert.Equal("You are an explorer.", fm.Body);
        }

        [Fact]
        public void CreateAgent_NormalizesSlug()
        {
            _writer.CreateAgent(null, "Code Reviewer", "Code Reviewer", "Reviews code.", null, null, null, 0, "body");
            Assert.True(File.Exists(AgentFile("code-reviewer")));
        }

        [Fact]
        public void CreateAgent_OmitsOptionalFieldsWhenAbsent()
        {
            _writer.CreateAgent(null, "minimal", "Minimal", "A minimal agent.", null, null, null, 0, "body");
            string text = File.ReadAllText(AgentFile("minimal"));
            Assert.DoesNotContain("tools:", text);
            Assert.DoesNotContain("max_tier:", text);
            Assert.DoesNotContain("model:", text);
            Assert.DoesNotContain("max_turns:", text);
        }

        [Fact]
        public void CreateAgent_EmptyToolsArray_WritesExplicitEmptyList()
        {
            _writer.CreateAgent(null, "notools", "No Tools", "An agent with no tools.",
                new string[0], null, null, 0, "body");
            Assert.Contains("tools: []", File.ReadAllText(AgentFile("notools")));
        }

        [Fact]
        public void CreateAgent_RefusesExisting()
        {
            _writer.CreateAgent(null, "dup", "Dup", "desc", null, null, null, 0, "body");
            Assert.Throws<AgentWriteException>(() =>
                _writer.CreateAgent(null, "dup", "Dup", "desc", null, null, null, 0, "body"));
        }

        [Fact]
        public void CreateAgent_RejectsUnknownMaxTier()
        {
            Assert.Throws<AgentWriteException>(() =>
                _writer.CreateAgent(null, "bad", "Bad", "desc", null, "sometimes", null, 0, "body"));
        }

        [Fact]
        public void CreateAgent_RejectsToolWithComma()
        {
            Assert.Throws<AgentWriteException>(() =>
                _writer.CreateAgent(null, "bad", "Bad", "desc", new[] { "files__read, git__log" }, null, null, 0, "body"));
        }

        [Fact]
        public void UpdateAgent_PartialFields_KeepOthers()
        {
            _writer.CreateAgent(null, "a", "A", "old desc", new[] { "files__read" }, "readonly", "m", 10, "old body");
            _writer.UpdateAgent(null, "a", null, "new desc", null, null, null, 0, null);

            string text = File.ReadAllText(AgentFile("a"));
            Assert.Contains("description: new desc", text);
            Assert.Contains("tools: [files__read]", text);   // kept
            Assert.Contains("max_tier: readonly", text);     // kept
            Assert.Contains("model: m", text);               // kept
            Assert.Contains("max_turns: 10", text);          // kept
            Assert.Contains("old body", text);               // kept
        }

        [Fact]
        public void UpdateAgent_EmptyToolsArray_ClearsTools()
        {
            _writer.CreateAgent(null, "a", "A", "desc", new[] { "files__read" }, null, null, 0, "body");
            _writer.UpdateAgent(null, "a", null, null, new string[0], null, null, 0, null);
            Assert.Contains("tools: []", File.ReadAllText(AgentFile("a")));
        }

        [Fact]
        public void UpdateAgent_MissingAgent_Throws()
        {
            Assert.Throws<AgentWriteException>(() =>
                _writer.UpdateAgent(null, "ghost", "G", "desc", null, null, null, 0, null));
        }

        [Fact]
        public void EditAgent_ReplacesInBodyOnly()
        {
            _writer.CreateAgent(null, "a", "A", "desc", null, "write", null, 0, "Step one. Step two.");
            _writer.EditAgent(null, "a", "Step two.", "Step three.", false);

            string text = File.ReadAllText(AgentFile("a"));
            Assert.Contains("Step three.", text);
            Assert.DoesNotContain("Step two.", text);
            // Frontmatter preserved.
            Assert.Contains("max_tier: write", text);
        }

        [Fact]
        public void EditAgent_NonUniqueWithoutReplaceAll_Throws()
        {
            _writer.CreateAgent(null, "a", "A", "desc", null, null, null, 0, "x and x and x");
            Assert.Throws<AgentWriteException>(() => _writer.EditAgent(null, "a", "x", "y", false));
        }

        [Fact]
        public void ReadAgent_ReturnsFullText()
        {
            _writer.CreateAgent(null, "a", "A", "desc", null, null, null, 0, "the body");
            string text = _writer.ReadAgent(null, "a");
            Assert.Contains("name: A", text);
            Assert.Contains("the body", text);
        }

        [Fact]
        public void ListAgents_ListsSlugs()
        {
            _writer.CreateAgent(null, "alpha", "Alpha", "d", null, null, null, 0, "b");
            _writer.CreateAgent(null, "beta", "Beta", "d", null, null, null, 0, "b");
            string listing = _writer.ListAgents(null);
            Assert.Contains("- alpha", listing);
            Assert.Contains("- beta", listing);
        }

        [Fact]
        public void DeleteAgent_RemovesFile()
        {
            _writer.CreateAgent(null, "a", "A", "desc", null, null, null, 0, "body");
            _writer.DeleteAgent(null, "a");
            Assert.False(File.Exists(AgentFile("a")));
        }

        [Fact]
        public void ValidateAgent_OkForLoadableAgent()
        {
            _writer.CreateAgent(null, "a", "A", "desc", new[] { "files__read" }, "readonly", null, 0, "body");
            Assert.StartsWith("OK:", _writer.ValidateAgent(null, "a"));
        }

        [Fact]
        public void ValidateAgent_WarnsOnUnknownMaxTier()
        {
            // Write a file directly with a bad max_tier (bypassing create's validation) to exercise the
            // validator's warning path.
            File.WriteAllText(AgentFile("a"), "---\nname: A\ndescription: d\nmax_tier: bogus\n---\nbody\n");
            string result = _writer.ValidateAgent(null, "a");
            Assert.StartsWith("OK:", result);
            Assert.Contains("WARNING", result);
        }
    }
}
