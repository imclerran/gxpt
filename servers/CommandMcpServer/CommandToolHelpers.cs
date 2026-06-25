using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace CommandMcpServer
{
    /// <summary>
    /// Helpers shared by the Command server's tools (run + the PowerShell tools) so the timeout
    /// coercion, the per-stream output cap, and the result JSON shape stay identical across them - a
    /// change here updates every command tool at once instead of drifting copy-by-copy.
    /// </summary>
    internal static class CommandToolHelpers
    {
        public const int OutputCap = 100000; // chars per stream

        // Read an integer argument, clamped to [min,max], falling back when absent/null/unparseable.
        public static int IntArg(JObject args, string name, int fallback, int min, int max)
        {
            JToken t = args != null ? args[name] : null;
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        // Truncate a captured stream to OutputCap, reporting whether it was cut.
        public static string Cap(string s, out bool truncated)
        {
            truncated = false;
            if (s == null) return string.Empty;
            if (s.Length <= OutputCap) return s;
            truncated = true;
            return s.Substring(0, OutputCap);
        }

        // The shared result shape: { [version], exitCode, stdout, stderr, timedOut, [truncated] }.
        // 'version' is emitted only when non-empty (the PowerShell tools set it; run passes null).
        public static JObject BuildResult(ProcessResult result, string version)
        {
            bool outTrunc, errTrunc;
            JObject o = new JObject();
            if (!string.IsNullOrEmpty(version)) o["version"] = version;
            o["exitCode"] = result.ExitCode;
            o["stdout"] = Cap(result.StdOut, out outTrunc);
            o["stderr"] = Cap(result.StdErr, out errTrunc);
            o["timedOut"] = result.TimedOut;
            if (outTrunc || errTrunc) o["truncated"] = true;
            return o;
        }
    }
}
