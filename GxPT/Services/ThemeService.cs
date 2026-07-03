using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Strongly-typed color palette used by the UI
    internal sealed class ThemeColors
    {
        public Color UiBackground;
        public Color UiForeground;
        public Color UserBubbleBack;
        public Color UserBubbleBorder;
        public Color AssistantBubbleBack;
        public Color AssistantBubbleBorder;
        public Color SystemBubbleBack;
        public Color SystemBubbleBorder;
        public Color CodeBack;
        public Color CodeBorder;
        public Color InlineCodeBack;
        public Color InlineCodeBorder;
        public Color Link;
        public Color CopyHover;
        public Color CopyPressed;
        public Color ScrollTrack;
        public Color ScrollThumb;
        public Color ScrollTrackBorder;
        public Color ScrollThumbBorder;
    }

    internal sealed class ThemeInfo
    {
        // Use public properties so WinForms data binding can read them
        public string Id { get; set; }
        public string Name { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Name) ? (Id ?? base.ToString()) : Name;
        }
    }

    // Internal parsed model
    internal sealed class ThemeDefinition
    {
        public string Id;
        public string Name;
        public ThemeColors Light;
        public ThemeColors Dark;

        public ThemeColors Get(bool dark)
        {
            return dark
                ? (Dark ?? Light ?? ThemeService.BuildBuiltinBlue().Dark)
                : (Light ?? Dark ?? ThemeService.BuildBuiltinBlue().Light);
        }

        public static ThemeDefinition FromDto(ThemeFileDto dto)
        {
            if (dto == null) return null;
            var def = new ThemeDefinition();
            def.Id = (dto.id ?? dto.name ?? "").Trim();
            if (string.IsNullOrEmpty(def.Id)) def.Id = "theme";
            def.Name = string.IsNullOrEmpty(dto.name) ? def.Id : dto.name;
            def.Light = FromPalette(dto.light);
            def.Dark = FromPalette(dto.dark);
            return def;
        }

        private static ThemeColors FromPalette(ThemePaletteDto p)
        {
            if (p == null) return null;
            var c = new ThemeColors();
            c.UiBackground = ThemeService.ParseColorOrDefault(p.uiBackground, SystemColors.Window);
            c.UiForeground = ThemeService.ParseColorOrDefault(p.uiForeground, SystemColors.WindowText);
            if (p.userBubble != null)
            {
                c.UserBubbleBack = ThemeService.ParseColorOrDefault(p.userBubble.background, Color.FromArgb(225, 240, 255));
                c.UserBubbleBorder = ThemeService.ParseColorOrDefault(p.userBubble.border, Color.FromArgb(160, 190, 220));
            }
            else { c.UserBubbleBack = Color.FromArgb(225, 240, 255); c.UserBubbleBorder = Color.FromArgb(160, 190, 220); }

            if (p.assistantBubble != null)
            {
                c.AssistantBubbleBack = ThemeService.ParseColorOrDefault(p.assistantBubble.background, Color.FromArgb(235, 235, 235));
                c.AssistantBubbleBorder = ThemeService.ParseColorOrDefault(p.assistantBubble.border, Color.FromArgb(200, 200, 200));
            }
            else { c.AssistantBubbleBack = Color.FromArgb(235, 235, 235); c.AssistantBubbleBorder = Color.FromArgb(200, 200, 200); }

            if (p.systemBubble != null)
            {
                c.SystemBubbleBack = ThemeService.ParseColorOrDefault(p.systemBubble.background, Color.FromArgb(255, 250, 220));
                c.SystemBubbleBorder = ThemeService.ParseColorOrDefault(p.systemBubble.border, Color.FromArgb(210, 200, 150));
            }
            else { c.SystemBubbleBack = Color.FromArgb(255, 250, 220); c.SystemBubbleBorder = Color.FromArgb(210, 200, 150); }

            if (p.code != null)
            {
                c.CodeBack = ThemeService.ParseColorOrDefault(p.code.background, Color.FromArgb(245, 245, 245));
                c.CodeBorder = ThemeService.ParseColorOrDefault(p.code.border, Color.FromArgb(210, 210, 210));
                c.InlineCodeBack = ThemeService.ParseColorOrDefault(p.code.inlineBackground, Color.FromArgb(240, 240, 240));
                c.InlineCodeBorder = ThemeService.ParseColorOrDefault(p.code.inlineBorder, Color.FromArgb(200, 200, 200));
            }
            else
            {
                c.CodeBack = Color.FromArgb(245, 245, 245);
                c.CodeBorder = Color.FromArgb(210, 210, 210);
                c.InlineCodeBack = Color.FromArgb(240, 240, 240);
                c.InlineCodeBorder = Color.FromArgb(200, 200, 200);
            }

            c.Link = ThemeService.ParseColorOrDefault(p.link, Color.FromArgb(0, 102, 204));
            c.CopyHover = ThemeService.ParseColorOrDefault(p.copyHover, Color.FromArgb(230, 230, 230));
            c.CopyPressed = ThemeService.ParseColorOrDefault(p.copyPressed, Color.FromArgb(210, 210, 210));
            c.ScrollTrack = ThemeService.ParseColorOrDefault(p.scrollTrack, Color.FromArgb(235, 235, 235));
            c.ScrollThumb = ThemeService.ParseColorOrDefault(p.scrollThumb, Color.FromArgb(200, 200, 200));
            c.ScrollTrackBorder = ThemeService.ParseColorOrDefault(p.scrollTrackBorder, Color.FromArgb(210, 210, 210));
            c.ScrollThumbBorder = ThemeService.ParseColorOrDefault(p.scrollThumbBorder, Color.FromArgb(160, 160, 160));
            return c;
        }
    }

    // JSON DTOs
    internal sealed class ThemeFileDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public ThemePaletteDto light { get; set; }
        public ThemePaletteDto dark { get; set; }
    }

    internal sealed class ThemePaletteDto
    {
        public string uiBackground { get; set; }
        public string uiForeground { get; set; }
        public BubbleDto userBubble { get; set; }
        public BubbleDto assistantBubble { get; set; }
        public BubbleDto systemBubble { get; set; }
        public CodeDto code { get; set; }
        public string link { get; set; }
        public string copyHover { get; set; }
        public string copyPressed { get; set; }
        public string scrollTrack { get; set; }
        public string scrollThumb { get; set; }
        public string scrollTrackBorder { get; set; }
        public string scrollThumbBorder { get; set; }
    }

    internal sealed class BubbleDto
    {
        public string background { get; set; }
        public string border { get; set; }
    }
    internal sealed class CodeDto
    {
        public string background { get; set; }
        public string border { get; set; }
        public string inlineBackground { get; set; }
        public string inlineBorder { get; set; }
    }

    internal static class ThemeService
    {
        private static Dictionary<string, ThemeDefinition> _themes = new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        private static bool _loaded;

        public static string UserThemesDirectory
        {
            get { return Path.Combine(AppSettings.SettingsDirectory, "Themes"); }
        }

        public static string InstallThemesDirectory
        {
            get
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // Prefer "Themes" at the app root; fallback to "Resources\\Themes"
                string p1 = Path.Combine(baseDir, "Themes");
                if (Directory.Exists(p1)) return p1;
                return Path.Combine(Path.Combine(baseDir, "Resources"), "Themes");
            }
        }

        public static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                Reload();
            }
        }

        public static void Reload()
        {
            var dict = new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase);
            try { Directory.CreateDirectory(UserThemesDirectory); }
            catch { }
            LoadFromFolder(InstallThemesDirectory, dict);
            LoadFromFolder(UserThemesDirectory, dict);
            if (dict.Count == 0)
            {
                // Seed with built-ins if none found on disk
                dict["blue"] = BuildBuiltinBlue();
                dict["purple"] = BuildBuiltinPurple();
                dict["orange"] = BuildBuiltinOrange();
            }
            lock (_lock)
            {
                _themes = dict; _loaded = true;
            }
        }

        public static List<ThemeInfo> GetAvailableThemes()
        {
            EnsureLoaded();
            var list = new List<ThemeInfo>();
            lock (_lock)
            {
                foreach (var kv in _themes)
                    list.Add(new ThemeInfo { Id = kv.Key, Name = kv.Value != null ? (kv.Value.Name ?? kv.Key) : kv.Key });
            }
            list.Sort(delegate(ThemeInfo a, ThemeInfo b) { return string.Compare(a.Name ?? a.Id, b.Name ?? b.Id, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static ThemeColors GetColors(bool dark)
        {
            EnsureLoaded();
            string themeName = null;
            try { themeName = AppSettings.GetString("color_theme"); }
            catch { themeName = null; }
            if (string.IsNullOrEmpty(themeName)) themeName = "blue"; // default
            // "red" was retired in favor of purple (its dark chrome was already Sparkle Purple);
            // a settings.json still carrying it gets the purple transcript so bubbles keep matching
            // the purple chrome the bridge gives it.
            if (themeName.Trim().Equals("red", StringComparison.OrdinalIgnoreCase)) themeName = "purple";
            ThemeDefinition def = null;
            lock (_lock) { _themes.TryGetValue(themeName, out def); }
            if (def == null) def = BuildBuiltinBlue();
            return def.Get(dark);
        }

        private static void LoadFromFolder(string folder, Dictionary<string, ThemeDefinition> dict)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                string[] files = null;
                try { files = Directory.GetFiles(folder, "*.json"); }
                catch { files = null; }
                if (files == null) return;
                for (int i = 0; i < files.Length; i++)
                {
                    var def = ReadThemeDefinition(files[i]);
                    if (def != null && !string.IsNullOrEmpty(def.Id))
                    {
                        dict[def.Id] = def; // last one wins (user overrides install)
                    }
                }
            }
            catch { }
        }

        private static ThemeDefinition ReadThemeDefinition(string path)
        {
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var dto = ser.Deserialize<ThemeFileDto>(text);
                return ThemeDefinition.FromDto(dto);
            }
            catch { return null; }
        }

        internal static Color ParseColorOrDefault(string s, Color def)
        {
            Color c;
            if (TryParseColor(s, out c)) return c;
            return def;
        }

        internal static bool TryParseColor(string s, out Color color)
        {
            color = Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            try
            {
                if (s.StartsWith("#"))
                {
                    // Support #RRGGBB or #AARRGGBB
                    if (s.Length == 7)
                    {
                        int r = int.Parse(s.Substring(1, 2), System.Globalization.NumberStyles.HexNumber);
                        int g = int.Parse(s.Substring(3, 2), System.Globalization.NumberStyles.HexNumber);
                        int b = int.Parse(s.Substring(5, 2), System.Globalization.NumberStyles.HexNumber);
                        color = Color.FromArgb(r, g, b);
                        return true;
                    }
                    if (s.Length == 9)
                    {
                        int a = int.Parse(s.Substring(1, 2), System.Globalization.NumberStyles.HexNumber);
                        int r = int.Parse(s.Substring(3, 2), System.Globalization.NumberStyles.HexNumber);
                        int g = int.Parse(s.Substring(5, 2), System.Globalization.NumberStyles.HexNumber);
                        int b = int.Parse(s.Substring(7, 2), System.Globalization.NumberStyles.HexNumber);
                        color = Color.FromArgb(a, r, g, b);
                        return true;
                    }
                }
                else
                {
                    // Named colors (simple)
                    color = Color.FromName(s);
                    if (color.A > 0 || string.Equals(s, "transparent", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        // Built-in defaults used as a safety net and for initial run
        internal static ThemeDefinition BuildBuiltinBlue()
        {
            var d = new ThemeDefinition();
            d.Id = "blue"; d.Name = "Blue";
            d.Light = new ThemeColors
            {
                UiBackground = SystemColors.Window,
                UiForeground = SystemColors.WindowText,
                UserBubbleBack = Color.FromArgb(225, 240, 255),
                UserBubbleBorder = Color.FromArgb(160, 190, 220),
                AssistantBubbleBack = Color.FromArgb(235, 235, 235),
                AssistantBubbleBorder = Color.FromArgb(200, 200, 200),
                SystemBubbleBack = Color.FromArgb(255, 250, 220),
                SystemBubbleBorder = Color.FromArgb(210, 200, 150),
                CodeBack = Color.FromArgb(245, 245, 245),
                CodeBorder = Color.FromArgb(210, 210, 210),
                InlineCodeBack = Color.FromArgb(240, 240, 240),
                InlineCodeBorder = Color.FromArgb(200, 200, 200),
                Link = Color.FromArgb(0, 102, 204),
                CopyHover = Color.FromArgb(230, 230, 230),
                CopyPressed = Color.FromArgb(210, 210, 210),
                ScrollTrack = Color.FromArgb(235, 235, 235),
                ScrollThumb = Color.FromArgb(200, 200, 200),
                ScrollTrackBorder = Color.FromArgb(210, 210, 210),
                ScrollThumbBorder = Color.FromArgb(160, 160, 160)
            };
            d.Dark = new ThemeColors
            {
                UiBackground = Color.FromArgb(0x24, 0x24, 0x24),
                UiForeground = Color.FromArgb(230, 230, 230),
                UserBubbleBack = ColorTranslator.FromHtml("#0b4e7a"), // deep blue
                UserBubbleBorder = ColorTranslator.FromHtml("#0f6aa1"),
                AssistantBubbleBack = Color.FromArgb(48, 49, 52),
                AssistantBubbleBorder = Color.FromArgb(70, 72, 75),
                SystemBubbleBack = Color.FromArgb(64, 60, 40),
                SystemBubbleBorder = Color.FromArgb(90, 85, 60),
                CodeBack = Color.FromArgb(28, 29, 31),
                CodeBorder = Color.FromArgb(70, 72, 75),
                InlineCodeBack = Color.FromArgb(45, 46, 49),
                InlineCodeBorder = Color.FromArgb(70, 72, 75),
                Link = Color.FromArgb(120, 170, 255),
                CopyHover = Color.FromArgb(60, 62, 66),
                CopyPressed = Color.FromArgb(52, 54, 58),
                ScrollTrack = Color.FromArgb(45, 46, 49),
                ScrollThumb = Color.FromArgb(90, 92, 96),
                ScrollTrackBorder = Color.FromArgb(70, 72, 75),
                ScrollThumbBorder = Color.FromArgb(110, 112, 116)
            };
            return d;
        }

        // Replaces the old "red" theme: red's dark-mode chrome mapped onto Krypton's Sparkle Purple
        // palette (no red Sparkle variant exists), so the transcript now matches it with real purple
        // accents. A persisted color_theme of "red" resolves to no theme and falls back to blue.
        internal static ThemeDefinition BuildBuiltinPurple()
        {
            var d = new ThemeDefinition();
            d.Id = "purple"; d.Name = "Purple";
            // Light adaptation
            d.Light = new ThemeColors
            {
                UiBackground = SystemColors.Window,
                UiForeground = SystemColors.WindowText,
                UserBubbleBack = ColorTranslator.FromHtml("#efe6fa"),
                UserBubbleBorder = ColorTranslator.FromHtml("#c7b2e8"),
                AssistantBubbleBack = Color.FromArgb(235, 235, 235),
                AssistantBubbleBorder = Color.FromArgb(200, 200, 200),
                SystemBubbleBack = Color.FromArgb(255, 250, 220),
                SystemBubbleBorder = Color.FromArgb(210, 200, 150),
                CodeBack = Color.FromArgb(245, 245, 245),
                CodeBorder = Color.FromArgb(210, 210, 210),
                InlineCodeBack = Color.FromArgb(240, 240, 240),
                InlineCodeBorder = Color.FromArgb(200, 200, 200),
                Link = ColorTranslator.FromHtml("#7a3cb8"),
                CopyHover = Color.FromArgb(230, 230, 230),
                CopyPressed = Color.FromArgb(210, 210, 210),
                ScrollTrack = Color.FromArgb(235, 235, 235),
                ScrollThumb = Color.FromArgb(200, 200, 200),
                ScrollTrackBorder = Color.FromArgb(210, 210, 210),
                ScrollThumbBorder = Color.FromArgb(160, 160, 160)
            };
            // Dark based on current dark palette
            d.Dark = new ThemeColors
            {
                UiBackground = Color.FromArgb(0x24, 0x24, 0x24),
                UiForeground = Color.FromArgb(230, 230, 230),
                UserBubbleBack = ColorTranslator.FromHtml("#3f2a66"),
                UserBubbleBorder = ColorTranslator.FromHtml("#5c3f99"),
                AssistantBubbleBack = Color.FromArgb(48, 49, 52),
                AssistantBubbleBorder = Color.FromArgb(70, 72, 75),
                SystemBubbleBack = Color.FromArgb(64, 60, 40),
                SystemBubbleBorder = Color.FromArgb(90, 85, 60),
                CodeBack = Color.FromArgb(28, 29, 31),
                CodeBorder = Color.FromArgb(70, 72, 75),
                InlineCodeBack = Color.FromArgb(45, 46, 49),
                InlineCodeBorder = Color.FromArgb(70, 72, 75),
                Link = ColorTranslator.FromHtml("#c0a0f0"),
                CopyHover = Color.FromArgb(60, 62, 66),
                CopyPressed = Color.FromArgb(52, 54, 58),
                ScrollTrack = Color.FromArgb(45, 46, 49),
                ScrollThumb = Color.FromArgb(90, 92, 96),
                ScrollTrackBorder = Color.FromArgb(70, 72, 75),
                ScrollThumbBorder = Color.FromArgb(110, 112, 116)
            };
            return d;
        }

        internal static ThemeDefinition BuildBuiltinOrange()
        {
            var d = new ThemeDefinition();
            d.Id = "orange"; d.Name = "Orange";
            d.Light = new ThemeColors
            {
                UiBackground = SystemColors.Window,
                UiForeground = SystemColors.WindowText,
                UserBubbleBack = ColorTranslator.FromHtml("#ffe7d9"),
                UserBubbleBorder = ColorTranslator.FromHtml("#ffcfb3"),
                AssistantBubbleBack = Color.FromArgb(235, 235, 235),
                AssistantBubbleBorder = Color.FromArgb(200, 200, 200),
                SystemBubbleBack = Color.FromArgb(255, 250, 220),
                SystemBubbleBorder = Color.FromArgb(210, 200, 150),
                CodeBack = Color.FromArgb(245, 245, 245),
                CodeBorder = Color.FromArgb(210, 210, 210),
                InlineCodeBack = Color.FromArgb(240, 240, 240),
                InlineCodeBorder = Color.FromArgb(200, 200, 200),
                Link = ColorTranslator.FromHtml("#d2691e"),
                CopyHover = Color.FromArgb(230, 230, 230),
                CopyPressed = Color.FromArgb(210, 210, 210),
                ScrollTrack = Color.FromArgb(235, 235, 235),
                ScrollThumb = Color.FromArgb(200, 200, 200),
                ScrollTrackBorder = Color.FromArgb(210, 210, 210),
                ScrollThumbBorder = Color.FromArgb(160, 160, 160)
            };
            d.Dark = new ThemeColors
            {
                UiBackground = Color.FromArgb(0x24, 0x24, 0x24),
                UiForeground = Color.FromArgb(230, 230, 230),
                UserBubbleBack = ColorTranslator.FromHtml("#5a2f00"),
                UserBubbleBorder = ColorTranslator.FromHtml("#8a4500"),
                AssistantBubbleBack = Color.FromArgb(48, 49, 52),
                AssistantBubbleBorder = Color.FromArgb(70, 72, 75),
                SystemBubbleBack = Color.FromArgb(64, 60, 40),
                SystemBubbleBorder = Color.FromArgb(90, 85, 60),
                CodeBack = Color.FromArgb(28, 29, 31),
                CodeBorder = Color.FromArgb(70, 72, 75),
                InlineCodeBack = Color.FromArgb(45, 46, 49),
                InlineCodeBorder = Color.FromArgb(70, 72, 75),
                Link = ColorTranslator.FromHtml("#ffb86c"),
                CopyHover = Color.FromArgb(60, 62, 66),
                CopyPressed = Color.FromArgb(52, 54, 58),
                ScrollTrack = Color.FromArgb(45, 46, 49),
                ScrollThumb = Color.FromArgb(90, 92, 96),
                ScrollTrackBorder = Color.FromArgb(70, 72, 75),
                ScrollThumbBorder = Color.FromArgb(110, 112, 116)
            };
            return d;
        }
    }
}
