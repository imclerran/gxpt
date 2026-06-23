using System;
using System.Text;

namespace ExtensionsMcpServer
{
    // Normalizes a caller-supplied slug into kebab-case (lowercase [a-z0-9] runs joined by single
    // hyphens, boundaries at non-alphanumerics and lower/digit->Upper transitions). Same algorithm as
    // the host's SkillSlug (servers are independent, so it's copied). Returns null when there are no
    // usable [A-Za-z0-9] characters. XP / .NET 3.5 friendly.
    internal static class SkillSlug
    {
        public static string Make(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string trimmed = name.Trim();

            StringBuilder sb = new StringBuilder();
            bool sepPending = false;
            bool prevWasLowerOrDigit = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                bool isUpper = c >= 'A' && c <= 'Z';
                bool isLower = c >= 'a' && c <= 'z';
                bool isDigit = c >= '0' && c <= '9';

                if (!(isUpper || isLower || isDigit))
                {
                    if (sb.Length > 0) sepPending = true;
                    prevWasLowerOrDigit = false;
                    continue;
                }

                if (isUpper && prevWasLowerOrDigit) sepPending = true;
                if (sepPending && sb.Length > 0) sb.Append('-');
                sepPending = false;

                sb.Append(isUpper ? (char)(c - 'A' + 'a') : c);
                prevWasLowerOrDigit = isLower || isDigit;
            }

            if (sb.Length == 0) return null;
            return sb.ToString();
        }
    }
}
