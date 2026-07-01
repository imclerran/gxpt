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
    // No owner-draw: KryptonComboBox paints its own closed box from the item's ToString(), so a small
    // display wrapper is used instead - that way BOTH the closed box and the dropdown show the short
    // name (owner-draw only reached the dropdown). Callers work in full ids via SetModels/SelectedModelId.
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

        // Widen the dropdown to fit the longest (short) item text; never narrower than the control.
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
