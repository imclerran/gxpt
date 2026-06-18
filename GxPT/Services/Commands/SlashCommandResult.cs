namespace GxPT
{
    // The outcome of dispatching one slash command. The send funnel (MainForm.btnSend_Click) reads
    // this to decide what to do:
    //   * SendToModel == true            -> replace the outgoing text with TextToSend and send it.
    //   * SendToModel == false, no Error -> the command was handled locally; clear input, do not send.
    //   * Error != null                  -> show Error to the user, clear input, do not send.
    // Prompt commands always return Send(...); client commands (later) return Handled(); either kind
    // can return Fail(...) when a precondition (server disabled, bad path argument) blocks execution.
    internal sealed class SlashCommandResult
    {
        public bool SendToModel;
        public string TextToSend;
        public string Error;

        // Optional hidden system message to commit to history alongside the outgoing user message (used
        // by /use-skill to attach a skill's body without it showing in the transcript). Carried on the result
        // rather than mutated during command Process, so an early return (e.g. an active edit) can't
        // leave an orphaned system message behind.
        public string SystemContext;

        private SlashCommandResult() { }

        // Prompt expansion: send this text to the model in place of the typed "/command".
        public static SlashCommandResult Send(string text)
        {
            SlashCommandResult r = new SlashCommandResult();
            r.SendToModel = true;
            r.TextToSend = text ?? string.Empty;
            return r;
        }

        // Send `text` as the user message, and commit `systemContext` as a hidden system message just
        // before it (only when the send is actually committed).
        public static SlashCommandResult Send(string text, string systemContext)
        {
            SlashCommandResult r = Send(text);
            r.SystemContext = systemContext;
            return r;
        }

        // Client command handled locally; nothing goes to the model.
        public static SlashCommandResult Handled()
        {
            return new SlashCommandResult();
        }

        // Precondition failed (e.g. required server disabled, path argument outside the workspace).
        public static SlashCommandResult Fail(string message)
        {
            SlashCommandResult r = new SlashCommandResult();
            r.Error = message ?? "Command could not be run.";
            return r;
        }
    }
}
