using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Identifies a plugin and lists its member skills/agents. The same JSON shape is used in two places:
    //   - inside a .gxpl archive (plugin.json at the root), describing what the bundle ships;
    //   - in the installed registry (%AppData%/GxPT/plugins/<slug>/plugin.json), the source of truth for
    //     which skills/agents belong to the plugin so it can be upgraded, disabled, or uninstalled as one
    //     unit.
    // Membership is tracked HERE, never in the skills'/agents' own frontmatter (which stays untouched, by
    // design). Interdependency between members is just prose inside their bodies - nothing structural.
    // Parsed leniently with JavaScriptSerializer (the repo's one JSON lib), mirroring SkillEnablement.
    // XP / .NET 3.5 friendly.
    //
    //   { "name": "my-bundle", "version": "1.0.0", "description": "...",
    //     "enabled": true, "skills": ["a","b"], "agents": ["helper"] }
    //
    // enabled -> only meaningful in the registry copy (a disabled plugin's files are moved aside out of the
    //            active roots); an exported archive's manifest is always enabled.
    internal sealed class PluginManifest
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public List<string> Skills { get; private set; }
        public List<string> Agents { get; private set; }

        // The MCP tools the plugin's agents require (see RequiredToolsDetect/Check). Empty when none were
        // declared; an older reader simply ignores the key.
        public List<RequiredToolGroup> RequiredTools { get; private set; }

        public PluginManifest()
        {
            Enabled = true;
            Skills = new List<string>();
            Agents = new List<string>();
            RequiredTools = new List<RequiredToolGroup>();
        }

        public string ToJson()
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            obj["name"] = Name ?? string.Empty;
            if (!string.IsNullOrEmpty(Version)) obj["version"] = Version;
            if (!string.IsNullOrEmpty(Description)) obj["description"] = Description;
            obj["enabled"] = Enabled;
            obj["skills"] = Skills.ToArray();
            obj["agents"] = Agents.ToArray();
            if (RequiredTools.Count > 0) obj["requiredTools"] = RequiredTool.ToJsonValue(RequiredTools);

            JavaScriptSerializer ser = new JavaScriptSerializer();
            return ser.Serialize(obj);
        }

        // Lenient parse: a malformed value for one key never aborts the rest (one bad entry shouldn't make
        // an installed plugin un-manageable). An absent "enabled" defaults to true.
        public static PluginManifest Parse(string text)
        {
            PluginManifest m = new PluginManifest();
            if (string.IsNullOrEmpty(text)) return m;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> obj = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (obj == null) return m;

                object v;
                if (obj.TryGetValue("name", out v) && v != null) m.Name = Convert.ToString(v);
                if (obj.TryGetValue("version", out v) && v != null) m.Version = Convert.ToString(v);
                if (obj.TryGetValue("description", out v) && v != null) m.Description = Convert.ToString(v);
                if (obj.TryGetValue("enabled", out v) && v != null)
                {
                    try { m.Enabled = Convert.ToBoolean(v); }
                    catch { m.Enabled = true; }
                }
                ReadStringArray(obj, "skills", m.Skills);
                ReadStringArray(obj, "agents", m.Agents);
                if (obj.TryGetValue("requiredTools", out v) && v != null)
                {
                    List<RequiredToolGroup> rt = RequiredTool.FromJsonValue(v);
                    if (rt != null) { m.RequiredTools.Clear(); m.RequiredTools.AddRange(rt); }
                }
            }
            catch { }
            return m;
        }

        private static void ReadStringArray(Dictionary<string, object> obj, string key, List<string> into)
        {
            object v;
            if (!obj.TryGetValue(key, out v)) return;
            object[] arr = v as object[];
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] == null) continue;
                string s = Convert.ToString(arr[i]);
                if (!string.IsNullOrEmpty(s) && !into.Contains(s)) into.Add(s);
            }
        }
    }
}
