using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxPT;
using Xunit;

namespace GxPT.Tests.Commands
{
    public class ClientCommandsTests
    {
        private static FakeSlashCommandContext CtxWithModels()
        {
            var ctx = new FakeSlashCommandContext("/work");
            ctx.ModelStore.Models.AddRange(new[]
            {
                "openai/gpt-4o", "openai/gpt-4o-mini", "anthropic/claude-3.5-sonnet"
            });
            return ctx;
        }

        // ---- /model completion (two-level: author/ then model) ----

        [Fact]
        public void Model_completes_distinct_authors_first()
        {
            var cmd = new ModelCommand();
            var items = cmd.CompleteArgument("", CtxWithModels());
            var inserts = items.Select(i => i.InsertArg).ToList();

            Assert.Contains("openai/", inserts);
            Assert.Contains("anthropic/", inserts);
            Assert.Equal(2, inserts.Count); // openai appears once despite two models
            Assert.All(items, i => Assert.True(i.ContinueCompleting)); // author level keeps completing
        }

        [Fact]
        public void Model_filters_authors_by_prefix()
        {
            var items = new ModelCommand().CompleteArgument("ant", CtxWithModels());
            Assert.Single(items);
            Assert.Equal("anthropic/", items[0].InsertArg);
        }

        [Fact]
        public void Model_completes_models_under_author()
        {
            var items = new ModelCommand().CompleteArgument("openai/", CtxWithModels());
            var inserts = items.Select(i => i.InsertArg).ToList();

            Assert.Contains("openai/gpt-4o", inserts);
            Assert.Contains("openai/gpt-4o-mini", inserts);
            Assert.DoesNotContain("anthropic/claude-3.5-sonnet", inserts);
            Assert.All(items, i => Assert.False(i.ContinueCompleting)); // model level is terminal
        }

        [Fact]
        public void Model_filters_models_by_partial()
        {
            var items = new ModelCommand().CompleteArgument("openai/gpt-4o-m", CtxWithModels());
            Assert.Single(items);
            Assert.Equal("openai/gpt-4o-mini", items[0].InsertArg);
        }

        [Fact]
        public void Model_invoke_sets_the_model()
        {
            var ctx = CtxWithModels();
            var result = new ModelCommand().Invoke("openai/gpt-4o", ctx);
            Assert.False(result.SendToModel);
            Assert.Null(result.Error);
            Assert.Equal("openai/gpt-4o", ctx.ModelStore.LastModelSet);
        }

        [Fact]
        public void Model_invoke_without_arg_fails()
        {
            var result = new ModelCommand().Invoke("", CtxWithModels());
            Assert.NotNull(result.Error);
        }

        // ---- /server completion + invoke ----

        private static FakeSlashCommandContext CtxWithServers()
        {
            var ctx = new FakeSlashCommandContext("/work");
            ctx.ServerStore.ServerStates["git"] = true;
            ctx.ServerStore.ServerStates["files"] = false;
            ctx.ServerStore.ServerStates["command"] = false;
            return ctx;
        }

        [Fact]
        public void Server_completes_names_then_onoff()
        {
            var cmd = new ToggleToolCommand();
            var names = cmd.CompleteArgument("", CtxWithServers()).Select(i => i.InsertArg).ToList();
            Assert.Contains("git ", names);
            Assert.Contains("files ", names);

            var choices = cmd.CompleteArgument("git ", CtxWithServers()).Select(i => i.InsertArg).ToList();
            Assert.Contains("git on", choices);
            Assert.Contains("git off", choices);
        }

        [Fact]
        public void Server_invoke_explicit_on_off()
        {
            var ctx = CtxWithServers();
            new ToggleToolCommand().Invoke("files on", ctx);
            Assert.True(ctx.ServerStore.ServerStates["files"]);

            new ToggleToolCommand().Invoke("git off", ctx);
            Assert.False(ctx.ServerStore.ServerStates["git"]);
        }

