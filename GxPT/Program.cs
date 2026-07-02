using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace GxPT
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            PerfLog.Mark("Main enter");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Settings defaults live in one place (SettingsSchema). Point its font-size default at the
            // live chat control's font (computed once, outside AppSettings' lock), then seed any absent
            // keys into settings.json so the file is complete before any component reads it (issue #164).
            try
            {
                double chatFont = 9.0;
                try { chatFont = SettingsForm.GetChatDefaultFontSize(); }
                catch { }
                SettingsSchema.DefaultFontSizeProvider = delegate { return chatFont; };
            }
            catch { }
            try { AppSettings.EnsureSeeded(); }
            catch { }
            PerfLog.Mark("settings seeded");

            // Install global hover-to-scroll router (keeps focus where it is)
            try { HoverWheelRouter.Install(); }
            catch { }
            // The gap between this mark and "ctor begin" is MainForm type/assembly load (Krypton JIT).
            PerfLog.Mark("hover router installed");
            // Handle shell-open: if launched with a .gxpt/.gxcv/.gxsk/.gxpl file, import it on startup
            string fileArg = null;
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args != null && args.Length > 1)
                {
                    // Prefer the first existing file with a supported extension
                    for (int i = 1; i < args.Length; i++)
                    {
                        var a = args[i];
                        if (a == null) continue;
                        if (a.Length == 0 || a.Trim().Length == 0) continue;
                        try
                        {
                            string p = a.Trim().Trim('"');
                            if (System.IO.File.Exists(p))
                            {
                                string ext = System.IO.Path.GetExtension(p);
                                if (ext != null) ext = ext.ToLowerInvariant();
                                if (ext == ".gxpt" || ext == ".gxcv" || ext == ".gxsk" || ext == ".gxpl" || ext == ".zip") { fileArg = p; break; }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            var mainForm = new MainForm();
            PerfLog.Mark("MainForm constructed");
            if (!string.IsNullOrEmpty(fileArg))
            {
                // Defer to after the form is shown so dialogs are parented correctly. Queued via
                // BeginInvoke so it runs AFTER the session restore (which MainForm queues from its
                // own Shown handler, subscribed earlier) - the import should land on top of the
                // restored session, not race it.
                mainForm.Shown += (s, e) =>
                {
                    try
                    {
                        mainForm.BeginInvoke((MethodInvoker)delegate
                        {
                            try { mainForm.ImportArchiveFromShell(fileArg); }
                            catch { }
                        });
                    }
                    catch { }
                };
            }
            Application.Run(mainForm);
        }
    }
}
