using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GxPT
{
    // Discovers sub-agents under one or more roots and exposes them by slug. Unlike skills (a folder per
    // skill), an agent is a single flat file: <root>/<slug>.md (design A4). Bundled agents are scanned
    // first, then user, then project; a more specific source shadows a less specific one of the same slug
    // (project > user > bundled, design A11). Build takes explicit root paths so it stays pure and
    // net48-testable; resolving the real roots (<exe>/agents, %AppData%/GxPT/agents, <workdir>/.gxpt/agents)
    // is AgentRoots' job. XP / .NET 3.5 friendly.
    internal sealed class AgentCatalog
    {
        private readonly List<Agent> _agents;                 // sorted by slug (ordinal)
        private readonly Dictionary<string, Agent> _bySlug;   // case-insensitive

        private AgentCatalog(List<Agent> agents, Dictionary<string, Agent> bySlug)
        {
            _agents = agents;
            _bySlug = bySlug;
        }

        // All discovered agents, slug-sorted (read-only).
        public IList<Agent> Agents
        {
            get { return _agents.AsReadOnly(); }
        }

        public bool TryGet(string slug, out Agent agent)
        {
            agent = null;
            if (string.IsNullOrEmpty(slug)) return false;
            return _bySlug.TryGetValue(slug, out agent);
        }

        // Convenience: a catalog with no user-global root (bundled + project only).
        public static AgentCatalog Build(string bundledRoot, string projectRoot)
        {
            return Build(bundledRoot, null, projectRoot);
        }

        // Scans bundled, then user, then project; a more specific source shadows a less specific one of
        // the same slug (project > user > bundled, design A11). Any root may be null/missing - it is
        // simply skipped.
        public static AgentCatalog Build(string bundledRoot, string userRoot, string projectRoot)
        {
            Dictionary<string, Agent> bySlug =
                new Dictionary<string, Agent>(StringComparer.OrdinalIgnoreCase);

            ScanRoot(bundledRoot, AgentSource.Bundled, bySlug);
            ScanRoot(userRoot, AgentSource.User, bySlug);         // user overrides bundled
            ScanRoot(projectRoot, AgentSource.Project, bySlug);   // project overrides user + bundled

            List<Agent> list = new List<Agent>(bySlug.Values);
            list.Sort(delegate(Agent a, Agent b) { return string.CompareOrdinal(a.Slug, b.Slug); });
            return new AgentCatalog(list, bySlug);
        }

        private static void ScanRoot(string root, AgentSource source, Dictionary<string, Agent> bySlug)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            string[] files;
            try { files = Directory.GetFiles(root, "*.md"); }
            catch { return; }
            // Sort so two files in the SAME root that normalize to the same slug (e.g. "Code Explorer.md"
            // and "code-explorer.md") resolve deterministically (last in ordinal order wins), not in
            // filesystem-enumeration order. Cross-root shadowing is handled by the scan order in Build.
            Array.Sort(files, StringComparer.Ordinal);

            for (int i = 0; i < files.Length; i++)
            {
                Agent agent = TryLoad(files[i], source);
                if (agent != null) bySlug[agent.Slug] = agent;   // last writer wins -> project shadows bundled
            }
        }

        // Loads an agent from a <slug>.md file if its frontmatter declares a non-empty description (the
        // manifest line every agent needs). Returns null for anything malformed, so a bad file is skipped
        // rather than breaking discovery. The slug is the file name (minus .md), normalized like a skill
        // folder name (reusing SkillSlug.Make).
        internal static Agent TryLoad(string file, AgentSource source)
        {
            if (string.IsNullOrEmpty(file)) return null;

            // Guard the Win32 "*.md" wildcard quirk (it can also match longer extensions): require an
            // exact .md file, so a stray "notes.mdx" is never treated as an agent.
            string ext = Path.GetExtension(file);
            if (ext == null || !ext.Equals(".md", StringComparison.OrdinalIgnoreCase)) return null;

            string baseName = Path.GetFileNameWithoutExtension(file);
            string slug = SkillSlug.Make(baseName);
            if (string.IsNullOrEmpty(slug)) return null;
            if (!File.Exists(file)) return null;

            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch { return null; }

            AgentFrontmatter fm = AgentFrontmatter.Parse(text);
            if (fm == null || string.IsNullOrEmpty(fm.Description)) return null;

            string name = (fm.Name != null && fm.Name.Length > 0) ? fm.Name : slug;
            return new Agent(slug, name, fm.Description, fm.Tools, fm.MaxTier, fm.Model,
                             fm.MaxTurns, file, source);
        }

        // The manifest body the model sees: one "- <slug> - <description>" line per agent, slug-ordered
        // (design sec.4 Level-1). The surrounding system-message framing + the feature-enabled gate are a
        // later phase (AgentInjection); this is the list.
        public string BuildManifest()
        {
            return BuildManifest(_agents);
        }

        public static string BuildManifest(IEnumerable<Agent> agents)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Agent a in agents)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("- ").Append(a.Slug).Append(" - ").Append(a.Description);
            }
            return sb.ToString();
        }
    }
}
