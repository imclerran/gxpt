using System.Collections.Generic;
using System.Linq;
using GxPT;
using Xunit;

namespace GxPT.Tests.Commands
{
    public class SlashCommandConfigTests
    {
        [Fact]
        public void Defaults_parse_into_the_seed_command_set()
        {
            var cmds = SlashCommandConfig.ParseCommands(SlashCommandConfig.DefaultsJson, null);
            var names = cmds.Select(c => c.Name).ToList();

            Assert.Contains("commit", names);
            Assert.Contains("diff", names);
            Assert.Contains("review", names);
            Assert.Contains("explain", names);
            Assert.Contains("test", names);
            Assert.Contains("fix", names);
        }

        [Fact]
        public void Explain_takes_a_path_argument_and_requires_files()
        {
            var explain = SlashCommandConfig.ParseCommands(SlashCommandConfig.DefaultsJson, null)
                .Single(c => c.Name == "explain");

            Assert.True(explain.TakesPathArgument);
            Assert.Contains("files", explain.Requires);
            Assert.Equal("<path>", explain.ArgumentHint);
        }

        [Fact]
        public void Test_and_fix_use_requires_any_for_msbuild_or_command()
        {
            var cmds = SlashCommandConfig.ParseCommands(SlashCommandConfig.DefaultsJson, null);
            foreach (var name in new[] { "test", "fix" })
            {
                var c = cmds.Single(x => x.Name == name);
                Assert.Empty(c.Requires);
                Assert.Contains("msbuild", c.RequiresAny);
                Assert.Contains("command", c.RequiresAny);
            }
        }

        [Fact]
        public void Invalid_json_yields_no_commands_and_does_not_throw()
        {
            var cmds = SlashCommandConfig.ParseCommands("{ not json", null);
            Assert.Empty(cmds);
        }

        [Fact]
        public void Prompt_command_without_template_is_skipped()
        {
            string json = "{\"commands\":[{\"type\":\"prompt\",\"name\":\"x\"}]}";
            Assert.Empty(SlashCommandConfig.ParseCommands(json, null));
        }

        [Fact]
        public void Non_prompt_types_are_skipped_in_v1()
        {
            string json = "{\"commands\":[{\"type\":\"client\",\"name\":\"model\",\"template\":\"ignored\"}]}";
            Assert.Empty(SlashCommandConfig.ParseCommands(json, null));
        }

        [Fact]
        public void Names_are_lowercased()
        {
            string json = "{\"commands\":[{\"name\":\"SHOUT\",\"template\":\"hi\"}]}";
            var c = SlashCommandConfig.ParseCommands(json, null).Single();
            Assert.Equal("shout", c.Name);
        }

        [Fact]
        public void User_command_overrides_builtin_in_place()
        {
            string user = "{\"commands\":[{\"name\":\"commit\",\"template\":\"custom commit\"}]}";
            var merged = SlashCommandConfig.LoadMerged(user, null);

            // Same count as defaults (override replaces, not appends).
            var defaults = SlashCommandConfig.ParseCommands(SlashCommandConfig.DefaultsJson, null);
            Assert.Equal(defaults.Count, merged.Count);

            var commit = (PromptCommand)merged.Single(c => c.Name == "commit");
            Assert.Equal("custom commit", commit.Template);
        }

        [Fact]
        public void New_user_command_is_appended_after_builtins()
        {
            string user = "{\"commands\":[{\"name\":\"hello\",\"template\":\"say hi\"}]}";
            var merged = SlashCommandConfig.LoadMerged(user, null);

            Assert.Contains(merged, c => c.Name == "hello");
            Assert.Equal("hello", merged.Last().Name);
        }

        [Fact]
        public void LoadMerged_with_no_user_file_returns_defaults()
        {
            var merged = SlashCommandConfig.LoadMerged(null, null);
            var defaults = SlashCommandConfig.ParseCommands(SlashCommandConfig.DefaultsJson, null);
            Assert.Equal(defaults.Count, merged.Count);
        }
    }
}
