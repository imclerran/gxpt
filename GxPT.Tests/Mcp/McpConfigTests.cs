using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxPT;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class McpConfigTests
    {
        // valid PAT shapes (fake but well-formed)
        private const string ClassicPat = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";          // ghp_ + 36
        private const string FineGrainedPat = "github_pat_11AABBCCDD11AABBCCDD11AABBCCDD11AABB"; // long

        // ---- Tier 2: mcp.json parsing ----

        [Fact]
        public void Parses_http_server_with_headers()
        {
            string json = "{\"mcp_servers\":{\"acme\":{\"url\":\"https://x/mcp/\",\"headers\":{\"Authorization\":\"Bearer abc\"}}}}";
            var specs = McpConfig.ParseUserServers(json, null);
            var s = specs.Single();
            Assert.Equal("acme", s.Name);
            Assert.Equal(McpTransportKind.Http, s.Kind);
            Assert.Equal("https://x/mcp/", s.Url);
            Assert.Equal("Bearer abc", s.Headers["Authorization"]);
            Assert.True(s.Enabled);
            Assert.False(s.BuiltIn);
        }

        [Fact]
        public void Parses_stdio_server_with_args_and_env()
        {
            string json = "{\"mcp_servers\":{\"local\":{\"command\":\"C:\\\\srv.exe\",\"args\":[\"--flag\",\"x\"],\"env\":{\"K\":\"V\"}}}}";
            var s = McpConfig.ParseUserServers(json, null).Single();
            Assert.Equal(McpTransportKind.Stdio, s.Kind);
            Assert.Equal("C:\\srv.exe", s.Command);
            Assert.Equal(new[] { "--flag", "x" }, s.Args);
            Assert.Equal("V", s.Env["K"]);
        }

        [Fact]
        public void Github_with_placeholder_pat_is_disabled()
        {
            var s = McpConfig.ParseUserServers(McpConfig.SeedJson, null).Single();
            Assert.Equal("github", s.Name);
            Assert.False(s.Enabled); // unedited "Bearer YOUR_GITHUB_PAT"
        }

        [Theory]
        [InlineData(ClassicPat)]
        [InlineData(FineGrainedPat)]
        public void Github_with_valid_pat_is_enabled(string pat)
        {
            string json = "{\"mcp_servers\":{\"github\":{\"url\":\"https://api.githubcopilot.com/mcp/\",\"headers\":{\"Authorization\":\"Bearer " + pat + "\"}}}}";
            var s = McpConfig.ParseUserServers(json, null).Single();
            Assert.True(s.Enabled);
        }

        [Fact]
        public void Invalid_json_yields_no_servers_without_throwing()
        {
            Assert.Empty(McpConfig.ParseUserServers("{ not json", null));
            Assert.Empty(McpConfig.ParseUserServers("", null));
            Assert.Empty(McpConfig.ParseUserServers("{\"mcp_servers\":{}}", null));
        }

        [Fact]
        public void Entry_without_url_or_command_is_skipped()
        {
            string json = "{\"mcp_servers\":{\"bad\":{\"note\":\"nothing useful\"},\"good\":{\"url\":\"https://y\"}}}";
            var specs = McpConfig.ParseUserServers(json, null);
            Assert.Single(specs);
            Assert.Equal("good", specs[0].Name);
        }

        // ---- PAT shape / bearer extraction ----

        [Theory]
        [InlineData(ClassicPat, true)]
        [InlineData(FineGrainedPat, true)]
        [InlineData("YOUR_GITHUB_PAT", false)]
        [InlineData("ghp_short", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Validates_github_pat_shape(string token, bool expected)
        {
            Assert.Equal(expected, McpConfig.IsValidGitHubPat(token));
        }

        [Fact]
        public void Extracts_bearer_token_case_insensitively()
        {
            var headers = new Dictionary<string, string> { { "authorization", "Bearer xyz" } };
            Assert.Equal("xyz", McpConfig.BearerFromHeaders(headers));
        }

        // ---- Tier 1: built-in specs ----

        [Fact]
        public void GitHubSpec_is_http_and_enabled_only_with_a_valid_pat()
        {
            var ok = McpConfig.GitHubSpec(true, ClassicPat);
            Assert.Equal("github", ok.Name);
            Assert.Equal(McpTransportKind.Http, ok.Kind);
            Assert.Equal(McpConfig.GitHubUrl, ok.Url);
            Assert.Equal("Bearer " + ClassicPat, ok.Headers["Authorization"]);
            Assert.False(ok.WorkdirScoped);
            Assert.True(ok.Enabled);

            Assert.False(McpConfig.GitHubSpec(true, "YOUR_GITHUB_PAT").Enabled); // malformed PAT
            Assert.False(McpConfig.GitHubSpec(false, ClassicPat).Enabled);       // toggle off
        }

        [Fact]
        public void Builtins_cover_the_seven_servers_with_toggles_and_env()
        {
            var o = new McpConfig.BuiltInOptions
            {
                WebEnabled = true,
                FilesEnabled = true,
                GitEnabled = false,
                CommandEnabled = true,
                MsBuildEnabled = true,
                MemoryEnabled = true,
                SkillsEnabled = true,
                WebSearchKey = "tav_key",
                CurlPath = "C:\\curl.exe",
                GitPath = "git",
                CmdShell = "cmd.exe",
                SkillsBundledRoot = "C:\\app\\skills",
                SkillsUserRoot = "C:\\users\\me\\AppData\\Roaming\\GxPT\\skills",
                ServerDir = "C:\\app\\servers"
            };
            var specs = McpConfig.BuiltInSpecs(o);
            var byName = specs.ToDictionary(s => s.Name);

            Assert.Equal(7, specs.Count);
            Assert.True(specs.All(s => s.BuiltIn && s.Kind == McpTransportKind.Stdio));

            var web = byName["web"];
            Assert.True(web.Enabled);
            Assert.False(web.WorkdirScoped); // workdir-independent
            Assert.Equal(Path.Combine("C:\\app\\servers", "WebSearchMcpServer.exe"), web.Command);
            Assert.Equal("tav_key", web.Env[McpConfig.EnvWebSearchKey]);
            Assert.Equal("C:\\curl.exe", web.Env[McpConfig.EnvCurlPath]);

            var files = byName["files"];
            Assert.True(files.WorkdirScoped);
            Assert.Empty(files.Env); // GXPT_WORKDIR injected at launch, not baked in

            Assert.False(byName["git"].Enabled); // toggle off
            Assert.Equal("git", byName["git"].Env[McpConfig.EnvGitPath]);

            Assert.Equal("cmd.exe", byName["command"].Env[McpConfig.EnvCmdShell]);
            Assert.True(byName["command"].WorkdirScoped);
            // The command server is the only scratch-eligible scoped built-in (runs in a per-
            // conversation scratch dir when no workspace is set); the others stay workspace-only.
            Assert.True(byName["command"].RunsInScratch);
            Assert.False(byName["files"].RunsInScratch);
            Assert.False(byName["git"].RunsInScratch);
            Assert.False(byName["msbuild"].RunsInScratch);
            Assert.False(byName["memory"].RunsInScratch);
            Assert.False(byName["skills"].RunsInScratch);

            // msbuild — workdir-scoped, engines discovered (no extra env baked in).
            var msbuild = byName["msbuild"];
            Assert.True(msbuild.Enabled);
            Assert.True(msbuild.WorkdirScoped);
            Assert.Equal(Path.Combine("C:\\app\\servers", "MSBuildMcpServer.exe"), msbuild.Command);
            Assert.Empty(msbuild.Env);

            // memory — workdir-scoped; soft index cap injected as env.
            var memory = byName["memory"];
            Assert.True(memory.Enabled);
            Assert.True(memory.WorkdirScoped);
            Assert.Equal(Path.Combine("C:\\app\\servers", "MemoryMcpServer.exe"), memory.Command);
            Assert.Equal("40", memory.Env[McpConfig.EnvMemoryMaxLines]);

            // skills — workdir-scoped authoring/execution server; GXPT_WORKDIR injected at launch, the
            // bundled + user skill roots baked in so run_skill_script can resolve those scopes.
            var skills = byName["skills"];
            Assert.True(skills.Enabled);
            Assert.True(skills.WorkdirScoped);
            Assert.Equal(Path.Combine("C:\\app\\servers", "SkillsMcpServer.exe"), skills.Command);
            Assert.Equal("C:\\app\\skills", skills.Env[McpConfig.EnvSkillsBundledRoot]);
            Assert.Equal("C:\\users\\me\\AppData\\Roaming\\GxPT\\skills", skills.Env[McpConfig.EnvSkillsUserRoot]);
            Assert.True(skills.RunsWithoutWorkdir); // also runs a workdir-less instance for global authoring
        }
    }
}
