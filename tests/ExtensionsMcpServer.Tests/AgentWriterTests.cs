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
        private readonly string _bundled;    // a read-only bundled agents root (shipped agents)
        private readonly AgentWriter _writer;

        public AgentWriterTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_agentwriter_" + Guid.NewGuid().ToString("N"));
            _project = Path.Combine(_root, "agents");
            _bundled = Path.Combine(_root, "bundled");
            Directory.CreateDirectory(_project);
            Directory.CreateDirectory(_bundled);
            _writer = new AgentWriter(_project, null, _bundled, "project");  // user-global root not wired
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private string AgentFile(string slug) { return Path.Combine(_project, slug + ".md"); }

        // Lay down a bundled (read-only) agent <slug>.md directly, bypassing the writer.
        private void MakeBundled(string slug, string body)
        {
            File.WriteAllText(Path.Combine(_bundled, slug + ".md"),
                "---\nname: " + slug + "\ndescription: a bundled agent\n---\n" + body + "\n");
        }

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
        public void CreateAgent_RejectsNameThatDoesNotMatchSlug()
        {
            // slug 'code-review' but name 'Code Reviewer' -> they describe different handles.
            AgentWriteException ex = Assert.Throws<AgentWriteException>(() =>
                _writer.CreateAgent(null, "code-review", "Code Reviewer", "Reviews code.", null, null, null, 0, "body"));
            Assert.Contains("don't match", ex.Message);
            Assert.False(File.Exists(AgentFile("code-review")));  // nothing written on rejection
        }

        [Fact]
        public void CreateAgent_RejectsNameWithNoAlphanumerics()
        {
            // A name with no letters/digits can't form a handle - the error should say so, not give the
            // circular "set slug to <slug>" guidance.
            AgentWriteException ex = Assert.Throws<AgentWriteException>(() =>
                _writer.CreateAgent(null, "my-agent", "!!!", "desc", null, null, null, 0, "body"));
            Assert.Contains("no letters or digits", ex.Message);
            Assert.False(File.Exists(AgentFile("my-agent")));
        }

        [Fact]
        public void CreateAgent_AllowsAcronymNameAlignment()
        {
            // 'GitHub Researcher' kebab-splits oddly, but ignoring word boundaries it matches the slug.
            _writer.CreateAgent(null, "github-researcher", "GitHub Researcher", "Researches a repo.",
                null, null, null, 0, "body");
            Assert.True(File.Exists(AgentFile("github-researcher")));
            Assert.Contains("name: GitHub Researcher", File.ReadAllText(AgentFile("github-researcher")));
        }

        [Fact]
        public void UpdateAgent_RejectsNameThatDivergesFromSlug()
        {
            _writer.CreateAgent(null, "code-explore", "Code Explore", "Explores code.", null, null, null, 0, "body");
            AgentWriteException ex = Assert.Throws<AgentWriteException>(() =>
                _writer.UpdateAgent(null, "code-explore", "Code Reviewer", null, null, null, null, 0, null));
            Assert.Contains("rename", ex.Message);
            // The original name is left intact - the rejected update wrote nothing.
            Assert.Contains("name: Code Explore", File.ReadAllText(AgentFile("code-explore")));
        }

        [Fact]
        public void UpdateAgent_AllowsNameReCasingThatPreservesSlug()
        {
            _writer.CreateAgent(null, "release-notes", "Release Notes", "Drafts notes.", null, null, null, 0, "body");
            _writer.UpdateAgent(null, "release-notes", "RELEASE notes", null, null, null, null, 0, null);
            Assert.Contains("name: RELEASE notes", File.ReadAllText(AgentFile("release-notes")));
        }

        [Fact]
        public void RenameAgent_MovesFileDerivesNameAndPreservesContract()
        {
            _writer.CreateAgent(null, "code-explore", "Code Explore", "Explores code.",
                new[] { "files__read" }, "readonly", "deepseek/x", 20, "You are an explorer.");

            _writer.RenameAgent(null, "code-explore", "code-search", null);   // no new_name -> derived

            Assert.False(File.Exists(AgentFile("code-explore")));   // old file gone
            string text = File.ReadAllText(AgentFile("code-search"));
            Assert.Contains("name: Code Search", text);            // Title Case derived from the new slug
            AgentFrontmatter fm = AgentFrontmatter.Parse(text);
            Assert.Equal("Explores code.", fm.Description);        // contract preserved
            Assert.Equal("[files__read]", fm.ToolsRaw);
            Assert.Equal("readonly", fm.MaxTierRaw);
            Assert.Equal("deepseek/x", fm.ModelRaw);
            Assert.Equal("You are an explorer.", fm.Body);
        }

        [Fact]
        public void RenameAgent_KeepsExplicitNameWhenAligned()
        {
            _writer.CreateAgent(null, "github-sync", "GitHub Sync", "Syncs a repo.", null, null, null, 0, "body");
            _writer.RenameAgent(null, "github-sync", "github-mirror", "GitHub Mirror");  // acronym casing preserved
            Assert.Contains("name: GitHub Mirror", File.ReadAllText(AgentFile("github-mirror")));
        }

        [Fact]
        public void RenameAgent_RefusesExistingTarget()
        {
            _writer.CreateAgent(null, "alpha", "Alpha", "d", null, null, null, 0, "b");
            _writer.CreateAgent(null, "beta", "Beta", "d", null, null, null, 0, "b");
            Assert.Throws<AgentWriteException>(() => _writer.RenameAgent(null, "alpha", "beta", null));
            Assert.True(File.Exists(AgentFile("alpha")));   // source untouched on refusal
            Assert.True(File.Exists(AgentFile("beta")));
        }

        [Fact]
        public void RenameAgent_RejectsNameNotMatchingNewSlug()
        {
            _writer.CreateAgent(null, "code-explore", "Code Explore", "Explores code.", null, null, null, 0, "body");
            Assert.Throws<AgentWriteException>(() =>
                _writer.RenameAgent(null, "code-explore", "code-search", "Totally Different"));
            Assert.True(File.Exists(AgentFile("code-explore")));      // nothing moved
            Assert.False(File.Exists(AgentFile("code-search")));
        }

        [Fact]
        public void RenameAgent_RefusesBundledSource()
        {
            MakeBundled("explore", "bundled body");   // lives only in the read-only bundled root
            AgentWriteException ex = Assert.Throws<AgentWriteException>(
                () => _writer.RenameAgent(null, "explore", "code-explore", null));
            Assert.Contains("bundled", ex.Message);
            Assert.Contains("can't be renamed", ex.Message);   // rename-specific, not the generic edit message
            Assert.Contains("create_agent", ex.Message);
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
            string text = _writer.ReadAgent("a");
            Assert.Contains("name: A", text);
            Assert.Contains("the body", text);
        }

        [Fact]
        public void ReadAgent_FindsBundledAgent()
        {
            // The reported bug: a bundled (shipped) agent like 'explore' was invisible to read_agent because
            // it only looked at the writable scopes. Reads must span the bundled root too.
            MakeBundled("explore", "You are an explorer.");
            string text = _writer.ReadAgent("explore");
            Assert.Contains("You are an explorer.", text);
        }

        [Fact]
        public void ReadAgent_NormalizesNonKebabFilename()
        {
            // A hand-placed file with a non-kebab name resolves under its normalized slug, matching the host
            // catalog (which derives the slug via SkillSlug.Make), not only the canonical "<slug>.md".
            File.WriteAllText(Path.Combine(_bundled, "Code Explorer.md"),
                "---\nname: Code Explorer\ndescription: d\n---\nthe body\n");
            Assert.Contains("the body", _writer.ReadAgent("code-explorer"));
            Assert.Contains("- code-explorer (bundled)", _writer.ListAgents());
        }

        [Fact]
        public void ReadAgent_MissingEverywhere_Throws()
        {
            Assert.Throws<AgentWriteException>(() => _writer.ReadAgent("nope"));
        }

        [Fact]
        public void ReadAgent_ProjectShadowsBundled()
        {
            MakeBundled("explore", "bundled body");
            _writer.CreateAgent(null, "explore", "Explore", "desc", null, null, null, 0, "project body");
            Assert.Contains("project body", _writer.ReadAgent("explore"));
        }

        [Fact]
        public void ListAgents_SpansScopesWithSource()
        {
            MakeBundled("explore", "b");
            _writer.CreateAgent(null, "alpha", "Alpha", "d", null, null, null, 0, "b");
            string listing = _writer.ListAgents();
            Assert.Contains("- alpha (project)", listing);
            Assert.Contains("- explore (bundled)", listing);
        }

        [Fact]
        public void UpdateAgent_BundledOnly_ReportsBundledNotMissing()
        {
            MakeBundled("explore", "bundled body");
            AgentWriteException ex = Assert.Throws<AgentWriteException>(
                () => _writer.UpdateAgent(null, "explore", null, "new desc", null, null, null, 0, null));
            Assert.Contains("bundled", ex.Message);
            Assert.Contains("create_agent", ex.Message); // points at the override path
        }

        [Fact]
        public void DeleteAgent_BundledOnly_ReportsBundledReadOnly()
        {
            MakeBundled("explore", "bundled body");
            AgentWriteException ex = Assert.Throws<AgentWriteException>(
                () => _writer.DeleteAgent(null, "explore"));
            Assert.Contains("bundled", ex.Message);
            Assert.Contains("read-only", ex.Message);
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
            Assert.StartsWith("OK:", _writer.ValidateAgent("a"));
        }

        [Fact]
        public void ValidateAgent_WarnsOnUnknownMaxTier()
        {
            // Write a file directly with a bad max_tier (bypassing create's validation) to exercise the
            // validator's warning path.
            File.WriteAllText(AgentFile("a"), "---\nname: A\ndescription: d\nmax_tier: bogus\n---\nbody\n");
            string result = _writer.ValidateAgent("a");
            Assert.StartsWith("OK:", result);
            Assert.Contains("WARNING", result);
        }

        [Fact]
        public void UpdateAgent_BlankScalarKeepsExisting()
        {
            // A present-but-empty model/name means "keep", not "clear" (only a non-blank value changes it).
            _writer.CreateAgent(null, "a", "A", "desc", null, "readonly", "deepseek/x", 0, "body");
            _writer.UpdateAgent(null, "a", "", "new desc", null, null, "", 0, null); // name="" and model="" => keep
            string text = File.ReadAllText(AgentFile("a"));
            Assert.Contains("name: A", text);            // kept (not wiped)
            Assert.Contains("model: deepseek/x", text);  // kept (not wiped)
            Assert.Contains("description: new desc", text);
        }

        [Fact]
        public void UpdateAgent_ExistsInOtherScope_PointsToThatScope()
        {
            string user = Path.Combine(_root, "user");
            Directory.CreateDirectory(user);
            AgentWriter w = new AgentWriter(_project, user, _bundled, "project");
            w.CreateAgent("user", "reviewer", "Reviewer", "desc", null, null, null, 0, "body");

            AgentWriteException ex = Assert.Throws<AgentWriteException>(
                () => w.UpdateAgent("project", "reviewer", null, "new", null, null, null, 0, null));
            Assert.Contains("'user' scope", ex.Message);
            Assert.Contains("scope:\"user\"", ex.Message);
        }

        [Fact]
        public void ReadAgent_DescriptionlessDraftDoesNotShadowBundled()
        {
            // A description-less project draft must not shadow a valid bundled agent of the same slug -
            // read_agent and list_agents resolve to what the host actually dispatches (the bundled one).
            MakeBundled("explore", "bundled body");                                   // valid (has description)
            File.WriteAllText(AgentFile("explore"), "---\nname: Explore\n---\ndraft body\n"); // project, NO description
            Assert.Contains("bundled body", _writer.ReadAgent("explore"));
            Assert.Contains("- explore (bundled)", _writer.ListAgents());
        }

        [Fact]
        public void ValidateAgent_LoneDescriptionlessDraft_ReportsInvalid()
        {
            // When NO root has a described copy, the lone draft is still found so validate can diagnose it.
            File.WriteAllText(AgentFile("draft"), "---\nname: Draft\n---\nbody\n"); // project-only, no description
            Assert.StartsWith("INVALID", _writer.ValidateAgent("draft"));
        }

        [Fact]
        public void EditAgent_MatchesExactly_PaddedOldStringFailsCleanly()
        {
            _writer.CreateAgent(null, "a", "A", "desc", null, null, null, 0, "You are X.");
            // edit_agent matches the body exactly: a whitespace-padded copy does NOT fuzzy-match (it throws
            // rather than silently editing the wrong span).
            Assert.Throws<AgentWriteException>(
                () => _writer.EditAgent(null, "a", "\n  You are X.  \n", "You are Y.", false));
            // The exact interior span still edits fine.
            _writer.EditAgent(null, "a", "You are X.", "You are Y.", false);
            string text = File.ReadAllText(AgentFile("a"));
            Assert.Contains("You are Y.", text);
            Assert.DoesNotContain("You are X.", text);
        }
    }
}
