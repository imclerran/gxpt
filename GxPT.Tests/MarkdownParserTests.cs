using System.Collections.Generic;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public class MarkdownParserTests
    {
        private static string InlineText(IEnumerable<InlineRun> runs)
        {
            var sb = new StringBuilder();
            if (runs != null)
                foreach (var r in runs)
                    if (r != null && r.Text != null) sb.Append(r.Text);
            return sb.ToString();
        }

        private static T FirstBlock<T>(List<Block> blocks) where T : Block
        {
            foreach (var b in blocks)
            {
                var t = b as T;
                if (t != null) return t;
            }
            return null;
        }

        [Fact]
        public void EmptyInput_ReturnsSingleEmptyParagraph()
        {
            var blocks = MarkdownParser.ParseMarkdown("");
            Assert.Single(blocks);
            var p = Assert.IsType<ParagraphBlock>(blocks[0]);
            Assert.Empty(p.Inlines);
        }

        [Fact]
        public void Heading_ParsesLevelAndText()
        {
            var blocks = MarkdownParser.ParseMarkdown("## Title Here");
            var h = FirstBlock<HeadingBlock>(blocks);
            Assert.NotNull(h);
            Assert.Equal(2, h.Level);
            Assert.Equal("Title Here", InlineText(h.Inlines));
        }

        [Fact]
        public void FencedCodeBlock_CapturesLanguageAndText()
        {
            var blocks = MarkdownParser.ParseMarkdown("```csharp\nint x = 1;\n```");
            var code = FirstBlock<CodeBlock>(blocks);
            Assert.NotNull(code);
            Assert.Equal("csharp", code.Language);
            Assert.Equal("int x = 1;", code.Text);
        }

        [Fact]
        public void BulletList_CollectsItems()
        {
            var blocks = MarkdownParser.ParseMarkdown("- one\n- two");
            var list = FirstBlock<BulletListBlock>(blocks);
            Assert.NotNull(list);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal("one", InlineText(list.Items[0].Content));
            Assert.Equal("two", InlineText(list.Items[1].Content));
        }

        [Fact]
        public void NestedBullet_HasIndentLevel()
        {
            var blocks = MarkdownParser.ParseMarkdown("- a\n  - b");
            var list = FirstBlock<BulletListBlock>(blocks);
            Assert.NotNull(list);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(0, list.Items[0].IndentLevel);
            Assert.Equal(1, list.Items[1].IndentLevel);
        }

        [Fact]
        public void NumberedList_PreservesNumbers()
        {
            var blocks = MarkdownParser.ParseMarkdown("1. first\n2. second");
            var list = FirstBlock<NumberedListBlock>(blocks);
            Assert.NotNull(list);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(1, list.Items[0].Number);
            Assert.Equal(2, list.Items[1].Number);
        }

        [Fact]
        public void Table_ParsesHeaderAndRow()
        {
            var blocks = MarkdownParser.ParseMarkdown("| A | B |\n| --- | --- |\n| 1 | 2 |");
            var table = FirstBlock<TableBlock>(blocks);
            Assert.NotNull(table);
            Assert.Equal(2, table.Header.Count);
            Assert.Equal("A", InlineText(table.Header[0]));
            Assert.Equal("B", InlineText(table.Header[1]));
            Assert.Single(table.Rows);
            Assert.Equal(2, table.Rows[0].Count);
            Assert.Equal("1", InlineText(table.Rows[0][0]));
            Assert.Equal("2", InlineText(table.Rows[0][1]));
        }

        [Fact]
        public void Inline_Bold_SetsBoldStyle()
        {
            var runs = MarkdownParser.ParseInlines("**bold**");
            Assert.Contains(runs, r => r.Text == "bold" && (r.Style & RunStyle.Bold) != 0);
        }

        [Fact]
        public void Inline_Italic_SetsItalicStyle()
        {
            var runs = MarkdownParser.ParseInlines("*italic*");
            Assert.Contains(runs, r => r.Text == "italic" && (r.Style & RunStyle.Italic) != 0);
        }

        [Fact]
        public void Inline_Code_SetsCodeStyle()
        {
            var runs = MarkdownParser.ParseInlines("`x = 1`");
            Assert.Contains(runs, r => r.Text == "x = 1" && (r.Style & RunStyle.Code) != 0);
        }

        [Fact]
        public void Inline_Link_SetsLinkUrl()
        {
            var runs = MarkdownParser.ParseInlines("[Google](https://google.com)");
            Assert.Contains(runs, r => r.Text == "Google"
                && (r.Style & RunStyle.Link) != 0
                && r.LinkUrl == "https://google.com");
        }

        [Fact]
        public void Inline_BareUrl_BecomesLink()
        {
            var runs = MarkdownParser.ParseInlines("see https://example.com now");
            Assert.Contains(runs, r => r.LinkUrl == "https://example.com" && (r.Style & RunStyle.Link) != 0);
        }

        [Fact]
        public void Inline_UnderscoresInsideWord_AreNotItalic()
        {
            // snake_case identifiers must not be treated as emphasis
            var runs = MarkdownParser.ParseInlines("call snake_case_name here");
            foreach (var r in runs)
                Assert.True((r.Style & RunStyle.Italic) == 0, "underscores within a word should not be italic");
        }

        [Fact]
        public void Inline_UnderscoreEmphasis_IsItalic()
        {
            // A proper _word_ delimited by boundaries should be italic
            var runs = MarkdownParser.ParseInlines("an _emphasized_ word");
            Assert.Contains(runs, r => r.Text == "emphasized" && (r.Style & RunStyle.Italic) != 0);
        }

        [Fact]
        public void Inline_BareUrl_TrailingPeriodExcludedFromUrl()
        {
            var runs = MarkdownParser.ParseInlines("visit https://example.com.");
            // The trailing sentence period must not be part of the captured URL.
            Assert.Contains(runs, r => r.LinkUrl == "https://example.com");
            Assert.DoesNotContain(runs, r => r.LinkUrl != null && r.LinkUrl.EndsWith("."));
        }

        [Fact]
        public void Table_ParsesColumnAlignments()
        {
            var blocks = MarkdownParser.ParseMarkdown("| L | C | R |\n| :-- | :-: | --: |\n| a | b | c |");
            var table = FirstBlock<TableBlock>(blocks);
            Assert.NotNull(table);
            Assert.Equal(3, table.Alignments.Count);
            Assert.Equal(TableAlign.Left, table.Alignments[0]);
            Assert.Equal(TableAlign.Center, table.Alignments[1]);
            Assert.Equal(TableAlign.Right, table.Alignments[2]);
        }

        [Fact]
        public void Table_RaggedRow_IsPaddedToColumnCount()
        {
            var blocks = MarkdownParser.ParseMarkdown("| A | B |\n| --- | --- |\n| 1 |");
            var table = FirstBlock<TableBlock>(blocks);
            Assert.NotNull(table);
            Assert.Single(table.Rows);
            Assert.Equal(2, table.Rows[0].Count);
            Assert.Equal("1", InlineText(table.Rows[0][0]));
            Assert.Equal("", InlineText(table.Rows[0][1]));
        }

        [Fact]
        public void CodeBlock_WithoutLanguage_HasNullLanguage()
        {
            var blocks = MarkdownParser.ParseMarkdown("```\nplain text\n```");
            var code = FirstBlock<CodeBlock>(blocks);
            Assert.NotNull(code);
            Assert.Null(code.Language);
            Assert.Equal("plain text", code.Text);
        }

        [Fact]
        public void CodeBlock_UnclosedFenceAtEof_StillCaptured()
        {
            var blocks = MarkdownParser.ParseMarkdown("```python\nx = 1");
            var code = FirstBlock<CodeBlock>(blocks);
            Assert.NotNull(code);
            Assert.Equal("python", code.Language);
            Assert.Equal("x = 1", code.Text);
        }

        [Fact]
        public void NumberedList_ParenDelimiter_IsPreserved()
        {
            var blocks = MarkdownParser.ParseMarkdown("1) first\n2) second");
            var list = FirstBlock<NumberedListBlock>(blocks);
            Assert.NotNull(list);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(1, list.Items[0].Number);
            Assert.Equal(')', list.Items[0].NumberDelimiter);
        }

        [Fact]
        public void Divider_TripleDash_ProducesDividerBlock()
        {
            var blocks = MarkdownParser.ParseMarkdown("above\n\n---\n\nbelow");
            Assert.NotNull(FirstBlock<DividerBlock>(blocks));
            // The divider separates the two paragraphs.
            int dividerIdx = blocks.FindIndex(b => b is DividerBlock);
            Assert.True(dividerIdx > 0 && dividerIdx < blocks.Count - 1);
            Assert.IsType<ParagraphBlock>(blocks[dividerIdx - 1]);
            Assert.IsType<ParagraphBlock>(blocks[dividerIdx + 1]);
        }

        [Fact]
        public void Divider_AsteriskAndUnderscoreMarkers_ProduceDividers()
        {
            Assert.NotNull(FirstBlock<DividerBlock>(MarkdownParser.ParseMarkdown("***")));
            Assert.NotNull(FirstBlock<DividerBlock>(MarkdownParser.ParseMarkdown("___")));
            Assert.NotNull(FirstBlock<DividerBlock>(MarkdownParser.ParseMarkdown("-----")));
        }

        [Fact]
        public void Divider_SpacedDashes_AreNotBullets()
        {
            var blocks = MarkdownParser.ParseMarkdown("- - -");
            Assert.NotNull(FirstBlock<DividerBlock>(blocks));
            Assert.Null(FirstBlock<BulletListBlock>(blocks));
        }

        [Fact]
        public void Divider_TwoDashes_IsNotADivider()
        {
            var blocks = MarkdownParser.ParseMarkdown("--");
            Assert.Null(FirstBlock<DividerBlock>(blocks));
        }

        [Fact]
        public void Divider_AfterParagraphWithoutBlankLine_SplitsParagraph()
        {
            var blocks = MarkdownParser.ParseMarkdown("text\n---");
            var p = FirstBlock<ParagraphBlock>(blocks);
            Assert.NotNull(p);
            Assert.Equal("text", InlineText(p.Inlines));
            Assert.NotNull(FirstBlock<DividerBlock>(blocks));
        }

        [Fact]
        public void Divider_TableSeparator_IsStillParsedAsTable()
        {
            // A "---" separator that belongs to a pipe table must not be hijacked as a divider.
            var blocks = MarkdownParser.ParseMarkdown("| A | B |\n| --- | --- |\n| 1 | 2 |");
            Assert.NotNull(FirstBlock<TableBlock>(blocks));
            Assert.Null(FirstBlock<DividerBlock>(blocks));
        }

        [Fact]
        public void Crlf_IsNormalized()
        {
            var blocks = MarkdownParser.ParseMarkdown("# Heading\r\n\r\nA paragraph");
            var h = FirstBlock<HeadingBlock>(blocks);
            var p = FirstBlock<ParagraphBlock>(blocks);
            Assert.NotNull(h);
            Assert.Equal("Heading", InlineText(h.Inlines));
            Assert.NotNull(p);
            Assert.Equal("A paragraph", InlineText(p.Inlines));
        }
    }
}
