using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace GxPT
{
    // A tiny modal shown while the tool-probe worker runs (connecting MCP servers to a throwaway workspace
    // can take a few seconds). A UI-thread Timer polls the worker and closes the dialog when it finishes, so
    // nothing crosses threads. XP / .NET 3.5 friendly.
    internal sealed class ToolProbeWaitForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer;

        // Assigned by the caller before ShowDialog; the dialog closes once this thread is no longer alive.
        public Thread Worker;

        public ToolProbeWaitForm()
        {
            Text = "Plugins";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(300, 70);

            Label label = new Label();
            label.Text = "Detecting available tools...";
            label.SetBounds(20, 26, 260, 20);
            Controls.Add(label);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 150;
            _timer.Tick += new EventHandler(OnTick);
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (Worker == null || !Worker.IsAlive)
            {
                _timer.Stop();
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PluginImportExportManager.ApplyOwnerIcon(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
