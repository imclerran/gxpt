using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace ExtensionsMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec sec.1), read once. The writable extension roots
    /// come in two families that share the .gxpt project home:
    ///   skills  - project = GXPT_WORKDIR/.gxpt/skills; user = GXPT_SKILLS_USER_ROOT (user-global, %AppData%);
    ///   agents  - project = GXPT_WORKDIR/.gxpt/agents; user = GXPT_AGENTS_USER_ROOT (user-global, %AppData%).
    /// WorkDir and the bundled skill root (GXPT_SKILLS_BUNDLED_ROOT) are used by run_skill_script. Agents
    /// have no execution surface, so they need no bundled root or shell here.
    /// </summary>
    internal sealed class ExtensionsConfig
    {
        public readonly string WorkDir;       // conversation workspace (cwd for scripts), or null

        // Skill roots (authoring + run_skill_script resolution).
        public readonly string ProjectRoot;   // <workdir>/.gxpt/skills, or null when no workspace
        public readonly string UserRoot;      // %AppData%/GxPT/skills (user-global), or null if unset
        public readonly string BundledRoot;   // <exe>/skills, or null (for run_skill_script)
        public readonly string Shell;         // cmd.exe (ComSpec / GXPT_CMD_SHELL) for run_skill_script

        // Agent roots (authoring only - agents are flat <slug>.md files, no execution).
        public readonly string AgentProjectRoot; // <workdir>/.gxpt/agents, or null when no workspace
        public readonly string AgentUserRoot;    // %AppData%/GxPT/agents (user-global), or null if unset

        private ExtensionsConfig(string workDir, string projectRoot, string userRoot, string bundledRoot,
            string shell, string agentProjectRoot, string agentUserRoot)
        {
            WorkDir = workDir;
            ProjectRoot = projectRoot;
            UserRoot = userRoot;
            BundledRoot = bundledRoot;
            Shell = shell;
            AgentProjectRoot = agentProjectRoot;
            AgentUserRoot = agentUserRoot;
        }

        // Test-only construction (the production path is FromEnvironment). Internal, so it is reachable
        // only from the linked-source test assembly.
        internal static ExtensionsConfig ForTesting(string workDir, string projectRoot, string userRoot,
            string bundledRoot, string shell, string agentProjectRoot, string agentUserRoot)
        {
            return new ExtensionsConfig(workDir, projectRoot, userRoot, bundledRoot, shell,
                agentProjectRoot, agentUserRoot);
        }

        public static ExtensionsConfig FromEnvironment(ILogSink log)
        {
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir)) workDir = null;

            string projectRoot = string.IsNullOrEmpty(workDir)
                ? null
                : Path.Combine(Path.Combine(workDir, ".gxpt"), "skills");

            string userRoot = Environment.GetEnvironmentVariable("GXPT_SKILLS_USER_ROOT");
            if (string.IsNullOrEmpty(userRoot)) userRoot = null;

            string bundledRoot = Environment.GetEnvironmentVariable("GXPT_SKILLS_BUNDLED_ROOT");
            if (string.IsNullOrEmpty(bundledRoot)) bundledRoot = null;

            string shell = Environment.GetEnvironmentVariable("GXPT_CMD_SHELL");
            if (string.IsNullOrEmpty(shell)) shell = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(shell)) shell = "cmd.exe";

            // Agent roots reuse the same .gxpt project home; no bundled root (agents never execute).
            string agentProjectRoot = string.IsNullOrEmpty(workDir)
                ? null
                : Path.Combine(Path.Combine(workDir, ".gxpt"), "agents");

            string agentUserRoot = Environment.GetEnvironmentVariable("GXPT_AGENTS_USER_ROOT");
            if (string.IsNullOrEmpty(agentUserRoot)) agentUserRoot = null;

            if (log != null)
                log.Log("extensions", "skills project=" + (projectRoot != null ? projectRoot : "(none)")
                    + " user=" + (userRoot != null ? userRoot : "(none)")
                    + " bundled=" + (bundledRoot != null ? bundledRoot : "(none)")
                    + "; agents project=" + (agentProjectRoot != null ? agentProjectRoot : "(none)")
                    + " user=" + (agentUserRoot != null ? agentUserRoot : "(none)"));

            return new ExtensionsConfig(workDir, projectRoot, userRoot, bundledRoot, shell,
                agentProjectRoot, agentUserRoot);
        }
    }
}
