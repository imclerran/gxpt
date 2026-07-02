using System;
using System.Collections;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;

namespace GxPT
{
    // MSI custom action: generates native images (NGEN) for GxPT.exe and its static dependency
    // closure - notably the Krypton assemblies - at install time, and removes them at uninstall.
    //
    // Why: launch profiling showed ~650ms of every cold start going to JIT-compiling the Krypton
    // toolkit (assembly load gap + control construction + first palette install). NGEN moves that
    // compilation to install time; at run time the CLR maps the precompiled native images instead.
    //
    // Wired into GxPT.Setup as an installer-class custom action on the GxPT primary output
    // (Commit = ngen install, Uninstall = ngen uninstall). Failures are deliberately swallowed:
    // NGEN is an optimization, and the app must install and run fine without it (the CLR silently
    // falls back to JIT when no native image exists).
    [RunInstaller(true)]
    public class NgenInstaller : Installer
    {
        public override void Commit(IDictionary savedState)
        {
            base.Commit(savedState);
            RunNgen("install");
        }

        public override void Uninstall(IDictionary savedState)
        {
            RunNgen("uninstall");
            base.Uninstall(savedState);
        }

        private void RunNgen(string verb)
        {
            try
            {
                // The MSI passes the installed GxPT.exe path as /assemblypath (set automatically for
                // installer-class custom actions).
                string asmPath = Context != null ? Context.Parameters["assemblypath"] : null;
                if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath)) return;

                // ngen.exe lives in the runtime directory of the CLR we're running on (v2.0.50727
                // for a .NET 3.5 app) - present on XP and up whenever the framework is installed.
                string ngen = Path.Combine(
                    System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    "ngen.exe");
                if (!File.Exists(ngen)) return;

                ProcessStartInfo psi = new ProcessStartInfo(ngen,
                    verb + " \"" + asmPath + "\" /nologo");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process p = Process.Start(psi))
                {
                    // Compiling the full closure takes tens of seconds on period hardware; cap it so
                    // a wedged ngen can never hang the installer indefinitely.
                    if (!p.WaitForExit(5 * 60 * 1000))
                    {
                        try { p.Kill(); }
                        catch { }
                    }
                }
            }
            catch
            {
                // Never fail (or roll back) the install over a missed optimization.
            }
        }
    }
}
