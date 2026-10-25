using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    // Seek-bar hover popup: a frame preview rendered by a second, silent libmpv instance, with
    // a time label underneath (or the label alone). An owned, never-activating top-level window
    // that stays above its owner, fullscreen included.
    public sealed class ThumbnailPreview : IDisposable
    {
        // Logical (96-dpi) sizes.
        private const int ImageWidthLogical = 200, ImageMaxHeightLogical = 150;
        private const int PadLogical = 4, LabelHeightLogical = 24, TextOnlyHeightLogical = 26;
        private const int TextPadLogical = 10, LabelGapLogical = 8, AnchorGapLogical = 6, LabelFontPx = 12;

        private const int StableMs = 120;          // hover time unchanged this long -> exact seek
        private const double DefaultAspect = 16.0 / 9.0;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const TextFormatFlags MeasureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        private readonly Form _owner;
        private readonly Timer _stable = new Timer { Interval = StableMs };
        private readonly SolidBrush _borderBrush = new SolidBrush(Theme.Border);
        private PopupForm _form;
        private MpvPlayer _player;
        private bool _playerFailed, _disposed, _imagesEnabled = true;

        // _wantPath is what the preview player should hold, _currentPath what it holds or is
        // opening. Only one loadfile is outstanding at a time (_loadPending until its start-file
        // event): mpv skips a file entirely when loads overlap, and with a single load in flight
        // every file-loaded/end-file event that arrives while nothing is pending belongs to
        // _currentPath. _fileSerial invalidates seek replies that belong to an older file.
        private string _source, _wantPath, _currentPath;
        private bool _loadPending;
        private int _fileSerial;
        private bool _fileReady, _loadFailed, _noVideo;
        private double _aspect;

        // Never read the preview player's properties synchronously: while a file opens that
        // blocks the UI thread for hundreds of ms. The video track and video-params are observed
        // instead. Their notifications can arrive before or after file-loaded and may still carry
        // a value read before it (the previous file's, or none), so nothing is acted on until
        // _currentPath is loaded (_fileLoaded, reset with every new serial), and the track is
        // classified only from asynchronous reads issued after file-loaded (a notification just
        // asks for another read). _trackKnown: such a read has classified the file (video, or
        // audio/image for the text-only popup). One read in flight; _trackReadAgain queues one more.
        private bool _fileLoaded, _trackKnown, _trackReadPending, _trackReadAgain;
        private Dictionary<string, object> _videoParams;   // latest observed video-params (any file)

        // Seek coalescing: at most one seek command in flight, the latest target remembered.
        private double? _target;
        private bool _wantExact, _seekInFlight, _shownExact;
        private double _shownTime = double.NaN;

        private string _label = "";
        private int _anchorX;
        private Rectangle _anchorBar;
        private Rectangle _labelRect;
        private bool _imagesShown;
        private Font _font;
        private int _fontDpi;

        public ThumbnailPreview(Form owner)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            _owner = owner;
            _owner.FormClosed += OnOwnerClosed;
            _stable.Tick += (s, e) =>
            {
                _stable.Stop();
                _wantExact = true;
                PumpSeek();
            };
        }

        public bool ImagesEnabled
        {
            get { return _imagesEnabled; }
            set
            {
                if (_imagesEnabled == value) return;
                _imagesEnabled = value;
                if (!value)
                {
                    _wantPath = null; // free the decoder and the file handle
                    SyncPlayer();
                }
                if (Visible) LayoutAndShow();
            }
        }

        public void SetSource(string path)
        {
            if (_disposed) return;
            string p = string.IsNullOrEmpty(path) ? null : path;
            if (string.Equals(p, _source, StringComparison.Ordinal)) return;
            _source = p;
            _target = null;
            _wantExact = false;
            _stable.Stop();
            // Release the previous file now (not on the next hover) so the main window can
            // delete or rename it; the new one loads lazily.
            _wantPath = null;
            SyncPlayer();
            Hide();
        }

        public void Show(double seconds, string label, int cursorScreenX, Rectangle barScreenBounds)
        {
            if (_disposed) return;
            if (!_owner.IsHandleCreated || !_owner.Visible || _owner.WindowState == FormWindowState.Minimized)
            {
                Hide();
                return;
            }
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            _label = label ?? "";
            _anchorX = cursorScreenX;
            _anchorBar = barScreenBounds;
            EnsureForm();
            if (_imagesEnabled && _source != null && !_playerFailed)
            {
                EnsurePlayer();
                if (_player != null)
                {
                    _wantPath = _source;
                    SyncPlayer();
                    // Recorded even before the file is classified, so the first frame shown is
                    // the hovered one; PumpSeek waits until the file is ready.
                    RequestFrame(seconds);
                }
            }
            LayoutAndShow();
        }

        public void Hide()
        {
            if (_disposed) return;
            _stable.Stop();
            if (_form != null && _form.Visible) _form.Visible = false;
        }

        public bool Visible { get { return _form != null && !_form.IsDisposed && _form.Visible; } }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.FormClosed -= OnOwnerClosed;
            _stable.Dispose();
            // mpv's video window is a child of our panel: destroy the core before the form.
            if (_player != null)
            {
                _player.Dispose();
                _player = null;
            }
            if (_form != null)
            {
                _form.Dispose();
                _form = null;
            }
            if (_font != null)
            {
                _font.Dispose();
                _font = null;
            }
            _borderBrush.Dispose();
        }

        private void OnOwnerClosed(object sender, FormClosedEventArgs e)
        {
            // Tear mpv down while our window still exists (the owner destroys owned windows).
            Dispose();
        }

        // ---- preview player -------------------------------------------------------------

        private void EnsureForm()
        {
            if (_form != null) return;
            _form = new PopupForm(this) { Owner = _owner };
            var img = ImageSize();
            _form.Video.Bounds = new Rectangle(0, 0, img.Width, img.Height);
            // Create both windows now, while hidden (CreateControl would skip an invisible form):
            // the preview player's event pump posts to the form, so it must have a handle before
            // mpv starts (a dropped wakeup is never repeated), and the panel must be created
            // inside the form rather than parked, since mpv renders into it.
            IntPtr formHandle = _form.Handle, videoHandle = _form.Video.Handle;
            GC.KeepAlive(formHandle);
            GC.KeepAlive(videoHandle);
        }

        private void EnsurePlayer()
        {
            if (_player != null || _playerFailed) return;
            MpvPlayer p = null;
            try
            {
                p = new MpvPlayer(_form, "preview");
                string[] options =
                {
                    "wid", _form.Video.Handle.ToInt64().ToString(Inv),
                    "aid", "no", "sid", "no", "sub-auto", "no", "audio-file-auto", "no",
                    "pause", "yes", "keep-open", "yes", "idle", "yes",
                    "osc", "no", "osd-level", "0",
                    "input-default-bindings", "no", "input-vo-keyboard", "no", "input-cursor", "no",
                    "input-media-keys", "no", "cursor-autohide", "no",
                    "terminal", "no", "config", "no", "ytdl", "no", "load-scripts", "no",
                    // Without this a watch-later entry written by the main player (or a standalone
                    // mpv) could restore tracks, filters or rotation into the preview.
                    "resume-playback", "no",
                    "hwdec", "auto-safe", "hr-seek", "yes", "hr-seek-framedrop", "yes",
                    "demuxer-max-bytes", "64MiB", "demuxer-max-back-bytes", "32MiB", "cache", "yes",
                    "vd-lavc-threads", "2", "force-window", "yes", "background-color", "#000000",
                };
                for (int i = 0; i + 1 < options.Length; i += 2) p.SetOption(options[i], options[i + 1]);
                p.Initialize();
                p.StartFile += OnStartFile;
                p.FileLoaded += OnFileLoaded;
                p.EndFile += OnEndFile;
                p.ObserveNode("current-tracks/video", OnVideoTrackChanged);
                p.ObserveNode("video-params", OnVideoParamsChanged);
                _player = p;
            }
            catch (Exception ex)
            {
                Log.Error("thumbnail preview: could not start the preview player", ex);
                _playerFailed = true;
                if (p != null) p.Dispose();
            }
        }

        // Moves the preview player towards _wantPath, one step at a time.
        private void SyncPlayer()
        {
            if (_player == null || _loadPending) return; // resumes on start-file
            if (string.Equals(_wantPath, _currentPath, StringComparison.Ordinal)) return;
            _fileSerial++;
            _fileReady = _loadFailed = _noVideo = false;
            _fileLoaded = _trackKnown = false;
            _aspect = 0;
            _shownTime = double.NaN;
            _shownExact = false;
            _currentPath = _wantPath;
            if (_currentPath == null)
            {
                _player.CommandAsync("stop");
                return;
            }
            _loadPending = true;
            string path = _currentPath;
            _player.CommandAsync((err, result) =>
            {
                if (err >= 0 || _disposed) return;
                // Rejected outright: no start-file will follow.
                Log.Info("thumbnail preview: loadfile " + path + " failed (" + err + ")");
                _loadPending = false;
                _loadFailed = true;
                SyncPlayer();
                RefreshIfVisible();
            }, "loadfile", path, "replace");
        }

        private void OnStartFile()
        {
            if (!_loadPending) return;
            _loadPending = false;
            SyncPlayer(); // the wanted file changed while this one was opening
        }

        private void OnFileLoaded()
        {
            if (_loadPending || _player == null || _currentPath == null) return;
            _fileLoaded = true;
            ReadVideoTrack();
            // Normally none yet (the first frame is still decoding); otherwise the latest value,
            // which this file's own notification replaces as soon as it differs.
            ApplyVideoParams();
        }

        private void OnEndFile(MpvEndReason reason, int error)
        {
            if (_loadPending) return; // a file we have already moved away from
            _fileReady = false;
            _fileLoaded = false;
            if (reason == MpvEndReason.Error && _currentPath != null)
            {
                Log.Info("thumbnail preview: could not open " + _currentPath + " (" + error + ")");
                _loadFailed = true;
                RefreshIfVisible();
            }
        }

        private bool CurrentFileLoaded { get { return _fileLoaded && !_loadPending && _player != null && _currentPath != null; } }

        private void OnVideoTrackChanged(object value)
        {
            // The value may have been read before file-loaded even when it is delivered after it
            // (mpv sends queued events first), so it is only a cue that the track may have changed.
            if (CurrentFileLoaded) ReadVideoTrack();
        }

        // Classifies the current file from an asynchronous read: issued after file-loaded, its
        // answer cannot describe an earlier state, and the UI thread never waits for it.
        private void ReadVideoTrack()
        {
            if (_trackReadPending)
            {
                _trackReadAgain = true;
                return;
            }
            _trackReadPending = true;
            _trackReadAgain = false;
            int serial = _fileSerial;
            _player.GetPropertyAsync("current-tracks/video", v =>
            {
                _trackReadPending = false;
                if (_disposed) return;
                if (serial == _fileSerial && CurrentFileLoaded) ClassifyTrack(v as Dictionary<string, object>);
                if (_trackReadAgain && CurrentFileLoaded) ReadVideoTrack();
            });
        }

        private void ClassifyTrack(Dictionary<string, object> video)
        {
            // Audio files (even with cover art) and still images get the text-only popup.
            bool noVideo = video == null || IsTrue(video, "albumart") || IsTrue(video, "image");
            if (_trackKnown && noVideo == _noVideo) return;
            _trackKnown = true;
            _noVideo = noVideo;
            _fileReady = !noVideo;
            if (_fileReady) PumpSeek();
            RefreshIfVisible();
        }

        private void OnVideoParamsChanged(object value)
        {
            _videoParams = value as Dictionary<string, object>;
            if (CurrentFileLoaded) ApplyVideoParams();
        }

        private void ApplyVideoParams()
        {
            var vp = _videoParams;
            if (vp == null) return; // not decoded yet (or no video): keep the current shape
            double dw = Number(vp, "dw"), dh = Number(vp, "dh");
            if (dw <= 0 || dh <= 0) return;
            long rotate = (long)Number(vp, "rotate");
            double a = rotate % 180 == 90 ? dh / dw : dw / dh;
            if (Math.Abs(a - _aspect) < 0.001) return;
            _aspect = a;
            RefreshIfVisible();
        }

        private void RequestFrame(double seconds)
        {
            if (!_target.HasValue || _target.Value != seconds)
            {
                _target = seconds;
                _wantExact = false;
                _stable.Stop();
                _stable.Start();
            }
            else if (!_wantExact && !_stable.Enabled)
            {
                _wantExact = true; // shown again at a time that has already settled
            }
            PumpSeek();
        }

        private void PumpSeek()
        {
            if (_player == null || !_fileReady || _seekInFlight || !_target.HasValue) return;
            double t = _target.Value;
            bool exact = _wantExact;
            if (t == _shownTime && (_shownExact || !exact)) return;
            _seekInFlight = true;
            int serial = _fileSerial;
            _player.CommandAsync((err, result) =>
            {
                _seekInFlight = false;
                if (_disposed) return;
                // Recorded even on failure: retrying the same target would only fail again.
                if (serial == _fileSerial)
                {
                    _shownTime = t;
                    _shownExact = exact;
                }
                PumpSeek();
            }, "seek", t.ToString("R", Inv), exact ? "absolute+exact" : "absolute+keyframes");
        }

        private static bool IsTrue(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        private static double Number(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            if (v is long) return (long)v;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            return 0;
        }

        // ---- layout ---------------------------------------------------------------------

        private int S(int logicalPx) { return Theme.Scale(_owner, logicalPx); }

        private int OwnerDpi { get { return Theme.DpiOf(_owner); } }

        private Font LabelFont
        {
            get
            {
                int dpi = OwnerDpi;
                if (_font == null || _fontDpi != dpi)
                {
                    if (_font != null) _font.Dispose();
                    // Pixel units: the size follows the owner's monitor, not the process DPI.
                    _font = new Font("Segoe UI", LabelFontPx * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
                    _fontDpi = dpi;
                }
                return _font;
            }
        }

        private bool ImagesShown
        {
            get
            {
                // Only once the player holds (or is opening) this source: never another file's frame.
                return _imagesEnabled && _player != null && _source != null && !_loadFailed && !_noVideo &&
                       string.Equals(_currentPath, _source, StringComparison.Ordinal);
            }
        }

        private Size ImageSize()
        {
            double a = _aspect > 0 ? _aspect : DefaultAspect;
            double w = S(ImageWidthLogical), h = w / a, maxH = S(ImageMaxHeightLogical);
            if (h > maxH)
            {
                h = maxH;
                w = h * a;
            }
            return new Size(Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
        }

        // "1:23.456  F 2001": the part after the double space is drawn dimmer.
        private void SplitLabel(out string main, out string extra)
        {
            int i = _label.IndexOf("  ", StringComparison.Ordinal);
            if (i <= 0) { main = _label.Trim(); extra = ""; return; }
            main = _label.Substring(0, i).Trim();
            extra = _label.Substring(i).Trim();
        }

        private int MeasureLabel(Graphics g, Font font, out int mainWidth)
        {
            string main, extra;
            SplitLabel(out main, out extra);
            mainWidth = main.Length == 0 ? 0 : Measure(g, main, font);
            if (extra.Length == 0) return mainWidth;
            return mainWidth + S(LabelGapLogical) + Measure(g, extra, font);
        }

        private static int Measure(Graphics g, string text, Font font)
        {
            return g == null
                ? TextRenderer.MeasureText(text, font, Size.Empty, MeasureFlags).Width
                : TextRenderer.MeasureText(g, text, font, Size.Empty, MeasureFlags).Width;
        }

        private void RefreshIfVisible()
        {
            if (Visible) LayoutAndShow();
        }

        private void LayoutAndShow()
        {
            if (_disposed || _form == null) return;
            bool images = ImagesShown;
            if (!images && _label.Trim().Length == 0)
            {
                Hide(); // nothing to show
                return;
            }
            int mainWidth;
            int textW = MeasureLabel(null, LabelFont, out mainWidth);
            int b = Math.Max(1, S(1)), pad = S(PadLogical), textPad = S(TextPadLogical);
            Size size;
            Rectangle video = Rectangle.Empty;
            if (images)
            {
                Size img = ImageSize();
                int innerW = Math.Max(img.Width, textW + 2 * textPad - 2 * pad);
                int labelH = S(LabelHeightLogical);
                size = new Size(2 * (b + pad) + innerW, b + pad + img.Height + labelH + b);
                video = new Rectangle(b + pad + (innerW - img.Width) / 2, b + pad, img.Width, img.Height);
                _labelRect = new Rectangle(b, b + pad + img.Height, size.Width - 2 * b, labelH);
            }
            else
            {
                size = new Size(2 * b + textW + 2 * textPad, S(TextOnlyHeightLogical));
                _labelRect = new Rectangle(b, b, size.Width - 2 * b, size.Height - 2 * b);
            }

            Rectangle area = ScreenArea();
            int gap = S(AnchorGapLogical);
            int x = _anchorX - size.Width / 2;
            int y = _anchorBar.Top - gap - size.Height;
            if (y < area.Top) y = _anchorBar.Bottom + gap; // no room above (bar at the top edge)
            x = Math.Max(area.Left, Math.Min(area.Right - size.Width, x));
            y = Math.Max(area.Top, Math.Min(area.Bottom - size.Height, y));

            bool modeChanged = images != _imagesShown;
            _imagesShown = images;
            // Always assigned: Control.Visible reads false while the form is hidden, so comparing
            // against it would leave the panel showing over the label on the next appearance.
            if (!images) _form.Video.Visible = false; // before shrinking, so it never covers the label
            _form.Bounds = new Rectangle(x, y, size.Width, size.Height);
            if (images)
            {
                // After the form: a monitor/DPI change during the move must not leave it stale.
                _form.Video.Bounds = video;
                _form.Video.Visible = true;
            }
            _form.Invalidate();
            if (!_form.Visible)
            {
                _form.Visible = true; // ShowWithoutActivation -> SW_SHOWNOACTIVATE
                BringToTop();
            }
            else if (modeChanged)
            {
                BringToTop();
            }
        }

        // Fullscreen owners cover the taskbar, so there the whole monitor is usable.
        private Rectangle ScreenArea()
        {
            Screen s = Screen.FromHandle(_owner.Handle);
            return _owner.Bounds.Contains(s.Bounds) ? s.Bounds : s.WorkingArea;
        }

        // Owned windows sit above their owner; also match the owner's topmost band so the popup
        // shows over an always-on-top or fullscreen window.
        private void BringToTop()
        {
            IntPtr h = _form.Handle;
            bool ownerTop = (Native.GetWindowLong(_owner.Handle, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
            bool selfTop = (Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
            IntPtr after = ownerTop ? Native.HWND_TOPMOST : selfTop ? Native.HWND_NOTOPMOST : Native.HWND_TOP;
            Native.SetWindowPos(h, after, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        // ---- painting -------------------------------------------------------------------

        private void PaintPopup(Graphics g, Size client, bool dwmFrame)
        {
            g.Clear(Theme.PanelAlt);
            if (!dwmFrame)
            {
                int b = Math.Max(1, S(1));
                g.FillRectangle(_borderBrush, 0, 0, client.Width, b);
                g.FillRectangle(_borderBrush, 0, client.Height - b, client.Width, b);
                g.FillRectangle(_borderBrush, 0, 0, b, client.Height);
                g.FillRectangle(_borderBrush, client.Width - b, 0, b, client.Height);
            }
            Rectangle r = _labelRect;
            if (r.Width <= 0 || r.Height <= 0) return;
            Font font = LabelFont;
            string main, extra;
            SplitLabel(out main, out extra);
            int mainWidth;
            int total = MeasureLabel(g, font, out mainWidth);
            const TextFormatFlags flags = MeasureFlags | TextFormatFlags.VerticalCenter;
            if (extra.Length == 0 || total > r.Width)
            {
                TextRenderer.DrawText(g, _label.Trim(), font, r, Theme.Text,
                    flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            int x = r.X + (r.Width - total) / 2;
            TextRenderer.DrawText(g, main, font, new Rectangle(x, r.Y, mainWidth, r.Height), Theme.Text, flags);
            int ex = x + mainWidth + S(LabelGapLogical);
            TextRenderer.DrawText(g, extra, font, new Rectangle(ex, r.Y, r.Right - ex, r.Height), Theme.TextDim, flags);
        }

        // ---- popup window ---------------------------------------------------------------

        private sealed class PopupForm : Form
        {
            private readonly ThumbnailPreview _outer;
            public readonly Panel Video;
            private bool _dwmFrame;

            public PopupForm(ThumbnailPreview outer)
            {
                _outer = outer;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                ControlBox = false;
                MinimizeBox = false;
                MaximizeBox = false;
                AutoScaleMode = AutoScaleMode.None;
                Text = "";
                BackColor = Theme.PanelAlt;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                // Disabled so hit-testing never stops at mpv's child window.
                Video = new Panel { BackColor = Theme.Video, Enabled = false };
                Controls.Add(Video);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style |= Native.WS_POPUP;
                    cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                    cp.ClassStyle |= Native.CS_DROPSHADOW;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // Appear/disappear instantly while hovering; on Windows 11 let DWM round the
                // corners and draw the 1 px frame (fails harmlessly on Windows 10).
                int on = 1;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_TRANSITIONS_FORCEDISABLED, ref on, 4);
                int corner = Native.DWMWCP_ROUNDSMALL;
                _dwmFrame = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4) == 0;
                if (_dwmFrame)
                {
                    int color = ColorTranslator.ToWin32(Theme.Border);
                    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref color, 4);
                }
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_MOUSEACTIVATE:
                        m.Result = (IntPtr)Native.MA_NOACTIVATE;
                        return;
                    case Native.WM_NCHITTEST:
                        // Click-through: if the popup ever ends up under the cursor it must not
                        // steal the hover from the seek bar beneath it.
                        m.Result = (IntPtr)Native.HTTRANSPARENT;
                        return;
                }
                base.WndProc(ref m);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                _outer.PaintPopup(e.Graphics, ClientSize, _dwmFrame);
            }

            protected override void OnDpiChanged(DpiChangedEventArgs e)
            {
                // Layout follows the owner's DPI and is recomputed on every Show; never let
                // WinForms apply the suggested rectangle or rescale the panel.
                e.Cancel = true;
                Invalidate();
            }
        }

        private static class Native
        {
            public const int WS_POPUP = unchecked((int)0x80000000);
            public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
            public const int CS_DROPSHADOW = 0x00020000;
            public const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3, WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
            public const int GWL_EXSTYLE = -20;
            public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
            public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
            public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
            public const int DWMWCP_ROUNDSMALL = 3;

            [DllImport("user32.dll")]
            public static extern int GetWindowLong(IntPtr hWnd, int index);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

            [DllImport("dwmapi.dll")]
            public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
        }
    }
}
