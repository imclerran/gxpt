using System;
using System.Collections.Generic;

namespace GxPT
{
    // Whether a required-tool group is satisfied by ANY of its entries being available, or needs ALL of them.
    internal enum RequiredToolMode { AnyOf, All }

    // A glob requirement: a server-qualified pattern (e.g. "command__*") plus the agent tier ceiling it was
    // derived under. Stored - not frozen-expanded - so the installer re-resolves it (glob a tier a available)
    // against its own catalog, staying correct as the server's tool set changes.
    internal sealed class RequiredToolGlob
    {
        public string Pattern;
        public AgentMaxTier MaxTier;

        public RequiredToolGlob() { MaxTier = AgentMaxTier.Write; }
        public RequiredToolGlob(string pattern, AgentMaxTier maxTier) { Pattern = pattern; MaxTier = maxTier; }
    }

    // One required-tool group, scoped to a single MCP server. Entries are concrete tool ids and/or globs;
    // mode controls whether any-one or all of them must be available. Built from the bundled agents' declared
    // tools (see RequiredToolsDetect) and refined by the author; checked at install (see RequiredToolsCheck).
    internal sealed class RequiredToolGroup
    {
        public string Server;
        public RequiredToolMode Mode;
        public List<string> Tools;
        public List<RequiredToolGlob> Globs;

        public RequiredToolGroup()
        {
            Mode = RequiredToolMode.AnyOf;
            Tools = new List<string>();
            Globs = new List<RequiredToolGlob>();
        }
    }

    // Helpers shared by detection, the picker, the manifest, and the install check. Pure; net48-testable.
    internal static class RequiredTool
    {
        // The server segment of a function name (everything before the first "__"); matches the convention
        // McpToolRegistry.Munge produces and HasServer keys off.
        public static string ServerOf(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return string.Empty;
            int i = functionName.IndexOf("__", StringComparison.Ordinal);
            return i > 0 ? functionName.Substring(0, i) : functionName;
        }

        // A "<server>__*" whole-server glob: a server prefix followed by a single trailing '*'.
        public static bool IsWholeServerGlob(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            return pattern.IndexOf('*') == pattern.Length - 1
                && pattern.EndsWith("__*", StringComparison.Ordinal);
        }

        public static bool HasWildcard(string s)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf('*') >= 0;
        }

        public static string TierToken(AgentMaxTier tier)
        {
            switch (tier)
            {
                case AgentMaxTier.ReadOnly: return "readonly";
                case AgentMaxTier.Destructive: return "destructive";
                default: return "write";
            }
        }

        public static AgentMaxTier ParseTier(string token)
        {
            AgentMaxTier t;
            if (AgentFrontmatter.TryParseMaxTier(token, out t)) return t;
            return AgentMaxTier.Write;
        }

        // ---- JSON (operates on JavaScriptSerializer's object graph: object[] of Dictionary<string,object>) ----

        public static object[] ToJsonValue(IList<RequiredToolGroup> groups)
        {
            List<object> arr = new List<object>();
            if (groups == null) return arr.ToArray();
            for (int i = 0; i < groups.Count; i++)
            {
                RequiredToolGroup g = groups[i];
                if (g == null || string.IsNullOrEmpty(g.Server)) continue;

                Dictionary<string, object> o = new Dictionary<string, object>();
                o["server"] = g.Server;
                o["mode"] = g.Mode == RequiredToolMode.All ? "all" : "anyOf";
                o["tools"] = (g.Tools ?? new List<string>()).ToArray();

                List<object> globs = new List<object>();
                if (g.Globs != null)
                {
                    for (int j = 0; j < g.Globs.Count; j++)
                    {
                        RequiredToolGlob gl = g.Globs[j];
                        if (gl == null || string.IsNullOrEmpty(gl.Pattern)) continue;
                        Dictionary<string, object> go = new Dictionary<string, object>();
                        go["pattern"] = gl.Pattern;
                        go["max_tier"] = TierToken(gl.MaxTier);
                        globs.Add(go);
                    }
                }
                o["globs"] = globs.ToArray();
                arr.Add(o);
            }
            return arr.ToArray();
        }

        public static List<RequiredToolGroup> FromJsonValue(object value)
        {
            List<RequiredToolGroup> result = new List<RequiredToolGroup>();
            object[] arr = value as object[];
            if (arr == null) return result;
            for (int i = 0; i < arr.Length; i++)
            {
                Dictionary<string, object> o = arr[i] as Dictionary<string, object>;
                if (o == null) continue;

                RequiredToolGroup g = new RequiredToolGroup();
                object v;
                if (o.TryGetValue("server", out v) && v != null) g.Server = Convert.ToString(v);
                if (o.TryGetValue("mode", out v) && v != null)
                    g.Mode = string.Equals(Convert.ToString(v), "all", StringComparison.OrdinalIgnoreCase)
                        ? RequiredToolMode.All : RequiredToolMode.AnyOf;

                if (o.TryGetValue("tools", out v))
                {
                    object[] ta = v as object[];
                    if (ta != null)
                        for (int j = 0; j < ta.Length; j++)
                        {
                            if (ta[j] == null) continue;
                            string s = Convert.ToString(ta[j]);
                            if (!string.IsNullOrEmpty(s) && !g.Tools.Contains(s)) g.Tools.Add(s);
                        }
                }
                if (o.TryGetValue("globs", out v))
                {
                    object[] ga = v as object[];
                    if (ga != null)
                        for (int j = 0; j < ga.Length; j++)
                        {
                            Dictionary<string, object> go = ga[j] as Dictionary<string, object>;
                            if (go == null) continue;
                            object pv, tv;
                            string pattern = go.TryGetValue("pattern", out pv) && pv != null ? Convert.ToString(pv) : null;
                            if (string.IsNullOrEmpty(pattern)) continue;
                            AgentMaxTier tier = go.TryGetValue("max_tier", out tv) && tv != null
                                ? ParseTier(Convert.ToString(tv)) : AgentMaxTier.Write;
                            g.Globs.Add(new RequiredToolGlob(pattern, tier));
                        }
                }

                if (!string.IsNullOrEmpty(g.Server)) result.Add(g);
            }
            return result;
        }
    }
}
