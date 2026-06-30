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
    //  * A theme is (accent, mode). We render with a STOCK Krypton palette mode
    //    (KryptonManager.GlobalPaletteMode = Office2007Blue / SparkleBlue|Orange|
    //    Purple) exactly as the Krypton sample apps do - no custom palette and no
    //    per-element overrides, so the whole window is internally consistent. In
    //    dark mode the accent picks the Sparkle variant; light mode is Office 2007.
    //  * A separate non-rendered "probe" palette tracks the active mode purely so
    //    the few custom-painted, non-Krypton bits (the tab +/x glyph, status strip)
    //    can read the matching theme colors.
    //  * Everything is wrapped defensively: a bad palette path must never crash
    //    theme application.
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

        // The color Krypton uses to draw StatusStrip text, for status-bar labels
        // and custom owner-drawn status items so they read on the themed strip.
        public static Color StatusStripTextColor()
        {
            try
            {
                if (_palette != null)
                {
                    Color c = _palette.ColorTable.StatusStripText;
                    if (!c.IsEmpty && c.A != 0) return c;
                }
            }
            catch { }
            return ReadDark() ? Color.FromArgb(0xDC, 0xDF, 0xE3) : SystemColors.ControlText;
        }

        // Background for the StatusStrip. Krypton does not fill the StatusStrip's
        // background itself (its BackColor shows through), so match it to the MENU
        // bar's color - not the StatusStrip-specific palette color, which Sparkle
        // renders near-black - so the top and bottom bars look alike.
        public static Color StatusStripBackColor()
        {
            try
            {
                if (_palette != null)
                {
                    Color c = _palette.ColorTable.MenuStripGradientBegin;
                    if (!c.IsEmpty && c.A != 0) return c;
                }
            }
            catch { }
            return ReadDark() ? Color.FromArgb(0x4D, 0x58, 0x64) : SystemColors.Control;
        }

        public static void Apply(string accentId, bool dark)
        {
            lock (_lock)
            {
                if (_manager == null) _manager = new KryptonManager();

                // Render with the STOCK palette mode, exactly as the Krypton sample
                // apps do (kryptonManager.GlobalPaletteMode = ...Office2007Blue): the
                // whole window - title bar, menu, panels, strips - is the canonical
                // Krypton theme, with no custom palette or overrides, so the chrome
                // and menu bar match by construction. The accent selects the Sparkle
                // variant in dark mode.
                _manager.GlobalPaletteMode = dark ? DarkSparkleModeManager(accentId)
                                                  : PaletteModeManager.Office2007Blue;
                // Force the shared stock-toolstrip renderer to rebuild on every apply.
                _manager.GlobalApplyToolstrips = false;
                _manager.GlobalApplyToolstrips = true;

                // A non-rendered "probe" palette kept in sync with the active mode.
                // It is never assigned as the global palette; we only read resolved
                // theme colors from it for the handful of custom-painted bits (the
                // tab +/x glyph, the status strip) that aren't Krypton controls.
                if (_palette == null) _palette = new KryptonPalette();
                _palette.BasePaletteMode = dark ? DarkSparkleMode(accentId) : PaletteMode.Office2007Blue;
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

        // Same Sparkle-variant mapping as DarkSparkleMode, for the manager's
        // GlobalPaletteMode (a different enum than the palette's BasePaletteMode).
        private static PaletteModeManager DarkSparkleModeManager(string accentId)
        {
            switch ((accentId ?? "blue").Trim().ToLowerInvariant())
            {
                case "orange": return PaletteModeManager.SparkleOrange;
                case "red": return PaletteModeManager.SparklePurple;
                case "blue":
                default: return PaletteModeManager.SparkleBlue;
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
