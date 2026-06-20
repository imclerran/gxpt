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
        private readonly RequestCancellation _group;   // tripped by the panel's Stop button (may be null)

        public AgentActivityUiBridge(MainForm form, TabManager.ChatTabContext ctx, RequestCancellation group)
        {
            _form = form;
            _ctx = ctx;
            _group = group;
        }

        public void OnFanOutStart(IList<string> slugs, IList<string> tasks)
        {
            // Copy the lists: they are the dispatcher's and may change; the UI thread reads them later.
            List<string> slugCopy = slugs != null ? new List<string>(slugs) : new List<string>();
            List<string> taskCopy = tasks != null ? new List<string>(tasks) : new List<string>();
            RequestCancellation group = _group;
            Marshal(delegate
            {
                AgentActivityPanel p = Panel();
                if (p != null) p.BeginFanOut(slugCopy, taskCopy, delegate { if (group != null) group.Cancel(); });
                if (_form != null) _form.NotifyAgentFanOutChanged(_ctx, true);
            });
        }

        public void OnAgentStart(int index, string slug, string task)
        {
            Marshal(delegate { AgentActivityPanel p = Panel(); if (p != null) p.SetRunning(index); });
        }

        public void OnAgentActivity(int index, string lastTool, int toolCount)
        {
            string tool = lastTool;
            int count = toolCount;
            Marshal(delegate { AgentActivityPanel p = Panel(); if (p != null) p.SetActivity(index, tool, count); });
        }

        public void OnAgentFinished(int index, string slug, bool cancelled)
        {
            Marshal(delegate
            {
                AgentActivityPanel p = Panel();
                if (p != null) { if (cancelled) p.SetCancelled(index); else p.SetDone(index); }
            });
        }

        public void OnFanOutEnd()
        {
            Marshal(delegate
            {
                AgentActivityPanel p = Panel();
                if (p != null) p.EndFanOut();
                if (_form != null) _form.NotifyAgentFanOutChanged(_ctx, false);
            });
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
