using System.Collections.Generic;

namespace GxPT
{
    // The host's view of a dispatch_agent fan-out, for the observability UI (design sec.14). The
    // AgentDispatcher calls these around the children it runs; the host (MainForm) implements them to
    // relabel the Stop button to "Stop N agents" and render the in-transcript activity panel. All methods
    // may be called from worker threads (children run on the ThreadPool), so an implementation must marshal
    // to the UI thread itself. Optional on the dispatcher - null means no observability (the headless/test
    // path). A child's live tool activity is shown to the user here, never fed to the parent model (A7).
    internal interface IAgentActivityUi
    {
        // A fan-out is beginning with `slugs.Count` agents (in dispatch order). The host shows the panel
        // and relabels Stop. Always paired with exactly one OnFanOutEnd.
        void OnFanOutStart(IList<string> slugs);

        // One child began / finished (index is its slot in the dispatch order). Under a parallel read-only
        // fan-out these can interleave across children; the index identifies which row to update.
        void OnAgentStart(int index, string slug, string task);
        void OnAgentFinished(int index, string slug);

        // The fan-out is complete; the host hides the panel and reverts the Stop button.
        void OnFanOutEnd();
    }
}
