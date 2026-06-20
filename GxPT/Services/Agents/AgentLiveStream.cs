using System.Collections.Generic;

namespace GxPT
{
    // The child agent's tool-loop UI, doubling as a live broadcaster (design sec.14, tier 3 "watch live").
    // It (a) forwards each tool call to the activity panel as the row's count line - the existing tier-2
    // behavior - and (b) records every event (text delta / tool call / tool result / complete) so a viewer
    // attaching mid-run can replay what already happened and then stream the rest. One instance per running
    // child. Thread-safe: the child raises events on a worker thread while the UI thread attaches/detaches a
    // viewer; a single lock guards the event log and the (at most one) attached sink. The replay-under-lock
    // on Attach guarantees no event is missed or duplicated across the snapshot/live boundary.
    internal sealed class AgentLiveStream : IToolLoopUi
    {
        private readonly IAgentActivityUi _activity;   // panel tool-count line (may be null)
        private readonly int _row;
        private readonly string _slug;
        private readonly string _task;

        private readonly object _lock = new object();
        private readonly List<Evt> _events = new List<Evt>();
        private IAgentLiveSink _sink;                   // the attached viewer, if any (v1: one at a time)
        private int _count;

        public AgentLiveStream(IAgentActivityUi activity, int row, string slug, string task)
        {
            _activity = activity;
            _row = row;
            _slug = slug;
            _task = task;
        }

        public string Slug { get { return _slug; } }
        public string Task { get { return _task; } }

        // ---------- IToolLoopUi (driven by the child orchestrator) ----------
        public void AppendTextDelta(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                _events.Add(Evt.Text(text));
                if (_sink != null) _sink.OnText(text);
            }
        }

        public void OnToolCall(string functionName, string argumentsJson, string callId)
        {
            lock (_lock)
            {
                _count++;
                _events.Add(Evt.Call(functionName, callId));
                if (_activity != null) _activity.OnAgentActivity(_row, functionName, _count);
                if (_sink != null) _sink.OnToolCall(functionName, callId);
            }
        }

        public void OnToolResult(string functionName, string resultText, bool isError, string callId)
        {
            lock (_lock)
            {
                _events.Add(Evt.Result(functionName, resultText, isError));
                if (_sink != null) _sink.OnToolResult(functionName, resultText, isError);
            }
        }

        public void OnError(string message) { }

        public void Complete()
        {
            lock (_lock)
            {
                _events.Add(Evt.Done());
                if (_sink != null) _sink.OnComplete();
            }
        }

        // ---------- Viewer attach/detach ----------
        // Replays the recorded events to the sink, then registers it for live events - all under the lock so
        // an event arriving concurrently can't slip between the replay and the registration.
        public void Attach(IAgentLiveSink sink)
        {
            if (sink == null) return;
            lock (_lock)
            {
                for (int i = 0; i < _events.Count; i++) _events[i].Dispatch(sink);
                _sink = sink;
            }
        }

        public void Detach(IAgentLiveSink sink)
        {
            lock (_lock) { if (_sink == sink) _sink = null; }
        }

        // A recorded event. Small tagged value so the log can be replayed to a late-attaching sink.
        private struct Evt
        {
            private const int TText = 0, TCall = 1, TResult = 2, TDone = 3;
            private int _t;
            private string _a;      // text delta / function name
            private string _b;      // call id / result text
            private bool _err;

            public static Evt Text(string s) { Evt e = new Evt(); e._t = TText; e._a = s; return e; }
            public static Evt Call(string fn, string id) { Evt e = new Evt(); e._t = TCall; e._a = fn; e._b = id; return e; }
            public static Evt Result(string fn, string res, bool err) { Evt e = new Evt(); e._t = TResult; e._a = fn; e._b = res; e._err = err; return e; }
            public static Evt Done() { Evt e = new Evt(); e._t = TDone; return e; }

            public void Dispatch(IAgentLiveSink s)
            {
                switch (_t)
                {
                    case TText: s.OnText(_a); break;
                    case TCall: s.OnToolCall(_a, _b); break;
                    case TResult: s.OnToolResult(_a, _b, _err); break;
                    case TDone: s.OnComplete(); break;
                }
            }
        }
    }
}
