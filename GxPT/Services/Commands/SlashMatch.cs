using System;

namespace GxPT
{
    // Matching helper shared by command-name and argument completion. A "hyphen-prefix" match treats each
    // hyphen-delimited segment of a name as its own anchor: the query matches when the name, read from the
    // start OR from just after any '-', begins with the query. So "/some" matches "something",
    // "do-something", "some-setting-status" and "toggle-some-setting"; once a hyphen is typed ("/some-")
    // only segment runs that continue past the hyphen match ("some-setting-status", "toggle-some-setting").
    // This lets users jump to the meaningful word of a multi-word command without typing its prefix.
    internal static class SlashMatch
    {
        public static bool HyphenPrefix(string name, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;        // empty prefix matches everything
            if (string.IsNullOrEmpty(name)) return false;

            for (int i = 0; i <= name.Length - query.Length; i++)
            {
                bool boundary = (i == 0) || (name[i - 1] == '-');
                if (!boundary) continue;
                if (string.Compare(name, i, query, 0, query.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }
    }
}
