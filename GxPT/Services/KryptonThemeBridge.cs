using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
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
    //  * The few custom-painted, non-Krypton bits (the tab +/x glyph, status strip,
    //    input NUDs) read their matching theme colors straight off the palette that
    //    Krypton is ACTUALLY rendering with - KryptonManager.CurrentGlobalPalette.
    //    We deliberately do NOT keep a second "probe" KryptonPalette in sync: a
    //    KryptonPalette rebuilds and re-hooks its whole state graph every time its
    //    BasePaletteMode is reassigned, and re-driving that on every light/dark
    //    toggle made each toggle progressively slower (the accumulating cost the
    //    user hit). CurrentGlobalPalette is free - Krypton already owns it - and is
    //    by definition an exact match for the rendered chrome.
    //  * Everything is wrapped defensively: a bad palette path must never crash
    //    theme application.
    internal static class KryptonThemeBridge
    {
        private static KryptonManager _manager;
        private static readonly object _lock = new object();

        // The palette Krypton is currently rendering all chrome with. This is the authoritative source
        // for every resolved theme color/font we read; it tracks GlobalPaletteMode automatically, so we
        // never have to keep (and repeatedly re-mode) a second palette instance. (In this Krypton build
        // CurrentGlobalPalette is typed IPalette, which carries all the Get* query methods we use.)
        private static IPalette Global()
        {
            try { return KryptonManager.CurrentGlobalPalette; }
            catch { return null; }
        }

        // Last-applied (accent, dark) so a repeat apply of the same theme is a true no-op. Several calls
        // apply the theme during startup (and the font-size path re-applies it as a no-op); without this
        // each redundant call pays the full Krypton palette/toolstrip cost.
        private static string _lastAccentId;
        private static bool _lastDark;
        private static bool _applied;

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
        // for menu/status colors (MenuStripText / StatusStripText) - the general
        // PaletteBase does not expose those Krypton-specific strip colors, so the
        // per-mode constant is the only fallback when the renderer isn't a Krypton one.
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
            return MenuTextColor();
        }

        // Background for the StatusStrip. Krypton does not fill the StatusStrip's
        // background itself (its BackColor shows through), so match it to the MENU
        // bar's color - not the StatusStrip-specific palette color, which Sparkle
        // renders near-black - so the top and bottom bars look alike.
        public static Color StatusStripBackColor()
        {
            try { KryptonColorTable ct = ActiveColorTable(); if (ct != null && Usable(ct.MenuStripGradientBegin)) return ct.MenuStripGradientBegin; } catch { }
            return ReadDark() ? Color.FromArgb(0x4D, 0x58, 0x64) : SystemColors.Control;
        }

        // The exact font Krypton paints input-control text with, resolved through the active
        // palette (so it reflects font substitution on the target OS - Segoe UI on modern Windows,
        // a substitute on XP). KryptonNumericUpDown paints its value with this font, but its hosted
        // WinForms edit uses the control's ambient Font (the .NET default, Microsoft Sans Serif
        // 8.25pt, when nothing sets one). That mismatch offsets the painted value from the caret/
        // edit and, on the inactive->active flip, exposes the raw edit. Assigning this font to a NUD
        // makes both the Krypton paint and the hosted edit resolve identical metrics, and keeps the
        // NUDs consistent with the other themed inputs. Returns a clone so the palette keeps its own.
        public static Font InputContentFont()
        {
            try
            {
                IPalette p = Global();
                if (p != null)
                {
                    Font f = p.GetContentShortTextFont(
                        PaletteContentStyle.InputControlStandalone, PaletteState.Normal);
                    if (f != null) return (Font)f.Clone();
                }
            }
            catch { }
            // XP-safe fallback matching the Krypton sample (which shows no offset).
            try { return new Font("Tahoma", 8.25f, FontStyle.Regular, GraphicsUnit.Point); }
            catch { return null; }
        }

        // The opaque background Krypton uses for input controls, read from the active palette so it
        // matches the other themed inputs. A NumericUpDown hosted on our transparent layout panels
        // inherits BackColor=Transparent, which breaks its Krypton-styled edit painting; assigning
        // this to the CONTAINED control gives it a solid, correctly-colored background.
        public static Color InputBackColor()
        {
            try
            {
                IPalette p = Global();
                if (p != null)
                {
                    Color c = p.GetBackColor1(
                        PaletteBackStyle.InputControlStandalone, PaletteState.Normal);
                    if (Usable(c)) return c;
                }
            }
            catch { }
            return ReadDark() ? Color.FromArgb(0x3C, 0x46, 0x51) : SystemColors.Window;
        }

        // The KryptonGroupBox panel background color, read from the active palette. Used to paint the
        // NUD-hosting layout tables an OPAQUE color that matches the panel behind them - instead of
        // BackColor=Transparent - so the labels stay readable AND the NumericUpDowns no longer sit on
        // a transparent parent chain (which disrupts their Krypton edit painting, dropping the active
        // edit to the raw hosted control). Visually identical to the transparent look; functionally
        // opaque for the hosted edit's benefit.
        public static Color PanelBackColor()
        {
            try
            {
                IPalette p = Global();
                if (p != null)
                {
                    Color c = p.GetBackColor1(
                        PaletteBackStyle.ControlGroupBox, PaletteState.Normal);
                    if (Usable(c)) return c;
                }
            }
            catch { }
            return ReadDark() ? Color.FromArgb(0x4D, 0x58, 0x64) : SystemColors.Control;
        }

        // The Krypton client/form background color, read from the active palette (PanelClient - the
        // canonical KryptonPanel/form surface color). Used to paint the main window's stock WinForms
        // layout panels (the composer chrome, split-container fill) so they match the themed KryptonForm
        // instead of showing the default Windows control grey. Per-mode fallback if the palette is unread.
        public static Color FormBackColor()
        {
            try
            {
                IPalette p = Global();
                if (p != null)
                {
                    Color c = p.GetBackColor1(PaletteBackStyle.PanelClient, PaletteState.Normal);
                    if (Usable(c)) return c;
                }
            }
            catch { }
            return ReadDark() ? Color.FromArgb(0x2E, 0x37, 0x41) : SystemColors.Control;
        }

        // KryptonScrollBar (this build) only repositions its thumb on direct mouse interaction; assigning
        // Value programmatically does not move the thumb (its Value setter doesn't update the backing field
        // reliably here), so wheel/programmatic scrolls leave the thumb stale. Write the backing field
        // directly so the value truly changes, then nudge Maximum (hi+1 -> hi) - whose setter reruns the
        // control's internal thumb reposition for the current value - so the thumb follows.
        private static System.Reflection.FieldInfo _scrollValueField;
        private static bool _scrollValueFieldResolved;
        public static void SetScrollBarValue(KryptonScrollBar bar, int value)
        {
            if (bar == null) return;
            try
            {
                int lo = bar.Minimum, hi = bar.Maximum;
                int v = Math.Max(lo, Math.Min(hi, value));

                if (!_scrollValueFieldResolved)
                {
                    _scrollValueFieldResolved = true;
                    try
                    {
                        _scrollValueField = typeof(KryptonScrollBar).GetField("_value",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    }
                    catch { }
                }

                if (_scrollValueField != null) _scrollValueField.SetValue(bar, v);
                else bar.Value = v;

                if (hi > lo)
                {
                    bar.Maximum = hi + 1; // force the internal thumb reposition for the new value
                    bar.Maximum = hi;
                }
                else bar.Invalidate();
            }
            catch { }
        }

        public static void Apply(string accentId, bool dark)
        {
            lock (_lock)
            {
                if (_manager == null)
                {
                    _manager = new KryptonManager();
                    // Install the Krypton toolstrip renderer ONCE. GlobalApplyToolstrips defaults to true,
                    // but the renderer is only (re)built when it flips or when GlobalPaletteMode changes;
                    // if the app happens to start in the palette that is already current, the first apply
                    // below is a no-op and would leave the menu/strips unthemed. This one-time flip forces
                    // the initial install. It is deliberately NOT repeated per apply.
                    _manager.GlobalApplyToolstrips = false;
                    _manager.GlobalApplyToolstrips = true;
                }

                // No-op when the theme hasn't actually changed. Reapplying the same palette still forced
                // a full toolstrip-renderer rebuild and re-broadcast (the source of the slow launch and
                // the progressively-slower theme toggles).
                string accent = accentId ?? "blue";
                if (_applied && _lastDark == dark &&
                    string.Equals(_lastAccentId, accent, StringComparison.OrdinalIgnoreCase))
                    return;
                _applied = true;
                _lastDark = dark;
                _lastAccentId = accent;

                // Render with the STOCK palette mode, exactly as the Krypton sample apps do
                // (kryptonManager.GlobalPaletteMode = ...Office2010Blue): the whole window - title bar,
                // menu, panels, strips - is the canonical Krypton theme, with no custom palette or
                // overrides, so the chrome and menu bar match by construction. The accent selects the
                // Sparkle variant in dark mode. GlobalApplyToolstrips defaults to true and changing
                // GlobalPaletteMode already re-themes the toolstrips (its setter calls
                // UpdateToolStripManager) AND updates CurrentGlobalPalette - which is what our
                // custom-painted bits read their colors from - so we neither toggle GlobalApplyToolstrips
                // nor keep any secondary palette in sync here.
                _manager.GlobalPaletteMode = dark ? DarkSparkleModeManager(accent)
                                                  : PaletteModeManager.Office2010Blue;

                // This Krypton build re-adds duplicate handlers to its palette-change events on every
                // palette swap (see DedupeKryptonSubscribers); strip them right after the swap so the
                // broadcast list cannot compound and each toggle stays fast.
                try { DedupeKryptonSubscribers(); }
                catch { }
            }
        }

        // ---- Krypton palette-change subscriber dedupe (permanent fix) ---------------------------
        //
        // This build of Krypton re-subscribes STATIC handlers to KryptonManager.GlobalPaletteChanged
        // (and to the stock palettes' PalettePaint) on every palette change without removing the old
        // registrations, so the GlobalPaletteChanged broadcast roughly doubled with each light/dark
        // toggle - measured toggles grew from ~50ms to ~9s within ten switches. After every palette
        // swap, walk the delegate-backed event fields on KryptonManager (static) and on the stock
        // palettes the app renders with, and drop exact duplicate (target, method) registrations.
        // Semantically safe: double-subscribing the same handler to the same event is never
        // intentional. Reflection-only and fully defensive - if any of it fails, Krypton just runs
        // with its native (leaky) behavior.
        private static void DedupeKryptonSubscribers()
        {
            DedupeDelegateFields(typeof(KryptonManager), null);

            // The stock palettes the app renders with; per-control PalettePaint subscriptions
            // migrate between them on every toggle, so stale entries can pool on either side.
            try { IPalette p = KryptonManager.PaletteOffice2010Blue; DedupeDelegateFields(p.GetType(), p); } catch { }
            try { IPalette p = KryptonManager.PaletteSparkleBlue; DedupeDelegateFields(p.GetType(), p); } catch { }
            try { IPalette p = KryptonManager.PaletteSparkleOrange; DedupeDelegateFields(p.GetType(), p); } catch { }
            try { IPalette p = KryptonManager.PaletteSparklePurple; DedupeDelegateFields(p.GetType(), p); } catch { }
        }

        // Walks every delegate-typed field on the given type (and its bases) and rewrites any whose
        // invocation list contains duplicate (target, method) registrations, keeping only the first
        // occurrence of each.
        private static void DedupeDelegateFields(Type type, object instance)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly |
                                 (instance == null ? BindingFlags.Static : BindingFlags.Instance);
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(flags); }
                catch { break; }
                foreach (FieldInfo f in fields)
                {
                    try
                    {
                        if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                        Delegate d = f.GetValue(instance) as Delegate;
                        if (d == null) continue;
                        Delegate[] list = d.GetInvocationList();
                        if (list.Length < 2) continue; // singleton lists can't hold duplicates

                        // Keep only the FIRST occurrence of each (target, method) pair.
                        List<Delegate> kept = new List<Delegate>(list.Length);
                        Dictionary<object, Dictionary<MethodInfo, bool>> seen =
                            new Dictionary<object, Dictionary<MethodInfo, bool>>();
                        object staticKey = new object(); // sentinel target for static handlers
                        foreach (Delegate item in list)
                        {
                            object target = item.Target ?? staticKey;
                            Dictionary<MethodInfo, bool> methods;
                            if (!seen.TryGetValue(target, out methods))
                            {
                                methods = new Dictionary<MethodInfo, bool>();
                                seen.Add(target, methods);
                            }
                            if (methods.ContainsKey(item.Method)) continue; // duplicate registration - drop
                            methods.Add(item.Method, true);
                            kept.Add(item);
                        }

                        if (kept.Count != list.Length)
                            f.SetValue(instance, Delegate.Combine(kept.ToArray()));
                    }
                    catch { }
                }
            }
        }

        // The Sparkle palette variant for the chosen accent color, for the manager's GlobalPaletteMode.
        // Sparkle ships Blue / Orange / Purple, matching the app's blue/orange/purple transcript
        // themes one-to-one. "red" is the retired theme purple replaced; a settings.json that still
        // carries it keeps the Sparkle Purple chrome it always had (ThemeService aliases its
        // transcript to purple the same way). Other unknown ids fall through to the blue default.
        private static PaletteModeManager DarkSparkleModeManager(string accentId)
        {
            switch ((accentId ?? "blue").Trim().ToLowerInvariant())
            {
                case "orange": return PaletteModeManager.SparkleOrange;
                case "purple":
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
                case "purple":
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

        // In Sparkle dark mode a KryptonCheckBox draws its caption noticeably dimmer
        // than a KryptonLabel sitting right next to it: the two controls resolve to
        // different label content styles and Sparkle colors them differently, and the
        // checkbox's LabelStyle doesn't move the drawn color. Setting the caption color
        // explicitly DOES work, so do that - using the brighter of the two normal label
        // styles, which in dark mode is the light text a label uses. In light mode the
        // override is cleared (Color.Empty) so the palette drives the caption color again -
        // important for controls that persist across theme toggles (e.g. the main window),
        // where a lingering dark-mode color would otherwise be unreadable on the light panel.
        public static void FixDarkCheckBoxText(Control root)
        {
            if (root == null) return;
            if (ReadDark())
            {
                Color fg = DarkCheckBoxTextColor();
                if (Usable(fg)) ApplyCheckBoxTextColor(root, fg);
            }
            else
            {
                ApplyCheckBoxTextColor(root, Color.Empty);
            }
        }

        private static void ApplyCheckBoxTextColor(Control root, Color fg)
        {
            foreach (Control c in root.Controls)
            {
                try
                {
                    KryptonCheckBox cb = c as KryptonCheckBox;
                    if (cb != null) cb.StateCommon.ShortText.Color1 = fg;
                }
                catch { }
                if (c.Controls.Count > 0) ApplyCheckBoxTextColor(c, fg);
            }
        }

        // The light caption color a KryptonLabel uses in dark mode. Read the brighter
        // of the two normal label content styles from the active global palette (whichever
        // one the labels resolve to, it's the higher-luminance value in dark mode), with a
        // near-white fallback if the palette can't be read.
        private static Color DarkCheckBoxTextColor()
        {
            Color best = Color.Empty;
            float bestLum = -1f;
            IPalette p = Global();
            if (p != null)
            {
                PaletteContentStyle[] styles =
                {
                    PaletteContentStyle.LabelNormalControl,
                    PaletteContentStyle.LabelNormalPanel
                };
                foreach (PaletteContentStyle s in styles)
                {
                    try
                    {
                        Color c = p.GetContentShortTextColor1(s, PaletteState.Normal);
                        if (Usable(c) && c.GetBrightness() > bestLum)
                        {
                            bestLum = c.GetBrightness();
                            best = c;
                        }
                    }
                    catch { }
                }
            }
            return Usable(best) ? best : Color.FromArgb(0xE6, 0xE8, 0xEB);
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