        [Fact]
        public void Server_invoke_toggles_when_state_omitted()
        {
            var ctx = CtxWithServers(); // git starts on
            new ToggleToolCommand().Invoke("git", ctx);
            Assert.False(ctx.ServerStore.ServerStates["git"]);
            new ToggleToolCommand().Invoke("git", ctx);
            Assert.True(ctx.ServerStore.ServerStates["git"]);
        }

        [Fact]
        public void Server_invoke_unknown_name_fails()
        {
            var result = new ToggleToolCommand().Invoke("bogus on", CtxWithServers());
            Assert.NotNull(result.Error);
        }

        // ---- /new and /export ----

        [Fact]
        public void New_opens_a_conversation()
        {
            var ctx = new FakeSlashCommandContext("/work");
            var result = new NewConversationCommand().Invoke("", ctx);
            Assert.False(result.SendToModel);
            Assert.Equal(1, ctx.NewConversationCount);
        }

        [Fact]
        public void Export_triggers_export()
        {
            var ctx = new FakeSlashCommandContext("/work");
            new ExportCommand().Invoke("", ctx);
            Assert.Equal(1, ctx.ExportCount);
        }

        [Fact]
        public void Export_with_slug_exports_that_skill()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxpt_exportcmd_" + Guid.NewGuid().ToString("N"));
            try
            {
                string dir = Path.Combine(root, ".gxpt", "skills", "exp-demo");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\ndescription: d.\n---\n\nb\n");

                var ctx = new FakeSlashCommandContext(root);
                var result = new ExportCommand().Invoke("exp-demo", ctx);

                Assert.Null(result.Error);
                Assert.Single(ctx.ExportedSkills);
                Assert.Equal("exp-demo", ctx.ExportedSkills[0].Slug);
                Assert.Equal(0, ctx.ExportCount); // skill export, not the conversations export
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch { }
            }
        }

        [Fact]
        public void Export_rejects_and_hides_bundled_skills()
        {
            // The bundled root is <exe>/skills; for tests that's the test bin. Use a unique slug so
            // parallel tests building catalogs never collide with it, and clean up afterwards.
            string slug = "bundled-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills", slug);
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\ndescription: d.\n---\n\nb\n");

                var ctx = new FakeSlashCommandContext(null);
                var cmd = new ExportCommand();

                var result = cmd.Invoke(slug, ctx);
                Assert.NotNull(result.Error);
                Assert.Empty(ctx.ExportedSkills);

                var completions = cmd.CompleteArgument(slug, ctx);
                Assert.Empty(completions);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        [Fact]
        public void Export_with_unknown_slug_fails()
        {
            var ctx = new FakeSlashCommandContext("/work");
            var result = new ExportCommand().Invoke("no-such-skill-zzz", ctx);
            Assert.NotNull(result.Error);
            Assert.Equal(0, ctx.ExportCount);
            Assert.Empty(ctx.ExportedSkills);
        }

        [Fact]
        public void Import_triggers_import()
        {
            var ctx = new FakeSlashCommandContext("/work");
            var result = new ImportCommand().Invoke("", ctx);
            Assert.False(result.SendToModel);
            Assert.Equal(1, ctx.ImportCount);
        }

        [Fact]
        public void Compact_triggers_compaction()
        {
            var ctx = new FakeSlashCommandContext("/work");
            var result = new CompactCommand().Invoke("", ctx);
            Assert.False(result.SendToModel);
            Assert.Equal(1, ctx.CompactCount);
        }

        // ---- registration / dispatch through the processor ----

        [Fact]
        public void Client_commands_dispatch_through_the_processor()
        {
            var registry = new SlashCommandRegistry(ClientCommands.BuiltIns());
            var processor = new SlashCommandProcessor(registry);
            var ctx = CtxWithModels();

            var result = processor.Process("/model openai/gpt-4o", ctx);
            Assert.NotNull(result);
            Assert.False(result.SendToModel);     // client command: not sent to the model
            Assert.Equal("openai/gpt-4o", ctx.ModelStore.LastModelSet);
        }
    }
}
