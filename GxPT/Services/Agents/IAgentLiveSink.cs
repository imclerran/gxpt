namespace GxPT
{
    // A live consumer of one child agent's activity - the streaming transcript viewer. An AgentLiveStream
    // replays its recorded events to a sink on attach, then forwards new ones as the child runs, so the
    // viewer shows the run from the beginning even when opened mid-flight (design sec.14, tier 3 "watch
    // live"). Callbacks may arrive on a worker thread (the child runs on the ThreadPool); the implementation
    // marshals to the UI thread itself. Shown to the user only, never fed to the parent model (A7).
    internal interface IAgentLiveSink
    {
        void OnText(string delta);
        void OnToolCall(string functionName, string argumentsJson, string callId);
        void OnToolResult(string functionName, string resultText, bool isError);
        void OnComplete();
    }
}
