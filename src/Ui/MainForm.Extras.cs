using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;
using VidFlow.Ui.Dialogs;

namespace VidFlow.Ui
{
    // Extras (hooks declared in MainForm.Hooks.cs): marker export (CSV for spreadsheets, a CMX3600
    // EDL for DaVinci Resolve), the first-run quick start card on the idle video surface, and the
    // click-only "Check for Updates".
    public sealed partial class MainForm
    {
        private const string QuickStartTitleText = "Cull a folder with six keys";

        private string _lastExportPath, _lastExportKind;   // automation dump
        private bool _updateBusy, _updateDialogOpen;
        private UpdateResult _updateResult;
        private int _updateChecks;
        private string _updatePageNotOpened;               // automation: the page a click would have opened

        // ---- hooks --------------------------------------------------------------------------

        partial void ExtrasRegisterActions()
        {
            Action<string, Action, Func<bool>, Func<bool>> r = (id, run, chk, en) => _actions.Register(id, run, chk, en);
            r(ActionIds.ExportMarkersCsv, ExportMarkersCsv, null, () => TotalMarkerCount() > 0);
            r(ActionIds.ExportMarkersEdl, ExportMarkersEdl, null, () => HasMedia && Fps > 0 && CurrentMarkers().Count > 0);
            r(ActionIds.CheckForUpdates, CheckForUpdates, null, UpdatesEnabled);
            r(ActionIds.ShowQuickStart, ShowQuickStart, null, null);
        }

        // The Store edition is updated by the Store, so it never offers GitHub releases.
        private bool UpdatesEnabled() { return !AppInfo.IsPackaged && UpdateCheck.IsValidRepo(_settings.UpdateRepo); }

        partial void ExtrasInit()
        {
            _host.QuickStartClicked += OnQuickStartClicked;
            UpdateQuickStart();
        }

        partial void ExtrasDump(Dictionary<string, object> d)
        {
            var s = _host.Surface;
            Func<Rectangle, object> rect = r => r.IsEmpty ? null : (object)new[] { r.X, r.Y, r.Width, r.Height };
            d["quickStart"] = new Dictionary<string, object>
            {
                { "visible", s.QuickStartVisible },
                { "shown", !s.QuickStartBounds.IsEmpty && s.Mode == SurfaceMode.Idle },
                { "card", rect(s.QuickStartBounds) },
                { "open", rect(s.QuickStartOpenBounds) },
                { "gotIt", rect(s.QuickStartDismissBounds) },
                { "rows", s.QuickStartRowsShown },
                { "hot", s.HotQuickStart.ToString() },
                { "settingShown", _settings.QuickStartShown },
            };
            d["hostHandle"] = _host.IsHandleCreated ? _host.Handle.ToInt64() : 0L;
            int files = 0;
            foreach (var kv in _markers) if (kv.Value.Count > 0) files++;
            d["markerExport"] = new Dictionary<string, object>
            {
                { "files", files },
                { "markers", TotalMarkerCount() },
                { "lastPath", _lastExportPath },
                { "lastKind", _lastExportKind },
            };
            var u = _updateResult;
            d["update"] = new Dictionary<string, object>
            {
                { "repo", _settings.UpdateRepo ?? "" },
                { "enabled", UpdatesEnabled() },
                { "packaged", AppInfo.IsPackaged },
                { "busy", _updateBusy },
                { "checks", _updateChecks },
                { "dialogOpen", _updateDialogOpen },
                { "current", UpdateCheck.Display(typeof(MainForm).Assembly.GetName().Version) },
                { "status", u == null ? null : u.Status.ToString() },
                { "latest", u == null || u.Latest == null ? null : UpdateCheck.Display(u.Latest) },
                { "tag", u == null ? null : u.Tag },
                { "pageUrl", u == null ? null : u.PageUrl },
                { "error", u == null ? null : u.Error },
                { "pageNotOpened", _updatePageNotOpened },
            };
        }

        // ---- marker export ------------------------------------------------------------------

