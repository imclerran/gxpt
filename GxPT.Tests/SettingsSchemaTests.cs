using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Locks in the single-source-of-truth defaults (issue #164): the credential-free MCP servers
    // default ON, the credential-gated ones and memory default OFF, and the schema's typed lookups
    // agree with the declared table.
    public sealed class SettingsSchemaTests
    {
        [Theory]
        // Credential-free first-party servers default ON so they work out of the box. (git/msbuild are
        // still gated at launch on the tool being installed; that gate lives at the call site.)
        [InlineData("mcp_files_enabled", true)]
        [InlineData("mcp_command_enabled", true)]
        [InlineData("mcp_git_enabled", true)]
        [InlineData("mcp_msbuild_enabled", true)]
        // Sub-agents ship enabled.
        [InlineData("agents_enabled", true)]
        // Status bar visible by default.
        [InlineData("statusbar_visible", true)]
        // Credential-gated servers can't run without a key/PAT, so they default OFF.
        [InlineData("mcp_web_enabled", false)]
        [InlineData("mcp_github_enabled", false)]
        // The command scratch sandbox is opt-in.
        [InlineData("mcp_command_scratch_enabled", false)]
        // Persistent memory (not strictly an MCP server) defaults OFF.
        [InlineData("mcp_memory_enabled", false)]
        // Logging and zero-data-retention default OFF.
        [InlineData("enable_logging", false)]
        [InlineData("provider_zdr", false)]
        public void BoolDefaults_MatchPolicy(string key, bool expected)
        {
            Assert.Equal(expected, SettingsSchema.BoolDefault(key));

            // BuildDefaults must declare the same value the typed lookup returns (no second source).
            object raw;
            Assert.True(SettingsSchema.BuildDefaults().TryGetValue(key, out raw));
            Assert.IsType<bool>(raw);
            Assert.Equal(expected, (bool)raw);
        }

        [Fact]
        public void Defaults_AreComplete()
        {
            var d = SettingsSchema.BuildDefaults();
            // A representative spread of keys from each section is present, so the seeded file is complete.
            Assert.True(d.ContainsKey("openrouter_api_key"));
            Assert.True(d.ContainsKey("models"));
            Assert.True(d.ContainsKey("default_model"));
            Assert.True(d.ContainsKey("theme"));
            Assert.True(d.ContainsKey("mcp_memory_max_lines"));
            // The legacy data-collection pref is intentionally NOT seeded (migration-only).
            Assert.False(d.ContainsKey("provider_data_collection"));
        }

        [Fact]
        public void NumericDefaults_AreSane()
        {
            Assert.Equal(40, SettingsSchema.DoubleDefault("mcp_memory_max_lines"));
            Assert.Equal(1000, SettingsSchema.DoubleDefault("transcript_max_width"));
            Assert.Equal(90, SettingsSchema.DoubleDefault("message_max_width"));
        }
    }
}
