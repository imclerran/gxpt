using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GxPT
{
    // Reads/writes the installed-plugin manifests under a plugins root (PluginRoots.UserRoot in the app; a
    // temp folder in tests). Each plugin gets its own folder <root>/<slug>/ holding plugin.json and, when
    // disabled, a "disabled/" holding area for its moved-aside skill folders and agent files. The slug is
    // the plugin name normalized like a skill/agent handle, so one identity == one folder. Pure path logic
    // plus JSON IO so it stays net48-testable. XP / .NET 3.5 friendly.
    internal sealed class PluginRegistry
    {
        public const string ManifestFileName = "plugin.json";
        public const string DisabledDirName = "disabled";

        private readonly string _root;

        public PluginRegistry(string root)
        {
            _root = root;
        }

        public string Root { get { return _root; } }

        // The plugin's own folder, named by the slugified plugin name (so "My Bundle" and "my-bundle" map
        // to the same identity deliberately). Null when the root or name yields no usable slug.
        public string PluginDir(string name)
        {
            string slug = SkillSlug.Make(name);
            if (string.IsNullOrEmpty(_root) || string.IsNullOrEmpty(slug)) return null;
            return Path.Combine(_root, slug);
        }

        public string ManifestPath(string name)
        {
            string dir = PluginDir(name);
            return dir == null ? null : Path.Combine(dir, ManifestFileName);
        }

        // Where a disabled plugin's files are parked: disabled/skills/<slug>/ and disabled/agents/<slug>.md.
        public string DisabledDir(string name)
        {
            string dir = PluginDir(name);
            return dir == null ? null : Path.Combine(dir, DisabledDirName);
        }

        public bool Exists(string name)
        {
            string p = ManifestPath(name);
            return !string.IsNullOrEmpty(p) && File.Exists(p);
        }

        // The installed manifest, or null when this plugin isn't installed (or its manifest is unreadable).
        public PluginManifest Load(string name)
        {
            string p = ManifestPath(name);
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
            try { return PluginManifest.Parse(File.ReadAllText(p, Encoding.UTF8)); }
            catch { return null; }
        }

        public void Save(PluginManifest manifest)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.Name)) return;
            string dir = PluginDir(manifest.Name);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            FileSafe.WriteAllTextAtomic(Path.Combine(dir, ManifestFileName),
                manifest.ToJson(), new UTF8Encoding(false));
        }

        // Removes the plugin's registry folder entirely (manifest + any disabled holding area). The member
        // skill/agent files in the user roots are the caller's responsibility (UninstallPlugin handles them).
        public void RemoveDir(string name)
        {
            string dir = PluginDir(name);
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        // Every installed plugin's manifest, slug-folder-sorted. Folders without a readable plugin.json are
        // skipped, so a stray directory under the root never breaks enumeration.
        public IList<PluginManifest> ListInstalled()
        {
            List<PluginManifest> result = new List<PluginManifest>();
            if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root)) return result;

            string[] dirs;
            try { dirs = Directory.GetDirectories(_root); }
            catch { return result; }
            Array.Sort(dirs, StringComparer.Ordinal);

            for (int i = 0; i < dirs.Length; i++)
            {
                string mp = Path.Combine(dirs[i], ManifestFileName);
                if (!File.Exists(mp)) continue;
                try
                {
                    PluginManifest m = PluginManifest.Parse(File.ReadAllText(mp, Encoding.UTF8));
                    if (m != null && !string.IsNullOrEmpty(m.Name)) result.Add(m);
                }
                catch { }
            }
            return result;
        }
    }
}
