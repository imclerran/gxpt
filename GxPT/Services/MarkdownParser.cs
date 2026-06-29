// MarkdownParser.cs
// Simple Markdown parser for chat transcript display
// Target: .NET 3.5, Windows XP compatible

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxPT
{
    // ---------- Markdown model ----------
    public enum BlockType { Paragraph, Heading, CodeBlock, BulletList, NumberedList, Table, EditDiff, Error, Divider }

    [Flags]
    public enum RunStyle { Normal = 0, Bold = 1, Italic = 2, Code = 4, Link = 8 }

    public abstract class Block
    {
        public BlockType Type;
    }

    public sealed class HeadingBlock : Block
    {
        public int Level;
        public List<InlineRun> Inlines;
    }

    public sealed class ParagraphBlock : Block
    {
        public List<InlineRun> Inlines;
    }

    public sealed class CodeBlock : Block
    {
        public string Text;
        public string Language; // Language for syntax highlighting
    }

    // A bespoke, chromeless, collapsible edit-diff record (files__edit). Carries only a stable key;
    // the {path, diff body, +/- counts, collapse state} are looked up at render time from the
    // transcript control's side map (the diff is derived from the persisted tool-call arguments, so
    // nothing diff-specific is stored in the conversation document). Anchored in the markdown by a
    // content-free sentinel line — see EditDiffSentinel/TryParseEditDiffKey.
    public sealed class EditDiffBlock : Block
    {
        public string Key;
    }

    // A chrome-less, red, user-facing error notice (e.g. a failed/empty model response). Unlike the
    // edit-diff record, it is self-contained: the message text travels inside the sentinel (Base64-
    // encoded), so it renders correctly even after the transcript is reloaded, with no side map.
    // Anchored in the markdown by a content-free sentinel line — see ErrorSentinel/TryParseErrorMessage.
    public sealed class ErrorBlock : Block
    {
        public string Message;
    }

    // A horizontal rule / thematic break, emitted for a line of 3+ '-', '*', or '_' (the markdown
    // "---" divider syntax). Carries no content; the transcript control draws a full-width line.
    public sealed class DividerBlock : Block
    {
    }

    public sealed class BulletListBlock : Block
    {
        public List<ListItem> Items = new List<ListItem>();
    }

    public sealed class NumberedListBlock : Block
    {
        public List<ListItem> Items = new List<ListItem>();
    }

    public enum TableAlign { Left, Center, Right }

    public sealed class TableBlock : Block
    {
        public List<List<InlineRun>> Header = new List<List<InlineRun>>();
        public List<TableAlign> Alignments = new List<TableAlign>();
        public List<List<List<InlineRun>>> Rows = new List<List<List<InlineRun>>>();
    }

    public sealed class ListItem
    {
        public List<InlineRun> Content;
        public int IndentLevel; // 0 = top level, 1 = nested once, etc.
        // For numbered lists, preserve the original number and delimiter ('.' or ')') from markdown
        public int? Number; // null for bullets or when not parsed
        public char NumberDelimiter; // '.' or ')'; default '.' when Number is set and delimiter not parsed
    }

    public sealed class InlineRun
    {
        public string Text;
        public RunStyle Style;
        public string LinkUrl; // non-null when this run is a hyperlink
    }

    public static class MarkdownParser
    {
        // ---------- Edit-diff sentinel ----------
        // A content-free anchor line emitted in place of the generic "using files / edit" marker.
        // Uses mathematical white square brackets (U+27E6/U+27E7) so it can't collide with model text
        // or ordinary markdown. The transcript control resolves the key to the actual diff record.
        public const string EditDiffOpen = "⟦editdiff:";
        public const string EditDiffClose = "⟧";

        public static string EditDiffSentinel(string key)
        {
            return EditDiffOpen + (key ?? string.Empty) + EditDiffClose;
        }

        public static bool TryParseEditDiffKey(string line, out string key)
        {
            key = null;
            if (line == null) return false;
            string t = line.Trim();
            if (!t.StartsWith(EditDiffOpen, StringComparison.Ordinal) ||
                !t.EndsWith(EditDiffClose, StringComparison.Ordinal)) return false;
            int start = EditDiffOpen.Length;
            int len = t.Length - start - EditDiffClose.Length;
            if (len <= 0) return false;
            key = t.Substring(start, len);
            return true;
        }

        // ---------- Error sentinel ----------
        // A self-contained anchor for a red error notice. The message is Base64-encoded (UTF-8) so it
        // can carry arbitrary text on a single line without colliding with markdown or the sentinel
        // brackets. Same mathematical white square brackets as the edit-diff sentinel.
        public const string ErrorOpen = "⟦error:";
        public const string ErrorClose = "⟧";

        public static string ErrorSentinel(string message)
        {
            string encoded;
            try { encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message ?? string.Empty)); }
            catch { encoded = string.Empty; }
            return ErrorOpen + encoded + ErrorClose;
        }

        public static bool TryParseErrorMessage(string line, out string message)
        {
            message = null;
            if (line == null) return false;
            string t = line.Trim();
            if (!t.StartsWith(ErrorOpen, StringComparison.Ordinal) ||
                !t.EndsWith(ErrorClose, StringComparison.Ordinal)) return false;
            int start = ErrorOpen.Length;
            int len = t.Length - start - ErrorClose.Length;
            if (len < 0) return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(t.Substring(start, len));
                message = System.Text.Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch { return false; }
        }

        // ---------- Public API ----------
        public static List<Block> ParseMarkdown(string md)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrEmpty(md))
            {
                blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = new List<InlineRun>() });
                return blocks;
            }

            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inCode = false;
            string codeLanguage = null; // Track the language for current code block
            string codeFenceIndent = null; // Leading whitespace before opening ``` to strip from code lines
            var codeAccum = new System.Text.StringBuilder();
            var paraAccum = new System.Text.StringBuilder();
            List<ListItem> bulletAccum = null;
            List<ListItem> numberedAccum = null;

            Action flushParagraph = delegate
            {
                string t = paraAccum.ToString().Trim();
                paraAccum.Length = 0;
                if (t.Length > 0)
                    blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = ParseInlines(t) });
            };

            Action flushBullets = delegate
            {
                if (bulletAccum != null && bulletAccum.Count > 0)
                {
                    var bl = new BulletListBlock { Type = BlockType.BulletList };
                    bl.Items.AddRange(bulletAccum);
                    blocks.Add(bl);
                    bulletAccum = null;
                }
            };

            Action flushNumbered = delegate
            {
                if (numberedAccum != null && numberedAccum.Count > 0)
                {
                    var nl = new NumberedListBlock { Type = BlockType.NumberedList };
                    nl.Items.AddRange(numberedAccum);
                    blocks.Add(nl);
                    numberedAccum = null;
                }
            };

            Action flushCode = delegate
            {
                if (codeAccum.Length > 0 || inCode)
                {
                    var text = codeAccum.ToString();
                    // Trim one leading/trailing newline if present
                    if (text.StartsWith("\n")) text = text.Substring(1);
                    if (text.EndsWith("\n")) text = text.Substring(0, text.Length - 1);
                    blocks.Add(new CodeBlock { Type = BlockType.CodeBlock, Text = text, Language = codeLanguage });
                    codeAccum.Length = 0;
                    codeLanguage = null;
                }
            };

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                // Support indented fenced code blocks by checking after leading whitespace
                int fenceWs = 0;
                while (fenceWs < line.Length && (line[fenceWs] == ' ' || line[fenceWs] == '\t')) fenceWs++;
                string lineTrimStart = line.Substring(fenceWs);
                if (lineTrimStart.TrimStart().StartsWith("```"))
                {
                    if (!inCode)
                    {
                        // enter code: first flush any open containers
                        flushParagraph();
                        flushBullets();
                        flushNumbered();
                        inCode = true;
                        // Remember the indentation before the opening fence to normalize code lines
                        codeFenceIndent = line.Substring(0, fenceWs);

                        // Extract language from opening fence (e.g., "```csharp")
                        if (lineTrimStart.Length > 3)
                        {
                            codeLanguage = lineTrimStart.Substring(3).Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(codeLanguage))
                                codeLanguage = null;
                        }
                    }
                    else
                    {
                        // leave code
                        inCode = false;
                        flushCode();
                        codeFenceIndent = null;
                    }
                    continue;
                }

                if (inCode)
                {
                    // Strip the same indentation that preceded the opening fence, if present
                    string codeLine = line;
                    if (!string.IsNullOrEmpty(codeFenceIndent) && codeLine.StartsWith(codeFenceIndent))
                        codeLine = codeLine.Substring(codeFenceIndent.Length);
                    codeAccum.Append(codeLine).Append('\n');
                    continue;
                }

                // blank line → flush paragraph/bullets
                if (line.Trim().Length == 0)
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    continue;
                }

                // edit-diff sentinel → its own bespoke block (content looked up at render time)
                string editDiffKey;
                if (TryParseEditDiffKey(line, out editDiffKey))
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    blocks.Add(new EditDiffBlock { Type = BlockType.EditDiff, Key = editDiffKey });
                    continue;
                }

                // error sentinel → its own chrome-less red notice (message carried inline)
                string errorMessage;
                if (TryParseErrorMessage(line, out errorMessage))
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    blocks.Add(new ErrorBlock { Type = BlockType.Error, Message = errorMessage });
                    continue;
                }

                // horizontal rule / thematic break (--- / *** / ___)? Checked before bullets so that a
                // spaced rule like "- - -" isn't mistaken for a bullet item.
                if (IsHorizontalRule(line))
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    blocks.Add(new DividerBlock { Type = BlockType.Divider });
                    continue;
                }

                // heading?
                int h = HeadingLevel(line);
                if (h > 0)
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    string content = line.Substring(h + 1).TrimEnd();
                    blocks.Add(new HeadingBlock { Type = BlockType.Heading, Level = h, Inlines = ParseInlines(content) });
                    continue;
                }

                // table? Expect header row with pipes followed by a separator row of dashes/colons
                if (LooksLikeTableHeader(line) && (li + 1) < lines.Length && LooksLikeTableSeparator(lines[li + 1]))
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    int consumed;
                    var table = ParseTable(lines, li, out consumed);
                    if (table != null)
                    {
                        blocks.Add(table);
                        li += consumed - 1; // consumed includes header+separator+rows; -1 for loop increment
                        continue;
                    }
                }

                // bullet?
                string bulletText;
                int bulletIndent;
                if (IsBullet(line, out bulletText, out bulletIndent))
                {
                    flushParagraph();
                    flushNumbered(); // switch list types
                    if (bulletAccum == null) bulletAccum = new List<ListItem>();
                    bulletAccum.Add(new ListItem { Content = ParseInlines(bulletText), IndentLevel = bulletIndent });
                    continue;
                }

                // numbered item?
                string numberedText;
                int numberedIndent;
                int numberedValue; char numberedDelim;
                if (IsNumberedItem(line, out numberedText, out numberedIndent, out numberedValue, out numberedDelim))
                {
                    flushParagraph();
                    flushBullets(); // switch list types
                    if (numberedAccum == null) numberedAccum = new List<ListItem>();
                    numberedAccum.Add(new ListItem { Content = ParseInlines(numberedText), IndentLevel = numberedIndent, Number = numberedValue, NumberDelimiter = numberedDelim == '\0' ? '.' : numberedDelim });
                    continue;
                }

                // otherwise paragraph text (accumulate)
                // Preserve single newlines within a paragraph as hard line breaks
                if (paraAccum.Length > 0) paraAccum.Append('\n');
                paraAccum.Append(line.TrimEnd());
            }

            // end flush
            if (inCode) { inCode = false; }
            flushCode();
            flushParagraph();
            flushBullets();
            flushNumbered();

            if (blocks.Count == 0)
                blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = new List<InlineRun>() });

            return blocks;
        }

        // Very simple inline parser: **bold**, *italic*, __bold__, _italic_, `code`, [text](url)
        // Notes:
        // - For underscores, apply stricter rules so identifiers_like_this aren't parsed as emphasis.
        //   Opening '_' or '__' must be at start-of-text or preceded by whitespace; closing must be
        //   followed by end-of-text, whitespace, or punctuation. Underscores with letters/digits on
        //   both sides are treated as literal.
        // - Asterisks keep the original simple toggle behavior.
        // - Escapes are not handled exhaustively (sufficient for chat UI).
        public static List<InlineRun> ParseInlines(string text)
        {
            var runs = new List<InlineRun>();
            if (string.IsNullOrEmpty(text))
            {
                runs.Add(new InlineRun { Text = "", Style = RunStyle.Normal });
                return runs;
            }

            int i = 0;
            RunStyle style = RunStyle.Normal;
            var sb = new System.Text.StringBuilder();

            Action flush = delegate
            {
                if (sb.Length > 0)
                {
                    AddTextWithAutoLinks(runs, sb.ToString(), style);
                    sb.Length = 0;
                }
            };

            while (i < text.Length)
            {
                // hyperlink [text](url)
                if (text[i] == '[')
                {
                    int linkTextStart = i + 1;
                    int bracketEnd = text.IndexOf(']', linkTextStart);
                    if (bracketEnd > linkTextStart && bracketEnd + 1 < text.Length && text[bracketEnd + 1] == '(')
                    {
                        int urlStart = bracketEnd + 2;
                        int parenEnd = text.IndexOf(')', urlStart);
                        if (parenEnd > urlStart)
                        {
                            // Flush pending normal text
                            flush();
                            string linkText = text.Substring(linkTextStart, bracketEnd - linkTextStart);
                            string url = text.Substring(urlStart, parenEnd - urlStart).Trim();
                            if (url.Length > 0)
                            {
                                runs.Add(new InlineRun { Text = linkText, Style = RunStyle.Link, LinkUrl = url });
                                i = parenEnd + 1;
                                continue;
                            }
                        }
                    }
                }
                // inline code
                if (text[i] == '`')
                {
                    flush();
                    i++;
                    int start = i;
                    while (i < text.Length && text[i] != '`') i++;
                    string code = text.Substring(start, i - start);
                    runs.Add(new InlineRun { Text = code, Style = RunStyle.Code });
                    if (i < text.Length && text[i] == '`') i++;
                    continue;
                }

                // **bold** (asterisks only keep simple toggle semantics)
                if (i + 1 < text.Length && (text[i] == '*' && text[i + 1] == '*'))
                {
                    bool turnOn = (style & RunStyle.Bold) == 0;
                    flush();
                    if (turnOn) style |= RunStyle.Bold; else style &= ~RunStyle.Bold;
                    i += 2;
                    continue;
                }

                // *italic* (asterisk toggle semantics)
                if (text[i] == '*')
                {
                    bool turnOn = (style & RunStyle.Italic) == 0;
                    flush();
                    if (turnOn) style |= RunStyle.Italic; else style &= ~RunStyle.Italic;
                    i++;
                    continue;
                }

                // __bold__ with underscores using stricter word-boundary rules
                if (i + 1 < text.Length && text[i] == '_' && text[i + 1] == '_')
                {
                    int openerLen = 2;
                    if (IsValidUnderscoreOpener(text, i, openerLen))
                    {
                        int closeIndex;
                        if (TryFindUnderscoreCloser(text, i + openerLen, openerLen, out closeIndex))
                        {
                            // Found a valid emphasis span; flush text before, then parse inside and apply Bold
                            flush();
                            string inner = text.Substring(i + openerLen, closeIndex - (i + openerLen));
                            var innerRuns = ParseInlines(inner);
                            foreach (var r in innerRuns)
                            {
                                r.Style |= (style | RunStyle.Bold);
                                runs.Add(r);
                            }
                            i = closeIndex + openerLen;
                            continue;
                        }
                    }
                    // Not a valid opener or no valid closer: treat the first '_' literally and continue
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                // _italic_ with underscores using stricter word-boundary rules
                if (text[i] == '_')
                {
                    int openerLen = 1;
                    if (IsValidUnderscoreOpener(text, i, openerLen))
                    {
                        int closeIndex;
                        if (TryFindUnderscoreCloser(text, i + openerLen, openerLen, out closeIndex))
                        {
                            flush();
                            string inner = text.Substring(i + openerLen, closeIndex - (i + openerLen));
                            var innerRuns = ParseInlines(inner);
                            foreach (var r in innerRuns)
                            {
                                r.Style |= (style | RunStyle.Italic);
                                runs.Add(r);
                            }
                            i = closeIndex + openerLen;
                            continue;
                        }
                    }
                    // Not a valid opener or no valid closer: treat as literal
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                sb.Append(text[i]);
                i++;
            }
            flush();
            return runs;
        }

        // Detect bare URLs in the given text and split into link/non-link runs.
        // Keeps existing style flags (e.g., Bold/Italic) and sets Link flag on URL segments.
        private static void AddTextWithAutoLinks(List<InlineRun> runs, string text, RunStyle baseStyle)
        {
            if (string.IsNullOrEmpty(text))
            {
                runs.Add(new InlineRun { Text = string.Empty, Style = baseStyle });
                return;
            }

            // Simple http/https URL regex; exclude whitespace and common trailing punctuation.
            // Examples matched: https://example.com, http://foo.bar/baz?x=1#y
            Regex rx = new Regex(@"https?://[^\x00-\x20<>""'()\[\]{}]+", RegexOptions.IgnoreCase);
            int idx = 0;
            foreach (Match m in rx.Matches(text))
            {
                if (!m.Success) continue;
                if (m.Index > idx)
                {
                    string before = text.Substring(idx, m.Index - idx);
                    if (before.Length > 0)
                        runs.Add(new InlineRun { Text = before, Style = baseStyle });
                }

                string url = m.Value;
                // Trim trailing punctuation that commonly follows URLs in prose
                url = TrimTrailingPunctuation(url);
                if (url.Length > 0)
                {
                    runs.Add(new InlineRun { Text = url, Style = baseStyle | RunStyle.Link, LinkUrl = url });
                }
                idx = m.Index + m.Length;
            }
            if (idx < text.Length)
            {
                string tail = text.Substring(idx);
                if (tail.Length > 0)
                    runs.Add(new InlineRun { Text = tail, Style = baseStyle });
            }
        }

        private static string TrimTrailingPunctuation(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            // Balance closing parentheses: keep a trailing ')' only if there are more '(' than ')' without it
            Func<string, bool> hasExtraClosingParen = s =>
            {
                int open = 0, close = 0;
                for (int i = 0; i < s.Length; i++) { if (s[i] == '(') open++; else if (s[i] == ')') close++; }
                return close > open;
            };

            // Remove trailing chars commonly not part of the URL
            while (url.Length > 0)
            {
                char c = url[url.Length - 1];
                if (c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?' || c == ']' || c == '}' || c == '>' || c == '\'' || c == '"')
                {
                    url = url.Substring(0, url.Length - 1);
                    continue;
                }
                if (c == ')' && hasExtraClosingParen(url))
                {
                    url = url.Substring(0, url.Length - 1);
                    continue;
                }
                break;
            }
            return url;
        }

        // ---------- Helper methods ----------
        private static bool IsBullet(string line, out string content, out int indentLevel)
        {
            content = null;
            indentLevel = 0;

            // Count leading spaces/tabs for nesting
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            {
                if (line[i] == ' ') indentLevel++;
                else indentLevel += 4; // treat tab as 4 spaces
                i++;
            }
            indentLevel = indentLevel / 2; // 2 spaces = 1 indent level

            if (i >= line.Length) return false;

            char c = line[i];
            if ((c == '-' || c == '*' || c == '+') && i + 1 < line.Length && (line[i + 1] == ' ' || line[i + 1] == '\t'))
            {
                content = line.Substring(i + 2).TrimEnd();
                return true;
            }
            return false;
        }

        private static bool IsNumberedItem(string line, out string content, out int indentLevel, out int number, out char delimiter)
        {
            content = null;
            indentLevel = 0;
            number = 0;
            delimiter = '\0';

            // Count leading spaces/tabs for nesting
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            {
                if (line[i] == ' ') indentLevel++;
                else indentLevel += 4; // treat tab as 4 spaces
                i++;
            }
            indentLevel = indentLevel / 2; // 2 spaces = 1 indent level

            if (i >= line.Length) return false;

            // Look for number followed by . or )
            int start = i;
            while (i < line.Length && char.IsDigit(line[i])) i++;

            if (i > start && i < line.Length && (line[i] == '.' || line[i] == ')') &&
                i + 1 < line.Length && (line[i + 1] == ' ' || line[i + 1] == '\t'))
            {
                // Try parse number
                try { number = int.Parse(line.Substring(start, i - start), System.Globalization.CultureInfo.InvariantCulture); }
                catch { number = 0; }
                delimiter = line[i];
                content = line.Substring(i + 2).TrimEnd();
                return true;
            }
            return false;
        }

        // A thematic break: a line made up solely of 3 or more of the same marker ('-', '*', or '_'),
        // optionally separated by spaces or tabs (e.g. "---", "***", "___", "- - -"). Matches the
        // CommonMark thematic-break rule. Note a lone "---" never reaches the table path because that
        // requires a preceding header row containing a pipe.
        private static bool IsHorizontalRule(string line)
        {
            if (line == null) return false;
            string s = line.Trim();
            if (s.Length < 3) return false;
            char marker = s[0];
            if (marker != '-' && marker != '*' && marker != '_') return false;
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == marker) count++;
                else if (c == ' ' || c == '\t') continue;
                else return false;
            }
            return count >= 3;
        }

        private static int HeadingLevel(string line)
        {
            int i = 0;
            while (i < line.Length && i < 6 && line[i] == '#') i++;
            if (i > 0 && i < line.Length && line[i] == ' ') return i;
            return 0;
        }

        // ---------- Table parsing (GitHub-style pipe tables) ----------
        private static bool LooksLikeTableHeader(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            // must contain at least one pipe and non-pipe text
            if (line.IndexOf('|') < 0) return false;
            // avoid treating code fences or headings as tables
            if (line.StartsWith("|") || char.IsLetterOrDigit(line.TrimStart()[0]))
                return true;
            return false;
        }

        private static bool LooksLikeTableSeparator(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            // a separator looks like: | --- | :---: | ---: |
            string s = line.Trim();
            if (!s.Contains("-")) return false;
            // Check segments between pipes contain only '-', ':' and optional spaces
            var parts = SplitTableRow(line);
            if (parts == null || parts.Count == 0) return false;
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.Length == 0) return false;
                foreach (char c in t) if (c != '-' && c != ':') return false;
                if (t.IndexOf('-') < 0) return false; // must have at least one dash
            }
            return true;
        }

        private static List<string> SplitTableRow(string line)
        {
            // Split by pipes, but allow leading/trailing pipes; no escaping support (simple)
            string s = line;
            if (s.StartsWith("|")) s = s.Substring(1);
            if (s.EndsWith("|")) s = s.Substring(0, s.Length - 1);
            var cells = new List<string>(s.Split('|'));
            // Trim one space around cells
            for (int i = 0; i < cells.Count; i++) cells[i] = cells[i].Trim();
            return cells;
        }

        private static TableBlock ParseTable(string[] lines, int startIndex, out int consumed)
        {
            consumed = 0;
            int i = startIndex;
            if (i + 1 >= lines.Length) return null;

            var headerCells = SplitTableRow(lines[i]);
            var sepCells = SplitTableRow(lines[i + 1]);
            if (headerCells == null || sepCells == null) return null;
            // Alignments derive from separator cells
            var aligns = new List<TableAlign>();
            foreach (var cell in sepCells)
            {
                string t = cell.Trim();
                bool left = t.StartsWith(":");
                bool right = t.EndsWith(":");
                if (left && right) aligns.Add(TableAlign.Center);
                else if (right) aligns.Add(TableAlign.Right);
                else aligns.Add(TableAlign.Left);
            }

            // Normalize number of columns to separator length
            int cols = aligns.Count;
            if (cols == 0) return null;

            var tbl = new TableBlock { Type = BlockType.Table };
            for (int c = 0; c < cols; c++)
            {
                string cell = (c < headerCells.Count) ? headerCells[c] : string.Empty;
                tbl.Header.Add(ParseInlines(cell));
                tbl.Alignments.Add(aligns[c]);
            }

            // Read body rows until a blank line or non-table line
            i += 2; // skip header and separator
            while (i < lines.Length)
            {
                string l = lines[i];
                if (l == null || l.Trim().Length == 0) break;
                // stop on lines that look like other blocks (simple heuristic): heading, bullet, numbered, code fence
                string tmpBulletText; int tmpBulletIndent;
                string tmpNumText; int tmpNumIndent; int tmpNumVal; char tmpNumDelim;
                bool isBullet = IsBullet(l, out tmpBulletText, out tmpBulletIndent);
                bool isNumbered = IsNumberedItem(l, out tmpNumText, out tmpNumIndent, out tmpNumVal, out tmpNumDelim);
                if (HeadingLevel(l) > 0 || isBullet || isNumbered || l.TrimStart().StartsWith("```")) break;
                if (l.IndexOf('|') < 0) break;

                var rowCells = SplitTableRow(l);
                var row = new List<List<InlineRun>>();
                for (int c = 0; c < cols; c++)
                {
                    string cell = (c < rowCells.Count) ? rowCells[c] : string.Empty;
                    row.Add(ParseInlines(cell));
                }
                tbl.Rows.Add(row);
                i++;
            }

            consumed = Math.Max(2, i - startIndex);
            return tbl;
        }

        // ---------- Inline underscore helper rules ----------
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c);
        }

        private static bool IsWhitespace(char c)
        {
            return char.IsWhiteSpace(c);
        }

        private static bool IsPunctuation(char c)
        {
            return char.IsPunctuation(c);
        }

        // Opening '_' or '__' is valid only if at start or preceded by whitespace, and not when both sides are word chars
        private static bool IsValidUnderscoreOpener(string text, int index, int len)
        {
            if (index < 0 || index + len > text.Length) return false;

            // Must be at start or preceded by whitespace
            if (!(index == 0 || IsWhitespace(text[index - 1]))) return false;

            // If letters/digits on both sides, it's within a word → not an opener
            char prev = (index > 0) ? text[index - 1] : '\0';
            char next = (index + len < text.Length) ? text[index + len] : '\0';
            if (index > 0 && index + len < text.Length && IsWordChar(prev) && IsWordChar(next))
                return false;

            return true;
        }

        // Find a matching closer at or after 'searchFrom' for '_' or '__'
        private static bool TryFindUnderscoreCloser(string text, int searchFrom, int len, out int closeIndex)
        {
            closeIndex = -1;
            if (string.IsNullOrEmpty(text) || searchFrom >= text.Length) return false;

            for (int j = searchFrom; j <= text.Length - len; j++)
            {
                bool allUnderscores = true;
                for (int k = 0; k < len; k++)
                {
                    if (text[j + k] != '_') { allUnderscores = false; break; }
                }
                if (!allUnderscores) continue;

                int after = j + len;
                // Closing must be followed by end-of-text, whitespace, or punctuation
                if (after == text.Length || IsWhitespace(text[after]) || IsPunctuation(text[after]))
                {
                    closeIndex = j;
                    return true;
                }
            }
            return false;
        }
    }
}
