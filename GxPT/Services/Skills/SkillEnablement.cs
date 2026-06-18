using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Global (app-wide) skill enablement, persisted to its own JSON file beside settings.json - the
    // dedicated-file pattern of recent-workdirs.json / mcp.json (design S6/S10). Written only by the
    // `... global` slash commands; there is no settings-form surface. Combined with a conversation's
    // overrides by SkillResolve under the "most specific setting wins" ladder. XP / .NET 3.5 friendly.
    //
    //   { "feature_off": false, "skills": { "noisy-skill": false, "always-pirate": true } }
    //
    // feature_off  -> the global feature default (rung 4): all skills off everywhere unless a more
    //                 specific rule (a per-skill setting, or a per-conversation setting) says otherwise.
    // skills        -> per-skill global setting (rung 2), tri-state: present => force on/off; absent =>
    //                 inherit. Symmetric with the conversation layer so `/toggle-skill X on|off global` works.
    internal sealed class SkillEnablement
    {
        public const string FileName = "skills.json";

        // Tests redirect IO here; null means use the default %APPDATA%\GxPT path.
        internal static string FilePathOverride;

        private static readonly object _gate = new object();

        private bool _featureOff;
        private readonly Dictionary<string, bool> _skills;

        public SkillEnablement()
        {
            _skills = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        public bool FeatureOff
        {
            get { return _featureOff; }
            set { _featureOff = value; }
        }

        // The global per-skill setting (rung 2): true/false force, null = inherit.
        public bool? GetSkillOverride(string slug)
        {
            bool v;
            if (!string.IsNullOrEmpty(slug) && _skills.TryGetValue(slug, out v)) return v;
            return null;
        }

        // value null clears the global setting for the slug (back to inherit).
        public void SetSkillOverride(string slug, bool? value)
        {
            if (string.IsNullOrEmpty(slug)) return;
            if (value.HasValue) _skills[slug] = value.Value;
            else _skills.Remove(slug);
        }

        // Clears every per-skill global setting (used by `/reset-skills global`); leaves FeatureOff.
        public void ClearSkillOverrides()
        {
            _skills.Clear();
        }

        private static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(FilePathOverride)) return FilePathOverride;
                return Path.Combine(AppSettings.SettingsDirectory, FileName);
            }
        }

        public static SkillEnablement LoadGlobal()
        {
            lock (_gate) { return ReadLocked(); }
        }

        public void SaveGlobal()
        {
            lock (_gate) { WriteLocked(this); }
        }

        private static SkillEnablement ReadLocked()
        {
            SkillEnablement result = new SkillEnablement();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return result;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return result;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> obj = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (obj == null) return result;

                object featureOff;
                bool fo;
                if (obj.TryGetValue("feature_off", out featureOff) && TryToBool(featureOff, out fo))
                    result._featureOff = fo;

                // New schema: a { slug: bool } map. Convert each value defensively and skip a malformed
                // one (a non-bool JSON value), so one bad entry doesn't abort parsing the rest of the file.
                object skills;
                if (obj.TryGetValue("skills", out skills))
                {
                    Dictionary<string, object> map = skills as Dictionary<string, object>;
                    if (map != null)
                    {
                        foreach (KeyValuePair<string, object> kv in map)
                        {
                            if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                            bool v;
                            if (!TryToBool(kv.Value, out v)) continue;
                            result._skills[kv.Key] = v;
                        }
                    }
                }

                // Backward-compat: an older "disabled": [..] array maps to force-off entries.
                object disabled;
                if (obj.TryGetValue("disabled", out disabled))
                {
                    object[] arr = disabled as object[];
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (arr[i] == null) continue;
                            string slug = Convert.ToString(arr[i]);
                            if (!string.IsNullOrEmpty(slug) && !result._skills.ContainsKey(slug))
                                result._skills[slug] = false;
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        // Coerce a deserialized JSON value to bool without throwing: accepts a real bool, or anything
        // Convert.ToBoolean handles ("true"/"false", numbers); returns false (skip) for anything else.
        private static bool TryToBool(object value, out bool result)
        {
            result = false;
            if (value == null) return false;
            if (value is bool) { result = (bool)value; return true; }
            try { result = Convert.ToBoolean(value); return true; }
            catch { return false; }
        }

        private static void WriteLocked(SkillEnablement e)
        {
            try
            {
                Dictionary<string, object> obj = new Dictionary<string, object>();
                obj["feature_off"] = e._featureOff;
                Dictionary<string, object> skills = new Dictionary<string, object>();
                foreach (KeyValuePair<string, bool> kv in e._skills) skills[kv.Key] = kv.Value;
                obj["skills"] = skills;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(obj);

                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                FileSafe.WriteAllTextAtomic(path, json, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
