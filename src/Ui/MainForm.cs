using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;

namespace VidFlow.Ui
{
    // The main window: owns the player, playlist, control bar and all the wiring between them.
    // Partials:  Player (mpv init, observed state, file lifecycle) · Actions (registry) ·
    //            Commands (playback/video/audio action bodies) · Files (open, delete, screenshots,
    //            drag & drop) · Input (keys, mouse, cursor) · View (fullscreen, zoom/pan, window
    //            sizing) · Automation (control channel, dump, menu data) · Native (Win32).
    public sealed partial class MainForm : Form
    {
        private const string AppName = "VidFlow Player";

        private readonly Settings _settings;
        private readonly LaunchOptions _options;
        private readonly string _pipeName;

        private readonly ActionRegistry _actions = new ActionRegistry();
        private readonly Playlist _playlist = new Playlist();
        private readonly VideoHost _host;
        private readonly ControlBar _bar;
        private readonly SeekBar _seek;
        private readonly PlaylistPanel _playlistPanel;
        private readonly Splitter _splitter;
        private readonly ThumbnailPreview _preview;
        private readonly MenuBuilder _menus;
        private readonly ContextMenuStrip _contextMenu, _speedMenu, _repeatMenu, _zoomMenu;

        private MpvPlayer _mpv;
        private ControlChannel _channel;
        private bool _shown, _closing;
        private FormWindowState _lastNonMinimizedState = FormWindowState.Normal;

        public MainForm(Settings settings, LaunchOptions options, string pipeName)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _options = options ?? new LaunchOptions();
            _pipeName = string.IsNullOrEmpty(pipeName) ? ControlChannel.DefaultPipeName : pipeName;

            SuspendLayout();
            Text = AppName;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.Manual;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException) { Log.Warn("icon: " + ex.Message); }

            _host = new VideoHost { Dock = DockStyle.Fill };
            _bar = new ControlBar { Dock = DockStyle.Bottom, Visible = false };
            _seek = _bar.SeekBar;
            _splitter = new Splitter { Dock = DockStyle.Right, BackColor = Theme.Window, Visible = false, TabStop = false };
            _playlistPanel = new PlaylistPanel { Dock = DockStyle.Right, Playlist = _playlist, Visible = false };
            // Docking runs from the last child to the first: the playlist takes the full height
            // on the right, the bar spans the remaining width, the video fills the rest.
            Controls.Add(_host);
            Controls.Add(_bar);
            Controls.Add(_splitter);
            Controls.Add(_playlistPanel);
            ResumeLayout(false);

            _preview = new ThumbnailPreview(this) { ImagesEnabled = _settings.ThumbnailPreview };
            _menus = new MenuBuilder(_actions, new MenuData(this));

            _playlist.Shuffle = _settings.Shuffle;
            ApplyPlaylistSettings(); // before the first Load, so the startup files arrive sorted
            _playlist.Changed += OnPlaylistChanged;
            _actions.Failed += (id, ex) => ShowOsd("Error: " + (ex != null ? ex.Message : id), true);
            _actions.Executed += id => Log.Info("action " + id);
            RegisterActions();
            BuildKeyMap();

            _contextMenu = _menus.BuildContextMenu();
            _speedMenu = _menus.BuildSpeedMenu();
            _repeatMenu = _menus.BuildRepeatMenu();
            _zoomMenu = _menus.BuildZoomMenu();
            foreach (var m in new[] { _contextMenu, _speedMenu, _repeatMenu, _zoomMenu })
                if (m != null) m.Closed += (s, e) => OnMenuClosed();
            FeatureMenus();
            CullMenus();

            WireControlBar();
            WirePlaylistPanel();
            WireVideoHost();
            WireDragDrop(this);
            WireDragDrop(_host);
            WireDragDrop(_bar);
            WireDragDrop(_splitter);
            if (_seek != null) WireDragDrop(_seek);
            // The panel accepts drops itself (AppendRequested) when it opts in.
            if (!_playlistPanel.AllowDrop) WireDragDrop(_playlistPanel);
            InitInputTimers();

