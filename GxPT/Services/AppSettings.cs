using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // JSON-based app settings (XP/.NET 3.5 compatible via JavaScriptSerializer)
    internal static class AppSettings
    {
        public static string SettingsDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GxPT");
            }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(SettingsDirectory, "settings.json"); }
        }

        // In-memory cache of the settings file, guarded by _gate. The cache is reloaded
        // automatically whenever the file's last-write timestamp changes on disk, so edits
        // made outside AppSettings (e.g. the Settings dialog's JSON editor writes the file
        // directly) are picked up without stale reads. Writes hold the lock across the whole
        // read-modify-write, eliminating the clobber race between the UI and naming threads.
        private static readonly object _gate = new object();
        private static Dictionary<string, object> _cache;
        private static DateTime _cacheStampUtc;
        private static bool _loaded;

        // Force the next access to reload from disk. Cheap, explicit invalidation hook
        // (e.g. after the Settings dialog closes) in addition to the timestamp check.
        public static void Reload()
        {
            lock (_gate)
            {
                _loaded = false;
                _cache = null;
                _cacheStampUtc = DateTime.MinValue;
            }
        }

        // Fill any settings.json key absent on disk with its declared default (SettingsSchema), then
        // persist - so the on-disk file is always complete and behavior never depends on whether a key
        // happens to be present (issue #164). Idempotent: writes only when something was actually added,
        // so it's cheap to call on every startup. Creating the file and its directory is handled by the
        // persist path (FileSafe). Call once early (Program.Main) before any component reads settings.
        public static void EnsureSeeded()
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                if (_cache == null) _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                bool changed = false;
                var defaults = SettingsSchema.BuildDefaults();
                foreach (var kv in defaults)
                {
                    if (_cache.ContainsKey(kv.Key)) continue;
                    object value = kv.Value;
                    // provider_zdr is special: when absent, derive its seed from the legacy
                    // data-collection pref so an older file migrates correctly instead of being pinned to
                    // the static default. (GetGlobalZdrDefault re-enters the lock, which is fine.)
                    if (string.Equals(kv.Key, "provider_zdr", StringComparison.OrdinalIgnoreCase))
                        value = GetGlobalZdrDefault();
                    _cache[kv.Key] = value;
                    changed = true;
                }

                if (changed) PersistLocked();
            }
        }

        // Ensure _cache reflects the current on-disk file. Must be called while holding _gate.
        private static void EnsureLoadedLocked()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    if (!_loaded || _cache == null)
                    {
                        _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        _cacheStampUtc = DateTime.MinValue;
                        _loaded = true;
                    }
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (_loaded && _cache != null && stamp == _cacheStampUtc) return; // cache is current
                _cache = ReadFromDisk(path);
                _cacheStampUtc = stamp;
                _loaded = true;
            }
            catch
            {
                if (_cache == null) _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _loaded = true;
            }
        }

        private static Dictionary<string, object> ReadFromDisk(string path)
        {
            var json = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return json;
                var ser = new JavaScriptSerializer();
                var obj = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (obj != null)
                {
                    foreach (var kv in obj) json[kv.Key] = kv.Value;
                }
            }
            catch { }
            return json;
        }

        // Serialize the cache to disk. Must be called while holding _gate.
        private static void PersistLocked()
        {
            try
            {
                if (_cache == null) _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var ser = new JavaScriptSerializer();
                string text = ser.Serialize(_cache);
                // Crash-safe write so a failure mid-save can't corrupt settings.json.
                FileSafe.WriteAllTextAtomic(SettingsPath, text, Encoding.UTF8);
                // Refresh the stamp so our own write doesn't trigger a needless reload next read.
                try { _cacheStampUtc = File.GetLastWriteTimeUtc(SettingsPath); }
                catch { }
            }
            catch { }
        }

        public static string GetString(string key)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                object val;
                if (_cache.TryGetValue(key, out val) && val != null)
                    return Convert.ToString(val);
                return null;
            }
        }

        public static void SetString(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                EnsureLoadedLocked();
                _cache[key] = value ?? string.Empty;
                PersistLocked();
            }
        }

        // Store a numeric (double) value, preserving numeric type in JSON
        public static void SetDouble(string key, double value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                EnsureLoadedLocked();
                _cache[key] = value;
                PersistLocked();
            }
        }

        // Store an integer value (convenience)
        public static void SetInt(string key, int value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                EnsureLoadedLocked();
                _cache[key] = value;
                PersistLocked();
            }
        }

        // Store a boolean value (preserved as a JSON bool, read back by GetBool)
        public static void SetBool(string key, bool value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                EnsureLoadedLocked();
                _cache[key] = value;
                PersistLocked();
            }
        }

        public static List<string> GetList(string key)
        {
            var result = new List<string>();
            lock (_gate)
            {
                EnsureLoadedLocked();
                object val;
                if (_cache.TryGetValue(key, out val) && val != null)
                {
                    try
                    {
                    var arr = val as object[];
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            if (item != null) result.Add(Convert.ToString(item));
                        }
                    }
                    else
                    {
                        var list = val as System.Collections.ArrayList;
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                if (item != null) result.Add(Convert.ToString(item));
                            }
                        }
                    }
                    }
                    catch { }
                }
            }
            return result;
        }

        // Store a list of strings (round-trips through GetList). Stored as object[] so the
        // JavaScriptSerializer emits a JSON array.
        public static void SetList(string key, IList<string> values)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                EnsureLoadedLocked();
                if (values == null)
                {
                    _cache[key] = new object[0];
                }
                else
                {
                    var arr = new object[values.Count];
                    for (int i = 0; i < values.Count; i++) arr[i] = values[i];
                    _cache[key] = arr;
                }
                PersistLocked();
            }
        }

        public static double GetDouble(string key, double defaultValue)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                object val;
                if (!_cache.TryGetValue(key, out val) || val == null) return defaultValue;
                try
                {
                    if (val is double) return (double)val;
                    if (val is float) return (double)(float)val;
                    if (val is decimal) return (double)(decimal)val;
                    if (val is int) return (int)val;
                    if (val is long) return (long)val;
                    string s = Convert.ToString(val);
                    if (string.IsNullOrEmpty(s)) return defaultValue;
                    double d;
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                        return d;
                    if (double.TryParse(s, out d)) return d;
                }
                catch { }
                return defaultValue;
            }
        }

        // Overload without explicit default for C# 3.0 compatibility
        public static double GetDouble(string key)
        {
            return GetDouble(key, 0);
        }

        // Overload that sources the default from the schema (SettingsSchema) - the single source of
        // truth - so call sites don't each pass a literal that could drift from the seeded value.
        public static bool GetBool(string key)
        {
            return GetBool(key, SettingsSchema.BoolDefault(key));
        }

        // Read boolean values (accepts true/false, 1/0, yes/no, on/off, strings or numbers)
        public static bool GetBool(string key, bool defaultValue)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                object val;
                if (!_cache.TryGetValue(key, out val) || val == null) return defaultValue;
                try
                {
                    if (val is bool) return (bool)val;
                    if (val is string)
                    {
                        string s = Convert.ToString(val);
                        if (string.IsNullOrEmpty(s)) return defaultValue;
                        s = s.Trim();
                        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                        if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)) return true;
                        if (string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)) return false;
                        if (string.Equals(s, "on", StringComparison.OrdinalIgnoreCase)) return true;
                        if (string.Equals(s, "off", StringComparison.OrdinalIgnoreCase)) return false;
                        int i;
                        if (int.TryParse(s, out i)) return i != 0;
                    }
                    if (val is int) return ((int)val) != 0;
                    if (val is long) return ((long)val) != 0L;
                    if (val is double) return ((double)val) != 0.0;
                    if (val is float) return ((float)val) != 0f;
                    if (val is decimal) return ((decimal)val) != 0m;
                }
                catch { }
                return defaultValue;
            }
        }

        // True when the settings file actually contains this key (vs. relying on a default).
        public static bool HasKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _cache.ContainsKey(key);
            }
        }

        // The effective global ZDR default for new conversations. The ZDR setting (provider_zdr)
        // replaced the older data-collection setting; when a user's settings predate it, derive the
        // default from their old preference: data_collection "deny" (false) means they already wanted
        // their data not retained, so default ZDR on.
        public static bool GetGlobalZdrDefault()
        {
            if (HasKey("provider_zdr")) return GetBool("provider_zdr", false);
            return !GetBool("provider_data_collection", true);
        }
    }
}
