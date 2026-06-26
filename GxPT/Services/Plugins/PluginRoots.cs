using System;
using System.IO;

namespace GxPT
{
    // Resolves the plugin registry root (%AppData%/GxPT/plugins) - the per-machine folder that records
    // which skills/agents each installed plugin owns. Parallel to SkillRoots/AgentRoots in spirit, but a
    // plugin is a bookkeeping unit, not a discovery source: a plugin's actual skill folders and agent files
    // live in the ordinary user skills/agents roots (so the catalogs find them unchanged); only the
    // membership manifests - and a disabled plugin's moved-aside files - live here. XP / .NET 3.5 friendly.
    internal static class PluginRoots
    {
        public const string PluginsDirName = "plugins";

        // User-global plugins live under %AppData%/GxPT/plugins - one set per Windows user. Returns null if
        // %AppData% can't be resolved.
        public static string UserRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return null;
            return Path.Combine(Path.Combine(appData, "GxPT"), PluginsDirName);
        }
    }
}
