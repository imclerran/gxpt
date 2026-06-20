using System.Collections.Generic;
using System.Windows.Forms;

namespace GxPT
{
    // Bridges the AgentDispatcher's IAgentActivityUi callbacks (raised on child worker threads) to a tab's
    // AgentActivityPanel on the UI thread (design sec.14). Bound to one tab's context for the duration of a
    // turn; updates that tab's own panel (which is only visible when the tab is active), so no active-tab
    // gating is needed. Created per send in MainForm and handed to the dispatcher.
    internal sealed class AgentActivityUiBridge : IAgentActivityUi
    {
        private readonly MainForm _form;
        private readonly TabManager.ChatTabContext _ctx;

        public AgentActivityUiBridge(MainForm form, TabManager.ChatTabContext ctx)
        {
            _form = form;
            _ctx = ctx;
        }

        public void OnFanOutStart(IList<string> slugs)
        {
            // Copy the list: it is the dispatcher's and may change; the UI thread reads it later.
            List<string> copy = slugs != null ? new List<string>(slugs) : new List<string>();
            Marshal(delegate { AgentActivityPanel p = Panel(); if (p != null) p.BeginFanOut(copy); });
        }

        public void OnAgentStart(int index, string slug, string task)
        {
            Marshal(delegate { AgentActivityPanel p = Panel(); if (p != null) p.SetRunning(index); });
        }

        public void OnAgentFinished(int index, string slug)
        {
            Marshal(delegate { AgentActivityPanel p = Panel(); if (p != null) p.SetDone(index); });
        }

        public void OnFanOutEnd()
        {
            Marshal(delegate { AgentActivityPanel p = Panel(); if (p != null) p.EndFanOut(); });
        }

        private AgentActivityPanel Panel()
        {
            return _ctx != null ? _ctx.AgentActivityPanel : null;
        }

        private void Marshal(MethodInvoker action)
        {
            try
            {
                if (_form == null || _form.IsDisposed || !_form.IsHandleCreated) return;
                _form.BeginInvoke(action);
            }
            catch
            {
                // Form closing / handle gone between the checks and the invoke - safe to drop.
            }
        }
    }
}
