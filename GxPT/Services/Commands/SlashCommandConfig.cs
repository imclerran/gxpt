using System.Collections.Generic;
using Mcp35.Core.Diagnostics;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Builds the slash-command set from two sources, mirroring the mcp.json pattern (McpConfig):
    //   * DefaultsJson  -- the bundled built-in commands, shipped in the same shape users author.
    //   * commands.json -- user-authored commands in %AppData%/GxPT, merged over the defaults by name.
    // Pure parsing (no disk, no WinForms) so it compiles into the unit-test assembly; the host reads the
    // file text and passes it to LoadMerged.
    internal static class SlashCommandConfig
    {
        // Bundled built-in prompt commands. Each maps to a server GxPT actually ships and declares its
        // dependency as data: Requires (all) for hard deps, RequiresAny (one-of) for alternatives.
        // /test and /fix work via MSBuild OR the command server, so a non-MSBuild project (dotnet test,
        // make, build scripts) still gets them. Templates stay tool-agnostic: the model picks from the
        // toolset it can see.
        public const string DefaultsJson = @"{
  ""commands"": [
    {
      ""type"": ""prompt"",
      ""name"": ""commit"",
      ""description"": ""Review changes and create a well-formed commit"",
      ""requires"": [""git""],
      ""template"": ""Review the staged and unstaged changes using the git tools and create a single well-formed commit with a clear, conventional message. Do not push.""
    },
    {
      ""type"": ""prompt"",
      ""name"": ""diff"",
      ""description"": ""Summarize what's changed in the working tree"",
      ""requires"": [""git""],
      ""template"": ""Summarize what has changed in the working tree (staged and unstaged) using the git tools. Give a concise, high-level overview grouped by area.""
    },
    {
      ""type"": ""prompt"",
      ""name"": ""review"",
      ""description"": ""Review the current diff for bugs and quality issues"",
      ""requires"": [""git""],
      ""template"": ""Review the current diff for correctness bugs and quality or simplification issues. Cite specific file:line locations and explain the impact of each finding.""
    },
    {
      ""type"": ""prompt"",
      ""name"": ""explain"",
      ""description"": ""Read a file or folder and explain what it does"",
      ""argument_hint"": ""<path>"",
      ""arg_type"": ""path"",
      ""requires"": [""files""],
      ""template"": ""Read `{args}` and explain what it does, its key types, and how it fits into the rest of the project.""
    },
    {
      ""type"": ""prompt"",
      ""name"": ""test"",
      ""description"": ""Build and run the project's tests"",
      ""requires_any"": [""msbuild"", ""command""],
      ""template"": ""Build the project and run its tests using whatever build tooling is available, then report any failures with the relevant output.""
    },
    {
      ""type"": ""prompt"",
      ""name"": ""fix"",
      ""description"": ""Build, fix the first error, and loop until green"",
      ""requires_any"": [""msbuild"", ""command""],
      ""template"": ""Build the project. Read the first error, fix it, and rebuild -- repeat until the build is green. Then report what changed.""
    }
  ]
}";

        // Built-in defaults overlaid with the user's commands.json (may be null/empty). A user command
        // with the same name as a built-in replaces it; new names are appended after the built-ins.
        public static IList<ISlashCommand> LoadMerged(string userJsonText, ILogSink log)
        {
            ILogSink sink = log != null ? log : NullLogSink.Instance;

            IList<ISlashCommand> defaults = ParseCommands(DefaultsJson, sink);
            IList<ISlashCommand> user = ParseCommands(userJsonText, sink);
            if (user.Count == 0) return defaults;

            // Index user commands by name for override lookup.
            Dictionary<string, ISlashCommand> userByName =
                new Dictionary<string, ISlashCommand>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < user.Count; i++)
            {
                ISlashCommand c = user[i];
                if (c != null && !string.IsNullOrEmpty(c.Name)) userByName[c.Name] = c;
            }

            List<ISlashCommand> merged = new List<ISlashCommand>();
            HashSet<string> seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            // Built-ins first (in their canonical order), substituting any user override in place.
            for (int i = 0; i < defaults.Count; i++)
            {
                ISlashCommand d = defaults[i];
                ISlashCommand ovr;
                if (userByName.TryGetValue(d.Name, out ovr))
                {
                    merged.Add(ovr);
                    seen.Add(d.Name);
                }
                else
                {
                    merged.Add(d);
                }
            }
            // Then any brand-new user commands, in their file order.
            for (int i = 0; i < user.Count; i++)
            {
                ISlashCommand c = user[i];
                if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                if (seen.Contains(c.Name)) continue;
                if (string.Equals(c.Name, FindBuiltInName(defaults, c.Name), System.StringComparison.OrdinalIgnoreCase))
                    continue; // already placed as an override above
                merged.Add(c);
            }
            return merged;
        }

        private static string FindBuiltInName(IList<ISlashCommand> defaults, string name)
        {
            for (int i = 0; i < defaults.Count; i++)
            {
                if (string.Equals(defaults[i].Name, name, System.StringComparison.OrdinalIgnoreCase))
                    return defaults[i].Name;
            }
            return null;
        }

        // Parse a { "commands": [ ... ] } document into command objects. Invalid JSON or malformed
        // entries are logged and skipped (never thrown) so a bad user file can't break the app.
        public static IList<ISlashCommand> ParseCommands(string json, ILogSink log)
        {
            ILogSink sink = log != null ? log : NullLogSink.Instance;
            List<ISlashCommand> result = new List<ISlashCommand>();
            if (string.IsNullOrEmpty(json)) return result;

            JObject root;
            try { root = JObject.Parse(json); }
            catch
            {
                sink.Log("commands", "commands.json is not valid JSON; ignoring custom commands.");
                return result;
            }

            JArray arr = root["commands"] as JArray;
            if (arr == null) return result;

            foreach (JToken tok in arr)
            {
                JObject obj = tok as JObject;
                if (obj == null) continue;

                string name = AsString(obj["name"]);
                if (string.IsNullOrEmpty(name)) { sink.Log("commands", "command entry has no name; skipped."); continue; }
                name = name.Trim().ToLowerInvariant();

                string type = AsString(obj["type"]);
                if (string.IsNullOrEmpty(type)) type = "prompt";

                if (!string.Equals(type, "prompt", System.StringComparison.OrdinalIgnoreCase))
                {
                    // Client commands are not registered from data in v1; skip quietly so a forward-
                    // looking commands.json doesn't error on older builds.
                    sink.Log("commands", "command '" + name + "' has unsupported type '" + type + "'; skipped.");
                    continue;
                }

                string template = AsString(obj["template"]);
                if (string.IsNullOrEmpty(template))
                {
                    sink.Log("commands", "prompt command '" + name + "' has no template; skipped.");
                    continue;
                }

                string description = AsString(obj["description"]);
                string argHint = AsString(obj["argument_hint"]);
                string argType = AsString(obj["arg_type"]);
                bool takesPath = string.Equals(argType, "path", System.StringComparison.OrdinalIgnoreCase);

                IList<string> aliases = ReadStringArray(obj["aliases"] as JArray, true);
                IList<string> requires = ReadStringArray(obj["requires"] as JArray, true);
                IList<string> requiresAny = ReadStringArray(obj["requires_any"] as JArray, true);

                result.Add(new PromptCommand(name, description, template, argHint, takesPath,
                    aliases, requires, requiresAny));
            }

            return result;
        }

        private static string AsString(JToken tok)
        {
            if (tok == null) return null;
            if (tok.Type == JTokenType.Null) return null;
            return tok.ToString();
        }

        private static IList<string> ReadStringArray(JArray arr, bool lower)
        {
            List<string> list = new List<string>();
            if (arr == null) return list;
            foreach (JToken t in arr)
            {
                string s = AsString(t);
                if (string.IsNullOrEmpty(s)) continue;
                s = s.Trim();
                if (lower) s = s.ToLowerInvariant();
                if (s.Length > 0) list.Add(s);
            }
            return list;
        }
    }
}
