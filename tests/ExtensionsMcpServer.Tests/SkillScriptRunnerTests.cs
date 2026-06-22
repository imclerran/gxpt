using System;
using System.Collections.Generic;
using System.IO;
using ExtensionsMcpServer;
using Xunit;

namespace ExtensionsMcpServer.Tests
{
    // Covers the pure parts of run_skill_script: slug+relpath resolution across roots, the .bat/.cmd
    // allowlist and sandbox, and literal-argument quoting. The actual process spawn (RunResolved) is
    // Windows-only and exercised by hand.
    public sealed class SkillScriptRunnerTests : IDisposable
    {
        private readonly string _root;
        private readonly string _project;   // project skills root (<root>/project)
        private readonly string _bundled;   // bundled skills root (<root>/bundled)
        private readonly string _workspace;

        public SkillScriptRunnerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillrun_" + Guid.NewGuid().ToString("N"));
            _project = Path.Combine(_root, "project");
            _bundled = Path.Combine(_root, "bundled");
            _workspace = Path.Combine(_root, "ws");
            Directory.CreateDirectory(_project);
            Directory.CreateDirectory(_bundled);
            Directory.CreateDirectory(_workspace);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private SkillScriptRunner Runner(bool withWorkspace)
        {
            ExtensionsConfig cfg = ExtensionsConfig.ForTesting(
                withWorkspace ? _workspace : null, _project, null, _bundled, "cmd.exe", null, null);
            return new SkillScriptRunner(cfg);
        }

        // Lay down <root>/<slug>/SKILL.md plus an optional file at relpath.
        private void MakeSkill(string root, string slug, string relpath, string content)
        {
            string dir = Path.Combine(root, slug);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: " + slug + "\ndescription: d\n---\nbody\n");
            if (relpath != null)
            {
                string full = Path.Combine(dir, relpath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, content);
            }
        }

        [Fact]
        public void Resolve_FindsBatInProjectRoot()
        {
            MakeSkill(_project, "gen", "scripts/gen.bat", "@echo off");
            SkillScriptTarget t = Runner(true).Resolve("gen", "scripts/gen.bat");

            Assert.Equal("gen", t.Slug);
            Assert.True(File.Exists(t.BatPath));
            Assert.EndsWith("gen.bat", t.BatPath);
        }

        [Fact]
        public void Resolve_ProjectShadowsBundled()
        {
            MakeSkill(_bundled, "gen", "scripts/gen.bat", "@echo off\nrem bundled");
            MakeSkill(_project, "gen", "scripts/gen.bat", "@echo off\nrem project");
            SkillScriptTarget t = Runner(true).Resolve("gen", "scripts/gen.bat");

            Assert.Contains("project", File.ReadAllText(t.BatPath));
        }

        [Fact]
        public void Resolve_FindsBundledWhenNotInProject()
        {
            MakeSkill(_bundled, "ship", "run.cmd", "@echo off");
            SkillScriptTarget t = Runner(true).Resolve("ship", "run.cmd");
            Assert.EndsWith("run.cmd", t.BatPath);
        }

        [Fact]
        public void Resolve_NoWorkspace_Throws()
        {
            MakeSkill(_project, "gen", "scripts/gen.bat", "@echo off");
            Assert.Throws<SkillScriptException>(() => Runner(false).Resolve("gen", "scripts/gen.bat"));
        }

        [Fact]
        public void Resolve_UnknownSkill_Throws()
        {
            Assert.Throws<SkillScriptException>(() => Runner(true).Resolve("nope", "scripts/gen.bat"));
        }

        [Fact]
        public void Resolve_NonBatExtension_Throws()
        {
            MakeSkill(_project, "gen", "scripts/gen.py", "print('hi')");
            Assert.Throws<SkillScriptException>(() => Runner(true).Resolve("gen", "scripts/gen.py"));
        }

        [Fact]
        public void Resolve_MissingFile_Throws()
        {
            MakeSkill(_project, "gen", null, null);
            Assert.Throws<SkillScriptException>(() => Runner(true).Resolve("gen", "scripts/nope.bat"));
        }

        [Fact]
        public void Resolve_Escape_Throws()
        {
            MakeSkill(_project, "gen", "scripts/gen.bat", "@echo off");
            Assert.Throws<SkillScriptException>(() => Runner(true).Resolve("gen", "../../evil.bat"));
        }

        [Fact]
        public void BuildCommandLine_QuotesEachToken()
        {
            string cmd = SkillScriptRunner.BuildCommandLine(
                "C:\\Program Files\\GxPT\\skills\\gen\\scripts\\gen.bat",
                new List<string> { "--since", "v1.2", "with space" });

            Assert.Equal(
                "\"C:\\Program Files\\GxPT\\skills\\gen\\scripts\\gen.bat\" \"--since\" \"v1.2\" \"with space\"",
                cmd);
        }

        [Fact]
        public void BuildCommandLine_NoArgs_JustQuotedPath()
        {
            Assert.Equal("\"a.bat\"", SkillScriptRunner.BuildCommandLine("a.bat", null));
        }

        [Fact]
        public void BuildCommandLine_RejectsQuoteInArg()
        {
            Assert.Throws<SkillScriptException>(() =>
                SkillScriptRunner.BuildCommandLine("a.bat", new List<string> { "ab\"cd" }));
        }

        [Fact]
        public void BuildCommandLine_RejectsPercentInArg()
        {
            Assert.Throws<SkillScriptException>(() =>
                SkillScriptRunner.BuildCommandLine("a.bat", new List<string> { "%PATH%" }));
        }

        [Fact]
        public void BuildCommandLine_RejectsControlCharInArg()
        {
            Assert.Throws<SkillScriptException>(() =>
                SkillScriptRunner.BuildCommandLine("a.bat", new List<string> { "a\nb" }));
        }
    }
}
