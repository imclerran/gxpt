using System;
using System.Collections.Generic;
using System.IO;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace MSBuildMcpServer
{
    /// <summary>
    /// Registers one build tool per discovered MSBuild version (servers-spec §4 — this server builds
    /// command lines, so it owns argv escaping). Each tool (e.g. build_4_0 → msbuild__build_4_0) runs
    /// a specific MSBuild engine against a project/solution under GXPT_WORKDIR and returns the engine's
    /// output and exit code verbatim. MSBuild runs arbitrary build logic (targets, Exec tasks, pre/post
    /// build events), so the host gates these tools as Destructive — the server's job is only to
    /// construct and run the invocation safely.
    /// </summary>
    internal static class MsBuildTools
    {
        private const int DefaultTimeoutMs = 600000;   // 10 min — builds can be slow
        private const int MaxTimeoutMs = 1800000;      // 30 min hard cap
        private const int OutputCap = 100000;          // chars per stream

        private static readonly string[] ProjectExtensions =
            new string[] { ".sln", ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".proj" };

        public static void Register(McpServer server, MsBuildConfig config)
        {
            ProcessRunner runner = new ProcessRunner(null);
            // Discovery diagnostics go to stderr (never stdout — that carries the JSON-RPC stream).
            IList<MsBuildInstall> installs = MsBuildDiscovery.Discover(new StdErrLogSink());

            foreach (MsBuildInstall install in installs)
            {
                MsBuildInstall inst = install; // capture a per-iteration copy for the closure
                string toolName = "build_" + inst.Version.Replace('.', '_');
                server.AddTool(toolName, BuildDescription(inst), BuildSchema(inst),
                    ToolAnnotations.Destructive(),
                    delegate(ToolCallContext ctx) { return Run(config, runner, inst, ctx); });
            }

            // devenv.com (full Visual Studio) builds whole solutions, including project types MSBuild
            // can't — notably .vdproj setup/deployment projects. One build_solution_<year> tool per IDE.
            IList<DevenvInstall> ides = MsBuildDiscovery.DiscoverDevenv(new StdErrLogSink());
            foreach (DevenvInstall ide in ides)
            {
                DevenvInstall vs = ide; // capture a per-iteration copy for the closure
                string toolName = "build_solution_" + vs.Year;
                server.AddTool(toolName, BuildDevenvDescription(vs), BuildDevenvSchema(),
                    ToolAnnotations.Destructive(),
                    delegate(ToolCallContext ctx) { return RunDevenv(config, runner, vs, ctx); });
            }
        }

        private static string BuildDescription(MsBuildInstall inst)
        {
            string d = "Build a project or solution with " + inst.Label + ". Runs MSBuild in the "
                + "conversation's working directory and returns its full output and exit code. If "
                + "'project' is omitted, the lone .sln (or lone project file) in the working directory "
                + "is built.";
            if (inst.HasBoth)
                d += " 'bitness' selects the 32- or 64-bit MSBuild engine (default x64); this is the "
                   + "build process bitness, NOT the output architecture - use the 'platform' property "
                   + "to control the built binary's architecture.";
            return d;
        }

        private static JObject BuildSchema(MsBuildInstall inst)
        {
            SchemaBuilder sb = SchemaBuilder.Object()
                .Str("project", false, "Path to a .sln/.csproj/.*proj (relative to the working directory, or absolute). Omit to build the lone solution/project in the working directory.")
                .Arr("targets", "string", false, "MSBuild targets to run, e.g. [\"Build\"], [\"Rebuild\"], [\"Clean\"], [\"Restore\"]. Default: the project's default target (Build).")
                .Str("configuration", false, "Build configuration, e.g. Debug or Release (/p:Configuration=).")
                .Str("platform", false, "Build/output platform, e.g. \"Any CPU\", x86, x64 (/p:Platform=). Controls the produced binary's architecture.")
                .Raw("properties", PropertiesSchema(), false)
                .Raw("verbosity", EnumSchema("MSBuild output verbosity (/v:). Default minimal.", new string[] { "quiet", "minimal", "normal", "detailed", "diagnostic" }), false)
                .Int("max_cpu_count", false, "Parallel build worker count (/m:N). Omit for MSBuild's single-process default.")
                .Int("timeout_ms", false, "Kill the build after this many milliseconds (default 600000, max 1800000).");

            if (inst.HasBoth)
                sb.Raw("bitness", EnumSchema("Which MSBuild engine (process bitness) to run; default x64.", new string[] { "x86", "x64" }), false);

            return sb.Build();
        }

        private static JObject PropertiesSchema()
        {
            JObject p = new JObject();
            p["type"] = "object";
            p["description"] = "Additional MSBuild properties as name/value pairs, each passed as /p:Name=Value.";
            JObject add = new JObject();
            add["type"] = "string";
            p["additionalProperties"] = add;
            return p;
        }

        private static JObject EnumSchema(string description, string[] values)
        {
            JObject p = new JObject();
            p["type"] = "string";
            p["description"] = description;
            JArray e = new JArray();
            for (int i = 0; i < values.Length; i++) e.Add(values[i]);
            p["enum"] = e;
            return p;
        }

        private static CallToolResult Run(MsBuildConfig config, ProcessRunner runner, MsBuildInstall inst, ToolCallContext ctx)
        {
            string projectFull, projectRel, resolveError;
            if (!ResolveProject(config.WorkDir, ctx.Arguments.Value<string>("project"), out projectFull, out projectRel, out resolveError))
                return ToolResults.Error(resolveError);

            JObject a = ctx.Arguments;
            string bitness = a.Value<string>("bitness");
            string exe = inst.PathForBitness(bitness);
            if (string.IsNullOrEmpty(exe))
                return ToolResults.Error("MSBuild " + inst.Version + " engine not found.");
            string engine = string.Equals(exe, inst.X64Path, StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";

            int timeout = IntArg(a, "timeout_ms", DefaultTimeoutMs, 1000, MaxTimeoutMs);

            List<string> args = BuildArgs(a, projectFull);

            // The build runs in the conversation's CURRENT directory (host `cd`); the project-path floor
            // stays the workspace anchor (resolved above against config.WorkDir). Current dir is
            // re-validated against the anchor — outside/removed => rejected, not silently widened.
            string workDir;
            string cwdErr;
            if (!CwdScope.TryResolveWorkingDir(ctx, config.WorkDir, "workspace root", out workDir, out cwdErr))
                return ToolResults.Error(cwdErr);

            ProcessRequest req = new ProcessRequest();
            req.FileName = exe;
            req.Arguments = ArgvQuoter.Join(args);   // discrete tokens, quoted once (servers-spec §4)
            req.WorkingDirectory = workDir;
            req.TimeoutMs = timeout;

            ProcessResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ToolResults.Error("MSBuild could not be launched (" + exe + "). The engine may have been uninstalled.");
            }
            catch (Exception ex)
            {
                return ToolResults.Error("failed to run MSBuild: " + ex.Message);
            }

            bool outTrunc, errTrunc;
            JObject outp = new JObject();
            outp["version"] = inst.Version;
            outp["engine"] = engine;
            outp["project"] = projectRel;
            outp["exitCode"] = result.ExitCode;
            outp["succeeded"] = (!result.TimedOut && result.ExitCode == 0);
            outp["timedOut"] = result.TimedOut;
            outp["stdout"] = Cap(result.StdOut, out outTrunc);
            outp["stderr"] = Cap(result.StdErr, out errTrunc);
            if (outTrunc || errTrunc) outp["truncated"] = true;
            return ToolResults.Json(outp);
        }

        // Build the MSBuild argument token list. The project comes first; everything else is a switch.
        // Pure (operates on the parsed arguments only) so it is unit-testable without the MCP runtime.
        internal static List<string> BuildArgs(JObject a, string projectFull)
        {
            List<string> args = new List<string>();
            args.Add(projectFull);

            List<string> targets = StrArrayArg(a, "targets");
            if (targets.Count > 0) args.Add("/t:" + string.Join(";", targets.ToArray()));

            string config = a.Value<string>("configuration");
            if (!string.IsNullOrEmpty(config)) args.Add("/p:Configuration=" + config);

            string platform = a.Value<string>("platform");
            if (!string.IsNullOrEmpty(platform)) args.Add("/p:Platform=" + platform);

            JObject props = a["properties"] as JObject;
            if (props != null)
            {
                foreach (KeyValuePair<string, JToken> kv in props)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    string val = kv.Value != null ? kv.Value.ToString() : string.Empty;
                    args.Add("/p:" + kv.Key + "=" + val);
                }
            }

            string verbosity = a.Value<string>("verbosity");
            args.Add("/v:" + (string.IsNullOrEmpty(verbosity) ? "minimal" : verbosity));

            int cpu = IntArg(a, "max_cpu_count", 0, 0, 1024);
            if (cpu > 0) args.Add("/m:" + cpu.ToString(System.Globalization.CultureInfo.InvariantCulture));

            args.Add("/nologo");
            return args;
        }

        // ---- devenv.com (Visual Studio IDE) build — handles whole solutions incl. .vdproj ----

        private static string BuildDevenvDescription(DevenvInstall vs)
        {
            return "Build an entire solution with " + vs.Label + " (devenv.com). Unlike the MSBuild "
                + "tools, this drives the Visual Studio IDE, so it builds EVERY project type in the "
                + "solution - including Visual Studio setup/deployment (.vdproj) projects, which MSBuild "
                + "cannot build. If 'solution' is omitted, the lone .sln in the working directory is used. "
                + "'configuration' defaults to Release.";
        }

        private static JObject BuildDevenvSchema()
        {
            return SchemaBuilder.Object()
                .Str("solution", false, "Path to the .sln (relative to the working directory, or absolute). Omit to use the lone .sln in the working directory.")
                .Raw("action", EnumSchema("What to do: Build (default), Rebuild, Clean, or Deploy.", new string[] { "Build", "Rebuild", "Clean", "Deploy" }), false)
                .Str("configuration", false, "Solution configuration name, e.g. Debug or Release. Default: Release.")
                .Str("platform", false, "Solution platform, e.g. \"Any CPU\", x86. Combined with configuration as \"Config|Platform\".")
                .Str("project", false, "Build only this project within the solution (devenv /Project).")
                .Str("project_config", false, "Project-specific configuration for the single-project build (devenv /ProjectConfig).")
                .Int("timeout_ms", false, "Kill the build after this many milliseconds (default 600000, max 1800000).")
                .Build();
        }

        private static CallToolResult RunDevenv(MsBuildConfig config, ProcessRunner runner, DevenvInstall vs, ToolCallContext ctx)
        {
            JObject a = ctx.Arguments;
            string solutionFull, solutionRel, resolveError;
            if (!ResolveSolution(config.WorkDir, a.Value<string>("solution"), out solutionFull, out solutionRel, out resolveError))
                return ToolResults.Error(resolveError);

            int timeout = IntArg(a, "timeout_ms", DefaultTimeoutMs, 1000, MaxTimeoutMs);
            List<string> args = BuildDevenvArgs(a, solutionFull);

            // The build runs in the conversation's CURRENT directory (host `cd`); the solution-path floor
            // stays the workspace anchor (resolved above against config.WorkDir).
            string workDir;
            string cwdErr;
            if (!CwdScope.TryResolveWorkingDir(ctx, config.WorkDir, "workspace root", out workDir, out cwdErr))
                return ToolResults.Error(cwdErr);

            ProcessRequest req = new ProcessRequest();
            req.FileName = vs.Path;
            req.Arguments = ArgvQuoter.Join(args);
            req.WorkingDirectory = workDir;
            req.TimeoutMs = timeout;

            ProcessResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ToolResults.Error("Visual Studio (devenv.com) could not be launched (" + vs.Path + ").");
            }
            catch (Exception ex)
            {
                return ToolResults.Error("failed to run devenv: " + ex.Message);
            }

            bool outTrunc, errTrunc;
            JObject outp = new JObject();
            outp["ide"] = vs.Label;
            outp["solution"] = solutionRel;
            outp["action"] = ActionName(a);
            outp["exitCode"] = result.ExitCode;
            outp["succeeded"] = (!result.TimedOut && result.ExitCode == 0);
            outp["timedOut"] = result.TimedOut;
            outp["stdout"] = Cap(result.StdOut, out outTrunc);
            outp["stderr"] = Cap(result.StdErr, out errTrunc);
            if (outTrunc || errTrunc) outp["truncated"] = true;
            return ToolResults.Json(outp);
        }

        // Build the devenv argument token list:
        //   <solution> /Build|/Rebuild|/Clean|/Deploy "<Config>[|<Platform>]" [/Project p [/ProjectConfig pc]]
        // Pure (operates on the parsed arguments only) so it is unit-testable.
        internal static List<string> BuildDevenvArgs(JObject a, string solutionFull)
        {
            List<string> args = new List<string>();
            args.Add(solutionFull);
            args.Add(ActionSwitch(a.Value<string>("action")));

            // devenv requires a solution configuration name for Build/Rebuild/Clean/Deploy.
            string config = a.Value<string>("configuration");
            if (string.IsNullOrEmpty(config)) config = "Release";
            string platform = a.Value<string>("platform");
            args.Add(string.IsNullOrEmpty(platform) ? config : (config + "|" + platform));

            string project = a.Value<string>("project");
            if (!string.IsNullOrEmpty(project)) { args.Add("/Project"); args.Add(project); }
            string projectConfig = a.Value<string>("project_config");
            if (!string.IsNullOrEmpty(projectConfig)) { args.Add("/ProjectConfig"); args.Add(projectConfig); }
            return args;
        }

        private static string ActionSwitch(string action)
        {
            if (string.IsNullOrEmpty(action)) return "/Build";
            switch (action.ToLowerInvariant())
            {
                case "rebuild": return "/Rebuild";
                case "clean": return "/Clean";
                case "deploy": return "/Deploy";
                default: return "/Build";
            }
        }

        private static string ActionName(JObject a)
        {
            string s = a.Value<string>("action");
            if (string.IsNullOrEmpty(s)) return "Build";
            switch (s.ToLowerInvariant())
            {
                case "rebuild": return "Rebuild";
                case "clean": return "Clean";
                case "deploy": return "Deploy";
                default: return "Build";
            }
        }

        // Resolve the .sln to build via devenv. Provided path is validated; otherwise a lone .sln in the
        // working directory is used.
        internal static bool ResolveSolution(string workDir, string solution, out string full, out string rel, out string error)
        {
            full = null; rel = null; error = null;
            try
            {
                if (!string.IsNullOrEmpty(solution))
                {
                    string candidate = Path.IsPathRooted(solution) ? solution : Path.Combine(workDir, solution);
                    candidate = Path.GetFullPath(candidate);
                    if (!File.Exists(candidate)) { error = "solution not found: " + solution; return false; }
                    full = candidate; rel = solution; return true;
                }
                string picked = PickSingle(workDir, ".sln");
                if (picked == null)
                {
                    error = "no 'solution' given and could not find a single .sln in the working directory; specify 'solution'.";
                    return false;
                }
                full = Path.GetFullPath(picked); rel = Path.GetFileName(picked); return true;
            }
            catch (Exception ex)
            {
                error = "failed to resolve solution: " + ex.Message;
                return false;
            }
        }

        // Resolve the project/solution to build. Returns the full path to run and a workdir-relative
        // (or original) form for display. Auto-discovers a lone solution/project when none is given.
        internal static bool ResolveProject(string workDir, string project, out string full, out string rel, out string error)
        {
            full = null; rel = null; error = null;
            try
            {
                if (!string.IsNullOrEmpty(project))
                {
                    string candidate = Path.IsPathRooted(project) ? project : Path.Combine(workDir, project);
                    candidate = Path.GetFullPath(candidate);
                    if (!File.Exists(candidate)) { error = "project not found: " + project; return false; }
                    full = candidate;
                    rel = project;
                    return true;
                }

                // No project given: prefer a single .sln, else a single project file, in the workdir root.
                string picked = PickSingle(workDir, ".sln");
                if (picked == null) picked = PickSingleProject(workDir);
                if (picked == null)
                {
                    error = "no 'project' given and could not find a single .sln or project file in the working directory; specify 'project'.";
                    return false;
                }
                full = Path.GetFullPath(picked);
                rel = Path.GetFileName(picked);
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to resolve project: " + ex.Message;
                return false;
            }
        }

        private static string PickSingle(string dir, string ext)
        {
            string[] hits = Directory.GetFiles(dir, "*" + ext);
            return hits.Length == 1 ? hits[0] : null;
        }

        private static string PickSingleProject(string dir)
        {
            List<string> hits = new List<string>();
            for (int i = 1; i < ProjectExtensions.Length; i++) // skip .sln (index 0)
                hits.AddRange(Directory.GetFiles(dir, "*" + ProjectExtensions[i]));
            return hits.Count == 1 ? hits[0] : null;
        }

        // ---- argument helpers (mirroring the other first-party servers) ----

        private static int IntArg(JObject a, string name, int fallback, int min, int max)
        {
            JToken t = a[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        // Read a string[] argument; also accepts a lone string for convenience. Skips null/empty.
        private static List<string> StrArrayArg(JObject a, string name)
        {
            List<string> result = new List<string>();
            JToken t = a[name];
            if (t == null) return result;
            if (t.Type == JTokenType.Array)
            {
                foreach (JToken item in (JArray)t)
                {
                    if (item == null || item.Type == JTokenType.Null) continue;
                    string s;
                    try { s = item.Value<string>(); }
                    catch { continue; }
                    if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }
            else if (t.Type == JTokenType.String)
            {
                string s = t.Value<string>();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            return result;
        }

        private static string Cap(string s, out bool truncated)
        {
            truncated = false;
            if (s == null) return string.Empty;
            if (s.Length <= OutputCap) return s;
            truncated = true;
            return s.Substring(0, OutputCap);
        }
    }
}
