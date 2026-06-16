using System;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillInjectionTests : IDisposable
    {
        private readonly string _root;

        public SkillInjectionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillinj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void BuildManifestMessage_EmptyCatalog_ReturnsNull()
        {
            SkillCatalog empty = SkillCatalog.Build(_root, null);   // _root has no skill folders
            Assert.Null(SkillInjection.BuildManifestMessage(empty.Skills));
            Assert.Null(SkillInjection.BuildManifestMessage(null));
        }

        [Fact]
        public void BuildManifestMessage_NonEmpty_FramesAndListsSkills()
        {
            string dir = Path.Combine(_root, "release-notes");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: Release Notes\ndescription: Draft notes.\n---\nbody\n", new UTF8Encoding(false));

            SkillCatalog cat = SkillCatalog.Build(_root, null);
            string msg = SkillInjection.BuildManifestMessage(cat.Skills);

            Assert.NotNull(msg);
            Assert.Contains("# Skills", msg);
            Assert.Contains("open_skill", msg);
            Assert.Contains("release-notes", msg);
            Assert.Contains("Draft notes.", msg);
        }
    }
}
