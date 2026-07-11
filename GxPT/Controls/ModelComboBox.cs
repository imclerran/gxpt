using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    // A KryptonComboBox for picking a model. It displays the SHORT model name (the "author/" prefix
    // stripped) while storing the full "author/model" id, and auto-sizes its dropdown to fit.
    //
    // Shared by the main window's model selector (cmbModel) and the settings effort-tier pickers.
    // The item's ToString() is always the bare short name: KryptonComboBox paints its own closed box
    // from ToString() (owner-draw only reaches the dropdown rows), and Sorted sorts on it - so the
    // closed box and sort order stay clean regardless of the badge feature below.
    //
    // ShowCapabilityBadges (opt-in, off by default) turns on owner-draw so the DROPDOWN ROWS append a
    // "[v]"/"[f]" capability suffix from the model catalog; the closed box keeps the bare name because
    // Krypton paints it from ToString(). The settings effort combos leave it off and behave exactly as
    // before; only the main window's model selector sets it on. Callers work in full ids via
    // SetModels/SelectedModelId.
    internal class ModelComboBox : KryptonComboBox
    {
        // Combo item that displays the short model name but remembers the full id. Equality is on the
        // id (case-insensitive) so selection/lookup resolve to the right entry.
        private sealed class Item
        {
            public readonly string Id;
            public Item(string id) { Id = id ?? string.Empty; }
            public override string ToString() { return MainForm.ShortModelName(Id); }
            public override bool Equals(object obj)
            {
                Item o = obj as Item;
                return o != null && string.Equals(o.Id, Id, StringComparison.OrdinalIgnoreCase);
            }
            public override int GetHashCode()
            {
                return Id == null ? 0 : Id.ToLowerInvariant().GetHashCode();
            }
        }

        public ModelComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DropDown += delegate { AdjustDropDownWidth(); };
            DrawItem += OnDrawModelItem;
        }

        private bool _showCapabilityBadges;

        // When true, dropdown rows append a "[v]"/"[f]" capability suffix (vision / file input) read
        // from the model catalog. Off by default so the settings effort combos render bare names.
        // Toggling flips owner-draw on/off; the closed box is unaffected (Krypton paints it from
        // ToString()), so only the dropdown gains the badges.
        public bool ShowCapabilityBadges
        {
            get { return _showCapabilityBadges; }
            set
            {
                if (_showCapabilityBadges == value) return;
                _showCapabilityBadges = value;
                DrawMode = value ? DrawMode.OwnerDrawFixed : DrawMode.Normal;
                AdjustDropDownWidth();
            }
        }

        // Replace the item list with the given model ids and select selectedId (added if not present,
        // so a stored value that is no longer in the catalog still stays visible/selectable).
        public void SetModels(IEnumerable<string> ids, string selectedId)
        {
            BeginUpdate();
            try
            {
                Items.Clear();
                if (ids != null)
                    foreach (string id in ids)
                        if (!string.IsNullOrEmpty(id)) Items.Add(new Item(id));

                string sel = selectedId ?? string.Empty;
                if (sel.Length > 0 && IndexOfId(sel) < 0) Items.Add(new Item(sel));
                SelectId(sel);
            }
            finally { EndUpdate(); }
            AdjustDropDownWidth();
        }

        // The full model id of the current selection, or null when nothing is selected. Setting selects
        // the matching item, adding it if it isn't already present.
        public string SelectedModelId
        {
            get { Item it = SelectedItem as Item; return it != null ? it.Id : null; }
            set
            {
                string sel = value ?? string.Empty;
                if (sel.Length > 0 && IndexOfId(sel) < 0) Items.Add(new Item(sel));
                SelectId(sel);
            }
        }

        private void SelectId(string id)
        {
            SelectedIndex = string.IsNullOrEmpty(id) ? -1 : IndexOfId(id); // -1 clears if not found
        }

        private int IndexOfId(string id)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Item it = Items[i] as Item;
                if (it != null && string.Equals(it.Id, id, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        // The text shown for an item in the dropdown: the short model name, plus a " [v]"/" [f]"
        // capability suffix when ShowCapabilityBadges is on and the catalog knows the model. Unknown
        // models (not yet fetched) get no suffix rather than a misleading one.
        private string DisplayText(int index) { return DisplayText(index, true); }

        private string DisplayText(int index, bool withBadge)
        {
            object raw = (index >= 0 && index < Items.Count) ? Items[index] : null;
            Item it = raw as Item;
            if (it == null) return raw != null ? raw.ToString() : string.Empty;

            string text = it.ToString(); // MainForm.ShortModelName(Id)
            if (!_showCapabilityBadges || !withBadge) return text;

            ModelInfo info;
            bool hit = ModelCatalogService.TryGetModelInfo(it.Id, out info) && info != null;
            try
            {
                Logger.Log("ModelCombo", "badge lookup id=" + it.Id + " hit=" + hit
                    + (hit ? (" img=" + info.SupportsImageInput + " file=" + info.SupportsFileInput) : ""));
            }
            catch { }
            if (!hit) return text + " [?]"; // TEMP diagnostic: catalog has no info for this id
            if (info.SupportsImageInput) text += " [v]";
            if (info.SupportsFileInput) text += " [f]";
            return text;
        }

        // Owner-draw for the dropdown rows (only invoked while DrawMode != Normal, i.e. when
        // ShowCapabilityBadges is on). Theme-aware so the badged dropdown matches the rest of the UI;
        // the selected row uses the system highlight for guaranteed contrast.
        private void OnDrawModelItem(object sender, DrawItemEventArgs e)
        {
            try { Logger.Log("ModelCombo", "DrawItem fired idx=" + e.Index + " state=" + e.State + " badges=" + _showCapabilityBadges); }
            catch { }
            if (e.Index < 0)
            {
                e.DrawBackground();
                e.DrawFocusRectangle();
                return;
            }

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            // The closed-box (edit portion) keeps the bare name - badges are a dropdown-only affordance.
            // Krypton paints the closed box from ToString() and normally doesn't route it here, but guard
            // against it in case it ever does.
            bool isEditPortion = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;
            ThemeColors colors;
            try { colors = ThemeService.GetColors(IsDarkTheme()); }
            catch { colors = null; }

            Color back = selected
                ? SystemColors.Highlight
                : (colors != null ? colors.UiBackground : SystemColors.Window);
            Color fore = selected
                ? SystemColors.HighlightText
                : (colors != null ? colors.UiForeground : SystemColors.WindowText);

            using (SolidBrush b = new SolidBrush(back))
                e.Graphics.FillRectangle(b, e.Bounds);

            TextRenderer.DrawText(e.Graphics, DisplayText(e.Index, !isEditPortion), e.Font ?? Font, e.Bounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix);

            e.DrawFocusRectangle();
        }

        // True when the active app theme is dark (mirrors ThemeManager.IsDarkTheme so this control
        // doesn't need a back-reference to the form).
        private static bool IsDarkTheme()
        {
            try
            {
                string theme = AppSettings.GetString("theme");
                return !string.IsNullOrEmpty(theme) &&
                    theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Widen the dropdown to fit the longest displayed item text (badges included); never narrower
        // than the control.
        private void AdjustDropDownWidth()
        {
            try
            {
                int maxWidth = Width;
                using (Graphics g = CreateGraphics())
                {
                    for (int i = 0; i < Items.Count; i++)
                    {
                        string s = DisplayText(i);
                        if (s.Length == 0) continue;
                        int w = TextRenderer.MeasureText(g, s, Font,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Width;
                        if (w > maxWidth) maxWidth = w;
                    }
                }
                int extra = 10;
                try { if (Items.Count > MaxDropDownItems) extra += SystemInformation.VerticalScrollBarWidth; }
                catch { }
                DropDownWidth = Math.Max(Width, Math.Min(maxWidth + extra, 2000));
            }
            catch { }
        }
    }
}
