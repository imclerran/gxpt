using System;
using System.Drawing;
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
            ThemeColors tc = ThemeService.GetColors(dark);
            AccentSeed accent = GetAccent(accentId);

            // Clear any prior-mode overrides so light/dark don't bleed together,
            // then re-base. Dark uses the Sparkle renderer (recolors uniformly,
            // the better dark base); light keeps the period Office 2007 look.
            try { palette.ResetToDefaults(true); }
            catch { }
            palette.BasePaletteMode = dark ? PaletteMode.SparkleBlue : PaletteMode.Office2007Blue;

            // --- Neutrals -------------------------------------------------------
            // In dark mode we override the base neutrals to exactly match the
            // transcript backdrop; in light mode we mostly inherit the familiar
            // Office light scheme and only inject the accent.
            if (dark)
            {
                Color bg = tc.UiBackground;          // #242424 family
                Color fg = tc.UiForeground;          // light grey text
                Color field = tc.InlineCodeBack;     // slightly raised field bg
                Color fieldBorder = tc.CodeBorder;

                ApplyControlBack(palette.ControlStyles.ControlCommon, bg);
                ApplyControlBack(palette.ControlStyles.ControlClient, bg);
                ApplyPanelBack(palette.PanelStyles.PanelCommon, bg);
                ApplyPanelBack(palette.PanelStyles.PanelClient, bg);

                // Form caption + client backdrop.
                ApplyFormBack(palette.FormStyles.FormMain, bg);

                // Input controls (text boxes, combos): raised field over the bg.
                ApplyInputBack(palette.InputControlStyles.InputControlCommon, field, fg, fieldBorder);
                ApplyInputBack(palette.InputControlStyles.InputControlStandalone, field, fg, fieldBorder);

                // Stock MenuStrip / StatusStrip / ToolStrip + dropdown menus. The
                // base palette's toolstrip colors don't go dark on their own, so
                // set them explicitly here.
                ApplyDarkToolStrips(palette, tc, accent);
            }

            // --- Accent ---------------------------------------------------------
            // Standalone buttons stay neutral (inherit the base palette). The
            // accent lives on a dedicated "primary" button style - Custom1 -
            // that a button opts into via ButtonStyle = ButtonStyle.Custom1.
            // This keeps button-dense dialogs calm while still letting key
            // affordances carry the theme color.
            ApplyButtonAccent(palette.ButtonStyles.ButtonCustom1, accent);

            // Headers (group/header captions) get a muted accent fill.
            ApplyHeaderAccent(palette.HeaderStyles.HeaderPrimary, accent);
        }

        // --- Style-group helpers (all paths verified against Krypton.Toolkit.xml) ---

        private static void ApplyControlBack(KryptonPaletteControl control, Color back)
        {
            if (control == null) return;
            try { control.StateCommon.Back.Color1 = back; } catch { }
            try { control.StateCommon.Back.Color2 = back; } catch { }
        }

        private static void ApplyPanelBack(KryptonPalettePanel panel, Color back)
        {
            if (panel == null) return;
            // A panel state is a PaletteBack directly (background only - no border
            // or content), so Color1/Color2 sit straight on StateCommon.
            try { panel.StateCommon.Color1 = back; } catch { }
            try { panel.StateCommon.Color2 = back; } catch { }
        }

        private static void ApplyFormBack(KryptonPaletteForm form, Color back)
        {
            if (form == null) return;
            try { form.StateCommon.Back.Color1 = back; } catch { }
            try { form.StateCommon.Back.Color2 = back; } catch { }
        }

        private static void ApplyInputBack(KryptonPaletteInputControl input, Color back, Color text, Color border)
        {
            if (input == null) return;
            try { input.StateCommon.Back.Color1 = back; } catch { }
            try { input.StateCommon.Back.Color2 = back; } catch { }
            try { input.StateCommon.Border.Color1 = border; } catch { }
            try { input.StateCommon.Border.Color2 = border; } catch { }
            // Input content is a regular PaletteContent (ShortText/LongText split).
            try { input.StateCommon.Content.ShortText.Color1 = text; } catch { }
            try { input.StateCommon.Content.LongText.Color1 = text; } catch { }
        }

        private static void ApplyButtonAccent(KryptonPaletteCheckButton button, AccentSeed a)
        {
            if (button == null || a == null) return;
            SetTripleFace(button.StateNormal, a.Normal, a.Normal, a.OnAccent);
            SetTripleFace(button.StateTracking, a.Track, a.Track, a.OnAccent);
            SetTripleFace(button.StatePressed, a.Pressed, a.Pressed, a.OnAccent);
        }

        // Explicit dark colors for the stock toolstrips and dropdown menus.
        // The base palette doesn't supply dark toolstrip colors, so set the
        // ProfessionalColorTable-style gradients/text directly.
        private static void ApplyDarkToolStrips(KryptonPalette palette, ThemeColors tc, AccentSeed accent)
        {
            if (palette == null) return;
            Color bg = tc.UiBackground;          // strip backdrop (#242424 family)
            Color raised = tc.InlineCodeBack;    // menu image-margin column
            Color text = tc.UiForeground;        // strip / item text
            Color border = tc.CodeBorder;        // strip + menu border

            KryptonPaletteTMS tms = palette.ToolMenuStatus;
            if (tms == null) return;

            // Top menu bar.
            try { tms.MenuStrip.MenuStripGradientBegin = bg; } catch { }
            try { tms.MenuStrip.MenuStripGradientEnd = bg; } catch { }
            try { tms.MenuStrip.MenuStripText = text; } catch { }

            // Status bar.
            try { tms.StatusStrip.StatusStripGradientBegin = bg; } catch { }
            try { tms.StatusStrip.StatusStripGradientEnd = bg; } catch { }
            try { tms.StatusStrip.StatusStripText = text; } catch { }

            // Tool strips + the body of drop-down menus.
            try { tms.ToolStrip.ToolStripGradientBegin = bg; } catch { }
            try { tms.ToolStrip.ToolStripGradientMiddle = bg; } catch { }
            try { tms.ToolStrip.ToolStripGradientEnd = bg; } catch { }
            try { tms.ToolStrip.ToolStripText = text; } catch { }
            try { tms.ToolStrip.ToolStripBorder = border; } catch { }
            try { tms.ToolStrip.ToolStripDropDownBackground = bg; } catch { }

            // Drop-down menu items: margin column, text, border, and the
            // accent-colored selection highlight.
            try { tms.Menu.ImageMarginGradientBegin = raised; } catch { }
            try { tms.Menu.ImageMarginGradientMiddle = raised; } catch { }
            try { tms.Menu.ImageMarginGradientEnd = raised; } catch { }
            try { tms.Menu.MenuItemText = text; } catch { }
            try { tms.Menu.MenuBorder = border; } catch { }
            try { tms.Menu.MenuItemSelected = accent.Normal; } catch { }
            try { tms.Menu.MenuItemSelectedGradientBegin = accent.Normal; } catch { }
            try { tms.Menu.MenuItemSelectedGradientEnd = accent.Pressed; } catch { }
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
