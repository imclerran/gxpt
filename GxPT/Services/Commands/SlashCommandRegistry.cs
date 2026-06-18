using System;
using System.Collections.Generic;

namespace GxPT
{
    // Holds the registered slash commands in insertion order and resolves names/aliases. Construction is
    // pure (takes an already-built list), so it is trivially unit-testable; SlashCommandConfig builds the
    // list from the bundled defaults merged with the user's commands.json.
    internal sealed class SlashCommandRegistry
    {
        private readonly List<ISlashCommand> _ordered = new List<ISlashCommand>();
        private readonly Dictionary<string, ISlashCommand> _byName =
            new Dictionary<string, ISlashCommand>(StringComparer.OrdinalIgnoreCase);

        public SlashCommandRegistry(IEnumerable<ISlashCommand> commands)
        {
            if (commands == null) return;
            foreach (ISlashCommand cmd in commands)
            {
                if (cmd == null || string.IsNullOrEmpty(cmd.Name)) continue;
                _ordered.Add(cmd);
                Index(cmd.Name, cmd);
                if (cmd.Aliases != null)
                {
                    for (int i = 0; i < cmd.Aliases.Count; i++)
                        Index(cmd.Aliases[i], cmd);
                }
            }
        }

        private void Index(string key, ISlashCommand cmd)
        {
            if (string.IsNullOrEmpty(key)) return;
            // First registration wins, so a user override (added before built-ins are re-added) keeps
            // its slot. SlashCommandConfig already resolves overrides by name before constructing us,
            // so this is just defense in depth.
            if (!_byName.ContainsKey(key)) _byName[key] = cmd;
        }

        public int Count { get { return _ordered.Count; } }

        public IList<ISlashCommand> All()
        {
            return _ordered.AsReadOnly();
        }

        // Resolve a typed token (no leading slash) to a command. Returns false for unknown tokens, which
        // the processor treats as "not a command" so the text is sent literally.
        public bool TryResolve(string token, out ISlashCommand cmd)
        {
            cmd = null;
            if (string.IsNullOrEmpty(token)) return false;
            return _byName.TryGetValue(token, out cmd);
        }

        // Commands whose primary name matches the given prefix (case-insensitive), in registration order.
        // Matching is hyphen-aware (see SlashMatch.HyphenPrefix): the prefix anchors at the start of the
        // name or just after any '-', so "/some" also surfaces "do-something" and "toggle-some-setting".
        // An empty prefix returns everything -- bare "/" shows the full list for discoverability.
        public IList<ISlashCommand> Match(string prefix)
        {
            string p = prefix ?? string.Empty;
            List<ISlashCommand> hits = new List<ISlashCommand>();
            for (int i = 0; i < _ordered.Count; i++)
            {
                ISlashCommand cmd = _ordered[i];
                if (SlashMatch.HyphenPrefix(cmd.Name, p))
                    hits.Add(cmd);
            }
            return hits;
        }
    }
}
