using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Typed view of a single OpenRouter model record. Only the fields GxPT currently consumes
    // are promoted to named properties; everything else is accessible via Raw for future use.
    // Raw is null when the entry was loaded from the persisted cache (models.json) rather than
    // a live fetch — consumers that need pricing data must call ForceRefresh first.
    internal sealed class ModelInfo
    {
        public string Id { get; private set; }
        public int ContextLength { get; private set; }
        // architecture.input_modalities contains "image"
        public bool SupportsImageInput { get; private set; }
        // architecture.input_modalities contains "file" (native PDF/document)
        public bool SupportsFileInput { get; private set; }
        // Full model record from a live fetch; null when loaded from the persisted cache.
        public JObject Raw { get; private set; }

        public ModelInfo(string id, int contextLength, bool supportsImageInput, bool supportsFileInput)
        {
            Id = id ?? string.Empty;
            ContextLength = contextLength;
            SupportsImageInput = supportsImageInput;
            SupportsFileInput = supportsFileInput;
            Raw = null;
        }

        public ModelInfo(string id, int contextLength, bool supportsImageInput, bool supportsFileInput, JObject raw)
        {
            Id = id ?? string.Empty;
            ContextLength = contextLength;
            SupportsImageInput = supportsImageInput;
            SupportsFileInput = supportsFileInput;
            Raw = raw;
        }
    }

    // Each OpenRouter model's context window size and capability flags, for the status bar's
    // context meter and multimodal attach gating. OpenRouter has no single-model endpoint, so
    // the full GET /api/v1/models list (public, no auth) is fetched at most once per
    // RefreshInterval when the app opens - always on a background thread, never blocking the UI
    // - and persisted under %AppData%\GxPT as two files:
    //   model-context.txt  — id<TAB>tokens, one per line (legacy; drives context meter)
    //   models.json        — compact JSON array with id, ctx, img, file fields (drives gating)
    // Until the first fetch completes (fresh install, or a model not yet listed), TryGetContextLength
    // and TryGetModelInfo miss and the callers fall back to their conservative defaults.
    internal static class ModelCatalogService
    {
        // Raised (on the fetch worker thread) after a fetch actually changed the in-memory map;
        // the UI marshals to its own thread and repaints the context meter.
        public static event Action CatalogUpdated;

        private const string CatalogFileName = "model-context.txt";
        private const string InfoCatalogFileName = "models.json";
        private const string ModelsUrl = "https://openrouter.ai/api/v1/models";
        // Once daily: context windows change rarely (new model releases), and the Settings
        // dialog's Update Model Info button covers the impatient case.
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

        // model id -> context tokens, loaded lazily from the catalog file and replaced wholesale
        // by a successful fetch. Guarded by _gate (lookups ride the UI thread, fetches don't).
        private static readonly object _gate = new object();
        private static Dictionary<string, int> _map;
        private static Dictionary<string, ModelInfo> _infoMap;
        private static int _refreshing; // 1 while a fetch thread is running (Interlocked)

        public static string CatalogPath
        {
            get { return Path.Combine(AppSettings.SettingsDirectory, CatalogFileName); }
        }

        public static string InfoCatalogPath
        {
            get { return Path.Combine(AppSettings.SettingsDirectory, InfoCatalogFileName); }
        }

        // The context window for a model id as configured by the user: tries the id verbatim,
        // then without the "~" alias marker, then without an OpenRouter ":variant" suffix (e.g.
        // ":nitro" routing variants that aren't separate catalog entries; ":free" usually is one
        // and matches verbatim). False when the catalog simply doesn't know the model yet.
        public static bool TryGetContextLength(string model, out int contextLength)
        {
            contextLength = 0;
            if (string.IsNullOrEmpty(model)) return false;
            string id = model.Trim();
            if (id.Length == 0) return false;

            lock (_gate)
            {
                EnsureLoadedLocked();
                if (_map.TryGetValue(id, out contextLength)) return true;
                if (id.StartsWith("~", StringComparison.Ordinal))
                {
                    id = id.Substring(1);
                    if (_map.TryGetValue(id, out contextLength)) return true;
                }
                int colon = id.LastIndexOf(':');
                if (colon > 0 && _map.TryGetValue(id.Substring(0, colon), out contextLength)) return true;
            }
            contextLength = 0;
            return false;
        }

        // Full model info for a model id. Same lookup ladder as TryGetContextLength.
        // Returns false (and null info) when the model is absent from the cache — callers should
        // treat this as "capability unknown" and fall back conservatively (§6.4).
        public static bool TryGetModelInfo(string model, out ModelInfo info)
        {
            info = null;
            if (string.IsNullOrEmpty(model)) return false;
            string id = model.Trim();
            if (id.Length == 0) return false;

            lock (_gate)
            {
                EnsureLoadedLocked();
                if (_infoMap.TryGetValue(id, out info)) return true;
                if (id.StartsWith("~", StringComparison.Ordinal))
                {
                    id = id.Substring(1);
                    if (_infoMap.TryGetValue(id, out info)) return true;
                }
                int colon = id.LastIndexOf(':');
                if (colon > 0 && _infoMap.TryGetValue(id.Substring(0, colon), out info)) return true;
            }
            info = null;
            return false;
        }

        // App-open refresh: fetch only when the on-disk catalog is missing or older than
        // RefreshInterval. Returns immediately; the fetch (if any) runs on a background thread.
        public static void RefreshIfDue()
        {
            StartRefresh(false, null);
        }

        // Settings' Update Model Info button: fetch now regardless of age. onDone(success) is
        // invoked on the worker thread when the fetch finishes (immediately with false if a
        // refresh is already running, so the button can re-enable).
        public static void ForceRefresh(Action<bool> onDone)
        {
            StartRefresh(true, onDone);
        }

        private static void StartRefresh(bool force, Action<bool> onDone)
        {
            if (!force && !IsStale()) return;
            // One fetch at a time; a second request while one is in flight just reports failure
            // (the running fetch's result will arrive moments later anyway).
            if (System.Threading.Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                if (onDone != null) { try { onDone(false); } catch { } }
                return;
            }
            var worker = new System.Threading.Thread(delegate()
            {
                bool ok = false;
                try { ok = FetchAndStore(); }
                catch (Exception ex)
                {
                    try { Logger.Log("Models", "catalog refresh failed: " + ex.Message); }
                    catch { }
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _refreshing, 0);
                    if (onDone != null) { try { onDone(ok); } catch { } }
                }
            });
            worker.IsBackground = true; // never holds the app open
            worker.Name = "ModelCatalogRefresh";
            worker.Start();
        }

        private static bool IsStale()
        {
            try
            {
                string path = CatalogPath;
                if (!File.Exists(path)) return true;
                // If models.json is absent (pre-multimodal install), re-fetch to populate it.
                if (!File.Exists(InfoCatalogPath)) return true;
                return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)) >= RefreshInterval;
            }
            catch { return true; }
        }

        // Must be called while holding _gate.
        private static void EnsureLoadedLocked()
        {
            if (_map == null)
            {
                string text = null;
                try
                {
                    string path = CatalogPath;
                    if (File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8);
                }
                catch { }
                _map = ParseCatalogFile(text);
            }
            if (_infoMap == null)
            {
                string json = null;
                try
                {
                    string path = InfoCatalogPath;
                    if (File.Exists(path)) json = File.ReadAllText(path, Encoding.UTF8);
                }
                catch { }
                _infoMap = ParseInfoCatalogFile(json ?? string.Empty);
            }
        }

        // Blocking fetch + parse + persist; runs on the worker thread only. A failed or empty
        // fetch keeps the previous catalog (stale context sizes beat no context sizes).
        private static bool FetchAndStore()
        {
            string body = HttpGetModels();
            if (string.IsNullOrEmpty(body))
            {
                try { Logger.Log("Models", "catalog fetch returned no body"); }
                catch { }
                return false;
            }
            Dictionary<string, int> fetched = ParseModelsJson(body);
            if (fetched == null || fetched.Count == 0)
            {
                try { Logger.Log("Models", "catalog fetch parsed no models (len=" + body.Length + ")"); }
                catch { }
                return false;
            }
            Dictionary<string, ModelInfo> fetchedInfo = ParseModelsJsonFull(body);

            try { FileSafe.WriteAllTextAtomic(CatalogPath, FormatCatalogFile(fetched), new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                // Keep the in-memory update even if the disk write failed; next launch refetches.
                try { Logger.Log("Models", "catalog write failed: " + ex.Message); }
                catch { }
            }

            if (fetchedInfo != null && fetchedInfo.Count > 0)
            {
                try { FileSafe.WriteAllTextAtomic(InfoCatalogPath, FormatInfoCatalogFile(fetchedInfo), new UTF8Encoding(false)); }
                catch (Exception ex)
                {
                    try { Logger.Log("Models", "info catalog write failed: " + ex.Message); }
                    catch { }
                }
            }

            lock (_gate)
            {
                _map = fetched;
                _infoMap = fetchedInfo ?? new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
            }
            try { Logger.Log("Models", "catalog updated: " + fetched.Count + " models"); }
            catch { }

            Action h = CatalogUpdated;
            if (h != null) { try { h(); } catch { } }
            return true;
        }

        // GET /api/v1/models via the bundled curl (no auth header needed - the endpoint is
        // public, so this works before an API key is configured). --max-time bounds a hung
        // connection so the refresh latch can't stay held forever.
        private static string HttpGetModels()
        {
            string curlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib\\curl.exe");
            if (!File.Exists(curlPath)) return null;

            var psi = new ProcessStartInfo
            {
                FileName = curlPath,
                Arguments = "-sS --fail-with-body --max-time 60 " + ModelsUrl,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                var utf8 = new UTF8Encoding(false, false);
                string output;
                using (var outReader = new StreamReader(p.StandardOutput.BaseStream, utf8, false))
                    output = outReader.ReadToEnd();
                try { p.StandardError.ReadToEnd(); }
                catch { }
                p.WaitForExit();
                int exitCode = -1;
                try { exitCode = p.ExitCode; }
                catch { }
                if (exitCode != 0)
                {
                    try { Logger.Log("Models", "catalog fetch curl exit=" + exitCode); }
                    catch { }
                    return null;
                }
                return output;
            }
        }

        // { "data": [ { "id": "anthropic/...", "context_length": 200000, ... }, ... ] }
        // -> id -> context_length. Entries without a positive context_length are skipped (they
        // can't drive a meter). Null when the payload isn't the expected shape.
        internal static Dictionary<string, int> ParseModelsJson(string json)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch { return null; }
            JArray data = root["data"] as JArray;
            if (data == null) return null;

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken item in data)
            {
                if (item == null || item.Type != JTokenType.Object) continue;
                JToken idTok = item["id"];
                if (idTok == null || idTok.Type != JTokenType.String) continue;
                string id = ((string)idTok ?? string.Empty).Trim();
                if (id.Length == 0) continue;
                JToken len = item["context_length"];
                if (len == null || (len.Type != JTokenType.Integer && len.Type != JTokenType.Float)) continue;
                int ctx;
                try { ctx = (int)len; }
                catch { continue; }
                if (ctx > 0) map[id] = ctx;
            }
            return map;
        }

        // Like ParseModelsJson but also extracts architecture.input_modalities to populate
        // SupportsImageInput / SupportsFileInput. Raw is set to the full model JObject so
        // pricing and other fields are available during the session.
        internal static Dictionary<string, ModelInfo> ParseModelsJsonFull(string json)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch { return null; }
            JArray data = root["data"] as JArray;
            if (data == null) return null;

            var map = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken item in data)
            {
                if (item == null || item.Type != JTokenType.Object) continue;
                JObject obj = (JObject)item;
                JToken idTok = obj["id"];
                if (idTok == null || idTok.Type != JTokenType.String) continue;
                string id = ((string)idTok ?? string.Empty).Trim();
                if (id.Length == 0) continue;
                JToken len = obj["context_length"];
                if (len == null || (len.Type != JTokenType.Integer && len.Type != JTokenType.Float)) continue;
                int ctx;
                try { ctx = (int)len; }
                catch { continue; }
                if (ctx <= 0) continue;

                bool img = false, file = false;
                JObject arch = obj["architecture"] as JObject;
                if (arch != null)
                {
                    JArray mods = arch["input_modalities"] as JArray;
                    if (mods != null)
                    {
                        foreach (JToken mod in mods)
                        {
                            if (mod == null || mod.Type != JTokenType.String) continue;
                            string m = (string)mod;
                            if (m == null) continue;
                            m = m.Trim().ToLowerInvariant();
                            if (m == "image") img = true;
                            else if (m == "file") file = true;
                        }
                    }
                }
                map[id] = new ModelInfo(id, ctx, img, file, obj);
            }
            return map;
        }

        // One "id<TAB>tokens" line per model, sorted by id so successive fetches diff cleanly.
        internal static string FormatCatalogFile(Dictionary<string, int> map)
        {
            var ids = new List<string>(map.Keys);
            ids.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string id in ids)
                sb.Append(id).Append('\t').Append(map[id].ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        // Tolerant inverse of FormatCatalogFile: blank lines, "#" comments, and lines that don't
        // parse are skipped, so a hand-edited file degrades to missing meters, never a crash.
        internal static Dictionary<string, int> ParseCatalogFile(string text)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            string[] lines = text.Split(new char[] { '\n', '\r' });
            foreach (string raw in lines)
            {
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0 || tab >= line.Length - 1) continue;
                string id = line.Substring(0, tab).Trim();
                int ctx;
                if (id.Length == 0) continue;
                if (!int.TryParse(line.Substring(tab + 1).Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out ctx)) continue;
                if (ctx > 0) map[id] = ctx;
            }
            return map;
        }

        // Compact JSON array: [{"id":"...","ctx":N,"img":bool,"file":bool}, ...], sorted by id.
        internal static string FormatInfoCatalogFile(Dictionary<string, ModelInfo> map)
        {
            var arr = new JArray();
            var ids = new List<string>(map.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                ModelInfo info = map[id];
                var obj = new JObject();
                obj["id"] = id;
                obj["ctx"] = info.ContextLength;
                obj["img"] = info.SupportsImageInput;
                obj["file"] = info.SupportsFileInput;
                arr.Add(obj);
            }
            return arr.ToString(Formatting.None);
        }

        // Tolerant inverse of FormatInfoCatalogFile. Returns an empty (non-null) map for any
        // parse failure so callers can safely proceed with "unknown → conservative" behavior.
        internal static Dictionary<string, ModelInfo> ParseInfoCatalogFile(string json)
        {
            var map = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return map;
            JArray arr;
            try { arr = JArray.Parse(json); }
            catch { return map; }
            foreach (JToken tok in arr)
            {
                JObject obj = tok as JObject;
                if (obj == null) continue;
                JToken idTok = obj["id"];
                if (idTok == null || idTok.Type != JTokenType.String) continue;
                string id = ((string)idTok ?? string.Empty).Trim();
                if (id.Length == 0) continue;
                int ctx = 0;
                JToken ctxTok = obj["ctx"];
                if (ctxTok != null && (ctxTok.Type == JTokenType.Integer || ctxTok.Type == JTokenType.Float))
                {
                    try { ctx = (int)ctxTok; } catch { }
                }
                bool img = false, file = false;
                JToken imgTok = obj["img"];
                if (imgTok != null && imgTok.Type == JTokenType.Boolean) img = (bool)imgTok;
                JToken fileTok = obj["file"];
                if (fileTok != null && fileTok.Type == JTokenType.Boolean) file = (bool)fileTok;
                // Raw is null: this entry was loaded from the persisted cache, not a live fetch.
                map[id] = new ModelInfo(id, ctx, img, file, null);
            }
            return map;
        }

        // Test seam: replace the in-memory context-length map without touching disk.
        internal static void SetMapForTests(Dictionary<string, int> map)
        {
            lock (_gate)
            {
                _map = (map != null) ? new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase) : null;
            }
        }

        // Test seam: replace the in-memory model-info map without touching disk.
        internal static void SetModelInfoForTests(Dictionary<string, ModelInfo> map)
        {
            lock (_gate)
            {
                _infoMap = (map != null)
                    ? new Dictionary<string, ModelInfo>(map, StringComparer.OrdinalIgnoreCase)
                    : null;
            }
        }
    }
}
