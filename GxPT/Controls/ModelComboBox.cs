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
    // KryptonComboBox paints BOTH its closed box and its dropdown rows from the item's ToString(), and
    // this build of Krypton never raises DrawItem, so owner-draw can't be used to badge only the
    // dropdown. Instead ToString() itself appends the capability suffix when ShowCapabilityBadges is on
    // (main window), and returns the bare name when off (settings) - so the two use sites differ by one
    // flag, not a forked control. Callers work in full ids via SetModels/SelectedModelId.
    internal class ModelComboBox : KryptonComboBox
    {
        // Combo item that displays the short model name (plus a "[v]"/"[f]" capability badge when its
        // owner has ShowCapabilityBadges on) but remembers the full id. Equality is on the id
        // (case-insensitive) so selection/lookup resolve to the right entry, and sorting - which keys on
        // ToString() - stays by name because the badge is only ever a trailing suffix.
        private sealed class Item
        {
            public readonly string Id;
            private readonly ModelComboBox _owner;
            public Item(ModelComboBox owner, string id) { _owner = owner; Id = id ?? string.Empty; }
            public override string ToString()
            {
                string name = MainForm.ShortModelName(Id);
                return (_owner != null && _owner._showCapabilityBadges)
                    ? name + _owner.CapabilitySuffix(Id)
                    : name;
            }
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
        }

        private bool _showCapabilityBadges;

        // When true, each item's displayed text gains a " [v]" (vision / image input) and/or " [f]"
        // (native file input) suffix read from the model catalog - in both the dropdown and the closed
        // box, since Krypton paints both from ToString(). Off by default, so the settings effort combos
        // render bare model names. Models the catalog doesn't know get no suffix rather than a
        // misleading one.
        public bool ShowCapabilityBadges
        {
            get { return _showCapabilityBadges; }
            set
            {
                if (_showCapabilityBadges == value) return;
                _showCapabilityBadges = value;
                RefreshItemDisplay();
            }
        }

        // The " [v]"/" [f]" suffix for a model id, or "" when the catalog has no info for it.
        private string CapabilitySuffix(string id)
        {
            ModelInfo info;
            if (!ModelCatalogService.TryGetModelInfo(id, out info) || info == null) return string.Empty;
            string s = string.Empty;
            if (info.SupportsImageInput) s += " [v]";
            if (info.SupportsFileInput) s += " [f]";
            return s;
        }

        // Force the closed box and dropdown to re-read ToString() after the badge flag flips. In
        // practice the flag is set once at construction (before items are populated), so this is a
        // no-op safety net for a runtime toggle.
        private void RefreshItemDisplay()
        {
            try
            {
                AdjustDropDownWidth();
                if (IsHandleCreated) Invalidate();
            }
            catch { }
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
                        if (!string.IsNullOrEmpty(id)) Items.Add(new Item(this, id));

                string sel = selectedId ?? string.Empty;
                if (sel.Length > 0 && IndexOfId(sel) < 0) Items.Add(new Item(this, sel));
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
                if (sel.Length > 0 && IndexOfId(sel) < 0) Items.Add(new Item(this, sel));
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
                        string s = Items[i] != null ? Items[i].ToString() : string.Empty;
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
