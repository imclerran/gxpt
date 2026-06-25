using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests
{
    public sealed class ExtensionsToolGateTests
    {
        private static Skill MakeSkill(string slug)
        {
            return new Skill(slug, slug, "desc", "/dir/" + slug, "/dir/" + slug + "/SKILL.md", SkillSource.Project);
        }

        [Fact]
        public void HiddenTools_SkillEnabledButNoWriters_HidesAuthoring_ShowsRunScript()
        {
            // A non-writer skill is enabled: the 9 skill-authoring tools AND the 8 agent-authoring tools are
            // hidden (neither meta-skill is on), but run_skill_script is available (any skill may ship one).
            ICollection<string> hidden = ExtensionsToolGate.HiddenTools(new List<Skill> { MakeSkill("greeting") });
            // skill authoring: tier 1
            Assert.Contains("extensions__create_skill", hidden);
            Assert.Contains("extensions__write_skill_file", hidden);
            Assert.Contains("extensions__update_skill", hidden);
            // skill authoring: tier 2 (maintenance)
            Assert.Contains("extensions__edit_skill_file", hidden);
            Assert.Contains("extensions__rename_skill", hidden);
            Assert.Contains("extensions__list_skill_files", hidden);
            Assert.Contains("extensions__delete_skill_file", hidden);
            Assert.Contains("extensions__delete_skill", hidden);
            Assert.Contains("extensions__validate_skill", hidden);
            // agent authoring (owned by agent-writer, which is off)
            Assert.Contains("extensions__create_agent", hidden);
            Assert.Contains("extensions__update_agent", hidden);
            Assert.Contains("extensions__edit_agent", hidden);
            Assert.Contains("extensions__rename_agent", hidden);
            Assert.Contains("extensions__read_agent", hidden);
            Assert.Contains("extensions__list_agents", hidden);
            Assert.Contains("extensions__delete_agent", hidden);
            Assert.Contains("extensions__validate_agent", hidden);
            // execution stays visible
            Assert.DoesNotContain("extensions__run_skill_script", hidden);
        }

        [Fact]
        public void HiddenTools_BothMetaSkillsEnabled_HidesNothing()
        {
            var enabled = new List<Skill>
            {
                MakeSkill("greeting"),
                MakeSkill(ExtensionsToolGate.SkillWriterSlug),
                MakeSkill(ExtensionsToolGate.AgentWriterSlug)
            };
            Assert.Empty(ExtensionsToolGate.HiddenTools(enabled));
        }

        [Fact]
        public void HiddenTools_SkillWriterOnly_HidesAgentAuthoringNotSkillAuthoring()
        {
            var enabled = new List<Skill> { MakeSkill(ExtensionsToolGate.SkillWriterSlug) };
            ICollection<string> hidden = ExtensionsToolGate.HiddenTools(enabled);
            Assert.DoesNotContain("extensions__create_skill", hidden); // skill-writer is on
            Assert.Contains("extensions__create_agent", hidden);       // agent-writer is off
        }

        [Fact]
        public void HiddenTools_AgentWriterOnly_HidesSkillAuthoringNotAgentAuthoring()
        {
            var enabled = new List<Skill> { MakeSkill(ExtensionsToolGate.AgentWriterSlug) };
            ICollection<string> hidden = ExtensionsToolGate.HiddenTools(enabled);
            Assert.Contains("extensions__create_skill", hidden);          // skill-writer is off
            Assert.DoesNotContain("extensions__create_agent", hidden);    // agent-writer is on
        }

        [Fact]
        public void HiddenTools_NoSkillEnabled_HidesEveryExtensionsTool()
        {
            // No skill enabled at all: skill + agent authoring AND run_skill_script are hidden.
            foreach (ICollection<string> hidden in new[] { ExtensionsToolGate.HiddenTools(null), ExtensionsToolGate.HiddenTools(new List<Skill>()) })
            {
                Assert.Contains("extensions__create_skill", hidden);
                Assert.Contains("extensions__validate_skill", hidden);
                Assert.Contains("extensions__create_agent", hidden);
                Assert.Contains("extensions__validate_agent", hidden);
                Assert.Contains("extensions__run_skill_script", hidden);
            }
        }
    }

    // The orchestrator-side filtering that applies the hidden set to the exposed defs + names manifest.
    public sealed class OrchestratorHiddenToolsTests
    {
        private static JObject Def(string name)
        {
            JObject fn = new JObject(); fn["name"] = name;
            JObject d = new JObject(); d["type"] = "function"; d["function"] = fn;
            return d;
        }

        [Fact]
        public void FilterHiddenDefs_RemovesHiddenByFunctionName()
        {
            var defs = new List<JObject> { Def("web__search"), Def("extensions__create_skill") };
            var hidden = new HashSet<string> { "extensions__create_skill" };

            IList<JObject> result = McpChatOrchestrator.FilterHiddenDefs(defs, hidden);

            Assert.Single(result);
            Assert.Equal("web__search", (string)result[0]["function"]["name"]);
        }

        [Fact]
        public void FilterHiddenDefs_NoHidden_ReturnsUnchanged()
        {
            var defs = new List<JObject> { Def("web__search") };
            Assert.Same(defs, McpChatOrchestrator.FilterHiddenDefs(defs, new HashSet<string>()));
        }

        [Fact]
        public void FilterHiddenManifest_DropsHiddenLinesKeepsOthers()
        {
            string manifest = "Available tools:\n- web__search\n- extensions__create_skill\n- files__read";
            var hidden = new HashSet<string> { "extensions__create_skill" };

            string result = McpChatOrchestrator.FilterHiddenManifest(manifest, hidden);

            Assert.DoesNotContain("extensions__create_skill", result);
            Assert.Contains("- web__search", result);
            Assert.Contains("- files__read", result);
        }
    }
}
