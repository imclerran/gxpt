using System;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Root/catalog resolution, split out of SkillInjection into SkillRoots (issue #119).
    public sealed class SkillRootsTests : IDisposable
    {
        private readonly string _root;

        public SkillRootsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillroots_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void BundledRoot_AppendsSkillsDir()
        {
            Assert.Equal(Path.Combine("base", "skills"), SkillRoots.BundledRoot("base"));
            Assert.Null(SkillRoots.BundledRoot(null));
        }

        [Fact]
        public void ProjectRoot_IsUnderDotGxpt_OrNull()
        {
            string expected = Path.Combine(Path.Combine("work", ".gxpt"), "skills");
            Assert.Equal(expected, SkillRoots.ProjectRoot("work"));
            Assert.Null(SkillRoots.ProjectRoot(null));
        }

        [Fact]
        public void BuildCatalog_DiscoversProjectSkillsUnderDotGxpt()
        {
            string skillDir = Path.Combine(Path.Combine(Path.Combine(_root, ".gxpt"), "skills"), "proj-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\ndescription: A project skill.\n---\nbody\n", new UTF8Encoding(false));

            SkillCatalog cat = SkillRoots.BuildCatalog("no-such-exe-dir", _root);

            Skill s;
            Assert.True(cat.TryGet("proj-skill", out s));
            Assert.Equal(SkillSource.Project, s.Source);
        }
    }
}
