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
    //  * A theme is (accent, mode). "mode" picks a Krypton base palette + neutral
    //    scheme; "accent" is injected on top. Adding a color is one accent seed.
    //  * Neutral background/foreground for dark mode are pulled straight from
    //    ThemeService.GetColors(true) so chrome and transcript share the exact
    //    same #242424-family backdrop - no separate color authoring, no drift.
    //  * Everything is wrapped defensively: a bad palette path must never crash
    //    theme application. Worst case, chrome falls back to the inherited base.
    internal static class KryptonThemeBridge
    {
        private static KryptonPalette _palette;
        private static KryptonManager _manager;
        private static readonly object _lock = new object();

        // Accent seed: the handful of colors injected on top of the neutral base.
        private sealed class AccentSeed
        {
            public Color Normal;   // button / header face
            public Color Track;    // hover / lighter accent
            public Color Pressed;  // pressed / darker accent
            public Color OnAccent; // text/icon drawn over the accent
        }

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
            AccentSeed accent = GetAccent(accentId);

            // Clear any prior-mode overrides so light/dark don't bleed together,
            // then re-base. Dark uses the Sparkle renderer, which recolors the
            // whole window (chrome, strips, panels, borders) into a cohesive
            // blue-grey - so we deliberately do NOT override its neutrals; doing
            // so (e.g. forcing #242424) produced black panels/borders that
            // clashed with the Sparkle title bar. Light keeps the period Office
            // 2007 look. Only the accent is layered on top, in both modes.
            try { palette.ResetToDefaults(true); }
            catch { }
            palette.BasePaletteMode = dark ? PaletteMode.SparkleBlue : PaletteMode.Office2007Blue;

            // --- Accent ---------------------------------------------------------
            // Standalone buttons stay neutral (inherit the base palette). The
            // accent lives on a dedicated "primary" button style - Custom1 -
            // that a button opts into via ButtonStyle = ButtonStyle.Custom1.
            // This keeps button-dense dialogs calm while still letting key
            // affordances carry the theme color.
            ApplyButtonAccent(palette.ButtonStyles.ButtonCustom1, accent);

            // Headers (group/header captions) get a muted accent fill.
            ApplyHeaderAccent(palette.HeaderStyles.HeaderPrimary, accent);

            // Sparkle's disabled-button treatment reads as a light/un-themed grey
            // against the dark chrome, so give disabled buttons an explicit muted
            // dark face + dimmed text in dark mode. (Light mode keeps Office's
            // disabled look, which already fits.)
            if (dark)
            {
                Color disabledBack = Color.FromArgb(0x3E, 0x47, 0x52);
                Color disabledText = Color.FromArgb(0x80, 0x88, 0x92);
                SetTripleFace(palette.ButtonStyles.ButtonStandalone.StateDisabled, disabledBack, disabledBack, disabledText);
                SetTripleFace(palette.ButtonStyles.ButtonCustom1.StateDisabled, disabledBack, disabledBack, disabledText);
            }
        }

        // Paint a (plain or Krypton) DataGridView's cell/header/background/selection
        // colors to match the active theme. KryptonDataGridView themes its chrome
        // but leaves cell interiors and header fills to the standard DataGridView
        // styles (white by default), so set them explicitly. In dark mode we use a
        // cohesive dark blue-grey that matches the Sparkle chrome; light mode keeps
        // the standard light grid.
        public static void StyleDataGrid(DataGridView grid)
        {
            if (grid == null) return;
            bool dark = ReadDark();
            AccentSeed accent = GetAccent(ReadAccentId());

            Color cellBack, cellFore, hdrBack, hdrFore, lines;
            if (dark)
            {
                cellBack = Color.FromArgb(0x3C, 0x46, 0x51); // dark blue-grey cells
                cellFore = Color.FromArgb(0xE8, 0xEA, 0xED); // near-white text
                hdrBack = Color.FromArgb(0x50, 0x5C, 0x6A);  // lighter header band
                hdrFore = Color.FromArgb(0xFF, 0xFF, 0xFF);
                lines = Color.FromArgb(0x58, 0x64, 0x72);    // subtle gridlines
            }
            else
            {
                cellBack = SystemColors.Window;
                cellFore = SystemColors.WindowText;
                hdrBack = SystemColors.Control;
                hdrFore = SystemColors.ControlText;
                lines = SystemColors.ControlDark;
            }

            try
            {
                // Required for the header colors below to apply instead of the OS style.
                grid.EnableHeadersVisualStyles = false;
                grid.BackgroundColor = cellBack; // fills the empty area below the rows
                grid.GridColor = lines;

                DataGridViewCellStyle cs = grid.DefaultCellStyle;
                cs.BackColor = cellBack;
                cs.ForeColor = cellFore;
                cs.SelectionBackColor = accent.Normal;
                cs.SelectionForeColor = accent.OnAccent;

                DataGridViewCellStyle hs = grid.ColumnHeadersDefaultCellStyle;
                hs.BackColor = hdrBack;
                hs.ForeColor = hdrFore;
                // Keep header color stable when a cell in that column is selected.
                hs.SelectionBackColor = hdrBack;
                hs.SelectionForeColor = hdrFore;
            }
            catch { }
        }

        // --- Style-group helpers (all paths verified against Krypton.Toolkit.xml) ---

        private static void ApplyButtonAccent(KryptonPaletteCheckButton button, AccentSeed a)
        {
            if (button == null || a == null) return;
            SetTripleFace(button.StateNormal, a.Normal, a.Normal, a.OnAccent);
            SetTripleFace(button.StateTracking, a.Track, a.Track, a.OnAccent);
            SetTripleFace(button.StatePressed, a.Pressed, a.Pressed, a.OnAccent);
        }

        private static void ApplyHeaderAccent(KryptonPaletteHeader header, AccentSeed a)
        {
            if (header == null || a == null) return;
            SetTripleFace(header.StateNormal, a.Normal, a.Pressed, a.OnAccent);
        }

        private static void SetTripleFace(PaletteTriple state, Color back1, Color back2, Color text)
        {
            if (state == null) return;
            try { state.Back.Color1 = back1; } catch { }
            try { state.Back.Color2 = back2; } catch { }
            try { state.Content.ShortText.Color1 = text; } catch { }
            try { state.Content.LongText.Color1 = text; } catch { }
        }

        // --- Accent seeds ---------------------------------------------------------

        private static AccentSeed GetAccent(string accentId)
        {
            switch ((accentId ?? "blue").Trim().ToLowerInvariant())
            {
                case "red":
                    return new AccentSeed
                    {
                        Normal = Color.FromArgb(0xB0, 0x3A, 0x33),
                        Track = Color.FromArgb(0xD0, 0x56, 0x4E),
                        Pressed = Color.FromArgb(0x8A, 0x2A, 0x24),
                        OnAccent = Color.White
                    };
                case "orange":
                    return new AccentSeed
                    {
                        Normal = Color.FromArgb(0xD9, 0x80, 0x26),
                        Track = Color.FromArgb(0xE8, 0x9A, 0x48),
                        Pressed = Color.FromArgb(0xA8, 0x5F, 0x12),
                        OnAccent = Color.White
                    };
                case "blue":
                default:
                    return new AccentSeed
                    {
                        Normal = Color.FromArgb(0x26, 0x66, 0xB0),
                        Track = Color.FromArgb(0x3B, 0x82, 0xDC),
                        Pressed = Color.FromArgb(0x1B, 0x4E, 0x8A),
                        OnAccent = Color.White
                    };
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
