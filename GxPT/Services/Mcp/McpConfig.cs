using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Mcp35.Core.Diagnostics;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Builds the host's MCP server specs from the two configuration tiers (architecture §8):
    //   Tier 1 — bundled first-party servers (web/files/git/command), hardcoded here, never in
    //            mcp.json (D15); managed via per-server enable toggles + a web-search key.
    //   Tier 2 — user-defined servers (and GitHub) from %AppData%/GxPT/mcp.json (snake_case).
    // Pure parsing/assembly (no AppSettings, no Mcp35.Client) so it is fully unit-testable; McpHost
    // supplies the live settings/paths and turns specs into transports.
    internal static class McpConfig
    {
        // Built-in server names (also their server-qualified prefix in tool names).
        public const string WebName = "web";
        public const string FilesName = "files";
        public const string GitName = "git";
        public const string CommandName = "command";
        public const string MsBuildName = "msbuild";
        public const string MemoryName = "memory";
        // The skills + agents authoring/execution server. Named "extensions" because it owns BOTH the
        // skill tools (authoring + run_skill_script) and the agent authoring tools - "skills" no longer
        // describes its whole surface. This value is also the server-qualified tool prefix (extensions__*).
        public const string ExtensionsName = "extensions";
        public const string GitHubName = "github";
        public const string GitHubUrl = "https://api.githubcopilot.com/mcp/";

        // The PowerShell tools the command server surfaces per discovered host: one Windows-PowerShell
        // tool (2.0-5.1 -> command__powershell, or 1.0 -> command__powershell_v1) plus an optional Core
        // tool (command__pwsh). Centralized so every site that special-cases PowerShell rendering (the
        // approval preview, the transcript record, ...) agrees on the set instead of each repeating the
        // name list. The approval classification keys off the broader CommandName + "__" prefix.
        public static bool IsPowerShellTool(string functionName)
        {
            return string.Equals(functionName, CommandName + "__powershell", System.StringComparison.Ordinal)
                || string.Equals(functionName, CommandName + "__powershell_v1", System.StringComparison.Ordinal)
                || string.Equals(functionName, CommandName + "__pwsh", System.StringComparison.Ordinal);
        }

        // Env var names the host injects (servers spec §1).
        public const string EnvWorkdir = "GXPT_WORKDIR";
        public const string EnvWebSearchKey = "GXPT_WEB_SEARCH_KEY";
        public const string EnvCurlPath = "GXPT_CURL_PATH";
        public const string EnvGitPath = "GXPT_GIT_PATH";
        public const string EnvCmdShell = "GXPT_CMD_SHELL";
        public const string EnvMemoryMaxLines = "GXPT_MEMORY_MAX_LINES";
        public const string EnvSkillsBundledRoot = "GXPT_SKILLS_BUNDLED_ROOT";
        public const string EnvSkillsUserRoot = "GXPT_SKILLS_USER_ROOT";
        public const string EnvAgentsUserRoot = "GXPT_AGENTS_USER_ROOT";
        public const string EnvAgentsBundledRoot = "GXPT_AGENTS_BUNDLED_ROOT";

        // Seeded into a fresh mcp.json so GitHub is discoverable; the user pastes a real PAT.
        public const string SeedJson =
            "{\n" +
            "  \"mcp_servers\": {\n" +
            "    \"github\": {\n" +
            "      \"url\": \"https://api.githubcopilot.com/mcp/\",\n" +
            "      \"headers\": { \"Authorization\": \"Bearer YOUR_GITHUB_PAT\" }\n" +
            "    }\n" +
            "  }\n" +
            "}\n";

        // ---- Tier 1: built-in server specs ----

        internal sealed class BuiltInOptions
        {
            public bool WebEnabled { get; set; }
            public bool FilesEnabled { get; set; }
            public bool GitEnabled { get; set; }
            public bool CommandEnabled { get; set; }
            public bool MsBuildEnabled { get; set; }
            public bool MemoryEnabled { get; set; }
            public bool SkillsEnabled { get; set; }

            public string WebSearchKey { get; set; }   // GXPT_WEB_SEARCH_KEY (web)
            public string CurlPath { get; set; }        // GXPT_CURL_PATH (web)
            public string GitPath { get; set; }         // GXPT_GIT_PATH (git)
            public string CmdShell { get; set; }        // GXPT_CMD_SHELL (command)
            public int MemoryMaxLines { get; set; }     // GXPT_MEMORY_MAX_LINES (memory)
            public string SkillsBundledRoot { get; set; } // GXPT_SKILLS_BUNDLED_ROOT (skills exec: <exe>/skills)
            public string SkillsUserRoot { get; set; }    // GXPT_SKILLS_USER_ROOT (skills: %AppData%/GxPT/skills)
            public string AgentsUserRoot { get; set; }    // GXPT_AGENTS_USER_ROOT (agents: %AppData%/GxPT/agents)
            public string AgentsBundledRoot { get; set; } // GXPT_AGENTS_BUNDLED_ROOT (agents: <exe>/agents, read-only)
            public string ServerDir { get; set; }       // directory holding the built server exes

            public BuiltInOptions()
            {
                GitPath = "git";
                CmdShell = "cmd.exe";
                MemoryMaxLines = 40;
            }
        }

        // The GitHub HTTP server, configured from settings.json (enable toggle + PAT) rather than
        // mcp.json. Enabled only when the toggle is on and the PAT is well-formed; workdir-independent.
        public static McpServerSpec GitHubSpec(bool enabled, string pat)
        {
            string token = pat != null ? pat.Trim() : string.Empty;
            McpServerSpec s = new McpServerSpec();
            s.Name = GitHubName;
            s.Kind = McpTransportKind.Http;
            s.BuiltIn = true;
            s.WorkdirScoped = false;
            s.Url = GitHubUrl;
            if (!string.IsNullOrEmpty(token))
                s.Headers["Authorization"] = "Bearer " + token;
            s.Enabled = enabled && IsValidGitHubPat(token);
            return s;
        }

        public static IList<McpServerSpec> BuiltInSpecs(BuiltInOptions o)
        {
            List<McpServerSpec> list = new List<McpServerSpec>();

            // web — workdir-independent (search over HTTP via curl); launched once.
            McpServerSpec web = NewBuiltIn(WebName, "WebSearchMcpServer.exe", o.ServerDir, false, o.WebEnabled);
            if (!string.IsNullOrEmpty(o.WebSearchKey)) web.Env[EnvWebSearchKey] = o.WebSearchKey;
            if (!string.IsNullOrEmpty(o.CurlPath)) web.Env[EnvCurlPath] = o.CurlPath;
            list.Add(web);

            // files — sandboxed to GXPT_WORKDIR (injected at launch, per conversation).
            list.Add(NewBuiltIn(FilesName, "FilesMcpServer.exe", o.ServerDir, true, o.FilesEnabled));

            // git — operates on the repo at GXPT_WORKDIR; git exe path injected.
            McpServerSpec git = NewBuiltIn(GitName, "GitMcpServer.exe", o.ServerDir, true, o.GitEnabled);
            if (!string.IsNullOrEmpty(o.GitPath)) git.Env[EnvGitPath] = o.GitPath;
            list.Add(git);

            // command — runs approved commands via the shell, in GXPT_WORKDIR.
            McpServerSpec cmd = NewBuiltIn(CommandName, "CommandMcpServer.exe", o.ServerDir, true, o.CommandEnabled);
            if (!string.IsNullOrEmpty(o.CmdShell)) cmd.Env[EnvCmdShell] = o.CmdShell;
            // The command server is the only workdir-scoped built-in allowed to run in a per-conversation
            // scratch dir when no workspace is set (files/git/msbuild require a real workspace). The host
            // gates whether scratch dirs are used at all on an opt-in setting.
            cmd.RunsInScratch = true;
            list.Add(cmd);

            // msbuild — discovers installed MSBuild versions and builds the project at GXPT_WORKDIR.
            // No extra env beyond the working directory: engines are discovered, not configured.
            list.Add(NewBuiltIn(MsBuildName, "MSBuildMcpServer.exe", o.ServerDir, true, o.MsBuildEnabled));

            // memory - persistent project memory under GXPT_WORKDIR/.gxpt/memory; workdir-scoped (each
            // folder has its own store). The soft index line cap is injected so the server's
            // over-cap nudge matches the user's configured value.
            McpServerSpec mem = NewBuiltIn(MemoryName, "MemoryMcpServer.exe", o.ServerDir, true, o.MemoryEnabled);
            if (o.MemoryMaxLines > 0)
                mem.Env[EnvMemoryMaxLines] = o.MemoryMaxLines.ToString(System.Globalization.CultureInfo.InvariantCulture);
            list.Add(mem);

            // extensions - author/edit skill files AND agent files under GXPT_WORKDIR/.gxpt/{skills,agents}
            // and run a skill's bundled .bat; workdir-scoped (GXPT_WORKDIR injected at launch). The bundled
            // (<exe>/skills) and user-global (%AppData%) skill roots are injected so run_skill_script can
            // resolve scripts shipped with the app or written user-globally, and so scope=user authoring has
            // a target; the agents user-global root is injected for the same reason on the agent side (agents
            // have no bundled root - they never execute).
            McpServerSpec ext = NewBuiltIn(ExtensionsName, "ExtensionsMcpServer.exe", o.ServerDir, true, o.SkillsEnabled);
            if (!string.IsNullOrEmpty(o.SkillsBundledRoot)) ext.Env[EnvSkillsBundledRoot] = o.SkillsBundledRoot;
            if (!string.IsNullOrEmpty(o.SkillsUserRoot)) ext.Env[EnvSkillsUserRoot] = o.SkillsUserRoot;
            if (!string.IsNullOrEmpty(o.AgentsUserRoot)) ext.Env[EnvAgentsUserRoot] = o.AgentsUserRoot;
            if (!string.IsNullOrEmpty(o.AgentsBundledRoot)) ext.Env[EnvAgentsBundledRoot] = o.AgentsBundledRoot;
            // Also run a workdir-less instance so user-global authoring works in a folderless conversation
            // (it advertises authoring tools only; the per-workdir instances add run_skill_script + project).
            ext.RunsWithoutWorkdir = true;
            list.Add(ext);

            return list;
        }

        private static McpServerSpec NewBuiltIn(string name, string exe, string serverDir, bool workdirScoped, bool enabled)
        {
            McpServerSpec s = new McpServerSpec();
            s.Name = name;
            s.Kind = McpTransportKind.Stdio;
            s.BuiltIn = true;
            s.WorkdirScoped = workdirScoped;
            s.Enabled = enabled;
            s.Command = string.IsNullOrEmpty(serverDir) ? exe : Path.Combine(serverDir, exe);
            return s;
        }

        // ---- Tier 2: mcp.json user/GitHub servers ----

        public static IList<McpServerSpec> ParseUserServers(string mcpJsonText, ILogSink log)
        {
            ILogSink sink = log != null ? log : NullLogSink.Instance;
            List<McpServerSpec> result = new List<McpServerSpec>();
            if (string.IsNullOrEmpty(mcpJsonText)) return result;

            JObject root;
            try { root = JObject.Parse(mcpJsonText); }
            catch
            {
                sink.Log("mcp", "mcp.json is not valid JSON; ignoring custom servers.");
                return result;
            }

            JToken serversTok = root["mcp_servers"];
            JObject servers = serversTok as JObject;
            if (servers == null) return result;

            foreach (KeyValuePair<string, JToken> entry in servers)
            {
                string name = entry.Key;
                JObject val = entry.Value as JObject;
                if (string.IsNullOrEmpty(name) || val == null) continue;

                McpServerSpec spec = new McpServerSpec();
                spec.Name = name;
                spec.BuiltIn = false;
                spec.Enabled = true;

                string url = AsString(val["url"]);
                string command = AsString(val["command"]);

                if (!string.IsNullOrEmpty(url))
                {
                    // url present → HTTP (with headers).
                    spec.Kind = McpTransportKind.Http;
                    spec.Url = url;
                    ReadStringMap(val["headers"] as JObject, spec.Headers);
                }
                else if (!string.IsNullOrEmpty(command))
                {
                    // command present → stdio (with args / env).
                    spec.Kind = McpTransportKind.Stdio;
                    spec.Command = command;
                    spec.Args = ReadStringArray(val["args"] as JArray);
                    ReadStringMap(val["env"] as JObject, spec.Env);
                }
                else
                {
                    sink.Log("mcp", "mcp.json entry '" + name + "' has neither url nor command; skipped.");
                    continue;
                }

                // GitHub PAT gate: a github entry whose Authorization bearer is not a well-formed PAT
                // (including the unedited placeholder) is simply disabled — no connection attempt.
                if (name == "github" && spec.Kind == McpTransportKind.Http)
                {
                    string token = BearerFromHeaders(spec.Headers);
                    if (!IsValidGitHubPat(token))
                    {
                        spec.Enabled = false;
                        sink.Log("mcp", "github entry has no valid PAT (placeholder?) — disabled.");
                    }
                }

                result.Add(spec);
            }
            return result;
        }

        // Classic (ghp_…) or fine-grained (github_pat_…) PAT shape; rejects the placeholder.
        public static bool IsValidGitHubPat(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (Regex.IsMatch(token, "^ghp_[A-Za-z0-9]{36,}$")) return true;
            if (Regex.IsMatch(token, "^github_pat_[A-Za-z0-9_]{30,}$")) return true;
            return false;
        }

        public static string BearerFromHeaders(IDictionary<string, string> headers)
        {
            if (headers == null) return null;
            foreach (KeyValuePair<string, string> h in headers)
            {
                if (string.Equals(h.Key, "Authorization", System.StringComparison.OrdinalIgnoreCase))
                {
                    string v = h.Value != null ? h.Value.Trim() : null;
                    if (!string.IsNullOrEmpty(v) && v.StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
                        return v.Substring(7).Trim();
                    return v;
                }
            }
            return null;
        }

        // ---- helpers ----

        private static string AsString(JToken t)
        {
            if (t == null || t.Type != JTokenType.String) return null;
            return (string)t;
        }

        private static string[] ReadStringArray(JArray arr)
        {
            if (arr == null) return new string[0];
            List<string> list = new List<string>();
            foreach (JToken t in arr)
                if (t != null && t.Type == JTokenType.String) list.Add((string)t);
            return list.ToArray();
        }

        private static void ReadStringMap(JObject obj, IDictionary<string, string> into)
        {
            if (obj == null) return;
            foreach (KeyValuePair<string, JToken> kv in obj)
            {
                if (kv.Value != null && kv.Value.Type == JTokenType.String)
                    into[kv.Key] = (string)kv.Value;
            }
        }
    }
}
