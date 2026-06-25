using System;
using System.IO;
using ExtensionsMcpServer;
using Xunit;

namespace ExtensionsMcpServer.Tests
{
    public sealed class SkillWriterTests : IDisposable
    {
        private readonly string _root;        // stand-in workspace root
        private readonly string _project;     // the project skills root the writer targets
        private readonly SkillWriter _writer;

        public SkillWriterTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillwriter_" + Guid.NewGuid().ToString("N"));
            _project = Path.Combine(_root, "skills");
            Directory.CreateDirectory(_project);
            _writer = new SkillWriter(_project, null);   // user-global root not wired
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private string SkillFile(string slug) { return Path.Combine(Path.Combine(_project, slug), "SKILL.md"); }

        [Fact]
        public void CreateSkill_WritesValidSkillMd()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "Say ahoy.");

            string text = File.ReadAllText(SkillFile("greeting"));
            Assert.Contains("name: Greeting", text);
            Assert.Contains("description: Be a pirate.", text);
            Assert.Contains("Say ahoy.", text);
            // Round-trips through the parser as a loadable skill.
            SkillFrontmatter fm = SkillFrontmatter.Parse(text);
            Assert.Equal("Greeting", fm.Name);
            Assert.Equal("Be a pirate.", fm.Description);
            Assert.Equal("Say ahoy.", fm.Body);
        }

        [Fact]
        public void CreateSkill_NormalizesSlug()
        {
            _writer.CreateSkill(null, "Release Notes", "Release Notes", "Draft notes.", "do it");
            Assert.True(File.Exists(SkillFile("release-notes")));
        }

        [Fact]
        public void CreateSkill_RejectsNameThatDoesNotMatchSlug()
        {
            SkillWriteException ex = Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill(null, "release-notes", "Changelog", "Draft notes.", "body"));
            Assert.Contains("don't match", ex.Message);
            Assert.False(File.Exists(SkillFile("release-notes")));
        }

        [Fact]
        public void CreateSkill_AllowsAcronymNameAlignment()
        {
            _writer.CreateSkill(null, "github-sync", "GitHub Sync", "Syncs a repo.", "body");
            Assert.True(File.Exists(SkillFile("github-sync")));
        }

        [Fact]
        public void UpdateSkill_RejectsNameThatDivergesFromSlug()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Old desc.", "Old body.");
            SkillWriteException ex = Assert.Throws<SkillWriteException>(() =>
                _writer.UpdateSkill(null, "greeting", "Farewell", null, null));
            Assert.Contains("rename", ex.Message);
            SkillFrontmatter fm = SkillFrontmatter.Parse(File.ReadAllText(SkillFile("greeting")));
            Assert.Equal("Greeting", fm.Name);   // unchanged
        }

        [Fact]
        public void RenameSkill_MovesFolderWithAssetsAndDerivesName()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Says hi.", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "reference content");

            _writer.RenameSkill(null, "greeting", "salutation", null);   // no new_name -> derived

            Assert.False(Directory.Exists(Path.Combine(_project, "greeting")));   // old folder gone
            string text = File.ReadAllText(SkillFile("salutation"));
            Assert.Contains("name: Salutation", text);                            // derived from new slug
            Assert.Contains("Says hi.", text);                                    // description preserved
            Assert.True(File.Exists(Path.Combine(_project, "salutation", "ref.md")));   // asset came along
        }

        [Fact]
        public void RenameSkill_RefusesExistingTarget()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "d", "b");
            _writer.CreateSkill(null, "farewell", "Farewell", "d", "b");
            Assert.Throws<SkillWriteException>(() => _writer.RenameSkill(null, "greeting", "farewell", null));
            Assert.True(File.Exists(SkillFile("greeting")));   // both untouched on refusal
            Assert.True(File.Exists(SkillFile("farewell")));
        }

        [Fact]
        public void RenameSkill_RejectsNameNotMatchingNewSlug()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Says hi.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.RenameSkill(null, "greeting", "salutation", "Something Else"));
            Assert.True(File.Exists(SkillFile("greeting")));                       // not moved
            Assert.False(Directory.Exists(Path.Combine(_project, "salutation")));
        }

        [Fact]
        public void CreateSkill_RefusesExisting()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body"));
        }

        [Fact]
        public void CreateSkill_RequiresNameAndDescription()
        {
            Assert.Throws<SkillWriteException>(() => _writer.CreateSkill(null, "a", "", "desc", "body"));
            Assert.Throws<SkillWriteException>(() => _writer.CreateSkill(null, "a", "Name", "  ", "body"));
        }

        [Fact]
        public void CreateSkill_RejectsNewlineInFrontmatter()
        {
            // A newline in name/description could close the --- block early or inject keys.
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill(null, "a", "Name", "ok\n---\nmalicious body", "body"));
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill(null, "a", "Na\nme", "desc", "body"));
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill(null, "a", "Name", "carriage\rreturn", "body"));
        }

        [Fact]
        public void UpdateSkill_RejectsNewlineInFrontmatter()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.UpdateSkill(null, "greeting", null, "bad\ndesc", null));
        }

        [Fact]
        public void WriteFile_RejectsNonAsciiBatch()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            // U+00E9 (e-acute) would be garbled by XP cmd.exe's OEM codepage.
            Assert.Throws<SkillWriteException>(() =>
                _writer.WriteFile(null, "greeting", "scripts/gen.bat", "@echo off\necho caf\u00e9"));
        }

        [Fact]
        public void WriteFile_AllowsNonAsciiInMarkdown()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "caf\u00e9 \u2014 fine in markdown");
            string path = Path.Combine(Path.Combine(_project, "greeting"), "ref.md");
            Assert.Contains("caf\u00e9", File.ReadAllText(path, System.Text.Encoding.UTF8));
        }

        [Fact]
        public void WriteFile_WritesSupportingScript_AsCrlf()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            _writer.WriteFile(null, "greeting", "scripts/gen.bat", "@echo off\necho hi");

            string path = Path.Combine(Path.Combine(_project, "greeting"), Path.Combine("scripts", "gen.bat"));
            string text = File.ReadAllText(path);
            Assert.Contains("@echo off", text);
            Assert.Contains("\r\n", text);   // .bat normalized to CRLF
        }

        [Fact]
        public void WriteFile_RejectsSkillMd()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.WriteFile(null, "greeting", "SKILL.md", "whatever"));
        }

        [Fact]
        public void WriteFile_RejectsEscape()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.WriteFile(null, "greeting", "../escape.txt", "x"));
        }

        [Fact]
        public void WriteFile_RequiresExistingSkill()
        {
            Assert.Throws<SkillWriteException>(() =>
                _writer.WriteFile(null, "nope", "ref.md", "x"));
        }

        [Fact]
        public void UpdateSkill_PartialFields_KeepsOthers()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Old desc.", "Old body.");
            _writer.UpdateSkill(null, "greeting", null, "New desc.", null);   // description only

            SkillFrontmatter fm = SkillFrontmatter.Parse(File.ReadAllText(SkillFile("greeting")));
            Assert.Equal("Greeting", fm.Name);        // unchanged
            Assert.Equal("New desc.", fm.Description); // changed
            Assert.Equal("Old body.", fm.Body);        // unchanged
        }

        [Fact]
        public void UpdateSkill_NonexistentSkill_Throws()
        {
            Assert.Throws<SkillWriteException>(() => _writer.UpdateSkill(null, "nope", "N", "D", "B"));
        }

        [Fact]
        public void Scope_User_NotEnabled_Throws()
        {
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill("user", "a", "Name", "Desc", "body"));
        }

        [Fact]
        public void Scope_Project_NoWorkspace_Throws()
        {
            SkillWriter noWs = new SkillWriter(null, null);
            Assert.Throws<SkillWriteException>(() =>
                noWs.CreateSkill(null, "a", "Name", "Desc", "body"));
        }

        [Fact]
        public void Scope_Unknown_Throws()
        {
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill("elsewhere", "a", "Name", "Desc", "body"));
        }

        [Fact]
        public void UpdateSkill_NameLessSkillMd_StaysNameLess()
        {
            // Hand-write a SKILL.md with no name line, then update an unrelated field.
            string dir = Path.Combine(_project, "nameless");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\ndescription: Old.\n---\n\nbody\n");

            _writer.UpdateSkill(null, "nameless", null, "New.", null); // description only

            string text = File.ReadAllText(SkillFile("nameless"));
            Assert.DoesNotContain("name:", text);          // no name forced onto it
            Assert.Contains("description: New.", text);
        }

        // ---- default scope (the workdir-less instance defaults to user, not project) ----

        [Fact]
        public void DefaultScope_User_OmittedScope_WritesToUserRoot()
        {
            string user = Path.Combine(_root, "user");
            Directory.CreateDirectory(user);
            // No project root (folderless), user is the default scope.
            SkillWriter w = new SkillWriter(null, user, "user");

            w.CreateSkill(null, "vendor-review", "Vendor Review", "desc", "body"); // scope omitted

            Assert.True(File.Exists(Path.Combine(Path.Combine(user, "vendor-review"), "SKILL.md")));
        }

        [Fact]
        public void DefaultScope_User_ExplicitProject_StillErrorsWithoutWorkspace()
        {
            string user = Path.Combine(_root, "user");
            Directory.CreateDirectory(user);
            SkillWriter w = new SkillWriter(null, user, "user");
            Assert.Throws<SkillWriteException>(() =>
                w.CreateSkill("project", "x", "X", "desc", "body")); // project unreachable, no workspace
        }

        [Fact]
        public void DefaultScope_TwoArgCtor_DefaultsToProject()
        {
            // The 2-arg ctor keeps the project default: omitting scope targets the project root.
            _writer.CreateSkill(null, "proj-skill", "Proj Skill", "desc", "body");
            Assert.True(File.Exists(SkillFile("proj-skill")));
        }

        // ---- tier 2: maintenance ----

        [Fact]
        public void EditFile_ReplacesUniqueSpan()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "alpha beta gamma");
            _writer.EditFile(null, "greeting", "ref.md", "beta", "DELTA", false);

            string path = Path.Combine(Path.Combine(_project, "greeting"), "ref.md");
            Assert.Equal("alpha DELTA gamma", File.ReadAllText(path));
        }

        [Fact]
        public void EditFile_NonUnique_WithoutReplaceAll_Throws()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "x x x");
            Assert.Throws<SkillWriteException>(() =>
                _writer.EditFile(null, "greeting", "ref.md", "x", "y", false));
        }

        [Fact]
        public void EditFile_ReplaceAll_ReplacesEvery()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "x x x");
            _writer.EditFile(null, "greeting", "ref.md", "x", "y", true);

            string path = Path.Combine(Path.Combine(_project, "greeting"), "ref.md");
            Assert.Equal("y y y", File.ReadAllText(path));
        }

        [Fact]
        public void EditFile_OldStringNotFound_Throws()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "hello");
            Assert.Throws<SkillWriteException>(() =>
                _writer.EditFile(null, "greeting", "ref.md", "nope", "y", false));
        }

        [Fact]
        public void EditFile_SkillMd_EditsBody_PreservesFrontmatter()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "Say ahoy to everyone.");
            _writer.EditFile(null, "greeting", "SKILL.md", "ahoy", "AHOY", false);

            SkillFrontmatter fm = SkillFrontmatter.Parse(File.ReadAllText(SkillFile("greeting")));
            Assert.Equal("Greeting", fm.Name);            // frontmatter untouched
            Assert.Equal("Be a pirate.", fm.Description);  // frontmatter untouched
            Assert.Equal("Say AHOY to everyone.", fm.Body); // body edited
        }

        [Fact]
        public void EditFile_SkillMd_OldStringInFrontmatter_NotFound()
        {
            // The name/description live in frontmatter, which edit_skill_file does not touch.
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body text");
            Assert.Throws<SkillWriteException>(() =>
                _writer.EditFile(null, "greeting", "SKILL.md", "Be a pirate.", "Be a ninja.", false));
        }

        [Fact]
        public void EditFile_BatStaysCrlf()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "scripts/gen.bat", "@echo off\necho one\necho two");
            _writer.EditFile(null, "greeting", "scripts/gen.bat", "echo one", "echo ONE", false);

            string path = Path.Combine(Path.Combine(_project, "greeting"), Path.Combine("scripts", "gen.bat"));
            string text = File.ReadAllText(path);
            Assert.Contains("echo ONE", text);
            Assert.Contains("\r\n", text);              // still CRLF after the edit
            Assert.DoesNotContain("echo one", text);    // the old line is gone
        }

        [Fact]
        public void ListFiles_EnumeratesRelativePaths()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "x");
            _writer.WriteFile(null, "greeting", "scripts/gen.bat", "@echo off");

            string listing = _writer.ListFiles(null, "greeting");
            Assert.Contains("SKILL.md", listing);
            Assert.Contains("ref.md", listing);
            Assert.Contains("scripts/gen.bat", listing); // forward slashes, relative
        }

        [Fact]
        public void DeleteFile_RemovesSupportingFile()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "x");
            string path = Path.Combine(Path.Combine(_project, "greeting"), "ref.md");
            Assert.True(File.Exists(path));

            _writer.DeleteFile(null, "greeting", "ref.md");
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void DeleteFile_RejectsSkillMd()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.DeleteFile(null, "greeting", "SKILL.md"));
        }

        [Fact]
        public void DeleteFile_RejectsEscape()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.DeleteFile(null, "greeting", "../escape.txt"));
        }

        [Fact]
        public void DeleteSkill_RemovesWholeFolder()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "desc", "body");
            _writer.WriteFile(null, "greeting", "ref.md", "x");
            string dir = Path.Combine(_project, "greeting");
            Assert.True(Directory.Exists(dir));

            _writer.DeleteSkill(null, "greeting");
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void DeleteSkill_Nonexistent_Throws()
        {
            Assert.Throws<SkillWriteException>(() => _writer.DeleteSkill(null, "nope"));
        }

        [Fact]
        public void ValidateSkill_Loadable_ReportsOk()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            string result = _writer.ValidateSkill(null, "greeting");
            Assert.StartsWith("OK", result);
            Assert.Contains("Greeting", result);
            Assert.Contains("Be a pirate.", result);
        }

        [Fact]
        public void ValidateSkill_MissingDescription_ReportsInvalid()
        {
            // Hand-write a SKILL.md with no description (bypassing create_skill's validation).
            string dir = Path.Combine(_project, "broken");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: Broken\n---\n\nbody\n");

            string result = _writer.ValidateSkill(null, "broken");
            Assert.StartsWith("INVALID", result);
        }

        [Fact]
        public void ValidateSkill_Nonexistent_Throws()
        {
            Assert.Throws<SkillWriteException>(() => _writer.ValidateSkill(null, "nope"));
        }

        [Fact]
        public void UpdateSkill_BundledOnly_ReportsBundledNotMissing()
        {
            // A bundled (shipped, read-only) skill the writer can't edit in place: the error should name it
            // as bundled and point at create_skill, not the bare "does not exist".
            string bundled = Path.Combine(_root, "bundled");
            Directory.CreateDirectory(Path.Combine(bundled, "shipped"));
            File.WriteAllText(Path.Combine(Path.Combine(bundled, "shipped"), "SKILL.md"),
                "---\nname: Shipped\ndescription: d\n---\n\nbody\n");
            SkillWriter w = new SkillWriter(_project, null, bundled, "project");

            SkillWriteException ex = Assert.Throws<SkillWriteException>(
                () => w.UpdateSkill(null, "shipped", null, "new desc", null));
            Assert.Contains("bundled", ex.Message);
            Assert.Contains("create_skill", ex.Message);
        }

        [Fact]
        public void DeleteSkill_BundledOnly_ReportsBundledReadOnly()
        {
            string bundled = Path.Combine(_root, "bundled");
            Directory.CreateDirectory(Path.Combine(bundled, "shipped"));
            File.WriteAllText(Path.Combine(Path.Combine(bundled, "shipped"), "SKILL.md"),
                "---\nname: Shipped\ndescription: d\n---\n\nbody\n");
            SkillWriter w = new SkillWriter(_project, null, bundled, "project");

            SkillWriteException ex = Assert.Throws<SkillWriteException>(() => w.DeleteSkill(null, "shipped"));
            Assert.Contains("bundled", ex.Message);
            Assert.Contains("read-only", ex.Message);
        }

        [Fact]
        public void RenameSkill_BundledOnly_ReportsCannotRename()
        {
            string bundled = Path.Combine(_root, "bundled");
            Directory.CreateDirectory(Path.Combine(bundled, "shipped"));
            File.WriteAllText(Path.Combine(Path.Combine(bundled, "shipped"), "SKILL.md"),
                "---\nname: Shipped\ndescription: d\n---\n\nbody\n");
            SkillWriter w = new SkillWriter(_project, null, bundled, "project");

            SkillWriteException ex = Assert.Throws<SkillWriteException>(
                () => w.RenameSkill(null, "shipped", "renamed", null));
            Assert.Contains("bundled", ex.Message);
            Assert.Contains("can't be renamed", ex.Message);   // rename-specific guidance, not the edit message
            Assert.Contains("create_skill", ex.Message);
        }

        [Fact]
        public void UpdateSkill_BlankNameKeepsExisting()
        {
            // A present-but-blank name means "keep", not "clear" (mirrors update_agent); passing "" must not
            // silently drop the existing name.
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            _writer.UpdateSkill(null, "greeting", "", "New desc.", null); // name="" => keep
            string text = File.ReadAllText(SkillFile("greeting"));
            Assert.Contains("name: Greeting", text);
            Assert.Contains("description: New desc.", text);
        }
    }
}
