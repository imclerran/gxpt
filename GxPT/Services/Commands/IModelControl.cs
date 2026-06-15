using System.Collections.Generic;

namespace GxPT
{
    // The active tab's model selection, split out of ISlashCommandContext (issue #119) so /model depends
    // only on this surface rather than the whole host facade.
    internal interface IModelControl
    {
        IList<string> GetModels();      // known "author/model" slugs (for completion)
        string GetActiveModel();        // the active tab's current model
        void SetModel(string slug);     // switch the active tab's model
    }
}
