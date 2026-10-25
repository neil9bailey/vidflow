using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui.Dialogs
{
    public static class SettingsDialog
    {
        // Edits a copy of `settings`; on OK copies every value back into the same instance
        // (including KeyOverrides, which only stores entries that differ from the defaults)
        // and returns true. Cancel leaves `settings` untouched and returns false.
        public static bool Show(IWin32Window owner, Settings settings, ActionRegistry registry)
        {
            return Show(owner, settings, registry, null);
        }

        // The same, opened on the page with this title (e.g. "Cull", "Shortcuts"); null or unknown =
        // the page used last.
        public static bool Show(IWin32Window owner, Settings settings, ActionRegistry registry, string page)
        {
            if (settings == null) return false;
            using (var w = new Window(settings, registry))
            {
                if (!string.IsNullOrEmpty(page)) w.SelectPage(page);
                if (w.ShowDialog(owner) != DialogResult.OK) return false;
                w.ApplyTo(settings);
                return true;
            }
        }

        // Reopening the dialog returns to the page used last.
        private static int _lastPage;

        internal sealed class Window : DarkDialog
        {
            private const string OtherCategory = "Other";

            private readonly Settings _baseline, _work;
            private readonly ActionRegistry _registry;
            private readonly DarkNavList _nav;
            private readonly Panel _pageHost;
            private readonly List<Control> _pages = new List<Control>();
            private readonly Label _restartNote;
            private readonly List<KeyValuePair<Label, Control>> _wrapping = new List<KeyValuePair<Label, Control>>();
            // Hints that show a shortcut ("Left / Right"); re-read whenever the editor changes keys.
            private readonly List<Action> _keyHintUpdates = new List<Action>();
            private bool _recentCleared;

            // Shortcut editor state: action id -> edited key list, in display order.
            private readonly Dictionary<string, List<Keys>> _keys = new Dictionary<string, List<Keys>>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _keyOrder = new List<string>();
            private DarkTable _shortcutsPage;
            private DarkList _keyGrid;
            private DarkTextBox _keyFilter, _keyEdit;
            private DarkButton _record, _clear, _reset, _resetAll, _moveKeys;
            private Label _keyTitle, _keyStatus;
            private bool _recording, _updatingEditor;
            // Text typed into the shortcut box stays pending until Enter, focus leaving the box
            // or OK, and is applied only if it parses completely. _editId = action shown in the box.
            private string _editId;
            private bool _keyEditDirty;
            private int _editorSyncs;

            public Window(Settings settings, ActionRegistry registry)
            {
                _baseline = settings.Clone();
                _work = settings.Clone();
                _registry = registry;
                LoadKeys();

                Text = "Settings";
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;

                var root = new DarkTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Window };
                _nav = new DarkNavList { Dock = DockStyle.Left };
                _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
                body.Controls.Add(_pageHost);
                body.Controls.Add(_nav);
                _pageHost.BringToFront();

                BuildPlaybackPage();
                BuildPlaylistPage();
                BuildCullPage();
                BuildVideoPage();
                BuildAudioPage();
                BuildWindowPage();
                BuildMousePage();
                BuildScreenshotPage();
                BuildFilesPage();
                BuildShortcutsPage();
                BuildAdvancedPage();

                _nav.SelectedIndexChanged += (s, e) => ShowPage(_nav.SelectedIndex);

                _restartNote = CreateLabel("", true);
                // OK checks the shortcut editor before closing (see Accept).
                var ok = CreateButton("OK", true);
                ok.Click += (s, e) => Accept();
                var cancel = CreateButton("Cancel", false, DialogResult.Cancel);
                root.Controls.Add(body, 0, 0);
                root.Controls.Add(CreateFooter(_restartNote, ok, cancel), 0, 1);
                Controls.Add(root);
                AcceptButton = ok;
                CancelButton = cancel;

                OnDpi(() =>
                {
                    _nav.Width = S(196);
                    _pageHost.Padding = Pad(28, 18, 12, 0);
                    ClientSize = new Size(S(800), S(600));
                    MinimumSize = SizeFromClientSize(new Size(S(720), S(460)));
                });
                _nav.SelectedIndex = Math.Max(0, Math.Min(_lastPage, _nav.Count - 1));
                UpdateRestartNote();
                ActiveControl = _nav;
            }

            // ---- pages -----------------------------------------------------------------

            internal void SelectPage(string title)
            {
                for (int i = 0; i < _nav.Count; i++)
                    if (string.Equals(_nav.TextAt(i), title, StringComparison.OrdinalIgnoreCase)) _nav.SelectedIndex = i;
            }

            private void ShowPage(int index)
            {
                if (index < 0 || index >= _pages.Count) return;
                StopRecording();
                _lastPage = index;
                for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
            }

            // A scrollable page: title, then rows of [label | control (+ hint)] or full-width items.
            private DarkTable NewPage(string glyph, string title)
            {
                var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Window, Visible = false };
                DarkControls.UseDarkTheme(scroller);
                var table = CreateTable(2);
                table.Dock = DockStyle.Top;
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                var heading = CreateLabel(title, false, 14f, FontStyle.Bold);
                OnDpi(() =>
                {
                    heading.Margin = Pad(0, 0, 0, 10);
                    table.Padding = Pad(0, 0, 16, 16);
                });
                AddFull(table, heading);
                scroller.Controls.Add(table);
                scroller.Resize += (s, e) => UpdateWrapping(scroller);
                _pageHost.Controls.Add(scroller);
                _pages.Add(scroller);
                _nav.AddItem(glyph, title);
                return table;
            }

            private static int NextRow(DarkTable t)
            {
                int row = t.RowCount++;
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                return row;
            }

            private void AddFull(DarkTable t, Control c)
            {
                int row = NextRow(t);
                t.Controls.Add(c, 0, row);
                t.SetColumnSpan(c, 2);
            }

            private void AddSection(DarkTable t, string text)
            {
                AddFull(t, CreateSectionLabel(text));
            }

            private DarkCheckBox AddCheck(DarkTable t, string text, bool value, Action<bool> set, string note = null)
            {
                var c = CreateCheckBox(text, value, note);
                c.CheckedChanged += (s, e) => { set(c.Checked); UpdateRestartNote(); };
                AddFull(t, c);
                return c;
            }

            // keyHint: a shortcut hint ("Left / Right") that follows edits on the Shortcuts page.
            private Label AddField(DarkTable t, string label, Control field, string hint = null, Func<string> keyHint = null)
            {
                int row = NextRow(t);
                var l = CreateLabel(label);
                l.Anchor = AnchorStyles.Left;
                OnDpi(() => l.Margin = Pad(0, 3, 18, 3));
                field.Anchor = AnchorStyles.Left;
                t.Controls.Add(l, 0, row);
                if (keyHint != null) hint = keyHint() ?? "";
                if (string.IsNullOrEmpty(hint) && keyHint == null)
                {
                    t.Controls.Add(field, 1, row);
                    return l;
                }
                // Short hint right after the control, vertically centred with it.
                var cell = CreateTable(2);
                cell.Anchor = AnchorStyles.Left;
                cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var h = CreateLabel(hint, true);
                h.Anchor = AnchorStyles.Left;
                OnDpi(() => h.Margin = Pad(12, 3, 0, 3));
                cell.Controls.Add(field, 0, 0);
                cell.Controls.Add(h, 1, 0);
                t.Controls.Add(cell, 1, row);
                if (keyHint != null) _keyHintUpdates.Add(() => h.Text = keyHint() ?? "");
                return l;
            }

            // Dim explanatory text across the page width; wraps with the page.
            private void AddHint(DarkTable t, string text)
            {
                var h = CreateLabel(text, true, 8.5f);
                OnDpi(() => h.Margin = Pad(0, 2, 0, 6));
                AddFull(t, h);
                _wrapping.Add(new KeyValuePair<Label, Control>(h, t.Parent ?? t)); // keyed by the page's scroller
            }

            private void UpdateWrapping(Control scroller)
            {
                int avail = scroller.ClientSize.Width - S(24);
                foreach (var kv in _wrapping)
                    if (kv.Value == scroller) kv.Key.MaximumSize = new Size(Math.Max(S(120), avail - kv.Key.Margin.Left), 0);
            }

            private DarkComboBox AddCombo(DarkTable t, string label, string[] items, int selected, Action<int> set, int width = 240, string hint = null, Func<string> keyHint = null)
            {
                var c = CreateComboBox(width, items);
                c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, selected));
                c.SelectedIndexChanged += (s, e) => { if (c.SelectedIndex >= 0) set(c.SelectedIndex); };
                AddField(t, label, c, hint, keyHint);
                return c;
            }

            private DarkNumericBox AddNumber(DarkTable t, string label, double min, double max, double value, double inc, int decimals, string suffix, Action<double> set, string hint = null, Func<string> keyHint = null)
            {
                decimal v = (decimal)Math.Max(min, Math.Min(max, double.IsNaN(value) ? min : value));
                var n = CreateNumericBox((decimal)min, (decimal)max, v, (decimal)inc, decimals, suffix, 120);
                n.ValueChanged += (s, e) => set((double)n.Value);
                AddField(t, label, n, hint, keyHint);
                return n;
            }

            // Index of `value` in `values` (case-insensitive), 0 when absent.
            private static int IndexOf(string[] values, string value)
            {
                int i = Array.FindIndex(values, v => string.Equals(v, (value ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                return i < 0 ? 0 : i;
            }

            // A combo or field with a check box after it on the same line, vertically centred.
            private Control Pair(Control first, Control second)
            {
                var cell = CreateTable(2);
                cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                first.Anchor = AnchorStyles.Left;
                second.Anchor = AnchorStyles.Left;
                OnDpi(() => second.Margin = Pad(16, 3, 0, 3));
                cell.Controls.Add(first, 0, 0);
                cell.Controls.Add(second, 1, 0);
                return cell;
            }

            private DarkTextBox AddText(DarkTable t, string label, string value, string cue, Action<string> set, int width = 240, string hint = null)
            {
                var b = CreateTextBox(value, width, cue);
                b.TextChanged += (s, e) => set(b.Text.Trim());
                AddField(t, label, b, hint);
                return b;
            }

            private void BuildPlaybackPage()
            {
                var t = NewPage(Glyphs.Play, "Playback");
                AddCombo(t, "Repeat by default", new[] { "Off", "One (loop the current file)", "All (go through the playlist)" },
                    (int)_work.Repeat, i => _work.Repeat = (RepeatMode)i, 260);
                AddCheck(t, "When repeat is off, continue with the next file at the end", _work.AutoAdvance, v => _work.AutoAdvance = v);
                AddCheck(t, "Resume each file where it was left", _work.ResumePlayback, v => _work.ResumePlayback = v);
                AddCheck(t, "Keep the playback speed when changing files", _work.KeepSpeedBetweenFiles, v => _work.KeepSpeedBetweenFiles = v);
                AddCheck(t, "Keep the audio pitch when changing speed", _work.PitchCorrection, v => _work.PitchCorrection = v);

                AddSection(t, "Seeking");
                AddNumber(t, "Seek step", 0.1, 600, _work.SeekStep, 0.5, 1, "s", v => _work.SeekStep = v,
                    keyHint: () => KeyHint(ActionIds.SeekBackward, ActionIds.SeekForward));
                AddNumber(t, "Short seek step", 0.1, 600, _work.SeekStepShort, 0.5, 1, "s", v => _work.SeekStepShort = v,
                    keyHint: () => KeyHint(ActionIds.SeekBackwardShort, ActionIds.SeekForwardShort));
                AddNumber(t, "Long seek step", 0.1, 3600, _work.SeekStepLong, 5, 1, "s", v => _work.SeekStepLong = v,
                    keyHint: () => KeyHint(ActionIds.SeekBackwardLong, ActionIds.SeekForwardLong));
                AddHint(t, "Keyboard seeks land on the exact frame. Dragging the seek bar previews keyframes, then lands exactly on release.");
            }

            // "Left / Right" from the first key of each action, as currently bound in the editor.
            private string KeyHint(string backId, string forwardId)
            {
                string b = KeyText(backId), f = KeyText(forwardId);
                if (b == null && f == null) return null;
                return (b ?? "—") + " / " + (f ?? "—");
            }

            // First key of an action as currently bound in the editor, or null.
            private string KeyText(string id)
            {
                List<Keys> keys;
                return _keys.TryGetValue(id, out keys) && keys.Count > 0 ? KeyNames.Format(keys[0]) : null;
            }

            // "Shortcut: O" (a lone key name next to a field reads like a value), or null.
            private string ShortcutHint(string id)
            {
                string k = KeyText(id);
                return k != null ? "Shortcut: " + k : null;
            }

            private static readonly string[] SortValues = { "Name", "DateModified", "Size", "Type" };

            private void BuildPlaylistPage()
            {
                var t = NewPage(Glyphs.Playlist, "Playlist");
                AddCheck(t, "Open the other videos in the same folder as a playlist", _work.AutoLoadFolder, v => _work.AutoLoadFolder = v);
                AddCheck(t, "Include subfolders when opening a folder", _work.IncludeSubfolders, v => _work.IncludeSubfolders = v);
                AddHint(t, "Applies to folders you open or drop. Opening a single file still lists only the videos next to it.");

                AddSection(t, "Order");
                var sort = CreateComboBox(180, "Name", "Date modified", "Size", "Type");
                sort.SelectedIndex = IndexOf(SortValues, _work.PlaylistSortBy);
                sort.SelectedIndexChanged += (s, e) => { if (sort.SelectedIndex >= 0) _work.PlaylistSortBy = SortValues[sort.SelectedIndex]; };
                var descending = CreateCheckBox("Descending", _work.PlaylistSortDescending);
                descending.CheckedChanged += (s, e) => _work.PlaylistSortDescending = descending.Checked;
                AddField(t, "Sort by", Pair(sort, descending));
                AddHint(t, "The order of the files listed from a folder. The playlist's sort menu changes it too. Descending with Date modified lists the newest first.");
            }

            private static readonly string[] PickActionValues = { "Move", "Copy", "None" };
            private static readonly string[] RejectActionValues = { "RecycleBin", "Folder", "None" };

            private void BuildCullPage()
            {
                var t = NewPage(Glyphs.Check, "Cull");
                AddCheck(t, "Cull mode: decide on each clip with one key", _work.CullMode, v => _work.CullMode = v);
                AddKeyHint(t, CullKeysHint);
                AddCheck(t, "Go to the next clip after each pick or reject", _work.CullAutoAdvance, v => _work.CullAutoAdvance = v);

                AddSection(t, "Apply decisions");
                AddKeyHint(t, () => "Nothing is moved or deleted while you decide. Apply Decisions"
                    + KeyInParens(ActionIds.CullCommit) + " lists what will happen and asks before doing it.");
                Control keepRow = null, rejectRow = null;
                var picks = AddCombo(t, "Picks", new[] { "Move to the Keep folder", "Copy to the Keep folder", "Leave in place" },
                    IndexOf(PickActionValues, _work.CullPickAction), i =>
                    {
                        _work.CullPickAction = PickActionValues[i];
                        if (keepRow != null) keepRow.Enabled = i != 2;
                    });
                keepRow = AddFolder(t, "Keep folder", _work.CullKeepFolder, "A “Keep” folder beside each clip",
                    "Choose the folder for picked clips", v => _work.CullKeepFolder = v, false);
                keepRow.Enabled = picks.SelectedIndex != 2;
                var rejects = AddCombo(t, "Rejects", new[] { "Move to the Recycle Bin", "Move to a folder", "Leave in place" },
                    IndexOf(RejectActionValues, _work.CullRejectAction), i =>
                    {
                        _work.CullRejectAction = RejectActionValues[i];
                        if (rejectRow != null) rejectRow.Enabled = i == 1;
                    });
                rejectRow = AddFolder(t, "Rejects folder", _work.CullRejectFolder, "A “Rejected” folder beside each clip",
                    "Choose the folder for rejected clips", v => _work.CullRejectFolder = v, false);
                rejectRow.Enabled = rejects.SelectedIndex == 1;
                AddHint(t, "Leave a folder empty to use one beside each clip. Clips that are neither picked nor rejected stay where they are.");

                AddSection(t, "Bins");
                AddHint(t, "Move Clip to Bin sends the current clip straight to one of these folders. A bin without a folder asks for one the first time.");
                for (int bin = 1; bin <= 5; bin++)
                {
                    string id = ActionIds.CullToBin + ":" + bin.ToString(CultureInfo.InvariantCulture);
                    string name = "Bin " + bin.ToString(CultureInfo.InvariantCulture);
                    int n = bin;
                    AddFolder(t, name, BinFolder(n), "Not set", "Choose the folder for " + name, v => SetBinFolder(n, v), true,
                        () => { string k = KeyText(id); return k != null ? name + " (" + k + ")" : name; });
                }
            }

            private string BinFolder(int n)
            {
                switch (n)
                {
                    case 1: return _work.CullBin1;
                    case 2: return _work.CullBin2;
                    case 3: return _work.CullBin3;
                    case 4: return _work.CullBin4;
                    default: return _work.CullBin5;
                }
            }

            private void SetBinFolder(int n, string value)
            {
                switch (n)
                {
                    case 1: _work.CullBin1 = value; break;
                    case 2: _work.CullBin2 = value; break;
                    case 3: _work.CullBin3 = value; break;
                    case 4: _work.CullBin4 = value; break;
                    default: _work.CullBin5 = value; break;
                }
            }

            // "P picks, X rejects, ..." from the keys currently bound in the editor (Cull layer).
            private string CullKeysHint()
            {
                var parts = new List<string>();
                Action<string, string> add = (id, what) => { string k = KeyText(id); if (k != null) parts.Add(k + " " + what); };
                add(ActionIds.CullPick, "picks");
                add(ActionIds.CullReject, "rejects");
                add(ActionIds.CullUnflag, "clears the pick or reject");
                string first = KeyText(ActionIds.CullRate + ":1"), last = KeyText(ActionIds.CullRate + ":5");
                if (first != null && last != null) parts.Add(first + "–" + last + " rate");
                add(ActionIds.CullNextUndecided, "goes to the next undecided clip");
                string text = parts.Count > 0
                    ? "In Cull mode, " + string.Join(", ", parts) + ". These keys do their usual job again when Cull mode is off."
                    : "Cull mode has no keys assigned; set them on the Shortcuts page.";
                string toggle = KeyText(ActionIds.ToggleCullMode);
                return toggle != null ? text + " " + toggle + " turns Cull mode on and off." : text;
            }

            // " (Ctrl+Enter)" or "".
            private string KeyInParens(string id)
            {
                string k = KeyText(id);
                return k != null ? " (" + k + ")" : "";
            }

            // A wrapping hint whose text follows edits on the Shortcuts page.
            private void AddKeyHint(DarkTable t, Func<string> text)
            {
                var h = CreateLabel(text(), true, 8.5f);
                OnDpi(() => h.Margin = Pad(0, 2, 0, 6));
                AddFull(t, h);
                _wrapping.Add(new KeyValuePair<Label, Control>(h, t.Parent ?? t));
                _keyHintUpdates.Add(() => h.Text = text());
            }

            // [label] [folder box][Browse…]([Clear]). The box's cue says what an empty value means.
            // Returns the row (disable it to disable the field). keyLabel: a label that follows key edits.
            private Control AddFolder(DarkTable t, string label, string value, string cue, string pickerTitle, Action<string> set, bool clearButton, Func<string> keyLabel = null)
            {
                var box = CreateTextBox(value, 250, cue);
                box.TextChanged += (s, e) => set(box.Text.Trim());
                var browse = CreateButton("Browse…");
                browse.Click += (s, e) =>
                {
                    string picked = FolderPicker.Show(this, pickerTitle, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
                    if (picked != null) box.Text = picked;
                };
                var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
                row.Controls.Add(box);
                row.Controls.Add(browse);
                DarkButton clear = null;
                if (clearButton)
                {
                    clear = CreateButton("Clear");
                    clear.Enabled = box.Text.Length > 0;
                    clear.Click += (s, e) => { box.Text = ""; box.Focus(); };
                    box.TextChanged += (s, e) => clear.Enabled = box.Text.Length > 0;
                    row.Controls.Add(clear);
                }
                OnDpi(() =>
                {
                    box.Margin = Pad(0, 3, 0, 3);
                    browse.Margin = Pad(8, 3, 0, 3);
                    if (clear != null)
                    {
                        clear.MinimumSize = new Size(S(64), 0);
                        clear.Margin = Pad(6, 3, 0, 3);
                    }
                });
                Label l = AddField(t, keyLabel != null ? keyLabel() : label, row);
                if (keyLabel != null) _keyHintUpdates.Add(() => l.Text = keyLabel());
                return row;
            }

            private static readonly string[] HwDecValues = { "auto-safe", "auto", "auto-copy-safe", "d3d11va", "d3d11va-copy", "nvdec", "dxva2-copy", "no" };

            private void BuildVideoPage()
            {
                var t = NewPage("", "Video");
                var hw = CreateComboBox(180, HwDecValues);
                hw.DropDownStyle = ComboBoxStyle.DropDown;
                hw.Text = string.IsNullOrWhiteSpace(_work.HwDec) ? "auto-safe" : _work.HwDec;
                hw.TextChanged += (s, e) => _work.HwDec = hw.Text.Trim();
                AddField(t, "Hardware decoding", hw, "Default: auto-safe");
                AddHint(t, "Any mpv --hwdec value can be typed. \"no\" decodes in software, which is slower but works everywhere. Applies to the next file opened.");
                AddCheck(t, "Show a thumbnail preview when hovering over the seek bar", _work.ThumbnailPreview, v => _work.ThumbnailPreview = v);
            }

            private void BuildAudioPage()
            {
                var t = NewPage(Glyphs.Subtitles, "Audio & Subtitles");
                AddText(t, "Preferred audio language", _work.PreferredAudioLang, "e.g. en,eng", v => _work.PreferredAudioLang = v, 200);
                AddText(t, "Preferred subtitle language", _work.PreferredSubLang, "e.g. en,eng", v => _work.PreferredSubLang = v, 200);
                AddHint(t, "Comma-separated language codes in order of preference. Leave empty to use the file's default tracks.");

                AddSection(t, "Audio");
                AddText(t, "Audio device", _work.AudioDevice, "auto", v => _work.AudioDevice = v, 260);
                AddHint(t, "auto = system default; pick a device from the Audio menu.");
                AddCombo(t, "Channels", new[] { "Original", "Stereo", "Mono" }, IndexOf(ChannelValues, _work.AudioChannels),
                    i => _work.AudioChannels = ChannelValues[i], 200);
                AddCheck(t, "Normalize volume", _work.AudioNormalize, v => _work.AudioNormalize = v);

                AddSection(t, "Subtitles");
                AddCheck(t, "Show subtitles", _work.SubtitlesVisible, v => _work.SubtitlesVisible = v);
                AddNumber(t, "Subtitle size", 0.1, 5, _work.SubScale, 0.1, 2, "×", v => _work.SubScale = v);
            }

            private static readonly string[] ChannelValues = { "auto", "stereo", "mono" };

            private void BuildWindowPage()
            {
                var t = NewPage("", "Window");
                DarkNumericBox max = null;
                var fit = AddCheck(t, "Resize the window to fit each video", _work.AutoFitWindow, v => { _work.AutoFitWindow = v; if (max != null) max.Enabled = v; });
                max = AddNumber(t, "Largest window size", 30, 100, _work.AutoFitMaxPercent, 5, 0, "%", v => _work.AutoFitMaxPercent = (int)Math.Round(v), "of the screen");
                max.Enabled = fit.Checked;
                AddCheck(t, "Keep the video's aspect ratio when resizing", _work.KeepAspectOnResize, v => _work.KeepAspectOnResize = v);
                AddCheck(t, "Hide the title bar", _work.Borderless, v => _work.Borderless = v);
                AddCheck(t, "Keep the window above other windows", _work.AlwaysOnTop, v => _work.AlwaysOnTop = v);
                AddCheck(t, "Show progress on the taskbar button", _work.TaskbarProgress, v => _work.TaskbarProgress = v);
                AddCheck(t, "Open files in the running VidFlow window", _work.SingleInstance, v => _work.SingleInstance = v, "restart");

                AddSection(t, "Controls");
                AddCheck(t, "Show the control bar", _work.ShowControls, v => _work.ShowControls = v);
                AddCheck(t, "Show the controls in fullscreen when the mouse is at the bottom", _work.FullscreenControlsOnHover, v => _work.FullscreenControlsOnHover = v);
                AddCheck(t, "Show the frame number in the control bar", _work.ShowFrameNumber, v => _work.ShowFrameNumber = v);
                AddCheck(t, "Show the remaining time instead of the length", _work.ShowRemainingTime, v => _work.ShowRemainingTime = v);
                AddCheck(t, "Hide the mouse cursor over the video when idle", _work.HideCursorWhenIdle, v => _work.HideCursorWhenIdle = v);

                AddSection(t, "On-screen display");
                AddCheck(t, "Show on-screen messages", _work.OsdMessages, v => _work.OsdMessages = v);
                AddCombo(t, "Persistent time & frame display", new[] { "Off", "Fullscreen only", "Always" }, (int)_work.OsdStatus,
                    i => _work.OsdStatus = (OsdStatusMode)i, 180, keyHint: () => ShortcutHint(ActionIds.ToggleOsdStatus));
                AddHint(t, "Keeps the time, frame number and speed on the video, e.g. for reviewing in fullscreen without the controls.");
            }

            private void BuildMousePage()
            {
                var t = NewPage("", "Mouse");
                AddCombo(t, "Mouse wheel", new[] { "Volume", "Seek", "Step frames", "Zoom" }, (int)_work.Wheel, i => _work.Wheel = (WheelAction)i, 200);
                AddCombo(t, "Left click", new[] { "Play / pause", "Do nothing" }, (int)_work.LeftClick, i => _work.LeftClick = (ClickAction)i, 200);
                AddCheck(t, "Double-click toggles fullscreen", _work.DoubleClickFullscreen, v => _work.DoubleClickFullscreen = v);
                AddCheck(t, "Drag the video to move the window (when not zoomed in)", _work.DragMovesWindow, v => _work.DragMovesWindow = v);
                AddHint(t, "Always available: Ctrl+Wheel zooms at the cursor, Shift+Wheel steps frames, Alt+Wheel changes speed, middle click resets the zoom, and dragging pans while zoomed in.");
            }

            private static readonly string[] ShotFormats = { "png", "jpg", "webp" };

            private void BuildScreenshotPage()
            {
                var t = NewPage(Glyphs.Camera, "Screenshots");
                string defaultFolder = DefaultScreenshotFolder();
                var folder = CreateTextBox(_work.ScreenshotFolder, 300, defaultFolder);
                folder.TextChanged += (s, e) => _work.ScreenshotFolder = folder.Text.Trim();
                var browse = CreateButton("Browse…");
                OnDpi(() => browse.Margin = Pad(8, 3, 0, 3));
                browse.Click += (s, e) =>
                {
                    string start = string.IsNullOrWhiteSpace(folder.Text) ? defaultFolder : folder.Text.Trim();
                    string picked = FolderPicker.Show(this, "Choose the screenshot folder", start);
                    if (picked != null) folder.Text = picked;
                };
                var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
                folder.Margin = Padding.Empty;
                OnDpi(() => folder.Margin = Pad(0, 3, 0, 3));
                row.Controls.Add(folder);
                row.Controls.Add(browse);
                AddField(t, "Save to", row);
                AddHint(t, "Leave empty to use " + defaultFolder + ".");
                int fmt = Array.FindIndex(ShotFormats, f => string.Equals(f, _work.ScreenshotFormat, StringComparison.OrdinalIgnoreCase));
                AddCombo(t, "Format", new[] { "PNG (lossless)", "JPEG (quality 95)", "WebP" }, fmt < 0 ? 0 : fmt, i => _work.ScreenshotFormat = ShotFormats[i], 200);
            }

            private static string DefaultScreenshotFolder()
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VidFlow");
            }

            private void BuildFilesPage()
            {
                var t = NewPage(Glyphs.Folder, "Files");
                AddCheck(t, "Ask before moving a file to the Recycle Bin", _work.ConfirmDelete, v => _work.ConfirmDelete = v);
                AddNumber(t, "Recent files to remember", 0, 50, _work.MaxRecentFiles, 1, 0, null, v => _work.MaxRecentFiles = (int)Math.Round(v));
                var clear = CreateButton(RecentButtonText());
                clear.Glyph = "";
                clear.Enabled = _work.RecentFiles.Count > 0;
                clear.Anchor = AnchorStyles.Left;
                OnDpi(() => clear.Margin = Pad(0, 12, 0, 0));
                clear.Click += (s, e) =>
                {
                    _work.RecentFiles.Clear();
                    _recentCleared = true;
                    clear.Text = "Recent files cleared";
                    clear.Enabled = false;
                };
                AddFull(t, clear);
                AddOpenWith(t);
            }

            // Explorer "Open with" registration. Runs right away through the player's actions
            // (not on OK); each button is left out when the player has no such action.
            private void AddOpenWith(DarkTable t)
            {
                bool canAdd = _registry != null && _registry.IsKnown(ActionIds.RegisterFileTypes);
                bool canRemove = _registry != null && _registry.IsKnown(ActionIds.UnregisterFileTypes);
                if (!canAdd && !canRemove) return;
                AddSection(t, "Open With");
                if (AppInfo.IsPackaged)
                {
                    AddHint(t, "Windows lists VidFlow in Explorer's “Open with” menu for video and audio files automatically in this edition. To make it the default player, choose it in Windows Settings › Apps › Default apps.");
                    return;
                }
                var add = CreateButton("Add VidFlow to Open With…");
                add.Glyph = Glyphs.Add;
                var remove = CreateButton("Remove from Open With");
                remove.Glyph = Glyphs.Remove;
                add.Visible = canAdd;
                remove.Visible = canRemove;
                var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
                OnDpi(() =>
                {
                    add.Margin = Pad(0, 2, 8, 2);
                    remove.Margin = Pad(0, 2, 0, 2);
                });
                row.Controls.Add(add);
                row.Controls.Add(remove);
                AddFull(t, row);
                AddHint(t, "Lists VidFlow in Explorer's “Open with” menu for video and audio files, for your account only. To make it the default player, choose it in Windows Settings › Apps › Default apps.");
                // Outcome of the last click, under the explanation (wraps with the page).
                var result = CreateLabel("");
                result.Visible = false;
                OnDpi(() => result.Margin = Pad(0, 2, 0, 6));
                AddFull(t, result);
                _wrapping.Add(new KeyValuePair<Label, Control>(result, t.Parent ?? t));

                Action refresh = () =>
                {
                    add.Enabled = canAdd && _registry.IsEnabled(ActionIds.RegisterFileTypes);
                    remove.Enabled = canRemove && _registry.IsEnabled(ActionIds.UnregisterFileTypes);
                };
                Action<string, string> run = (id, done) =>
                {
                    bool ok = _registry.Execute(id);
                    result.Text = ok ? done : "Couldn't update the “Open with” entries.";
                    result.ForeColor = ok ? Theme.Text : Theme.Warning;
                    result.Visible = true;
                    refresh();
                };
                add.Click += (s, e) => run(ActionIds.RegisterFileTypes, "VidFlow is listed under “Open with”.");
                remove.Click += (s, e) => run(ActionIds.UnregisterFileTypes, "VidFlow was removed from “Open with”.");
                refresh();
            }

            private string RecentButtonText()
            {
                int n = _work.RecentFiles.Count;
                return n > 0 ? "Clear recent files (" + n.ToString(CultureInfo.InvariantCulture) + ")" : "No recent files";
            }

            private void BuildAdvancedPage()
            {
                var t = NewPage("", "Advanced");
                var label = CreateLabel("Extra mpv options");
                var note = CreateLabel("(restart)", true);
                var head = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
                OnDpi(() => { label.Margin = Pad(0, 3, 4, 3); note.Margin = Pad(0, 3, 0, 3); });
                head.Controls.Add(label);
                head.Controls.Add(note);
                AddFull(t, head);

                var opts = new DarkTextBox { Multiline = true, Monospace = true, Text = (_work.ExtraMpvOptions ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine) };
                opts.TextBox.WordWrap = false;
                opts.TextBox.ScrollBars = ScrollBars.Both;
                opts.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                opts.TextChanged += (s, e) => { _work.ExtraMpvOptions = NormalizeOptions(opts.Text); UpdateRestartNote(); };
                OnDpi(() =>
                {
                    opts.Height = S(150);
                    opts.Margin = Pad(0, 4, 0, 4);
                });
                AddFull(t, opts);
                AddHint(t, "One option per line as name=value, for example deband=yes or scale=ewa_lanczossharp. Applied when VidFlow starts.");

                AddSection(t, "Files");
                string dir = SafeDirectory(_baseline.FilePath);
                var open = CreateButton("Open settings folder");
                open.Glyph = "";
                open.Anchor = AnchorStyles.Left;
                open.Enabled = dir != null;
                OnDpi(() => open.Margin = Pad(0, 2, 0, 10));
                open.Click += (s, e) => OpenFolder(dir);
                AddFull(t, open);
                AddPath(t, "Settings file", _baseline.FilePath);
                AddPath(t, "mpv config folder", dir != null ? Path.Combine(dir, "mpv") : null);
            }

            private static string NormalizeOptions(string text)
            {
                var lines = (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
                while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                return string.Join("\n", lines);
            }

            private void AddPath(DarkTable t, string label, string path)
            {
                var box = new TextBox
                {
                    Text = string.IsNullOrEmpty(path) ? "(not saved yet)" : path,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = Theme.Window,
                    ForeColor = Theme.Text,
                    TabStop = false,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right,
                };
                EditMenu.Attach(box);
                OnDpi(() => box.Margin = Pad(0, 4, 0, 4));
                int row = NextRow(t);
                var l = CreateLabel(label, true);
                l.Anchor = AnchorStyles.Left;
                OnDpi(() => l.Margin = Pad(0, 3, 18, 3));
                t.Controls.Add(l, 0, row);
                t.Controls.Add(box, 1, row);
            }

            private static string SafeDirectory(string file)
            {
                if (string.IsNullOrEmpty(file)) return null;
                try { return Path.GetDirectoryName(file); }
                catch (ArgumentException) { return null; }
                catch (PathTooLongException) { return null; }
            }

            private static void OpenFolder(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                try
                {
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                }
                catch (Exception ex)
                {
                    Log.Warn("open settings folder failed: " + ex.Message);
                }
            }

            private void UpdateRestartNote()
            {
                if (_restartNote == null) return;
                bool pending = _work.SingleInstance != _baseline.SingleInstance
                    || NormalizeOptions(_work.ExtraMpvOptions) != NormalizeOptions(_baseline.ExtraMpvOptions);
                _restartNote.Text = pending ? "Restart VidFlow to apply the changes marked (restart)." : "";
            }

            // ---- shortcuts -------------------------------------------------------------

            private static List<Keys> DefaultKeys(string id)
            {
                var list = new List<Keys>();
                var def = ActionCatalog.Find(id);
                if (def == null) return list;
                foreach (var name in def.DefaultKeys)
                {
                    Keys k;
                    if (KeyNames.TryParse(name, out k) && !list.Contains(k)) list.Add(k);
                }
                return list;
            }

            private void LoadKeys()
            {
                foreach (var cat in ActionCatalog.Categories)
                    foreach (var def in ActionCatalog.All.Where(a => a.Category == cat))
                    {
                        string ov;
                        _keys[def.Id] = _work.KeyOverrides.TryGetValue(def.Id, out ov) ? Distinct(KeyNames.ParseList(ov)) : DefaultKeys(def.Id);
                        _keyOrder.Add(def.Id);
                    }
                // Overrides for ids outside the catalog (hand-edited INI) stay editable.
                foreach (var kv in _work.KeyOverrides)
                {
                    if (_keys.ContainsKey(kv.Key)) continue;
                    _keys[kv.Key] = Distinct(KeyNames.ParseList(kv.Value));
                    _keyOrder.Add(kv.Key);
                }
            }

            private static List<Keys> Distinct(List<Keys> keys)
            {
                var result = new List<Keys>();
                foreach (var k in keys) if (!result.Contains(k)) result.Add(k);
                return result;
            }

            private static string TitleOf(string id)
            {
                var def = ActionCatalog.Find(id);
                return def != null ? def.Title : id;
            }

            private static string CategoryOf(string id)
            {
                var def = ActionCatalog.Find(id);
                return def != null ? def.Category : OtherCategory;
            }

            private static string LayerOf(string id) { return ActionRegistry.LayerOf(id); }

            private static bool InCullLayer(string id) { return LayerOf(id) == ActionCatalog.LayerCull; }

            // The Category column: keys of the Cull layer say that they only work in Cull mode
            // (short: the column is narrow).
            private const string CullLayerCategory = "Cull mode";

            private static string DisplayCategoryOf(string id) { return InCullLayer(id) ? CullLayerCategory : CategoryOf(id); }

            // The actions using each key, per layer. Only actions of the same layer compete for a key:
            // the Cull layer's keys work only in Cull mode, where they take precedence over the global ones.
            private Dictionary<(string Layer, Keys Key), List<string>> KeyUsers()
            {
                var users = new Dictionary<(string Layer, Keys Key), List<string>>();
                foreach (var id in _keyOrder)
                {
                    string layer = LayerOf(id);
                    foreach (var k in _keys[id])
                    {
                        List<string> l;
                        if (!users.TryGetValue((layer, k), out l)) users[(layer, k)] = l = new List<string>();
                        l.Add(id);
                    }
                }
                return users;
            }

            private void BuildShortcutsPage()
            {
                var page = new DarkTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Visible = false, Margin = Padding.Empty };
                _shortcutsPage = page;
                page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var heading = CreateLabel("Shortcuts", false, 14f, FontStyle.Bold);
                page.Controls.Add(heading, 0, 0);

                _keyFilter = new DarkTextBox { Dock = DockStyle.Fill, Glyph = Glyphs.Search, Cue = "Filter by action, category or key" };
                _keyFilter.TextChanged += (s, e) => FillKeyGrid();
                page.Controls.Add(_keyFilter, 0, 1);

                _keyGrid = new DarkList { Dock = DockStyle.Fill, EmptyText = "No actions match the filter." };
                _keyGrid.Columns.Add(new DarkList.Column("Action", 0.42f));
                _keyGrid.Columns.Add(new DarkList.Column("Category", 0.2f) { Dim = true });
                _keyGrid.Columns.Add(new DarkList.Column("Shortcuts", 0.38f, true));
                _keyGrid.SelectedIndexChanged += (s, e) => { StopRecording(); SyncEditor(); };
                _keyGrid.RowActivated += (s, e) => { _keyEdit.Focus(); _keyEdit.SelectAll(); };
                page.Controls.Add(_keyGrid, 0, 2);

                // Editor for the selected action.
                var editor = new DarkTable { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
                editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                for (int i = 0; i < 5; i++) editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _keyTitle = CreateLabel("", false, 9f, FontStyle.Bold);
                editor.Controls.Add(_keyTitle, 0, 0);
                editor.SetColumnSpan(_keyTitle, 6);
                _keyEdit = new DarkTextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Cue = "Not assigned" };
                _keyEdit.TextChanged += (s, e) => OnKeyTextEdited();
                _keyEdit.TextBox.Leave += (s, e) => CommitKeyText();
                editor.Controls.Add(_keyEdit, 0, 1);
                _record = CreateButton("Record");
                _record.Glyph = "";
                _record.Click += (s, e) => { if (_recording) StopRecording(); else StartRecording(); };
                _clear = CreateButton("Clear");
                _clear.Click += (s, e) => SetKeys(SelectedKeyId, new List<Keys>());
                _reset = CreateButton("Reset");
                _reset.Click += (s, e) => SetKeys(SelectedKeyId, DefaultKeys(SelectedKeyId));
                _resetAll = CreateButton("Reset all");
                _resetAll.Click += (s, e) => ResetAllKeys();
                var spacer = new Panel { Width = 1, Height = 1, Margin = Padding.Empty, BackColor = Color.Transparent };
                editor.Controls.Add(_record, 1, 1);
                editor.Controls.Add(_clear, 2, 1);
                editor.Controls.Add(_reset, 3, 1);
                editor.Controls.Add(spacer, 4, 1);
                editor.Controls.Add(_resetAll, 5, 1);
                page.Controls.Add(editor, 0, 3);

                // Status line; when the selected action shares a key, a button beside it offers
                // to take the key away from the other action(s).
                var status = new DarkTable { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
                status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _keyStatus = CreateLabel("", true, 8.5f);
                _keyStatus.Anchor = AnchorStyles.Left;
                _moveKeys = CreateButton("");
                _moveKeys.Anchor = AnchorStyles.Right;
                _moveKeys.Visible = false;
                _moveKeys.Click += (s, e) => MoveKeysHere();
                status.Controls.Add(_keyStatus, 0, 0);
                status.Controls.Add(_moveKeys, 1, 0);
                page.Controls.Add(status, 0, 4);
                page.Resize += (s, e) => UpdateStatusWrap();

                OnDpi(() =>
                {
                    heading.Margin = Pad(0, 0, 0, 10);
                    _keyFilter.Margin = Pad(0, 0, 16, 8);
                    _keyGrid.Margin = Pad(0, 0, 16, 8);
                    editor.Margin = Pad(0, 0, 16, 0);
                    _keyTitle.Margin = Pad(0, 2, 0, 6);
                    _keyEdit.Margin = Pad(0, 0, 8, 0);
                    _keyEdit.Width = S(80); // stretches with its column; a small base width lets the row shrink
                    foreach (var b in new[] { _record, _clear, _reset, _resetAll })
                    {
                        b.MinimumSize = new Size(S(72), 0);
                        b.Margin = Pad(0, 0, 6, 0);
                    }
                    _resetAll.Margin = Padding.Empty;
                    spacer.Width = S(10);
                    status.Margin = Pad(0, 6, 16, 8);
                    _keyStatus.Margin = Pad(0, 3, 0, 3);
                    _moveKeys.Margin = Pad(12, 0, 0, 0);
                    _moveKeys.MaximumSize = new Size(S(260), 0);
                    _moveKeys.MinimumSize = Size.Empty;
                });

                _pageHost.Controls.Add(page);
                _pages.Add(page);
                _nav.AddItem("", "Shortcuts");
                FillKeyGrid();
            }

            private string SelectedKeyId
            {
                get { return _keyGrid != null && _keyGrid.SelectedRow != null ? _keyGrid.SelectedRow.Tag as string : null; }
            }

            private void FillKeyGrid()
            {
                string[] tokens = (_keyFilter.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var users = KeyUsers();
                var rows = new List<DarkList.Row>();
                foreach (var id in _keyOrder)
                {
                    if (ActionCatalog.IsHiddenInEdition(id)) continue;   // still in _keys, so a saved binding is kept
                    string title = TitleOf(id), cat = DisplayCategoryOf(id), keys = KeyNames.FormatList(_keys[id]);
                    bool match = tokens.All(t =>
                        title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cat.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        CategoryOf(id).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        keys.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        id.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) continue;
                    rows.Add(KeyRow(id, users));
                }
                // SetRows raises SelectedIndexChanged (which syncs the editor) whenever a row was
                // selected; syncing again would replace a note SyncEditor just showed.
                int syncs = _editorSyncs;
                _keyGrid.SetRows(rows);
                if (_editorSyncs == syncs) SyncEditor();
            }

            // A grid row; only the chips of keys another action of the same layer also uses are drawn as warnings.
            private DarkList.Row KeyRow(string id, Dictionary<(string Layer, Keys Key), List<string>> users)
            {
                string layer = LayerOf(id);
                var shared = _keys[id].Where(k => users[(layer, k)].Count > 1).Select(k => KeyNames.FormatList(new[] { k })).ToList();
                return new DarkList.Row(id, TitleOf(id), DisplayCategoryOf(id), KeyNames.FormatList(_keys[id]))
                {
                    Warning = shared.Count > 0,
                    WarningChips = shared,
                    Dim = _keys[id].Count == 0,
                };
            }

            // After the selection or the list changed: a valid pending edit is applied to its own
            // action; an invalid one stays in the box while that action is still selected, and is
            // dropped (with a note) when another action is shown.
            private void SyncEditor()
            {
                _editorSyncs++;
                string pendingId = _editId;
                if (_keyEditDirty && pendingId != null && !CommitKeyText())
                {
                    if (string.Equals(pendingId, SelectedKeyId, StringComparison.OrdinalIgnoreCase)) return;
                    UpdateEditor();
                    SetStatus("The shortcut text typed for “" + TitleOf(pendingId) + "” was not valid, so its shortcuts were left unchanged.", true);
                    return;
                }
                UpdateEditor();
            }

            private void UpdateEditor()
            {
                string id = SelectedKeyId;
                bool has = id != null;
                _editId = id;
                _keyEditDirty = false;
                _keyEdit.Enabled = has;
                _record.Enabled = has;
                _clear.Enabled = has && _keys[id].Count > 0;
                _reset.Enabled = has && !SameKeys(_keys[id], DefaultKeys(id));
                _resetAll.Enabled = _keyOrder.Any(k => !SameKeys(_keys[k], DefaultKeys(k)));
                _keyTitle.Text = !has ? "Select an action to change its shortcuts"
                    : InCullLayer(id) ? TitleOf(id) + "  ·  Cull mode only" : TitleOf(id);
                _updatingEditor = true;
                try { _keyEdit.Text = has ? KeyNames.FormatList(_keys[id]) : ""; }
                finally { _updatingEditor = false; }
                _keyEdit.Invalid = false;
                ShowKeyStatus();
            }

            private void ShowKeyStatus()
            {
                string id = SelectedKeyId;
                if (_recording)
                {
                    SetStatus("Press the new shortcut for “" + TitleOf(id) + "”. It is added to the list; Esc cancels.", false);
                    return;
                }
                if (id == null)
                {
                    SetStatus("Shortcuts used by more than one action are shown in orange. Such a key runs only one of them: "
                        + "an action whose shortcuts you changed wins over one left at its defaults, otherwise the one listed later wins. "
                        + "Keys of the " + CullLayerCategory + " category work only in Cull mode and only clash with each other.", false);
                    return;
                }
                var users = KeyUsers();
                string layer = LayerOf(id);
                var clashes = new List<string>();
                var sharers = new List<string>();
                foreach (var k in _keys[id])
                {
                    var same = users[(layer, k)];
                    var others = same.Where(o => !SameId(o, id)).ToList();
                    if (others.Count == 0) continue;
                    foreach (var o in others) if (!sharers.Any(x => SameId(x, o))) sharers.Add(o);
                    string winner = WinnerOf(same);
                    clashes.Add(KeyQuote(k) + " is also assigned to " + Titles(others)
                        + (SameId(winner, id) ? "; this action wins" : "; “" + TitleOf(winner) + "” wins"));
                }
                if (clashes.Count > 0)
                {
                    SetStatus(string.Join(".  ", clashes) + ".", true, sharers);
                    return;
                }
                string notes = LayerNotes(id, users);
                SetStatus(notes ?? "Type shortcuts separated by commas (e.g. Ctrl+Right, Comma) and press Enter, or press Record.", false);
            }

            // Keys shared with the other layer are not conflicts (Cull mode keys win while it is on),
            // but the status says what happens. Null when there is nothing to say.
            private string LayerNotes(string id, Dictionary<(string Layer, Keys Key), List<string>> users)
            {
                string layer = LayerOf(id);
                bool cull = layer == ActionCatalog.LayerCull;
                var notes = new List<string>();
                foreach (var k in _keys[id])
                    foreach (var kv in users)
                    {
                        if (kv.Key.Key != k || kv.Key.Layer == layer) continue;
                        notes.Add(cull ? KeyQuote(k) + " is used instead of " + Titles(kv.Value)
                                       : "While Cull mode is on, " + KeyQuote(k) + " runs " + Titles(kv.Value) + " instead");
                    }
                if (!cull) return notes.Count > 0 ? string.Join(".  ", notes) + "." : null;
                string toggle = KeyText(ActionIds.ToggleCullMode);
                string head = "Works only while Cull mode is on" + (toggle != null ? " (" + toggle + ")" : "");
                return notes.Count > 0 ? head + ", where " + string.Join(", ", notes) + "." : head + ".";
            }

            // moveFrom: actions the selected one shares keys with; shows the button that takes them away.
            private void SetStatus(string text, bool warning, List<string> moveFrom = null)
            {
                _keyStatus.Text = text;
                _keyStatus.ForeColor = warning ? Theme.Warning : Theme.TextDim;
                bool move = moveFrom != null && moveFrom.Count > 0;
                if (move)
                {
                    string target = moveFrom.Count == 1 ? "“" + TitleOf(moveFrom[0]) + "”" : "the other actions";
                    _moveKeys.Text = ("Remove from " + target).Replace("&", "&&");
                }
                _moveKeys.Visible = move;
                UpdateStatusWrap();
            }

            // The status text wraps beside the (optional) button.
            private void UpdateStatusWrap()
            {
                if (_shortcutsPage == null || _keyStatus == null) return;
                int avail = _shortcutsPage.ClientSize.Width - S(16);
                if (_moveKeys.Visible) avail -= _moveKeys.GetPreferredSize(Size.Empty).Width + _moveKeys.Margin.Horizontal;
                _keyStatus.MaximumSize = new Size(Math.Max(S(120), avail), 0);
            }

            private static bool SameId(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

            private static string Titles(IEnumerable<string> ids) { return string.Join(", ", ids.Select(o => "“" + TitleOf(o) + "”")); }

            private static string KeyQuote(Keys k) { return "“" + KeyNames.Format(k) + "”"; }

            // An action counts as changed when its keys differ from the catalog defaults; those are
            // the ones written as overrides.
            private bool IsChanged(string id) { return !SameKeys(_keys[id], DefaultKeys(id)); }

            // The action a shared key runs once saved (ActionRegistry.BuildKeyMap): overrides are
            // bound after the defaults and in list order (ApplyTo writes them in _keyOrder), so the
            // last changed action wins; among actions at their defaults, the last in the catalog.
            // `ids` must be in _keyOrder order, as KeyUsers() lists them.
            private string WinnerOf(IList<string> ids)
            {
                var changed = ids.Where(IsChanged).ToList();
                if (changed.Count > 0) return changed[changed.Count - 1];
                return ids.OrderBy(CatalogIndex).Last();
            }

            private static int CatalogIndex(string id)
            {
                for (int i = 0; i < ActionCatalog.All.Count; i++)
                    if (SameId(ActionCatalog.All[i].Id, id)) return i;
                return int.MaxValue;
            }

            // Parses the shortcut box completely; badNames = the parts that are not key names.
            private bool TryParseKeyText(out List<Keys> keys, out List<string> badNames)
            {
                badNames = new List<string>();
                keys = Distinct(KeyNames.ParseList(_keyEdit.Text, badNames));
                return badNames.Count == 0;
            }

            private static string UnknownKeysText(List<string> badNames)
            {
                return "Unknown key name: " + string.Join(", ", badNames) + ". Examples: Ctrl+Shift+Right, Space, PgDn, Num5, F11, Comma.";
            }

            // Typing only validates and previews; the text is applied by Enter, focus leaving the
            // box or OK (CommitKeyText).
            private void OnKeyTextEdited()
            {
                if (_updatingEditor || _editId == null) return;
                List<Keys> parsed;
                List<string> bad;
                if (!TryParseKeyText(out parsed, out bad))
                {
                    _keyEditDirty = true;
                    _keyEdit.Invalid = true;
                    SetStatus(UnknownKeysText(bad), true);
                    return;
                }
                _keyEdit.Invalid = false;
                _keyEditDirty = !SameKeys(parsed, _keys[_editId]);
                if (!_keyEditDirty)
                {
                    ShowKeyStatus();
                    return;
                }
                var users = KeyUsers();
                string layer = LayerOf(_editId);
                var taken = new List<string>();
                foreach (var k in parsed)
                {
                    List<string> u;
                    if (!users.TryGetValue((layer, k), out u)) continue;
                    var others = u.Where(o => !SameId(o, _editId)).ToList();
                    if (others.Count > 0) taken.Add(KeyQuote(k) + " is assigned to " + Titles(others));
                }
                string text = "Press Enter to apply.";
                if (taken.Count > 0) text += "  " + string.Join(".  ", taken) + ".";
                SetStatus(text, taken.Count > 0);
            }

            // Applies pending typed text to the action shown in the box. False (text kept and
            // flagged) when it does not parse completely.
            private bool CommitKeyText()
            {
                if (!_keyEditDirty || _editId == null) return true;
                List<Keys> parsed;
                List<string> bad;
                if (!TryParseKeyText(out parsed, out bad))
                {
                    _keyEdit.Invalid = true;
                    SetStatus(UnknownKeysText(bad), true);
                    return false;
                }
                _keyEditDirty = false;
                SetKeys(_editId, parsed);
                return true;
            }

            private void SetKeys(string id, List<Keys> keys)
            {
                if (id == null) return;
                _keys[id] = Distinct(keys);
                RefreshKeyRows();
                UpdateEditor();
            }

            // Takes the selected action's keys away from every other action of its layer using them.
            private void MoveKeysHere()
            {
                string id = SelectedKeyId;
                if (id == null || !CommitKeyText()) return;
                string layer = LayerOf(id);
                foreach (var k in _keys[id])
                    foreach (var other in _keyOrder)
                        if (!SameId(other, id) && LayerOf(other) == layer) _keys[other].Remove(k);
                RefreshKeyRows();
                UpdateEditor();
                _keyGrid.Focus();
            }

            private void ResetAllKeys()
            {
                StopRecording();
                foreach (var id in _keyOrder) _keys[id] = DefaultKeys(id);
                RefreshKeyRows();
                UpdateEditor();
            }

            // Updates the shortcut cells and conflict flags in place (keeps scroll and selection).
            private void RefreshKeyRows()
            {
                FillKeyGridPreservingScroll();
                _clear.Enabled = SelectedKeyId != null && _keys[SelectedKeyId].Count > 0;
                _reset.Enabled = SelectedKeyId != null && !SameKeys(_keys[SelectedKeyId], DefaultKeys(SelectedKeyId));
                _resetAll.Enabled = _keyOrder.Any(k => !SameKeys(_keys[k], DefaultKeys(k)));
                foreach (var update in _keyHintUpdates) update();
            }

            private void FillKeyGridPreservingScroll()
            {
                var users = KeyUsers();
                var rows = new List<DarkList.Row>();
                foreach (var row in _keyGrid.Rows)
                {
                    string id = row.Tag as string;
                    if (id == null) continue;
                    rows.Add(KeyRow(id, users));
                }
                _keyGrid.ReplaceRows(rows);
            }

            private static bool SameKeys(List<Keys> a, List<Keys> b)
            {
                return a.Count == b.Count && a.All(b.Contains);
            }

            private void StartRecording()
            {
                if (SelectedKeyId == null) return;
                _recording = true;
                _record.Checked = true;
                _record.Text = "Press keys…";
                _keyGrid.Focus();
                ShowKeyStatus();
            }

            private void StopRecording()
            {
                if (!_recording) return;
                _recording = false;
                _record.Checked = false;
                _record.Text = "Record";
                ShowKeyStatus();
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (_recording)
                {
                    Keys code = keyData & Keys.KeyCode;
                    if (code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin)
                        return true; // wait for the actual key
                    if (keyData == Keys.Escape)
                    {
                        StopRecording();
                        return true;
                    }
                    string id = SelectedKeyId;
                    StopRecording();
                    if (id != null)
                    {
                        var list = new List<Keys>(_keys[id]);
                        if (!list.Contains(keyData)) list.Add(keyData);
                        SetKeys(id, list);
                    }
                    return true;
                }
                // Enter in the shortcut box applies the typed text instead of pressing OK.
                if (keyData == Keys.Enter && _keyEditDirty && _keyEdit != null && _keyEdit.TextBox.Focused)
                {
                    if (CommitKeyText()) _keyEdit.TextBox.Select(_keyEdit.Text.Length, 0);
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected override bool HandleEscape()
            {
                if (_recording)
                {
                    StopRecording();
                    return true;
                }
                // Esc in the shortcut box drops the typed text and shows the action's keys again.
                if (_keyEditDirty && _keyEdit != null && _keyEdit.TextBox.Focused)
                {
                    UpdateEditor();
                    _keyEdit.SelectAll();
                    return true;
                }
                return false;
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                StopRecording();
            }

            protected override void OnDpiApplied()
            {
                foreach (var p in _pages) UpdateWrapping(p);
                UpdateStatusWrap();
            }

            // ---- OK --------------------------------------------------------------------

            // OK: applies a pending shortcut edit (refusing to close while it does not parse), then
            // settles keys that more than one action uses, so the saved map is unambiguous.
            private void Accept()
            {
                StopRecording();
                string pendingId = _editId;
                if (!CommitKeyText())
                {
                    List<Keys> parsed;
                    List<string> bad;
                    TryParseKeyText(out parsed, out bad);
                    SelectPage("Shortcuts");
                    _keyEdit.Focus();
                    _keyEdit.SelectAll();
                    SetStatus("Not saved: the shortcuts typed for “" + TitleOf(pendingId) + "” contain an unknown key name ("
                        + string.Join(", ", bad) + "). Correct them, or press Esc in the box to restore the previous ones.", true);
                    return;
                }
                var conflicts = SharedKeys();
                if (conflicts.Count > 0)
                {
                    bool? dontAsk = null;
                    if (!ConfirmDialog.Show(this, "Shortcut Conflicts", ConflictMessage(conflicts), "Save", "Go back", ref dontAsk))
                    {
                        ShowConflict(conflicts[0]);
                        return;
                    }
                    foreach (var c in conflicts)
                        foreach (var id in c.Users)
                            if (!SameId(id, c.Winner)) _keys[id].Remove(c.Key);
                    RefreshKeyRows();
                    UpdateEditor();
                }
                DialogResult = DialogResult.OK;
            }

            internal sealed class SharedKey
            {
                public Keys Key;
                public string Layer;         // "" = global, ActionCatalog.LayerCull = Cull mode
                public List<string> Users;   // in list order, all of that layer
                public string Winner;        // the action the key runs once saved
            }

            // Keys used by more than one action of the same layer. Winners are decided on the current
            // state before anything is removed, matching what the player would bind.
            internal List<SharedKey> SharedKeys()
            {
                var result = new List<SharedKey>();
                foreach (var kv in KeyUsers())
                    if (kv.Value.Count > 1)
                        result.Add(new SharedKey { Key = kv.Key.Key, Layer = kv.Key.Layer, Users = kv.Value, Winner = WinnerOf(kv.Value) });
                return result;
            }

            private static string ConflictMessage(List<SharedKey> conflicts)
            {
                const int maxLines = 6;
                var lines = new List<string>();
                foreach (var c in conflicts.Take(maxLines))
                {
                    var losers = c.Users.Where(id => !SameId(id, c.Winner));
                    string where = c.Layer == ActionCatalog.LayerCull ? " (in Cull mode)" : "";
                    lines.Add("•  " + KeyQuote(c.Key) + where + " runs “" + TitleOf(c.Winner) + "”, not " + string.Join(", ", losers.Select(id => "“" + TitleOf(id) + "”")));
                }
                if (conflicts.Count > maxLines)
                    lines.Add("…and " + (conflicts.Count - maxLines).ToString(CultureInfo.InvariantCulture) + " more.");
                return (conflicts.Count == 1 ? "A shortcut is" : "Some shortcuts are") + " assigned to more than one action, but each key runs only one:"
                    + "\n\n" + string.Join("\n", lines)
                    + "\n\nSave, and remove " + (conflicts.Count == 1 ? "it" : "them") + " from the actions that would not run?";
            }

            // "Go back": shows the Shortcuts page with an action that uses the key selected.
            private void ShowConflict(SharedKey conflict)
            {
                SelectPage("Shortcuts");
                Func<int> find = () => _keyGrid.Rows.ToList().FindIndex(r => SameId(r.Tag as string, conflict.Winner));
                int index = find();
                if (index < 0 && !string.IsNullOrEmpty(_keyFilter.Text))
                {
                    _keyFilter.Text = "";
                    index = find();
                }
                if (index >= 0) _keyGrid.SelectedIndex = index;
                _keyGrid.Focus();
            }

            // ---- apply -----------------------------------------------------------------

            // Overrides that differ from the catalog defaults ("" = unbound).
            internal Dictionary<string, string> BuildKeyOverrides()
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in _keyOrder)
                {
                    var keys = _keys[id];
                    if (SameKeys(keys, DefaultKeys(id))) continue;
                    result[id] = KeyNames.FormatList(keys);
                }
                return result;
            }

            // Copies what was edited back into the live settings. Only properties changed in
            // the dialog are written, so values the player updated meanwhile (recent files,
            // last folder, ...) are not rolled back.
            internal void ApplyTo(Settings target)
            {
                if (string.IsNullOrWhiteSpace(_work.HwDec)) _work.HwDec = "auto-safe";
                if (string.IsNullOrWhiteSpace(_work.AudioDevice)) _work.AudioDevice = "auto";
                foreach (var p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || !p.CanWrite || p.GetSetMethod() == null || p.GetIndexParameters().Length > 0) continue;
                    object now = p.GetValue(_work, null), before = p.GetValue(_baseline, null);
                    if (!Equals(now, before)) p.SetValue(target, now, null);
                }
                if (_recentCleared) target.RecentFiles.Clear();
                int max = Math.Max(0, target.MaxRecentFiles);
                if (target.RecentFiles.Count > max) target.RecentFiles.RemoveRange(max, target.RecentFiles.Count - max);
                target.KeyOverrides.Clear();
                foreach (var kv in BuildKeyOverrides()) target.KeyOverrides[kv.Key] = kv.Value;
            }
        }
    }
}
