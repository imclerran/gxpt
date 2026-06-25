using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Mcp35.Core.Diagnostics;

namespace Mcp35.Server.Process
{
    /// <summary>
    /// Runs an external process and captures its output. Pure BCL; mirrors the ProcessStartInfo
    /// pattern in OpenRouterClient. stdout/stderr are drained on separate threads to avoid pipe
    /// deadlock on large output. See mcp35-server-spec.md §6.
    /// </summary>
    public sealed class ProcessRunner
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        private readonly ILogSink _log;

        public ProcessRunner(ILogSink log)
        {
            _log = log ?? NullLogSink.Instance;
        }

        public ProcessResult Run(ProcessRequest req)
        {
            if (req == null) throw new ArgumentNullException("req");
            if (string.IsNullOrEmpty(req.FileName)) throw new ArgumentException("FileName is required.", "req");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = req.FileName;
            psi.Arguments = req.Arguments ?? string.Empty;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = Utf8;
            psi.StandardErrorEncoding = Utf8;
            if (!string.IsNullOrEmpty(req.WorkingDirectory)) psi.WorkingDirectory = req.WorkingDirectory;

            if (req.Environment != null)
            {
                foreach (System.Collections.Generic.KeyValuePair<string, string> kv in req.Environment)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

            ProcessResult result = new ProcessResult();
            StringBuilder outBuf = new StringBuilder();
            StringBuilder errBuf = new StringBuilder();

            System.Diagnostics.Process proc = new System.Diagnostics.Process();
            proc.StartInfo = psi;

            // Drain each pipe on its own thread so a large stream on one can't block the other.
            Thread outThread = null;
            Thread errThread = null;

            try
            {
                proc.Start();

                outThread = StartDrain(proc.StandardOutput, outBuf);
                errThread = StartDrain(proc.StandardError, errBuf);

                if (req.StdinText != null)
                {
                    try
                    {
                        if (req.StdinEncoding != null)
                        {
                            // The StandardInput StreamWriter's encoding isn't settable on .NET 3.5, so
                            // to control how non-ASCII stdin bytes are emitted we write them ourselves in
                            // the requested encoding via the underlying stream.
                            byte[] bytes = req.StdinEncoding.GetBytes(req.StdinText);
                            proc.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                            proc.StandardInput.BaseStream.Flush();
                        }
                        else
                        {
                            proc.StandardInput.Write(req.StdinText);
                        }
                    }
                    finally
                    {
                        proc.StandardInput.Close();
                    }
                }
                else
                {
                    proc.StandardInput.Close();
                }

                bool exited;
                if (req.TimeoutMs > 0)
                {
                    exited = proc.WaitForExit(req.TimeoutMs);
                    if (!exited)
                    {
                        result.TimedOut = true;
                        KillTree(proc);
                        proc.WaitForExit(); // reap after kill
                    }
                }
                else
                {
                    proc.WaitForExit();
                    exited = true;
                }

                // Let the drain threads finish flushing the pipes.
                JoinDrain(outThread);
                JoinDrain(errThread);

                result.ExitCode = SafeExitCode(proc);
                result.StdOut = outBuf.ToString();
                result.StdErr = errBuf.ToString();
                return result;
            }
            catch (Exception ex)
            {
                _log.Log("process", "Run failed for '" + req.FileName + "': " + ex.Message);
                throw;
            }
            finally
            {
                try { proc.Close(); }
                catch { }
            }
        }

        private Thread StartDrain(System.IO.StreamReader reader, StringBuilder sink)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    char[] buf = new char[4096];
                    int n;
                    while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        lock (sink) { sink.Append(buf, 0, n); }
                    }
                }
                catch (Exception ex)
                {
                    _log.Log("process", "Drain error: " + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        private static void JoinDrain(Thread t)
        {
            if (t != null) t.Join(2000);
        }

        private void KillTree(System.Diagnostics.Process proc)
        {
            try
            {
                if (!proc.HasExited) proc.Kill();
            }
            catch (Exception ex)
            {
                _log.Log("process", "Kill failed: " + ex.Message);
            }
        }

        private static int SafeExitCode(System.Diagnostics.Process proc)
        {
            try { return proc.ExitCode; }
            catch { return -1; }
        }
    }
}
