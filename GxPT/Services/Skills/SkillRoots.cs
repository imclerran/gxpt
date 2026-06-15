using System;
using System.IO;

namespace GxPT
{
    // Resolves the three skill roots - bundled (<exe>/skills), user-global (%AppData%/GxPT/skills), and
    // project (<workdir>/.gxpt/skills) - and builds the SkillCatalog over them. Split out of SkillInjection
    // (issue #119) so the path/root resolution - consumed independently by the /skill commands and the
    // host's enablement checks that do no injection - is named for what it does; SkillInjection keeps the
    // injection framing and the enablement helper.
    internal static class SkillRoots
    {
        public const string SkillsDirName = "skills";

        // Bundled skills ship beside GxPT.exe (deployed by the AfterBuild copy like mcp-servers).
        public static string BundledRoot(string exeDir)
        {
            if (string.IsNullOrEmpty(exeDir)) return null;
            return Path.Combine(exeDir, SkillsDirName);
        }

        // Project skills live under the conversation's working folder, reusing the memory system's .gxpt.
        public static string ProjectRoot(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            return Path.Combine(Path.Combine(workingDir, MemoryInjection.MemoryDirName), SkillsDirName);
        }

        // User-global skills live under %AppData%/GxPT/skills - one set per Windows user, independent of
        // workspace. The SkillsMcpServer writes/runs them at the same path (GXPT_SKILLS_USER_ROOT), so
        // read and write stay in sync. Returns null if %AppData% can't be resolved.
        public static string UserRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return null;
            return Path.Combine(Path.Combine(appData, "GxPT"), SkillsDirName);
        }

        public static SkillCatalog BuildCatalog(string exeDir, string workingDir)
        {
            return SkillCatalog.Build(BundledRoot(exeDir), UserRoot(), ProjectRoot(workingDir));
        }
    }
}
