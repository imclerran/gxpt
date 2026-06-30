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
    //    (KryptonManager.GlobalPaletteMode = Office2010Blue / SparkleBlue|Orange|
    //    Purple) exactly as the Krypton sample apps do - no custom palette and no
    //    per-element overrides, so the whole window is internally consistent. In
    //    dark mode the accent picks the Sparkle variant; light mode is Office 2010
    //    (its caption matches the menu, unlike Office 2007's glossier title bar).
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

        // The KryptonColorTable that Krypton is ACTUALLY rendering the toolstrips
        // with (via the global ToolStrip renderer). This is the authoritative source
        // for menu/status colors - the non-rendered probe palette doesn't compute the
        // Krypton-specific colors (MenuStripText / StatusStripText), so reading them
        // off the probe yields empty and falls through to an approximate color that
        // won't match the labels. The probe is only a last-resort fallback.
        private static KryptonColorTable ActiveColorTable()
        {
            try
            {
                ToolStripProfessionalRenderer r = ToolStripManager.Renderer as ToolStripProfessionalRenderer;
                if (r != null) return r.ColorTable as KryptonColorTable;
            }
            catch { }
            return null;
        }

        private static bool Usable(Color c) { return !c.IsEmpty && c.A != 0; }

        // The exact color Krypton uses to draw menu-bar text (File/View/Help), so a
        // custom strip glyph can match it. Falls back to a sensible per-mode color.
        public static Color MenuTextColor()
        {
            try { KryptonColorTable ct = ActiveColorTable(); if (ct != null && Usable(ct.MenuStripText)) return ct.MenuStripText; } catch { }
            try { if (_palette != null && Usable(_palette.ColorTable.MenuStripText)) return _palette.ColorTable.MenuStripText; } catch { }
            return ReadDark() ? Color.FromArgb(0xDC, 0xDF, 0xE3) : Color.FromArgb(0x50, 0x50, 0x50);
        }

        // The exact color Krypton draws StatusStrip text with, so the status labels
        // and custom owner-drawn status items (meter border, etc.) match. Several
        // themes (e.g. Office 2010) leave StatusStripText unset and draw the labels
        // in the general menu/chrome text color instead, so fall back to MenuTextColor
        // rather than a black system color (which didn't match the blue labels).
        public static Color StatusStripTextColor()
        {
            try { KryptonColorTable ct = ActiveColorTable(); if (ct != null && Usable(ct.StatusStripText)) return ct.StatusStripText; } catch { }
            try { if (_palette != null && Usable(_palette.ColorTable.StatusStripText)) return _palette.ColorTable.StatusStripText; } catch { }
            return MenuTextColor();
        }

        // Background for the StatusStrip. Krypton does not fill the StatusStrip's
        // background itself (its BackColor shows through), so match it to the MENU
        // bar's color - not the StatusStrip-specific palette color, which Sparkle
        // renders near-black - so the top and bottom bars look alike.
        public static Color StatusStripBackColor()
        {
            try { KryptonColorTable ct = ActiveColorTable(); if (ct != null && Usable(ct.MenuStripGradientBegin)) return ct.MenuStripGradientBegin; } catch { }
            try { if (_palette != null && Usable(_palette.ColorTable.MenuStripGradientBegin)) return _palette.ColorTable.MenuStripGradientBegin; } catch { }
            return ReadDark() ? Color.FromArgb(0x4D, 0x58, 0x64) : SystemColors.Control;
        }

        public static void Apply(string accentId, bool dark)
        {
            lock (_lock)
            {
                if (_manager == null) _manager = new KryptonManager();

                // Render with the STOCK palette mode, exactly as the Krypton sample
                // apps do (kryptonManager.GlobalPaletteMode = ...Office2010Blue): the
                // whole window - title bar, menu, panels, strips - is the canonical
                // Krypton theme, with no custom palette or overrides, so the chrome
                // and menu bar match by construction. The accent selects the Sparkle
                // variant in dark mode.
                _manager.GlobalPaletteMode = dark ? DarkSparkleModeManager(accentId)
                                                  : PaletteModeManager.Office2010Blue;
                // Force the shared stock-toolstrip renderer to rebuild on every apply.
                _manager.GlobalApplyToolstrips = false;
                _manager.GlobalApplyToolstrips = true;

                // A non-rendered "probe" palette kept in sync with the active mode.
                // It is never assigned as the global palette; we only read resolved
                // theme colors from it for the handful of custom-painted bits (the
                // tab +/x glyph, the status strip) that aren't Krypton controls.
                if (_palette == null) _palette = new KryptonPalette();
                _palette.BasePaletteMode = dark ? DarkSparkleMode(accentId) : PaletteMode.Office2010Blue;
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

        // The palette color Krypton draws ordinary label text with (the same blue-ish
        // tone a KryptonLabel uses on Office 2010, or the light grey on Sparkle dark).
        // Plain WinForms Labels built in code don't follow the palette, so read the
        // exact color here and assign it so they match the surrounding KryptonLabels.
        public static Color LabelTextColor()
        {
            try
            {
                if (_palette != null)
                {
                    Color c = _palette.GetContentShortTextColor1(
                        PaletteContentStyle.LabelNormalControl, PaletteState.Normal);
                    if (Usable(c)) return c;
                }
            }
            catch { }
            return ReadDark() ? Color.FromArgb(0xDC, 0xDF, 0xE3) : SystemColors.ControlText;
        }

        // Make stock WinForms layout containers (TableLayoutPanel / Panel /
        // FlowLayoutPanel) transparent so the themed Krypton surface they sit on
        // (a KryptonPage, KryptonPanel, or KryptonGroupBox.Panel) shows through.
        //
        // Why this is needed: Krypton controls (KryptonLabel, KryptonGroupBox,
        // KryptonPage, ...) follow the global palette, so in dark mode their text
        // is light and their backgrounds are dark. But a plain WinForms layout
        // panel keeps its system BackColor (light grey) regardless of palette, and
        // when one of those panels is used purely for layout *between* a themed
        // surface and the Krypton labels on it, it paints a light rectangle over
        // the dark surface - leaving light text on a light panel (unreadable).
        // Setting these layout-only containers to Transparent lets the dark
        // Krypton surface paint through, so the labels read correctly again. The
        // colors track the palette automatically, in both light and dark mode.
        public static void MakeLayoutContainersTransparent(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                try
                {
                    // Only retarget genuine stock WinForms layout containers; never
                    // touch Krypton controls (they paint themselves) or input/edit
                    // controls (transparency there looks wrong).
                    if ((c is TableLayoutPanel || c is FlowLayoutPanel || c is Panel) &&
                        c.GetType().Namespace == "System.Windows.Forms")
                    {
                        c.BackColor = Color.Transparent;
                    }
                }
                catch { }
                if (c.Controls.Count > 0) MakeLayoutContainersTransparent(c);
            }
        }

        // Seat every KryptonGroupBox caption fully inside its own group area.
        //
        // By default a KryptonGroupBox caption straddles the top border
        // (CaptionOverlap = 0.5): half the text sits on the group panel, half on
        // whatever is behind the group box. That is fine when both surfaces share a
        // color, but here the group panel is dark while the navigator page behind it
        // is light, so the half that spills above the border is unreadable. Dropping
        // the overlap to 0.0 seats the caption entirely within the group area, on the
        // (dark) panel, so it reads cleanly. Krypton still picks the caption text
        // color from the palette, so this works in light mode too.
        public static void SeatGroupBoxCaptions(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                try
                {
                    KryptonGroupBox gb = c as KryptonGroupBox;
                    if (gb != null) gb.CaptionOverlap = 0.0;
                }
                catch { }
                if (c.Controls.Count > 0) SeatGroupBoxCaptions(c);
            }
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
