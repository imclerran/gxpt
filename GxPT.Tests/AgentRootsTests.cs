using System;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Root/catalog resolution for agents - the AgentRoots analogue of SkillRootsTests.
    public sealed class AgentRootsTests : IDisposable
    {
        private readonly string _root;

        public AgentRootsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_agentroots_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void BundledRoot_AppendsAgentsDir()
        {
            Assert.Equal(Path.Combine("base", "agents"), AgentRoots.BundledRoot("base"));
            Assert.Null(AgentRoots.BundledRoot(null));
        }

        [Fact]
        public void ProjectRoot_IsUnderDotGxpt_OrNull()
        {
            string expected = Path.Combine(Path.Combine("work", ".gxpt"), "agents");
            Assert.Equal(expected, AgentRoots.ProjectRoot("work"));
            Assert.Null(AgentRoots.ProjectRoot(null));
        }

        [Fact]
        public void BuildCatalog_DiscoversProjectAgentUnderDotGxpt()
        {
            string agentsDir = Path.Combine(Path.Combine(_root, ".gxpt"), "agents");
            Directory.CreateDirectory(agentsDir);
            File.WriteAllText(Path.Combine(agentsDir, "proj-agent.md"),
                "---\ndescription: A project agent.\n---\nbody\n", new UTF8Encoding(false));

            AgentCatalog cat = AgentRoots.BuildCatalog("no-such-exe-dir", _root);

            Agent a;
            Assert.True(cat.TryGet("proj-agent", out a));
            Assert.Equal(AgentSource.Project, a.Source);
        }
    }
}
