using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using GxPT.Tests.Commands;
using Xunit;

namespace GxPT.Tests
{
    // Shares a collection with SkillEnablementTests so the static SkillEnablement.FilePathOverride they
    // both set can't race under xUnit's parallel-by-collection execution.
    [Collection("SkillsGlobalState")]
    public sealed class SkillCommandsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _work;
        private readonly FakeSlashCommandContext _ctx;

        public SkillCommandsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillcmd_" + Guid.NewGuid().ToString("N"));
            _work = Path.Combine(_root, "work");
            Directory.CreateDirectory(_work);
            SkillEnablement.FilePathOverride = Path.Combine(_root, "skills.json");
            _ctx = new FakeSlashCommandContext(_work);
        }

        public void Dispose()
        {
            SkillEnablement.FilePathOverride = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        // Creates a project skill under <work>/.gxpt/skills/<slug>/SKILL.md.
        private void WriteSkill(string slug, string description, string body)
        {
            string dir = Path.Combine(Path.Combine(Path.Combine(_work, ".gxpt"), "skills"), slug);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\ndescription: " + description + "\n---\n\n" + body + "\n", new UTF8Encoding(false));
        }

        private string LastInfo()
        {
            return _ctx.Infos.Count > 0 ? _ctx.Infos[_ctx.Infos.Count - 1] : null;
        }

        [Fact]
        public void Skills_NoArgs_ListsSkills()
        {
            WriteSkill("alpha", "First.", "b");
            WriteSkill("beta", "Second.", "b");

            SlashCommandResult r = new ListSkillsCommand().Invoke("", _ctx);

            Assert.False(r.SendToModel);
            Assert.Null(r.Error);
            Assert.Contains("alpha", LastInfo());
            Assert.Contains("beta", LastInfo());
        }

        [Fact]
        public void Skill_OffHere_SetsConversationOverride()
        {
            WriteSkill("greeting", "Be a pirate.", "b");

            SlashCommandResult r = new ToggleSkillCommand().Invoke("greeting off", _ctx);

            Assert.False(r.SendToModel);
            Assert.False(_ctx.SkillStore.ConvOverrides["greeting"]);   // forced off for this conversation
        }

        [Fact]
        public void Skill_BareSlug_TogglesConversation()
        {
            WriteSkill("greeting", "Be a pirate.", "b");

            new ToggleSkillCommand().Invoke("greeting", _ctx);     // on by default -> toggles off
            Assert.False(_ctx.SkillStore.ConvOverrides["greeting"]);

            new ToggleSkillCommand().Invoke("greeting", _ctx);     // now off -> toggles on
            Assert.True(_ctx.SkillStore.ConvOverrides["greeting"]);
        }

        [Fact]
        public void Skill_OffGlobal_PersistsToSkillsJson()
        {
            WriteSkill("greeting", "Be a pirate.", "b");

            new ToggleSkillCommand().Invoke("greeting off global", _ctx);

            Assert.Equal((bool?)false, SkillEnablement.LoadGlobal().GetSkillOverride("greeting"));
            Assert.Empty(_ctx.SkillStore.ConvOverrides);   // global scope must not touch the conversation
        }

        // The transcript scenario: /skills off then /skill greeting on -> greeting still listed ON.
        [Fact]
        public void SkillOnHere_OverridesSkillsOffHere_InList()
        {
            WriteSkill("greeting", "Be a pirate.", "b");

            new ToggleSkillsCommand().Invoke("off", _ctx);          // all skills off here
            new ToggleSkillCommand().Invoke("greeting on", _ctx);   // but this one on here
            new ListSkillsCommand().Invoke("", _ctx);             // list

            Assert.Contains("greeting", LastInfo());
            Assert.Contains("(on here)", LastInfo());         // effectively on despite /skills off
        }

        [Fact]
        public void Skill_OnGlobal_ForcesOnOverGlobalFeatureOff()
        {
            WriteSkill("pirate", "Always pirate.", "b");

            new ToggleSkillsCommand().Invoke("off global", _ctx);   // feature off globally
            new ToggleSkillCommand().Invoke("pirate on global", _ctx);

            Assert.Equal((bool?)true, SkillEnablement.LoadGlobal().GetSkillOverride("pirate"));
        }

        [Fact]
        public void Skill_UnknownSlug_Fails()
        {
            SlashCommandResult r = new ToggleSkillCommand().Invoke("nope off", _ctx);
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Skills_OffGlobal_ThenReset()
        {
            new ToggleSkillsCommand().Invoke("off global", _ctx);
            Assert.True(SkillEnablement.LoadGlobal().FeatureOff);

            new ToggleSkillsCommand().Invoke("reset global", _ctx);
            Assert.False(SkillEnablement.LoadGlobal().FeatureOff);
        }

        [Fact]
        public void Skills_TrailingTokens_Fail()
        {
            SlashCommandResult r = new ToggleSkillsCommand().Invoke("on here garbage", _ctx);
            Assert.NotNull(r.Error);
            Assert.False(SkillEnablement.LoadGlobal().FeatureOff); // nothing applied
        }

        [Fact]
        public void Skill_TrailingTokens_Fail()
        {
            WriteSkill("greeting", "Be a pirate.", "b");
            SlashCommandResult r = new ToggleSkillCommand().Invoke("greeting on global junk", _ctx);
            Assert.NotNull(r.Error);
            Assert.Null(SkillEnablement.LoadGlobal().GetSkillOverride("greeting")); // nothing applied
        }

        [Fact]
        public void Skills_AnyEnablementChange_RefreshesSkillsServer()
        {
            // The Skills MCP server follows skill enablement, so every enablement change - global OR
            // per-conversation - asks the host to bring it into line (a no-op unless it crosses on/off).
            new ToggleSkillsCommand().Invoke("off global", _ctx);
            new ToggleSkillsCommand().Invoke("on global", _ctx);
            new ToggleSkillsCommand().Invoke("reset global", _ctx);
            new ToggleSkillsCommand().Invoke("off here", _ctx);
            new ToggleSkillsCommand().Invoke("reset", _ctx);
            Assert.Equal(5, _ctx.SkillStore.RefreshSkillsServerCount);
        }

        [Fact]
        public void Skill_EnablementChange_RefreshesSkillsServer()
        {
            WriteSkill("greeting", "Be a pirate.", "b");
            new ToggleSkillCommand().Invoke("greeting on here", _ctx);
            new ToggleSkillCommand().Invoke("greeting off global", _ctx);
            new ToggleSkillCommand().Invoke("greeting reset", _ctx);
            Assert.Equal(3, _ctx.SkillStore.RefreshSkillsServerCount);
        }

        [Fact]
        public void Skills_OffHere_AndReset()
        {
            new ToggleSkillsCommand().Invoke("off", _ctx);
            Assert.Equal(true, _ctx.SkillStore.ConvFeatureOff);

            new ToggleSkillsCommand().Invoke("reset", _ctx);
            Assert.Null(_ctx.SkillStore.ConvFeatureOff);
        }

        [Fact]
        public void Skills_UnknownScope_Fails()
        {
            SlashCommandResult r = new ToggleSkillsCommand().Invoke("off nowhere", _ctx);
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Use_KnownSkill_SendsShortMessage_AttachesBodyAsSystemContext()
        {
            WriteSkill("greeting", "Be a pirate.", "ARRR MATEY");

            SlashCommandResult r = new UseSkillCommand().Invoke("greeting say hi", _ctx);

            // The user message is the short ask, with the trailing text - NOT the body.
            Assert.True(r.SendToModel);
            Assert.Equal("Use the greeting skill. say hi", r.TextToSend);
            Assert.DoesNotContain("ARRR MATEY", r.TextToSend);

            // The body rides as a hidden system message (carried on the result, committed at send).
            Assert.NotNull(r.SystemContext);
            Assert.Contains("# Skill: greeting", r.SystemContext);
            Assert.Contains("ARRR MATEY", r.SystemContext);
        }

        [Fact]
        public void Use_NoTrailingText_SendsBareAsk()
        {
            WriteSkill("greeting", "Be a pirate.", "ARRR");

            SlashCommandResult r = new UseSkillCommand().Invoke("greeting", _ctx);
            Assert.Equal("Use the greeting skill.", r.TextToSend);
        }

        [Fact]
        public void Use_DisabledSkill_StillWorks()
        {
            WriteSkill("greeting", "Be a pirate.", "ARRR");
            new ToggleSkillCommand().Invoke("greeting off global", _ctx);   // disable globally

            SlashCommandResult r = new UseSkillCommand().Invoke("greeting", _ctx);

            Assert.True(r.SendToModel);   // explicit /use ignores enablement
            Assert.Contains("ARRR", r.SystemContext); // body still attached
        }

        [Fact]
        public void Use_UnknownSkill_Fails()
        {
            SlashCommandResult r = new UseSkillCommand().Invoke("nope", _ctx);
            Assert.NotNull(r.Error);
            Assert.False(r.SendToModel);
        }

        // Sets up the four skills used by the list-rendering tests, with release-notes off globally.
        private void SeedFourSkills()
        {
            WriteSkill("build-helper", "Build stuff.", "b");
            WriteSkill("formatter", "Format stuff.", "b");
            WriteSkill("greeting", "Be a pirate.", "b");
            WriteSkill("release-notes", "Draft notes.", "b");

            SkillEnablement g = SkillEnablement.LoadGlobal();
            g.SetSkillOverride("release-notes", false);   // off globally
            g.SaveGlobal();
            _ctx.SkillStore.ConvOverrides["greeting"] = true;        // on here
        }

        // Feature OFF here: unset skills fall through to rung 3 -> "all skills off here" (NOT "default").
        [Fact]
        public void List_FeatureOffHere_UnsetSkillsAreAllSkillsOffHere_NotDefault()
        {
            SeedFourSkills();
            _ctx.SkillStore.ConvFeatureOff = true;

            new ListSkillsCommand().Invoke("", _ctx);
            string info = LastInfo();

            Assert.Contains("Default: ON globally \u00b7 OFF here", info);
            Assert.Contains("(on here)", info);             // greeting
            Assert.Contains("(off globally)", info);        // release-notes
            Assert.Contains("(all skills off here)", info); // build-helper / formatter
            Assert.DoesNotContain("(default)", info);       // nothing is "default" when feature is off here
        }

        // Each skill renders as a consistent "- <slug>" bullet (slug-sorted).
        [Fact]
        public void List_SkillLines_AreBulleted()
        {
            SeedFourSkills();

            new ListSkillsCommand().Invoke("", _ctx);
            string info = LastInfo();

            Assert.Contains("\n- build-helper", info);
            Assert.Contains("\n- formatter", info);
            Assert.Contains("\n- greeting", info);
            Assert.Contains("\n- release-notes", info);
        }

        // Feature UNSET here: unset skills fall through to rung 5 -> "default"; header has no "here" half.
        [Fact]
        public void List_FeatureUnsetHere_UnsetSkillsAreDefault()
        {
            SeedFourSkills();   // _ctx.SkillStore.ConvFeatureOff stays null

            new ListSkillsCommand().Invoke("", _ctx);
            string info = LastInfo();

            Assert.Contains("Default: ON globally", info);
            Assert.DoesNotContain("\u00b7", info);           // no "  OFF/ON here" header half when unset
            Assert.Contains("(default)", info);              // build-helper / formatter
            Assert.DoesNotContain("(all skills off here)", info);
        }

        // ---- autocomplete ----

        private static ArgCompletion Find(IList<ArgCompletion> list, string display)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Display == display) return list[i];
            return null;
        }

        [Fact]
        public void Complete_Skills_Empty_OffersVerbsThatAdvance()
        {
            IList<ArgCompletion> c = new ToggleSkillsCommand().CompleteArgument("", _ctx);

            // No bare-command entry: a verb is required (listing is /list-skills).
            Assert.Null(Find(c, "(toggle skills for this conversation)"));

            ArgCompletion on = Find(c, "on");
            Assert.NotNull(on);
            Assert.Equal("on ", on.InsertArg);   // trailing space so the scope level populates next
            Assert.True(on.ContinueCompleting);
        }

        [Fact]
        public void Complete_Skills_AfterVerb_OffersScope()
        {
            IList<ArgCompletion> c = new ToggleSkillsCommand().CompleteArgument("on ", _ctx);

            Assert.NotNull(Find(c, "here"));
            Assert.Equal("on here", Find(c, "here").InsertArg);
            Assert.NotNull(Find(c, "global"));
        }

        [Fact]
        public void Complete_Skill_AfterVerb_OffersScope()
        {
            IList<ArgCompletion> c = new ToggleSkillCommand().CompleteArgument("greeting on ", _ctx);

            Assert.NotNull(Find(c, "here"));
            Assert.Equal("greeting on here", Find(c, "here").InsertArg);
        }
    }
}
