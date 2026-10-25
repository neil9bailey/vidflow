using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui.Dialogs
{
    public static class ShortcutsDialog
    {
        // Searchable list of every catalog action with its current keys, grouped by category,
        // plus a fixed "Mouse" section describing the mouse gestures for the given settings.
        public static void Show(IWin32Window owner, ActionRegistry registry, Settings settings)
        {
            using (var w = new Window(registry, settings))
                w.ShowDialog(owner);
        }

        private const string MouseCategory = "Mouse";
        // Actions of the Cull key layer get a group of their own, right after "Cull".
        private const string CullModeCategory = "Cull mode";

        private sealed class Entry
        {
            public string Category, Title, Keys;
            public bool Dim;
        }

        internal sealed class Window : DarkDialog
        {
            private readonly List<Entry> _all;
            // One-line explanations shown under a group's header (not actions: never matched or counted).
            private readonly Dictionary<string, Entry> _notes = new Dictionary<string, Entry>(StringComparer.Ordinal);
            private readonly DarkTextBox _search;
            private readonly DarkList _list;
            private readonly Label _count;

            public Window(ActionRegistry registry, Settings settings)
            {
                settings = settings ?? new Settings();
                _all = BuildEntries(registry, settings);
                _notes[CullModeCategory] = CullModeNote(registry, settings);
                Text = "Keyboard Shortcuts";
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;

                var root = new DarkTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                _search = new DarkTextBox { Dock = DockStyle.Fill, Glyph = Glyphs.Search, Cue = "Search actions, categories or keys" };
                _search.TextChanged += (s, e) => ApplyFilter();
                _search.TextBox.KeyDown += (s, e) =>
                {
                    if (e.KeyCode != Keys.Down && e.KeyCode != Keys.PageDown) return;
                    _list.Focus();
                    if (_list.SelectedIndex < 0) _list.SelectedIndex = _list.Rows.ToList().FindIndex(r => !r.IsGroup);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                };
                root.Controls.Add(_search, 0, 0);

                _list = new DarkList { Dock = DockStyle.Fill, EmptyText = "No shortcuts match your search." };
                _list.Columns.Add(new DarkList.Column("Action", 0.55f));
                _list.Columns.Add(new DarkList.Column("Shortcut", 0.45f, true));
                root.Controls.Add(_list, 0, 1);

                _count = CreateLabel("", true);
                var close = CreateButton("Close", true, DialogResult.Cancel);
                root.Controls.Add(CreateFooter(_count, close), 0, 2);
                Controls.Add(root);
                AcceptButton = close;
                CancelButton = close;

                OnDpi(() =>
                {
                    _search.Margin = Pad(16, 16, 16, 10);
                    _list.Margin = Pad(10, 0, 8, 8);
                    ClientSize = new Size(S(640), S(640));
                    MinimumSize = SizeFromClientSize(new Size(S(420), S(320)));
                });
                ApplyFilter();
                ActiveControl = _search.TextBox;
            }

            protected override bool HandleEscape()
            {
                // First Esc clears the search, the next one closes.
                if (_search.Text.Length == 0) return false;
                _search.Text = "";
                _search.Focus();
                return true;
            }

            private void ApplyFilter()
            {
                string[] tokens = (_search.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var rows = new List<DarkList.Row>();
                string group = null;
                int shown = 0;
                foreach (var e in _all)
                {
                    if (!Matches(e, tokens)) continue;
                    if (e.Category != group)
                    {
                        group = e.Category;
                        rows.Add(DarkList.Row.Group(group));
                        Entry note;
                        if (_notes.TryGetValue(group, out note)) rows.Add(new DarkList.Row(note, note.Title, note.Keys) { Dim = true });
                    }
                    rows.Add(new DarkList.Row(e, e.Title, e.Keys) { Dim = e.Dim });
                    shown++;
                }
                _list.SetRows(rows);
                _count.Text = tokens.Length == 0
                    ? _all.Count.ToString(CultureInfo.InvariantCulture) + " actions"
                    : shown.ToString(CultureInfo.InvariantCulture) + " of " + _all.Count.ToString(CultureInfo.InvariantCulture) + " actions";
            }

            private static bool Matches(Entry e, string[] tokens)
            {
                foreach (var t in tokens)
                {
                    if (e.Title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (e.Category.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (e.Keys.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    return false;
                }
                return true;
            }

            private static List<Entry> BuildEntries(ActionRegistry registry, Settings settings)
            {
                var list = new List<Entry>();
                foreach (var cat in ActionCatalog.Categories)
                {
                    var defs = ActionCatalog.All.Where(a => a.Category == cat && !ActionCatalog.IsHiddenInEdition(a.Id)).ToList();
                    // Global actions under the category; Cull layer actions in the "Cull mode" group after it.
                    foreach (var def in defs.Where(d => d.Layer == null)) list.Add(EntryFor(def, cat, registry, settings));
                    foreach (var def in defs.Where(d => d.Layer == ActionCatalog.LayerCull)) list.Add(EntryFor(def, CullModeCategory, registry, settings));
                }
                list.AddRange(MouseEntries(settings));
                return list;
            }

            private static Entry EntryFor(ActionDef def, string category, ActionRegistry registry, Settings settings)
            {
                string keys = KeyNames.FormatList(KeysOf(def, registry, settings));
                return new Entry { Category = category, Title = def.Title, Keys = keys, Dim = keys.Length == 0 };
            }

            private static IList<Keys> KeysOf(ActionDef def, ActionRegistry registry, Settings settings)
            {
                return registry != null ? registry.GetKeys(def.Id) : DeclaredKeys(def, settings);
            }

            // "In Cull mode (C) these replace the usual keys" (short enough for the Action column).
            private static Entry CullModeNote(ActionRegistry registry, Settings settings)
            {
                var toggle = ActionCatalog.Find(ActionIds.ToggleCullMode);
                var keys = toggle != null ? KeysOf(toggle, registry, settings) : new Keys[0];
                string text = "In Cull mode" + (keys.Count > 0 ? " (" + KeyNames.Format(keys[0]) + ")" : "") + " these replace the usual keys";
                return new Entry { Category = CullModeCategory, Title = text, Keys = "", Dim = true };
            }

            // Without a live registry, show the declared keys (defaults + overrides).
            private static IList<Keys> DeclaredKeys(ActionDef def, Settings settings)
            {
                string ov;
                if (settings.KeyOverrides.TryGetValue(def.Id, out ov)) return KeyNames.ParseList(ov);
                var keys = new List<Keys>();
                foreach (var name in def.DefaultKeys)
                {
                    Keys k;
                    if (KeyNames.TryParse(name, out k)) keys.Add(k);
                }
                return keys;
            }

            private static IEnumerable<Entry> MouseEntries(Settings s)
            {
                Func<string, string, bool, Entry> e = (title, keys, dim) => new Entry { Category = MouseCategory, Title = title, Keys = keys, Dim = dim };
                yield return s.LeftClick == ClickAction.PlayPause ? e("Play / Pause", "Left click", false) : e("No action", "Left click", true);
                yield return s.DoubleClickFullscreen ? e("Fullscreen", "Double-click", false) : e("No action", "Double-click", true);
                string wheel;
                switch (s.Wheel)
                {
                    case WheelAction.Seek: wheel = "Seek forward / backward"; break;
                    case WheelAction.FrameStep: wheel = "Next / previous frame"; break;
                    case WheelAction.Zoom: wheel = "Zoom in / out"; break;
                    default: wheel = "Volume up / down"; break;
                }
                yield return e(wheel, "Wheel", false);
                yield return e("Zoom in / out at the cursor", "Ctrl+Wheel", false);
                yield return e("Next / previous frame", "Shift+Wheel", false);
                yield return e("Speed ±0.05×", "Alt+Wheel", false);
                yield return e("Reset zoom", "Middle click", false);
                yield return e("Pan the video when zoomed", "Left drag, Middle drag", false);
                if (s.DragMovesWindow) yield return e("Move the window (when not zoomed)", "Left drag", false);
                yield return e("Menu", "Right click", false);
                yield return e("Previous / next file", "Back button, Forward button", false);
            }
        }
    }
}