        // All files with markers this session, one row per marker (see MarkerExport.Csv).
        private void ExportMarkersCsv()
        {
            var clips = MarkedClips();
            int count = 0;
            foreach (var c in clips) count += c.Times.Count;
            if (count == 0) return;
            string folder = ExportCommonFolder(clips);
            string name = folder != null ? ExportSafeName(FileNameOf(folder), "Markers") + " markers.csv" : "Markers.csv";
            string path = AskExportPath("Export Markers as CSV", "CSV file (*.csv)|*.csv", "csv",
                folder ?? ExportFolderOf(_st.Path) ?? _settings.LastFolder, name);
            if (path == null) return;
            // UTF-8 with a BOM so spreadsheet apps read non-ASCII file names correctly.
            if (!WriteMarkerExport(path, MarkerExport.Csv(clips), new UTF8Encoding(true), "csv")) return;
            ShowOsd("Exported " + ExportPlural(count, "marker") + (clips.Count > 1 ? " from " + clips.Count + " files" : "") + " to " + FileNameOf(path));
        }

        // The playing clip's markers as a Resolve-importable EDL (see MarkerExport.Edl).
        private void ExportMarkersEdl()
        {
            var clip = CurrentMarkedClip();
            if (clip == null)
            {
                ShowOsd(Fps > 0 ? "No markers on this clip" : "Error: the frame rate of this file is unknown", true);
                return;
            }
            string title = ExportTitleOf(clip.Path);
            string baseName = ExportSafeName(title, "Markers");
            string path = AskExportPath("Export Markers as EDL (DaVinci Resolve)", "Edit decision list (*.edl)|*.edl", "edl",
                ExportFolderOf(clip.Path) ?? _settings.LastFolder, baseName + " markers.edl");
            if (path == null) return;
            if (!WriteMarkerExport(path, MarkerExport.Edl(title, clip), new UTF8Encoding(false), "edl")) return;
            ShowOsd("Exported " + ExportPlural(clip.Times.Count, "marker") + " to " + FileNameOf(path) + " (" +
                    MarkerExport.FpsText(clip.Fps) + " fps" + (MarkerExport.IsDropFrame(clip.Fps) ? " drop-frame" : "") +
                    ", timeline start 01:00:00:00" + (clip.Variable ? "; variable frame rate: positions are approximate" : "") + ")", false, 4000);
        }

        private bool WriteMarkerExport(string path, string text, Encoding encoding, string kind)
        {
            try
            {
                File.WriteAllText(path, text, encoding);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                       ex is NotSupportedException || ex is SecurityException)
            {
                ShowOsd("Error: could not export markers (" + ex.Message + ")", true);
                return false;
            }
            _lastExportPath = path;
            _lastExportKind = kind;
            Log.Info("markers exported (" + kind + "): " + path);
            return true;
        }

