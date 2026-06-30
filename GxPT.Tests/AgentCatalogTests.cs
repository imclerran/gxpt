using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentCatalogTests : IDisposable
    {
        private readonly string _root;
        private readonly string _bundled;
        private readonly string _user;
        private readonly string _project;

        public AgentCatalogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_agents_" + Guid.NewGuid().ToString("N"));
            _bundled = Path.Combine(_root, "bundled");
            _user = Path.Combine(_root, "user");
            _project = Path.Combine(_root, "project");
            Directory.CreateDirectory(_bundled);
            Directory.CreateDirectory(_user);
            Directory.CreateDirectory(_project);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        // Writes <root>/<fileName> with the given frontmatter. fileName carries its own extension so the
        // ".md only" filter can be exercised. A null description omits the line.
        private static void WriteFile(string root, string fileName, string body, params string[] fmLines)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("---\n");
            for (int i = 0; i < fmLines.Length; i++)
                if (fmLines[i] != null) sb.Append(fmLines[i]).Append('\n');
            sb.Append("---\n\n").Append(body == null ? "" : body).Append('\n');
            File.WriteAllText(Path.Combine(root, fileName), sb.ToString(), new UTF8Encoding(false));
        }

        [Fact]
        public void Build_DiscoversBundledAgent()
        {
            WriteFile(_bundled, "code-explorer.md", "You explore code.",
                "name: Code Explorer", "description: Search the codebase.");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Assert.Single(cat.Agents);
            Agent a;
            Assert.True(cat.TryGet("code-explorer", out a));
            Assert.Equal("Code Explorer", a.Name);
            Assert.Equal("Search the codebase.", a.Description);
            Assert.Equal(AgentSource.Bundled, a.Source);
            Assert.EndsWith("code-explorer.md", a.FilePath);
        }

        [Fact]
        public void Build_NormalizesFileNameToSlug_NameFallsBackToSlug()
        {
            WriteFile(_bundled, "Code Explorer.md", "body", "description: d");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Agent a;
            Assert.True(cat.TryGet("code-explorer", out a));
            Assert.Equal("code-explorer", a.Name);   // no frontmatter name -> slug
        }

        [Fact]
        public void Build_CarriesFrontmatterContract()
        {
            WriteFile(_bundled, "verify.md", "You verify.",
                "description: Run the build and tests.",
                "tools: [files__read, command__run]",
                "max_tier: write",
                "model: anthropic/claude-sonnet-4-6",
                "effort: high");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Agent a;
            Assert.True(cat.TryGet("verify", out a));
            Assert.Equal(new string[] { "files__read", "command__run" }, a.Tools);
            Assert.Equal(AgentMaxTier.Write, a.MaxTier);
            Assert.Equal("anthropic/claude-sonnet-4-6", a.Model);
            Assert.Equal(AgentEffort.High, a.Effort);
        }

        [Fact]
        public void Build_ProjectShadowsUserShadowsBundled()
        {
            WriteFile(_bundled, "dup.md", "b", "description: bundled one");
            WriteFile(_user, "dup.md", "b", "description: user one");
            WriteFile(_project, "dup.md", "b", "description: project one");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _user, _project);

            Agent a;
            Assert.True(cat.TryGet("dup", out a));
            Assert.Equal("project one", a.Description);
            Assert.Equal(AgentSource.Project, a.Source);
            Assert.Single(cat.Agents);
        }

        [Fact]
        public void Build_UserShadowsBundled_WhenNoProject()
        {
            WriteFile(_bundled, "dup.md", "b", "description: bundled one");
            WriteFile(_user, "dup.md", "b", "description: user one");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _user, null);

            Agent a;
            Assert.True(cat.TryGet("dup", out a));
            Assert.Equal("user one", a.Description);
            Assert.Equal(AgentSource.User, a.Source);
        }

        [Fact]
        public void Build_SkipsFileWithoutDescription()
        {
            WriteFile(_bundled, "no-desc.md", "body", "name: No Description");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Assert.Empty(cat.Agents);
        }

        [Fact]
        public void Build_IgnoresNonMarkdownFiles()
        {
            WriteFile(_bundled, "real.md", "b", "description: real");
            File.WriteAllText(Path.Combine(_bundled, "notes.txt"), "not an agent", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_bundled, "README"), "nope", new UTF8Encoding(false));

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Assert.Single(cat.Agents);
            Agent a;
            Assert.True(cat.TryGet("real", out a));
        }

        [Fact]
        public void Build_MissingOrNullRootsAreSkipped()
        {
            WriteFile(_bundled, "only.md", "b", "description: only");

            AgentCatalog cat = AgentCatalog.Build(_bundled, null,
                Path.Combine(_root, "does-not-exist"));

            Assert.Single(cat.Agents);
        }

        [Fact]
        public void TryGet_IsCaseInsensitive()
        {
            WriteFile(_bundled, "code-explorer.md", "b", "description: d");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Agent a;
            Assert.True(cat.TryGet("CODE-Explorer", out a));
        }

        [Fact]
        public void Agents_AreSlugSorted()
        {
            WriteFile(_bundled, "zebra.md", "b", "description: z");
            WriteFile(_bundled, "alpha.md", "b", "description: a");
            WriteFile(_bundled, "mike.md", "b", "description: m");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Assert.Equal(3, cat.Agents.Count);
            Assert.Equal("alpha", cat.Agents[0].Slug);
            Assert.Equal("mike", cat.Agents[1].Slug);
            Assert.Equal("zebra", cat.Agents[2].Slug);
        }

        [Fact]
        public void BuildManifest_OneSlugDescriptionLinePerAgent_SlugOrdered()
        {
            WriteFile(_bundled, "zebra.md", "b", "description: Z agent.");
            WriteFile(_bundled, "alpha.md", "b", "description: A agent.");

            AgentCatalog cat = AgentCatalog.Build(_bundled, _project);

            Assert.Equal("- alpha - A agent.\n- zebra - Z agent.", cat.BuildManifest());
        }
    }
}
