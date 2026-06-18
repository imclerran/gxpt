using System.Collections.Generic;

namespace GxPT
{
    // A command that supplies its own argument completions to the autocomplete popup (e.g. /model over
    // the model list, /toggle-tool over the MCP server names). Path completion is handled separately by the
    // popup itself (filesystem-backed); this seam is for list/choice-style arguments.
    internal interface IArgumentCompleter
    {
        // Candidates for the current argument text (the part after the command name). Return an empty
        // list for "nothing to suggest"; the popup hides in that case.
        IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx);
    }

    // One argument completion entry. InsertArg is the full argument text to place after the command
    // (the popup prefixes "/<command> "). ContinueCompleting keeps the popup open after accepting -- used
    // for the first level of a two-level value such as "author/" before the model name.
    internal sealed class ArgCompletion
    {
        public string Display;
        public string InsertArg;
        public bool ContinueCompleting;

        public ArgCompletion(string display, string insertArg, bool continueCompleting)
        {
            Display = display;
            InsertArg = insertArg;
            ContinueCompleting = continueCompleting;
        }
    }
}