        private string AskExportPath(string title, string filter, string ext, string folder, string fileName)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = title;
                dlg.Filter = filter;
                dlg.DefaultExt = ext;
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                dlg.RestoreDirectory = true;
                dlg.FileName = fileName;
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dlg.InitialDirectory = folder;
                DialogResult result = DialogResult.Cancel;
                RunModal(() => result = dlg.ShowDialog(this));
                return result == DialogResult.OK && !string.IsNullOrEmpty(dlg.FileName) ? dlg.FileName : null;
            }
        }

        // The folder all the clips share, or null.
        private static string ExportCommonFolder(List<MarkedClip> clips)
        {
            string common = null;
            foreach (var c in clips)
            {
                string f = ExportFolderOf(c.Path);
                if (f == null) return null;
                if (common == null) common = f;
                else if (!string.Equals(common, f, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return common;
        }

        private static string ExportFolderOf(string path)
        {
            if (string.IsNullOrEmpty(path) || Playlist.IsUrl(path)) return null;
            try
            {
                string dir = Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException)
            {
                return null;
            }
        }

        // "clip01" for a path or URL, without the exceptions Path throws for characters it rejects.
        private static string ExportTitleOf(string path)
        {
            string name = FileNameOf(path);
            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private static string ExportSafeName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            var sb = new StringBuilder(name.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? fallback : s;
        }

        private static string ExportPlural(int n, string word)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");
        }

        // ---- quick start --------------------------------------------------------------------

        // Card content (key caps follow the current key map) and visibility (until "Got it").
        private void UpdateQuickStart()
        {
            var s = _host.Surface;
            s.QuickStartTitle = QuickStartTitleText;
            s.SetQuickStartRows(new[]
            {
                new QuickStartRow("Cull mode on / off", QuickStartKeyCap(ActionIds.ToggleCullMode)),
                new QuickStartRow("Pick: keep the clip", QuickStartKeyCap(ActionIds.CullPick)),
                new QuickStartRow("Reject the clip", QuickStartKeyCap(ActionIds.CullReject)),
                new QuickStartRow("Rate it", QuickStartRateCap()),
                new QuickStartRow("Apply your decisions", QuickStartKeyCap(ActionIds.CullCommit)),
                new QuickStartRow("Seek · step one frame", QuickStartKeyCap(ActionIds.SeekBackward), QuickStartKeyCap(ActionIds.SeekForward),
                                  QuickStartKeyCap(ActionIds.FrameBackStep), QuickStartKeyCap(ActionIds.FrameStep)),
            });
            s.QuickStartVisible = !_settings.QuickStartShown;
        }

        // First shortcut of an action as a key cap ("Ctrl+Enter", "←"); "—" when unbound.
        private string QuickStartKeyCap(string id)
        {
            string k = _actions.ShortcutText(id, 1);
            if (k.Length == 0) return "—";
            int plus = k.LastIndexOf('+');
            string head = plus > 0 && plus < k.Length - 1 ? k.Substring(0, plus + 1) : "";
            string key = head.Length > 0 ? k.Substring(plus + 1) : k;
            switch (key)
            {
                case "Left": key = "←"; break;
                case "Right": key = "→"; break;
                case "Up": key = "↑"; break;
                case "Down": key = "↓"; break;
            }
            return head + key;
        }

        // "1–5" with the default keys.
        private string QuickStartRateCap()
        {
            string first = QuickStartKeyCap(ActionIds.CullRate + ":1"), last = QuickStartKeyCap(ActionIds.CullRate + ":5");
            return first == "—" || last == "—" ? (first == "—" ? last : first) : first + "–" + last;
        }

        private void OnQuickStartClicked(QuickStartButton button)
        {
            switch (button)
            {
                case QuickStartButton.OpenFolder:
                    // After the mouse-up finishes: the folder picker is modal.
                    BeginInvoke((MethodInvoker)(() => { if (!_closing) Run(ActionIds.OpenFolder); }));
                    break;
                case QuickStartButton.GotIt:
                    _settings.QuickStartShown = true;
                    _settings.Save();
                    UpdateQuickStart();
                    Log.Info("quick start dismissed");
                    break;
            }
        }

        // Shows the card again (until "Got it"). Over a playing file it cannot be seen, so the
        // keys also appear on the OSD.
        private void ShowQuickStart()
        {
            _settings.QuickStartShown = false;
            _settings.Save();
            UpdateQuickStart();
            if (_host.Surface.Mode != SurfaceMode.Idle)
                ShowOsd("Cull a folder with six keys:\n" +
                        QuickStartKeyCap(ActionIds.ToggleCullMode) + "  cull mode   ·   " + QuickStartKeyCap(ActionIds.CullPick) + "  pick   ·   " +
                        QuickStartKeyCap(ActionIds.CullReject) + "  reject\n" + QuickStartRateCap() + "  rate   ·   " + QuickStartKeyCap(ActionIds.CullCommit) + "  apply\n" +
                        QuickStartKeyCap(ActionIds.SeekBackward) + " " + QuickStartKeyCap(ActionIds.SeekForward) + "  seek   ·   " +
                        QuickStartKeyCap(ActionIds.FrameBackStep) + " " + QuickStartKeyCap(ActionIds.FrameStep) + "  step one frame", true, 7000);
        }

        // ---- check for updates (click-only) -------------------------------------------------

        private void CheckForUpdates()
        {
            string repo = (_settings.UpdateRepo ?? "").Trim();
            if (!UpdatesEnabled()) return;
            if (_updateBusy)
            {
                ShowOsd("Already checking for updates…", true);
                return;
            }
            _updateBusy = true;
            _updateChecks++;
            ShowOsd("Checking for updates…", true, 2000);
            Log.Info("update check requested: " + UpdateCheck.LatestReleaseApiUrl(repo));
            UpdateCheck.CheckAsync(repo, AppName, typeof(MainForm).Assembly.GetName().Version, result =>
            {
                try
                {
                    BeginInvoke((Action<UpdateResult>)OnUpdateChecked, result);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
                {
                    // The window closed while the request was running.
                }
            });
        }

        private void OnUpdateChecked(UpdateResult r)
        {
            _updateBusy = false;
            _updateResult = r;
            if (_closing || IsDisposed) return;
            Log.Info("update check: " + r.Status + (r.Latest != null ? " latest " + UpdateCheck.Display(r.Latest) : "") +
                     (r.Error != null ? " (" + r.Error + ")" : ""));
            string headline, detail, accept = null, cancel = "OK";
            string glyph = Glyphs.Info;
            Color glyphColor = Theme.Accent;
            string current = UpdateCheck.Display(r.Current);
            switch (r.Status)
            {
                case UpdateStatus.Available:
                    headline = "Version " + UpdateCheck.Display(r.Latest) + " is available";
                    detail = "You have " + AppName + " " + current + "." +
                             (ReleaseNameAddsInfo(r) ? "\n" + r.ReleaseName.Trim() : "");
                    glyph = UpdateDownloadGlyph;
                    accept = r.PageUrl != null ? "Open Release Page" : null;
                    cancel = accept != null ? "Not Now" : "OK";
                    break;
                case UpdateStatus.UpToDate:
                    headline = "You're up to date";
                    detail = r.Latest != null && r.Latest < r.Current
                        ? "This build (" + current + ") is newer than the latest release (" + UpdateCheck.Display(r.Latest) + ")."
                        : AppName + " " + current + " is the latest version.";
                    glyph = Glyphs.Check;
                    break;
                case UpdateStatus.NoReleases:
                    headline = "No releases yet";
                    detail = "No release of " + (_settings.UpdateRepo ?? "").Trim() + " has been published on GitHub yet.";
                    glyphColor = Theme.TextDim;
                    break;
                default:
                    headline = "Couldn't check for updates";
                    detail = r.Error ?? "Unknown error.";
                    glyph = UpdateWarningGlyph;
                    glyphColor = Theme.Warning;
                    break;
            }
            bool open = false;
            _updateDialogOpen = true;
            try
            {
                RunModal(() => open = UpdateDialog.Show(this, "Check for Updates", glyph, glyphColor, headline, detail, accept, cancel));
            }
            finally
            {
                _updateDialogOpen = false;
            }
            if (open && r.PageUrl != null) OpenReleasePage(r.PageUrl);
        }

        private const string UpdateDownloadGlyph = "\uE896", UpdateWarningGlyph = "\uE7BA";

        // A release title worth a line of its own ("Faster stepping"), not just "VidFlow 1.2.0" again.
        private static bool ReleaseNameAddsInfo(UpdateResult r)
        {
            string name = (r.ReleaseName ?? "").Trim();
            if (name.Length == 0) return false;
            if (!string.IsNullOrEmpty(r.Tag) && name.IndexOf(r.Tag.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return r.Latest == null || name.IndexOf(UpdateCheck.Display(r.Latest), StringComparison.Ordinal) < 0;
        }

        private void OpenReleasePage(string url)
        {
            if (_options.Automation)
            {
                // Tests drive this window: never start a browser from them.
                _updatePageNotOpened = url;
                Log.Info("automation: release page not opened: " + url);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is FileNotFoundException)
            {
                ShowOsd("Error: could not open the release page (" + ex.Message + ")", true);
            }
        }

        // Small dark message box: glyph, headline, wrapped detail, one or two buttons.
        private sealed class UpdateDialog : DarkDialog
        {
            private const int MaxTextWidth = 360;

            private readonly Label _glyph, _headline, _detail;
            private readonly Button _accept, _cancel;
            private readonly string _glyphText;

            private UpdateDialog(string title, string glyph, Color glyphColor, string headline, string detail, string acceptText, string cancelText)
            {
                Text = title;
                _glyphText = glyph;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowIcon = false;
                AutoScaleMode = AutoScaleMode.None;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                BackColor = Theme.Window;
                ForeColor = Theme.Text;

                _glyph = MakeLabel(glyph, glyphColor);
                _headline = MakeLabel(headline, Theme.Text);
                _detail = MakeLabel(detail, Theme.TextDim);
                Controls.Add(_glyph);
                Controls.Add(_headline);
                Controls.Add(_detail);

                _cancel = MakeButton(cancelText, false, acceptText == null ? DialogResult.OK : DialogResult.Cancel);
                Controls.Add(_cancel);
                if (acceptText != null)
                {
                    _accept = MakeButton(acceptText, true, DialogResult.OK);
                    Controls.Add(_accept);
                    AcceptButton = _accept;
                    CancelButton = _cancel;
                }
                else
                {
                    AcceptButton = _cancel;
                    CancelButton = _cancel;
                }
            }

            // True when the accept button ("Open Release Page") was chosen.
            public static bool Show(IWin32Window owner, string title, string glyph, Color glyphColor, string headline, string detail,
                                    string acceptText, string cancelText)
            {
                using (var dlg = new UpdateDialog(title, glyph, glyphColor, headline, detail, acceptText, cancelText))
                {
                    var c = owner as Control;
                    dlg.LayoutFor(Theme.DpiOf(c));
                    return dlg.ShowDialog(owner) == DialogResult.OK && acceptText != null;
                }
            }

            private static Label MakeLabel(string text, Color color)
            {
                return new Label { Text = text ?? "", AutoSize = true, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false };
            }

            private static Button MakeButton(string text, bool accent, DialogResult result)
            {
                var b = new Button
                {
                    Text = text,
                    DialogResult = result,
                    FlatStyle = FlatStyle.Flat,
                    UseVisualStyleBackColor = false,
                    BackColor = accent ? Theme.Accent : Theme.PanelAlt,
                    ForeColor = accent ? Color.FromArgb(14, 14, 14) : Theme.Text,
                    UseMnemonic = false,
                };
                b.FlatAppearance.BorderColor = accent ? Theme.Accent : Theme.Border;
                b.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(110, 180, 255) : Theme.Hover;
                b.FlatAppearance.MouseDownBackColor = accent ? Theme.AccentDim : Theme.Pressed;
                return b;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                LayoutFor(Theme.DpiOf(this));
            }

            protected override void OnDpiChanged(DpiChangedEventArgs e)
            {
                base.OnDpiChanged(e);
                LayoutFor(e.DeviceDpiNew);
            }

            private int _layoutDpi;
            private readonly List<Font> _fonts = new List<Font>();

            private void LayoutFor(int dpi)
            {
                if (dpi <= 0 || dpi == _layoutDpi) return;
                _layoutDpi = dpi;
                Func<int, int> s = px => (int)Math.Round(px * dpi / 96.0);
                Func<string, float, Font> font = (family, px) => new Font(family, px * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
                var old = new List<Font>(_fonts);
                _fonts.Clear();
                Font glyphFont = font(Theme.IconFontFamily, 26f), headFont = font("Segoe UI Semibold", 16f),
                     textFont = font("Segoe UI", 12.5f), buttonFont = font("Segoe UI", 12f);
                _fonts.AddRange(new[] { glyphFont, headFont, textFont, buttonFont });

                SuspendLayout();
                Font = textFont;
                Padding = new Padding(s(24), s(22), s(24), s(18));
                _glyph.Font = glyphFont;
                _headline.Font = headFont;
                _detail.Font = textFont;
                _detail.MaximumSize = new Size(s(MaxTextWidth), 0);
                _headline.MaximumSize = new Size(s(MaxTextWidth), 0);

                int left = s(24), top = s(22);
                Size gs = TextRenderer.MeasureText(_glyphText, glyphFont, Size.Empty, TextFormatFlags.NoPadding);
                _glyph.Location = new Point(left, top + s(2));
                int textX = left + gs.Width + s(16);
                _headline.Location = new Point(textX, top);
                Size hs = _headline.GetPreferredSize(new Size(s(MaxTextWidth), 0));
                _detail.Location = new Point(textX, top + hs.Height + s(6));
                Size ds = _detail.GetPreferredSize(new Size(s(MaxTextWidth), 0));
                int contentRight = textX + Math.Max(Math.Max(hs.Width, ds.Width), s(220));
                int y = Math.Max(_detail.Top + ds.Height, _glyph.Top + gs.Height) + s(22);

                int bh = Math.Max(s(32), buttonFont.Height + s(14));
                var buttons = _accept != null ? new[] { _accept, _cancel } : new[] { _cancel };
                int x = contentRight;
                for (int i = buttons.Length - 1; i >= 0; i--)
                {
                    var b = buttons[i];
                    b.Font = buttonFont;
                    int bw = Math.Max(s(88), TextRenderer.MeasureText(b.Text, buttonFont).Width + s(32));
                    b.Bounds = new Rectangle(x - bw, y, bw, bh);
                    x -= bw + s(8);
                }
                ResumeLayout(true);
                foreach (var f in old) f.Dispose();
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing)
                {
                    foreach (var f in _fonts) f.Dispose();
                    _fonts.Clear();
                }
            }
        }
    }

    // One clip's markers for export: times in seconds and the frame rate it played at (0 = unknown).
    internal sealed class MarkedClip
    {
        public MarkedClip(string path, IList<double> times, double fps, bool variable)
        {
            Path = path ?? "";
            Times = new List<double>(times ?? new double[0]);
            Fps = fps > 0 && !double.IsNaN(fps) && !double.IsInfinity(fps) ? fps : 0;
            Variable = variable;
        }

        public string Path { get; private set; }
        public List<double> Times { get; private set; }
        public double Fps { get; private set; }
        public bool Variable { get; private set; }   // variable frame rate: frames are estimates
    }

    // Marker file writers (pure functions; golden-file tested).
    internal static class MarkerExport
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Resolve's default timeline start; markers land at this + the clip's frame offset.
        public const int TimelineStartHours = 1;

        // CSV, one row per marker: File, Folder, Marker (1-based per file), Seconds (3 decimals),
        // Timecode (hh:mm:ss.mmm), Frame, FPS, Approximate (yes = variable frame rate). Frame and
        // FPS are empty when the file's frame rate was never known. RFC 4180 quoting, CRLF.
        public static string Csv(IList<MarkedClip> clips)
        {
            var sb = new StringBuilder();
            sb.Append("File,Folder,Marker,Seconds,Timecode,Frame,FPS,Approximate\r\n");
            foreach (var c in clips)
            {
                string file = c.Path, folder = "";
                if (!LooksLikeUrl(c.Path))
                {
                    try
                    {
                        file = System.IO.Path.GetFileName(c.Path);
                        folder = System.IO.Path.GetDirectoryName(c.Path) ?? "";
                    }
                    catch (ArgumentException)
                    {
                        file = c.Path;
                    }
                }
                for (int i = 0; i < c.Times.Count; i++)
                {
                    double t = Math.Max(0, c.Times[i]);
                    sb.Append(CsvField(file)).Append(',')
                      .Append(CsvField(folder)).Append(',')
                      .Append((i + 1).ToString(Inv)).Append(',')
                      .Append(t.ToString("0.000", Inv)).Append(',')
                      .Append(ClockTime(t)).Append(',');
                    if (c.Fps > 0)
                        sb.Append(FrameAt(t, c.Fps).ToString(Inv)).Append(',').Append(FpsText(c.Fps)).Append(',').Append(c.Variable ? "yes" : "no");
                    else
                        sb.Append(",,");
                    sb.Append("\r\n");
                }
            }
            return sb.ToString();
        }

        // CMX3600 EDL with one single-frame event per marker, in the layout DaVinci Resolve writes
        // with Timelines > Export > Timeline Markers to EDL and reads back with Timelines > Import >
        // Timeline Markers from EDL: event line "001  001      V     C        <in> <out> <in> <out>  "
        // followed by the marker comment " |C:ResolveColorBlue |M:<name> |D:<frames>". Timecodes must
        // be four two-digit groups. Sources: Resolve's export layout as reproduced in X-Raym's REAPER
        // exporter (github.com/X-Raym/REAPER-ReaScripts, "Export markers and regions as Davinci
        // Resolve EDL file.lua") and the working Resolve 17 import sample in its issue #17;
        // Blackmagic forum thread viewtopic.php?p=727227 (|C:ResolveColor<colour> |M:<name> |D:<n>).
        // Timecode runs at the clip's rate (timebase = rounded fps), drop-frame for 29.97/59.94
        // (";" before the frames), starting at 01:00:00:00, Resolve's default timeline start.
        public static string Edl(string title, MarkedClip clip)
        {
            if (clip == null || clip.Fps <= 0) throw new ArgumentException("the clip's frame rate is unknown");
            bool df = IsDropFrame(clip.Fps);
            long start = StartFrames(clip.Fps);
            var sb = new StringBuilder();
            sb.Append("TITLE: ").Append(EdlText(title, 70, "Markers")).Append("\r\n");
            sb.Append(df ? "FCM: DROP FRAME" : "FCM: NON-DROP FRAME").Append("\r\n");
            var times = new List<double>(clip.Times);
            times.Sort();
            for (int i = 0; i < times.Count; i++)
            {
                long f = start + FrameAt(times[i], clip.Fps);
                string tcIn = Timecode(f, clip.Fps), tcOut = Timecode(f + 1, clip.Fps);
                sb.Append("\r\n");
                sb.Append((i + 1).ToString("000", Inv)).Append("  001      V     C        ")
                  .Append(tcIn).Append(' ').Append(tcOut).Append(' ').Append(tcIn).Append(' ').Append(tcOut).Append("  \r\n");
                sb.Append(" |C:ResolveColorBlue |M:Marker ").Append((i + 1).ToString(Inv)).Append(" |D:1\r\n");
            }
            return sb.ToString();
        }

        // Frame index shown at time t (same rule as the player's frame readout).
        public static long FrameAt(double t, double fps)
        {
            return (long)Math.Floor(Math.Max(0, t) * fps + 1e-6);
        }

        public static int Timebase(double fps)
        {
            return Math.Max(1, (int)Math.Round(fps));
        }

        // 29.97 (30000/1001) and 59.94 (60000/1001) use drop-frame timecode.
        public static bool IsDropFrame(double fps)
        {
            return Math.Abs(fps - 30000.0 / 1001) < 0.005 || Math.Abs(fps - 60000.0 / 1001) < 0.01;
        }

        // "30", "29.97", "23.976"
        public static string FpsText(double fps)
        {
            return fps.ToString("0.###", Inv);
        }

        // Frame count of 01:00:00:00 at this rate (drop-frame skips 2 or 4 labels in 54 minutes an hour).
        public static long StartFrames(double fps)
        {
            int tb = Timebase(fps);
            long frames = (long)tb * 3600 * TimelineStartHours;
            if (IsDropFrame(fps)) frames -= (tb / 30 * 2) * 54L * TimelineStartHours;
            return frames;
        }

        // Frame count -> "hh:mm:ss:ff", or "hh:mm:ss;ff" for drop-frame (labels ;00/;01 at 29.97 and
        // ;00-;03 at 59.94 are skipped at every minute except each tenth). Hours wrap at 24.
        public static string Timecode(long frames, double fps)
        {
            int tb = Timebase(fps);
            bool df = IsDropFrame(fps);
            if (frames < 0) frames = 0;
            if (df)
            {
                long drop = tb / 30 * 2;                    // 2 at 29.97, 4 at 59.94
                long perTen = tb * 600L - drop * 9;         // frames in ten minutes
                long perMinute = tb * 60L - drop;           // frames in a dropping minute
                long tens = frames / perTen, rest = frames % perTen;
                frames += drop * 9 * tens + (rest > drop ? drop * ((rest - drop) / perMinute) : 0);
            }
            long ff = frames % tb, totalSeconds = frames / tb;
            long ss = totalSeconds % 60, mm = totalSeconds / 60 % 60, hh = totalSeconds / 3600 % 24;
            return hh.ToString("00", Inv) + ":" + mm.ToString("00", Inv) + ":" + ss.ToString("00", Inv) + (df ? ";" : ":") + ff.ToString("00", Inv);
        }

        // 61.5 -> "00:01:01.500"
        public static string ClockTime(double seconds)
        {
            long ms = (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);
            return (ms / 3600000).ToString("00", Inv) + ":" + (ms / 60000 % 60).ToString("00", Inv) + ":" +
                   (ms / 1000 % 60).ToString("00", Inv) + "." + (ms % 1000).ToString("000", Inv);
        }

        // "scheme://..." (same rule as Playlist.IsUrl; kept here so the writer stands alone).
        private static bool LooksLikeUrl(string s)
        {
            int end = string.IsNullOrEmpty(s) ? -1 : s.IndexOf("://", StringComparison.Ordinal);
            if (end < 2 || !((s[0] >= 'a' && s[0] <= 'z') || (s[0] >= 'A' && s[0] <= 'Z'))) return false;
            for (int i = 1; i < end; i++)
            {
                char c = s[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.')) return false;
            }
            return true;
        }

        private static string CsvField(string s)
        {
            s = s ?? "";
            bool quote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || (s.Length > 0 && (s[0] == ' ' || s[s.Length - 1] == ' '));
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        // Printable ASCII only (EDL readers are not Unicode-aware), at most maxLength characters.
        private static string EdlText(string s, int maxLength, string fallback)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (sb.Length >= maxLength) break;
                sb.Append(c >= 0x20 && c < 0x7F && c != '|' ? c : '_');
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? fallback : r;
        }
    }
}
