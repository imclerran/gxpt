using System;
using System.IO;
using System.Text;
using GxPT;
using GxPT.Tests.Commands;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentCommandsTests : IDisposable
    {
        private readonly string _work;

        public AgentCommandsTests()
        {
            _work = Path.Combine(Path.GetTempPath(), "gxpt_agentcmd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_work);
        }

        public void Dispose()
        {
            try { Directory.Delete(_work, true); } catch { }
        }

        private void WriteProjectAgent(string slug, string desc)
        {
            string dir = Path.Combine(Path.Combine(_work, ".gxpt"), "agents");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, slug + ".md"),
                "---\nname: " + slug + "\ndescription: " + desc + "\n---\nbody\n", new UTF8Encoding(false));
        }

        // ---- /toggle-agents (conversation scope; global scope writes settings.json so is not exercised here) ----

        [Fact]
        public void ToggleAgents_Here_On_SetsConversationOverrideTrue()
        {
            var ctx = new FakeSlashCommandContext(_work);
            SlashCommandResult r = new ToggleAgentsCommand().Invoke("here on", ctx);

            Assert.Null(r.Error);
            Assert.True(ctx.AgentStore.ConvAgentsEnabled.HasValue && ctx.AgentStore.ConvAgentsEnabled.Value);
        }

        [Fact]
        public void ToggleAgents_Here_Off_SetsConversationOverrideFalse()
        {
            var ctx = new FakeSlashCommandContext(_work);
            new ToggleAgentsCommand().Invoke("here off", ctx);
            Assert.True(ctx.AgentStore.ConvAgentsEnabled.HasValue && !ctx.AgentStore.ConvAgentsEnabled.Value);
        }

        [Fact]
        public void ToggleAgents_Here_Inherit_ClearsConversationOverride()
        {
            var ctx = new FakeSlashCommandContext(_work);
            ctx.AgentStore.ConvAgentsEnabled = true;
            new ToggleAgentsCommand().Invoke("here inherit", ctx);
            Assert.False(ctx.AgentStore.ConvAgentsEnabled.HasValue);
        }

        [Fact]
        public void ToggleAgents_Global_Inherit_IsRejected()
        {
            var ctx = new FakeSlashCommandContext(_work);
            SlashCommandResult r = new ToggleAgentsCommand().Invoke("global inherit", ctx);
            Assert.NotNull(r.Error);
        }

        [Theory]
        [InlineData("")]            // no args
        [InlineData("here")]        // missing verb
        [InlineData("here on off")] // too many
        [InlineData("sideways on")] // bad scope
        [InlineData("here maybe")]  // bad verb
        public void ToggleAgents_BadUsage_Fails(string args)
        {
            var ctx = new FakeSlashCommandContext(_work);
            SlashCommandResult r = new ToggleAgentsCommand().Invoke(args, ctx);
            Assert.NotNull(r.Error);
        }

        // ---- /dispatch-agent ----

        [Fact]
        public void DispatchAgent_KnownAgent_SendsInstruction()
        {
            WriteProjectAgent("explorer", "Explore the code.");
            var ctx = new FakeSlashCommandContext(_work);

            SlashCommandResult r = new DispatchAgentCommand().Invoke("explorer find the parser", ctx);

            Assert.Null(r.Error);
            Assert.True(r.SendToModel);
            Assert.Equal("Dispatch the explorer agent with this task: find the parser", r.TextToSend);
        }

        [Fact]
        public void DispatchAgent_UnknownAgent_Fails()
        {
            var ctx = new FakeSlashCommandContext(_work);
            SlashCommandResult r = new DispatchAgentCommand().Invoke("ghost do something", ctx);
            Assert.NotNull(r.Error);
            Assert.Contains("Unknown agent", r.Error);
        }

        [Theory]
        [InlineData("")]            // empty
        [InlineData("explorer")]    // slug but no task
        public void DispatchAgent_BadUsage_Fails(string args)
        {
            WriteProjectAgent("explorer", "Explore.");
            var ctx = new FakeSlashCommandContext(_work);
            SlashCommandResult r = new DispatchAgentCommand().Invoke(args, ctx);
            Assert.NotNull(r.Error);
        }
    }
}