            RestoreWindowBounds();
            TopMost = _settings.AlwaysOnTop;
            PushStaticBarState();
            UpdateSurface();
        }

        // ---- lifecycle --------------------------------------------------------------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(Handle);
            ApplyDpiLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_shown) return;
            _shown = true;
            ApplyLayout(); // child handles now exist, so their preferred sizes are final (auto-fit relies on them)
            if (!InitPlayer()) return;
            StartControlChannel();
            ApplyLaunchOptions(_options, false); // --fullscreen, --paused, --speed, --repeat, --start, files
            // The other files of an Explorer multi-selection arrive as "open" hand-offs from processes
            // started at the same moment as this one.
            if (_options.Paths.Count > 0 && !_options.Automation) MarkOpenDone(LaunchOptions.ProcessStartUtcTicks());
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || _closing) return;
            _closing = true;
            StopInputTimers();
            HideOverlay();
            PersistState();
            SaveResumePosition(true);
            _settings.Save();
            ShowCursorNow();
            Application.RemoveMessageFilter(_mouseFilter);
            FeatureClosing();
            CullClosing();
            _preview.Dispose();
            if (_channel != null) _channel.Dispose();
            if (_mpv != null) _mpv.Dispose(); // pumps sent messages while mpv tears down its window
            _deleteTimer.Stop();
            FlushDeletesOnExit();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_overlay != null) _overlay.Dispose();
            foreach (var m in new[] { _contextMenu, _speedMenu, _repeatMenu, _zoomMenu })
                if (m != null) m.Dispose();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized) _lastNonMinimizedState = WindowState;
            // Restoring from minimized while fullscreen: WinForms re-applies its saved client size
            // with the (stripped) sizable frame, so the window grows past the monitor. Cover it
            // again once WinForms is done; calling CoverMonitor right here would fight that code.
            if (_fullscreen && WindowState == FormWindowState.Normal && IsHandleCreated && !_coverPending &&
                Bounds != Screen.FromHandle(Handle).Bounds)
            {
                _coverPending = true;
                BeginInvoke((MethodInvoker)(() =>
                {
                    _coverPending = false;
                    CoverMonitor();
                }));
            }
        }

        private bool _coverPending;

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyDpiLayout();
            // Children receive their own DPI notification after the parent; lay out again then.
            if (IsHandleCreated) BeginInvoke((MethodInvoker)ApplyDpiLayout);
        }

        // Logical (96-dpi) px -> device px for the monitor this window is on.
        private int Px(int logical)
        {
            return (int)Math.Round(logical * VideoHost.DpiOf(this) / 96.0);
        }

        private void ApplyDpiLayout()
        {
            if (!IsHandleCreated || _closing) return;
            // Client minimum 360x200 logical px; MinimumSize is a window size.
            Size nonClient = _fullscreen ? Size.Empty : Size - ClientSize;
            if (nonClient.Width < 0 || nonClient.Height < 0) nonClient = Size.Empty;
            MinimumSize = new Size(Px(360) + nonClient.Width, Px(200) + nonClient.Height);
            ApplyLayout();
            _host.Surface.Invalidate(); // re-creates its fonts for the new DPI on paint
            if (_fullscreen) CoverMonitor();
        }

        // ---- layout -----------------------------------------------------------------------

        // Shows/hides the bar and playlist for the current mode and sizes them for this DPI.
        private void ApplyLayout()
        {
            if (_closing) return;
            SuspendLayout();
            int barH = _bar.PreferredBarHeight;
            if (barH > 0 && _bar.Height != barH) _bar.Height = barH;
            bool showBar = !_fullscreen && _settings.ShowControls;
            if (_bar.Parent == this) _bar.Visible = showBar;

            bool showList = !_fullscreen && _settings.ShowPlaylist;
            _splitter.Width = Math.Max(2, Px(5));
            _splitter.MinSize = Px(180);
            _splitter.MinExtra = Px(240);
            int maxList = Math.Max(_splitter.MinSize, ClientSize.Width - _splitter.MinExtra - _splitter.Width);
            int listW = Math.Max(_splitter.MinSize, Math.Min(maxList, Px(Math.Max(120, _settings.PlaylistWidth))));
            if (_playlistPanel.Width != listW) _playlistPanel.Width = listW;
            // Windows does not move focus off a window whose parent is hidden: the keyboard would
            // stay in the invisible filter box and every shortcut would stop working.
            if (!showList && _playlistPanel.Visible && _playlistPanel.ContainsFocus) FocusVideo();
            _playlistPanel.Visible = showList;
            _splitter.Visible = showList;
            ResumeLayout(true);

            _bar.Fullscreen = _fullscreen;
            _bar.PlaylistVisible = _settings.ShowPlaylist;
            UpdateZoomText();
        }

        private void OnSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_playlistPanel.Visible)
                _settings.PlaylistWidth = (int)Math.Round(_playlistPanel.Width * 96.0 / VideoHost.DpiOf(this));
        }

        // ---- window bounds ----------------------------------------------------------------

        private void RestoreWindowBounds()
        {
            var b = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
            bool positioned = _settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue;
            if (!positioned || b.Width < 200 || b.Height < 150 || !IsReachable(b))
            {
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                int w = positioned ? Math.Max(200, b.Width) : Theme.Scale(null, 960);
                int h = positioned ? Math.Max(150, b.Height) : Theme.Scale(null, 600);
                w = Math.Min(w, wa.Width);
                h = Math.Min(h, wa.Height);
                b = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
            }
            Bounds = b;
            if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        // The title-bar strip must be on some monitor so the user can grab the window.
        private static bool IsReachable(Rectangle r)
        {
            var strip = new Rectangle(r.Left, r.Top, r.Width, 32);
            foreach (var s in Screen.AllScreens)
            {
                var i = Rectangle.Intersect(s.WorkingArea, strip);
                if (i.Width >= 100 && i.Height >= 16) return true;
            }
            return false;
        }

        private void PersistState()
        {
            Rectangle normal;
            bool maximized;
            if (_fullscreen)
            {
                normal = _fsRestoreBounds;
                maximized = _fsRestoreMaximized;
            }
            else if (WindowState == FormWindowState.Normal)
            {
                normal = Bounds;
                maximized = false;
            }
            else
            {
                normal = RestoreBounds;
                maximized = WindowState == FormWindowState.Maximized ||
                            (WindowState == FormWindowState.Minimized && _lastNonMinimizedState == FormWindowState.Maximized);
            }
            if (normal.Width > 0 && normal.Height > 0)
            {
                _settings.WindowX = normal.X;
                _settings.WindowY = normal.Y;
                _settings.WindowWidth = normal.Width;
                _settings.WindowHeight = normal.Height;
            }
            _settings.WindowMaximized = maximized;
            SyncLiveSettings();
        }

        // Copies state that lives in the player/UI back into the settings object.
        private void SyncLiveSettings()
        {
            _settings.AlwaysOnTop = TopMost;
            if (_mpv != null && _mpv.IsAlive && _mpv.IsInitialized)
            {
                _settings.Volume = Math.Max(0, Math.Min(Presets.VolumeMax, _st.Volume));
                _settings.Muted = _st.Muted;
            }
            _settings.Shuffle = _playlist.Shuffle;
        }

        // ---- title & bar ------------------------------------------------------------------

        private void OnPlaylistChanged()
        {
            UpdateTitle();
            CullPlaylistChanged();
        }

        private void UpdateTitle()
        {
            int count = _playlist.Count, index = _playlist.Index;
            string name = HasMedia ? CurrentDisplayName() : null;
            string title = name == null ? AppName
                : name + (count > 1 && index >= 0 ? " [" + (index + 1) + "/" + count + "]" : "") + " — " + AppName;
            if (Text != title) Text = title;
            _bar.PlaylistText = count > 1 && index >= 0 ? (index + 1) + " / " + count : "";
        }

        private string CurrentDisplayName()
        {
            string p = _st.Path ?? _playlist.Current;
            if (string.IsNullOrEmpty(p)) return null;
            if (Playlist.IsUrl(p))
                return !string.IsNullOrEmpty(_st.MediaTitle) ? _st.MediaTitle : p;
            return FileNameOf(p);
        }

        private static string FileNameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (Playlist.IsUrl(path)) return path;
            try
            {
                string n = Path.GetFileName(path.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(n) ? path : n;
            }
            catch (ArgumentException)
            {
                return path;
            }
        }

        // Values that do not come from mpv.
        private void PushStaticBarState()
        {
            _bar.VolumeMax = Presets.VolumeMax;
            _bar.Volume = Math.Max(0, Math.Min(Presets.VolumeMax, _settings.Volume));
            _bar.Muted = _settings.Muted;
            _bar.Repeat = _settings.Repeat;
            _bar.Shuffle = _playlist.Shuffle;
            _bar.ShowFrameNumber = _settings.ShowFrameNumber;
            _bar.ShowRemaining = _settings.ShowRemainingTime;
            _bar.Fullscreen = _fullscreen;
            _bar.PlaylistVisible = _settings.ShowPlaylist;
            _bar.TooltipProvider = TooltipFor;
            if (_seek != null) _seek.WheelStepSeconds = Math.Max(0.1, _settings.SeekStep);
            _preview.ImagesEnabled = _settings.ThumbnailPreview;
        }

        private void WireControlBar()
        {
            _bar.ButtonClicked += OnBarButton;
            _bar.VolumeChanged += v => SetVolume(v, false);
            _bar.TimeDisplayClicked += () =>
            {
                _settings.ShowRemainingTime = !_settings.ShowRemainingTime;
                _bar.ShowRemaining = _settings.ShowRemainingTime;
            };
            _splitter.SplitterMoved += OnSplitterMoved;
            if (_seek == null) return;
            _seek.SeekRequested += OnSeekBarSeek;
            _seek.ScrubEnded += () => { if (_st.EofReached && !_eofHandled) HandleEof(); };
            _seek.HoverChanged += OnSeekHover;
            _seek.HoverEnded += () => _preview.Hide();
        }

        private void OnBarButton(BarButton button, MouseButtons mouse, Rectangle screenRect)
        {
            bool right = mouse == MouseButtons.Right;
            switch (button)
            {
                case BarButton.PrevFile: Run(ActionIds.PrevFile); break;
                case BarButton.FrameBack: Run(ActionIds.FrameBackStep); break;
                case BarButton.PlayPause: Run(ActionIds.PlayPause); break;
                case BarButton.FrameForward: Run(ActionIds.FrameStep); break;
                case BarButton.NextFile: Run(ActionIds.NextFile); break;
                case BarButton.Repeat:
                    if (right) Run(ActionIds.CycleRepeat); else ShowDropDown(_repeatMenu, screenRect, false);
                    break;
                case BarButton.AbLoop: Run(right ? ActionIds.AbLoopClear : ActionIds.AbLoop); break;
                case BarButton.Shuffle: Run(ActionIds.ToggleShuffle); break;
                case BarButton.Speed:
                    if (right) Run(ActionIds.SpeedReset); else ShowDropDown(_speedMenu, screenRect, false);
                    break;
                case BarButton.Zoom:
                    if (right) Run(ActionIds.ZoomReset); else ShowDropDown(_zoomMenu, screenRect, false);
                    break;
                case BarButton.Mute: Run(ActionIds.ToggleMute); break;
                case BarButton.Playlist: Run(ActionIds.TogglePlaylist); break;
                case BarButton.Fullscreen: Run(ActionIds.ToggleFullscreen); break;
                case BarButton.Menu: ShowDropDown(_contextMenu, screenRect, true); break;
            }
        }

        // Drop-down opening upwards from a bar button (the bar sits at the bottom edge). ShowAt
        // sizes the menu for the monitor it opens on; it opens at a top-left point, so place it from
        // the size it had last time, then line it up against the button once its real size is known.
        private void ShowDropDown(ContextMenuStrip menu, Rectangle anchor, bool alignRight)
        {
            if (menu == null) return;
            ShowCursorNow();
            if (menu.Visible) { menu.Close(); return; }
            Size guess = menu.Size.Width > 0 && menu.Size.Height > 0 ? menu.Size : menu.GetPreferredSize(Size.Empty);
            MenuBuilder.ShowAt(menu, DropDownOrigin(anchor, guess, alignRight));
            if (!menu.Visible) return;
            Point want = DropDownOrigin(anchor, menu.Size, alignRight);
            if (menu.Location != want) menu.Location = want;
        }

        private static Point DropDownOrigin(Rectangle anchor, Size menu, bool alignRight)
        {
            Rectangle wa = Screen.FromPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top)).WorkingArea;
            int x = alignRight ? anchor.Right - menu.Width : anchor.Left;
            int y = anchor.Top - menu.Height;
            if (y < wa.Top) y = Math.Min(anchor.Bottom, wa.Bottom - menu.Height); // no room above: open below
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - menu.Width));
            return new Point(x, Math.Max(wa.Top, y));
        }

        private string TooltipFor(BarButton b)
        {
            switch (b)
            {
                case BarButton.PrevFile: return Tip(ActionIds.PrevFile);
                case BarButton.FrameBack: return Tip(ActionIds.FrameBackStep);
                case BarButton.PlayPause: return Tip(ActionIds.PlayPause);
                case BarButton.FrameForward: return Tip(ActionIds.FrameStep);
                case BarButton.NextFile: return Tip(ActionIds.NextFile);
                case BarButton.Repeat: return "Repeat: " + RepeatLabel(_settings.Repeat) + KeyHint(ActionIds.CycleRepeat) + " · right-click to cycle";
                case BarButton.AbLoop: return Tip(ActionIds.AbLoop) + " · right-click to clear";
                case BarButton.Shuffle: return Tip(ActionIds.ToggleShuffle);
                case BarButton.Speed: return "Playback Speed" + KeyHint(ActionIds.SpeedUp) + " · right-click for 1×";
                case BarButton.Zoom: return "Zoom" + KeyHint(ActionIds.ZoomIn) + " · right-click to fit";
                case BarButton.Mute: return Tip(ActionIds.ToggleMute);
                case BarButton.Playlist: return Tip(ActionIds.TogglePlaylist);
                case BarButton.Fullscreen: return Tip(ActionIds.ToggleFullscreen);
                case BarButton.Menu: return Tip(ActionIds.ContextMenu);
                default: return null;
            }
        }

        private string Tip(string id)
        {
            return ActionRegistry.TitleOf(id) + KeyHint(id);
        }

        private string KeyHint(string id)
        {
            string sc = _actions.ShortcutText(id, 1);
            return sc.Length > 0 ? " (" + sc + ")" : "";
        }

        private void OnSeekBarSeek(double t, bool final)
        {
            if (!HasMedia) return;
            ResetFrameStepQueue(); // a deliberate seek supersedes queued frame steps
            t = Math.Max(0, _st.Duration > 0 ? Math.Min(t, _st.Duration) : t);
            // Scrubbing: "keyframes" overrides hr-seek=yes, so each drag update only decodes up to the
            // nearest keyframe (fast even on long-GOP 4K); the release / click lands exactly - on a
            // constant-frame-rate file on the frame the hover label named (the one shown at t).
            if (final && FrameExact) t = FrameSeekTime(FrameAtTime(t));
            _mpv.CommandAsync("no-osd", "seek", Num(t), final ? "absolute+exact" : "absolute+keyframes");
        }

        private void OnSeekHover(double t, int screenX, Rectangle barBounds)
        {
            if (!HasMedia || _st.Duration <= 0) { _preview.Hide(); return; }
            t = Math.Max(0, Math.Min(_st.Duration, t));
            string label = Fmt.Time(t, true);
            bool estimate;
            long frame = FrameNumberAt(t, out estimate);
            if (frame >= 0) label += "  " + (FrameApproximate ? "≈F " : "F ") + frame;
            _preview.ImagesEnabled = _settings.ThumbnailPreview && HasVideo;
            _preview.Show(t, label, screenX, barBounds);
        }

        // ---- playlist panel ---------------------------------------------------------------

        private void WirePlaylistPanel()
        {
            _playlistPanel.ItemActivated += i => PlayIndex(i, true);
            _playlistPanel.RemoveRequested += RemoveFromPlaylist;
            _playlistPanel.DeleteRequested += idx => DeletePlaylistItems(idx, _settings.ConfirmDelete);
            _playlistPanel.ShowInFolderRequested += i =>
            {
                if (i >= 0 && i < _playlist.Count) ShowInExplorer(_playlist.Items[i]);
            };
            _playlistPanel.AppendRequested += paths => OpenPaths(paths, true);
            _playlistPanel.CloseRequested += () => SetPlaylistVisible(false);
            // The panel already re-sorted the playlist; remember the choice for the next session.
            _playlistPanel.SortChanged += () =>
            {
                _settings.PlaylistSortBy = _playlist.Sort.ToString();
                _settings.PlaylistSortDescending = _playlist.Descending;
            };
            // SaveRequested -> Run(SavePlaylist) is wired next to that action, in MainForm.Features.cs
            // (FeatureMenus); a second subscription here would open the Save dialog twice.
        }

        // Settings -> playlist behaviour (startup and after the Settings dialog).
        private void ApplyPlaylistSettings()
        {
            _playlist.IncludeSubfolders = _settings.IncludeSubfolders;
            PlaylistSort sort;
            if (!Enum.TryParse(_settings.PlaylistSortBy ?? "", true, out sort) || !Enum.IsDefined(typeof(PlaylistSort), sort))
                sort = PlaylistSort.Name;
            // Each setter re-sorts; skip unchanged ones so a big list is not sorted for nothing.
            if (_playlist.Sort != sort) _playlist.Sort = sort;
            if (_playlist.Descending != _settings.PlaylistSortDescending) _playlist.Descending = _settings.PlaylistSortDescending;
        }

        private void PlayIndex(int index, bool unpause)
        {
            if (index < 0 || index >= _playlist.Count) return;
            if (_playlist.MoveTo(index) || _playlist.Index == index) PlayCurrent(unpause);
        }

        private void RemoveFromPlaylist(int[] indices)
        {
            if (indices == null || indices.Length == 0) return;
            string before = _playlist.Current;
            var seen = new HashSet<int>();
            var valid = new List<int>();
            foreach (int i in indices)
                if (i >= 0 && i < _playlist.Count && seen.Add(i)) valid.Add(i);
            if (valid.Count == 0) return;
            // One batch (one Changed event): per-item removal rebuilt the panel once per row.
            if (valid.Count == _playlist.Count) _playlist.Clear();
            else _playlist.RemoveRange(valid);
            if (SamePath(before, _playlist.Current) || !SamePath(before, _st.Path)) return;
            if (_playlist.Current != null) PlayCurrent(false);
            else StopPlayback();
        }

        private static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return a == b;
            return string.Equals(a, b, Playlist.IsUrl(a) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        // Windowed, the window grows/shrinks by the panel so the video keeps its size.
        private void SetPlaylistVisible(bool visible)
        {
            bool resize = CanResizeForLayout && _settings.ShowPlaylist != visible;
            int delta = visible ? Px(Math.Max(120, _settings.PlaylistWidth)) + Math.Max(2, Px(5))
                                : (_playlistPanel.Visible ? _playlistPanel.Width + _splitter.Width : 0);
            _settings.ShowPlaylist = visible;
            if (resize && visible) GrowWindow(delta, 0); // first, so the panel gets its preferred width
            ApplyLayout();
            if (resize && !visible) GrowWindow(-delta, 0);
        }

        private void SetControlsVisible(bool visible)
        {
            bool resize = CanResizeForLayout && _settings.ShowControls != visible && _bar.Parent == this;
            int delta = visible ? _bar.PreferredBarHeight : (_bar.Visible ? _bar.Height : 0);
            _settings.ShowControls = visible;
            if (resize && visible) GrowWindow(0, delta);
            ApplyLayout();
            if (resize && !visible) GrowWindow(0, -delta);
        }

        private bool CanResizeForLayout
        {
            get { return IsHandleCreated && !_fullscreen && WindowState == FormWindowState.Normal; }
        }

        // Grows to the right/bottom, sliding back inside the work area when needed.
        private void GrowWindow(int dw, int dh)
        {
            if (dw == 0 && dh == 0) return;
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            Rectangle b = Bounds;
            int w = Math.Max(MinimumSize.Width, Math.Min(wa.Width, b.Width + dw));
            int h = Math.Max(MinimumSize.Height, Math.Min(wa.Height, b.Height + dh));
            int x = b.X + w > wa.Right ? Math.Max(wa.Left, wa.Right - w) : b.X;
            int y = b.Y + h > wa.Bottom ? Math.Max(wa.Top, wa.Bottom - h) : b.Y;
            Bounds = new Rectangle(x, y, w, h);
        }
    }
}
