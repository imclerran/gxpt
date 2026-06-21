using System;
using System.IO;

namespace GxPT
{
    // Resolves the three agent roots - bundled (<exe>/agents), user-global (%AppData%/GxPT/agents), and
    // project (<workdir>/.gxpt/agents) - and builds the AgentCatalog over them. The agents analogue of
    // SkillRoots: same three-tier layout, reusing the memory system's .gxpt project home. Kept separate
    // from discovery (AgentCatalog) so the path resolution stays pure and the catalog takes explicit
    // roots for net48 tests. XP / .NET 3.5 friendly.
    internal static class AgentRoots
    {
        public const string AgentsDirName = "agents";

        // Bundled agents ship beside GxPT.exe (deployed by the AfterBuild copy like mcp-servers/skills).
        public static string BundledRoot(string exeDir)
        {
            if (string.IsNullOrEmpty(exeDir)) return null;
            return Path.Combine(exeDir, AgentsDirName);
        }

        // Project agents live under the conversation's working folder, reusing the memory/skills .gxpt home.
        public static string ProjectRoot(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            return Path.Combine(Path.Combine(workingDir, MemoryInjection.MemoryDirName), AgentsDirName);
        }

        // User-global agents live under %AppData%/GxPT/agents - one set per Windows user, independent of
        // workspace. Returns null if %AppData% can't be resolved.
        public static string UserRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return null;
            return Path.Combine(Path.Combine(appData, "GxPT"), AgentsDirName);
        }

        public static AgentCatalog BuildCatalog(string exeDir, string workingDir)
        {
            return AgentCatalog.Build(BundledRoot(exeDir), UserRoot(), ProjectRoot(workingDir));
        }
    }
}
