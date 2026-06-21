using System;
using System.Collections.Generic;

namespace GxPT
{
    // Single source of truth for every global settings.json key and its default value (issue #164).
    //
    // Defaults used to live in three places that drifted apart: SettingsForm.BuildDefaultJson (the seed
    // file), SettingsForm.BuildDefaultSettings (the typed fallback), and each runtime GetBool(key,
    // <literal>) call site. They could - and did - disagree, so behavior depended on whether
    // settings.json happened to contain a key (e.g. files/command seeded ON in the file but read with a
    // default of OFF, so a user who never opened Settings got them off). Now every default is declared
    // here once:
    //   - AppSettings.EnsureSeeded fills any absent key from this table, so the on-disk file is always
    //     complete and the missing-key upgrade path matches a fresh seed.
    //   - AppSettings.GetBool(key) resolves its default from here (no per-call-site literal to drift).
    //   - The settings form derives both its seed JSON and its typed default from BuildDefaults().
    //
    // Pure / WinForms-free so it can be linked into the headless test project alongside AppSettings.
    internal static class SettingsSchema
    {
        // Resolves the default chat font size. The WinForms layer points this at the live
        // ChatTranscriptControl font during startup (Program.Main); the pure fallback used by tests and
        // any headless path is 9.
        public static Func<double> DefaultFontSizeProvider = delegate { return 9.0; };

        // Every key that belongs in a complete settings.json, mapped to its default value. The value's
        // CLR type is the type the key is stored as (bool / int / double / string / string[]).
        //
        // Note: provider_data_collection (legacy) is intentionally absent - it exists only so older files
        // can migrate to provider_zdr (see AppSettings.GetGlobalZdrDefault) and is never seeded anew.
        public static Dictionary<string, object> BuildDefaults()
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // --- Core ---
            d["openrouter_api_key"] = "";
            // Models / default model / acknowledged-recommendations fingerprint come from the shared
            // catalog so this seed can't drift from the combo's fresh-install fallback.
            d["models"] = (string[])ModelDefaults.Models.Clone();
            d["default_model"] = ModelDefaults.DefaultModel;
            d["recommended_hash_seen"] = ModelDefaults.RecommendedHash();

            // --- Appearance ---
            d["theme"] = "light";
            d["color_theme"] = "blue";
            d["font_size"] = ResolveFontSize();
            d["transcript_max_width"] = 1000;
            d["message_max_width"] = 90; // percent (50-100), stored under the legacy key name

            // --- Behavior ---
            d["enable_logging"] = false;
            d["statusbar_visible"] = true;
            // Sub-agents: on by default now that a first-party agent suite ships with the app
            // (mirrors AgentEnablement.GlobalDefault).
            d["agents_enabled"] = true;
            // Global zero-data-retention default for new conversations. EnsureSeeded overrides this seed
            // with the migrated value when an older file only carries provider_data_collection.
            d["provider_zdr"] = false;

            // --- First-party MCP servers ---
            // The credential-free servers default ON so they work out of the box. git/msbuild are still
            // gated at launch on the underlying tool being installed; files/command connect once a
            // working folder (or scratch dir) is set. The credential-gated servers (web/github) default
            // OFF because they can't run without a key/PAT.
            d["mcp_files_enabled"] = true;
            d["mcp_command_enabled"] = true;
            d["mcp_command_scratch_enabled"] = false;
            d["mcp_git_enabled"] = true;
            d["mcp_msbuild_enabled"] = true;
            d["mcp_web_enabled"] = false;
            d["mcp_github_enabled"] = false;
            d["mcp_websearch_key"] = "";
            d["mcp_github_pat"] = "";
            // Persistent project memory is NOT strictly an MCP server toggle; off by default, with a
            // user-configurable soft index cap (lines).
            d["mcp_memory_enabled"] = false;
            d["mcp_memory_max_lines"] = 40;

            return d;
        }

        private static double ResolveFontSize()
        {
            try
            {
                Func<double> f = DefaultFontSizeProvider;
                return f != null ? f() : 9.0;
            }
            catch { return 9.0; }
        }

        // Typed default lookups - the single source the runtime reads through.
        public static bool BoolDefault(string key)
        {
            object v;
            if (BuildDefaults().TryGetValue(key, out v) && v is bool) return (bool)v;
            return false;
        }

        public static double DoubleDefault(string key)
        {
            object v;
            if (BuildDefaults().TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v); }
                catch { }
            }
            return 0;
        }

        public static string StringDefault(string key)
        {
            object v;
            if (BuildDefaults().TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return null;
        }
    }
}
