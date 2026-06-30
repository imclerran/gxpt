using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    // Bridges the app's (accent, dark) theme selection onto Krypton's global
    // palette so that window chrome - forms, buttons, inputs, headers, and the
    // menu/status strips - is themed cohesively with the custom chat transcript.
    //
    // Design notes:
    //  * The transcript (ChatTranscriptControl) and Catppuccin code highlighting
    //    are deliberately NOT driven from here; they keep their own ThemeColors
    //    intake. This class only colors the Krypton-drawn chrome.
    //  * A theme is (accent, mode). In dark mode the accent maps straight to a
    //    Sparkle palette variant (Blue/Orange/Purple), which recolors the whole
    //    window cohesively - so there is no per-element color injection; we let
    //    Sparkle do the work and only nudge a couple of states (disabled buttons).
    //    Light mode uses the Office 2007 base.
    //  * Everything is wrapped defensively: a bad palette path must never crash
    //    theme application. Worst case, chrome falls back to the inherited base.
    internal static class KryptonThemeBridge
    {
        private static KryptonPalette _palette;
        private static KryptonManager _manager;
        private static readonly object _lock = new object();

        // Apply the current theme selection (read from settings) to all Krypton
        // chrome in one action. Safe to call repeatedly; cheap when unchanged.
        public static void Apply()
        {
            try
            {
                bool dark = ReadDark();
                string accentId = ReadAccentId();
                Apply(accentId, dark);
            }
            catch
            {
                // Never let chrome theming take down the app.
            }
        }

        // True when the app is in dark mode. Exposed so custom owner-drawn strip
        // items (e.g. the tab +/x glyph buttons) can pick a glyph color that reads
        // against the themed strip while letting Krypton draw the button chrome.
        public static bool IsDarkMode()
        {
            return ReadDark();
        }

        // The exact color Krypton uses to draw menu-bar text (File/View/Help), so a
        // custom strip glyph can match it. Falls back to a sensible per-mode color.
        public static Color MenuTextColor()
        {
            try
            {
                if (_palette != null)
                {
                    Color c = _palette.ColorTable.MenuStripText;
                    if (!c.IsEmpty && c.A != 0) return c;
                }
            }
            catch { }
            return ReadDark() ? Color.FromArgb(0xDC, 0xDF, 0xE3) : Color.FromArgb(0x50, 0x50, 0x50);
        }

        public static void Apply(string accentId, bool dark)
        {
            lock (_lock)
            {
                // Reuse one persistent palette + manager for the whole app life.
                // Re-theming mutates this same instance rather than swapping in a
                // new one: swapping (and disposing the old) left the stock
                // toolstrips stuck on the first palette's colors after a toggle.
                if (_manager == null) _manager = new KryptonManager();
                bool first = (_palette == null);
                if (_palette == null) _palette = new KryptonPalette();

                Configure(_palette, accentId, dark);

                if (first)
                {
                    _manager.GlobalPalette = _palette;
                    _manager.GlobalPaletteMode = PaletteModeManager.Custom;
                }

                // Krypton controls repaint from the palette's own change events,
                // but the shared stock-toolstrip renderer only rebuilds when
                // GlobalApplyToolstrips transitions - an in-place palette mutation
                // raises no GlobalPaletteChanged. Toggle off->on to force the
                // MenuStrip / StatusStrip / ToolStrip renderer to rebuild from the
                // freshly reconfigured palette every time.
                _manager.GlobalApplyToolstrips = false;
                _manager.GlobalApplyToolstrips = true;
            }
        }

        // Reconfigure the persistent palette in place for the given (accent, mode).
        private static void Configure(KryptonPalette palette, string accentId, bool dark)
        {
            // Clear any prior-mode overrides so light/dark don't bleed together,
            // then re-base. The Sparkle variant IS the accent in dark mode - it
            // recolors the whole window (chrome, strips, panels, selection) into a
            // cohesive blue/orange/purple, so the color choice maps straight to a
            // Sparkle variant and no separate color injection is needed. Light keeps
            // the period Office 2007 look.
            try { palette.ResetToDefaults(true); }
            catch { }
            palette.BasePaletteMode = dark ? DarkSparkleMode(accentId) : PaletteMode.Office2007Blue;

            // Sparkle's disabled-button treatment reads as a light/un-themed grey
            // against the dark chrome, so give disabled buttons an explicit muted
            // dark face + dimmed text in dark mode. (Light mode keeps Office's
            // disabled look, which already fits.)
            if (dark)
            {
                Color disabledBack = Color.FromArgb(0x3E, 0x47, 0x52);
                Color disabledText = Color.FromArgb(0x80, 0x88, 0x92);
                SetTripleFace(palette.ButtonStyles.ButtonStandalone.StateDisabled, disabledBack, disabledBack, disabledText);
            }
        }

        // The Sparkle palette variant for the chosen accent color. Sparkle ships
        // Blue / Orange / Purple; red maps to Purple (no red variant exists).
        private static PaletteMode DarkSparkleMode(string accentId)
        {
            switch ((accentId ?? "blue").Trim().ToLowerInvariant())
            {
                case "orange": return PaletteMode.SparkleOrange;
                case "red": return PaletteMode.SparklePurple;
                case "blue":
                default: return PaletteMode.SparkleBlue;
            }
        }

        // Selection-highlight color matching the active Sparkle variant, so a
        // selected grid row reads blue under Sparkle Blue, orange under Sparkle
        // Orange, etc. - consistent with the chrome rather than a fixed accent.
        private static Color SelectionColor(string accentId)
        {
            switch ((accentId ?? "blue").Trim().ToLowerInvariant())
            {
                case "orange": return Color.FromArgb(0xE3, 0x9B, 0x3F);
                case "red": return Color.FromArgb(0x8A, 0x70, 0xC8);
                case "blue":
                default: return Color.FromArgb(0x49, 0x86, 0xD6);
            }
        }

        // Paint a (plain or Krypton) DataGridView's cell body colors to match the
        // active theme. KryptonDataGridView leaves cell interiors to the standard
        // DataGridView styles (white by default), so set them explicitly: a dark
        // blue-grey matching the Sparkle chrome in dark mode, with the selection
        // highlight following the active Sparkle variant. The column header is left
        // at its default rendering. Light mode keeps the standard light grid.
        public static void StyleDataGrid(DataGridView grid)
        {
            if (grid == null) return;
            bool dark = ReadDark();

            Color cellBack, cellFore, lines, selBack, selFore;
            if (dark)
            {
                cellBack = Color.FromArgb(0x3C, 0x46, 0x51); // dark blue-grey cells
                cellFore = Color.FromArgb(0xE8, 0xEA, 0xED); // near-white text
                lines = Color.FromArgb(0x58, 0x64, 0x72);    // subtle gridlines
                selBack = SelectionColor(ReadAccentId());    // matches Sparkle variant
                selFore = Color.White;
            }
            else
            {
                cellBack = SystemColors.Window;
                cellFore = SystemColors.WindowText;
                lines = SystemColors.ControlDark;
                selBack = SystemColors.Highlight;
                selFore = SystemColors.HighlightText;
            }

            try
            {
                grid.BackgroundColor = cellBack; // fills the empty area below the rows
                grid.GridColor = lines;

                DataGridViewCellStyle cs = grid.DefaultCellStyle;
                cs.BackColor = cellBack;
                cs.ForeColor = cellFore;
                cs.SelectionBackColor = selBack;
                cs.SelectionForeColor = selFore;
            }
            catch { }
        }

        // --- Style-group helpers (all paths verified against Krypton.Toolkit.xml) ---

        private static void SetTripleFace(PaletteTriple state, Color back1, Color back2, Color text)
        {
            if (state == null) return;
            try { state.Back.Color1 = back1; } catch { }
            try { state.Back.Color2 = back2; } catch { }
            try { state.Content.ShortText.Color1 = text; } catch { }
            try { state.Content.LongText.Color1 = text; } catch { }
        }

        // --- Settings intake (mirrors ThemeManager; no settings-schema change) ----

        private static bool ReadDark()
        {
            try
            {
                string theme = AppSettings.GetString("theme");
                return !string.IsNullOrEmpty(theme) &&
                       theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ReadAccentId()
        {
            try
            {
                string id = AppSettings.GetString("color_theme");
                return string.IsNullOrEmpty(id) ? "blue" : id;
            }
            catch { return "blue"; }
        }
    }
}
