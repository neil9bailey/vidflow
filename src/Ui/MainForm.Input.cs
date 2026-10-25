using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    // Keyboard, media keys, mouse gestures over the video, wheel and cursor auto-hide.
    public sealed partial class MainForm
    {
        private enum DragMode { None, Pan, MoveWindow }

        private const int CursorHideDelayMs = 1500;

        private Timer _cursorTimer;
        private MouseMoveFilter _mouseFilter;
        private bool _cursorHidden;
        private int _cursorIdleSince;
        private Point _lastMousePos = new Point(int.MinValue, int.MinValue);

        // Current press over the video (one gesture at a time).
        private MouseButtons _downButton = MouseButtons.None;
        private Point _downPoint, _lastDragPoint;
        private bool _dragging, _suppressUp;
        private DragMode _dragMode;

        // Double-click undo: the first click's pause toggle is reverted when the second arrives.
        private bool _clickToggled, _pausedBeforeClick;
        private int _clickToggledAt;

        private int _wheelAccum;
        private Keys _wheelMods;

        // Sees every mouse move of this thread (video, bar, overlay, dialogs) to wake the cursor,
        // and every button press, so a click anywhere outside the playlist filter ends filter editing.
        private sealed class MouseMoveFilter : IMessageFilter
        {
            private readonly MainForm _owner;
            public MouseMoveFilter(MainForm owner) { _owner = owner; }

            public bool PreFilterMessage(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_MOUSEMOVE:
                    case Native.WM_NCMOUSEMOVE:
                        _owner.OnAnyMouseMove();
                        break;
                    case Native.WM_LBUTTONDOWN:
                    case Native.WM_RBUTTONDOWN:
                    case Native.WM_MBUTTONDOWN:
                    case Native.WM_NCLBUTTONDOWN:
                    case Native.WM_NCRBUTTONDOWN:
                    case Native.WM_NCMBUTTONDOWN:
                        _owner.OnAnyButtonDown(m.HWnd);
                        break;
                }
                return false;
            }
        }

        // The video host, bar, seek bar, splitter and overlay never take focus, so a click on them
        // would leave the keyboard in the filter box (Space typing into it). Clicks inside the
        // playlist panel are the panel's own business (it ends editing for non-filter parts).
        private void OnAnyButtonDown(IntPtr hwnd)
        {
            if (!_playlistPanel.IsEditingFilter) return;
            Control c = Control.FromChildHandle(hwnd);
            if (c == null || c == _playlistPanel || _playlistPanel.Contains(c)) return;
            if (OwnControlOf(hwnd) != null) _playlistPanel.EndFilterEditing();
        }

        // The control (of this window or the fullscreen overlay) a window handle belongs to, or null.
        private Control OwnControlOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            Control c = Control.FromChildHandle(hwnd);
            if (c == null) return null;
            Form f = c as Form ?? c.FindForm();
            return f == this || (f != null && f == _overlay) ? c : null;
        }

        private void InitInputTimers()
        {
            _cursorIdleSince = Environment.TickCount;
            _cursorTimer = new Timer { Interval = 100 };
            _cursorTimer.Tick += (s, e) =>
            {
                FlushPositionPush();
                UpdateOverlay();
                UpdateCursorVisibility();
            };
            _cursorTimer.Start();
            _mouseFilter = new MouseMoveFilter(this);
            Application.AddMessageFilter(_mouseFilter);
            InitDeleteTimer();
        }

        private void StopInputTimers()
        {
            _cursorTimer.Stop();
            _cursorTimer.Dispose();
        }

        // ---- keyboard ---------------------------------------------------------------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (IsTextInputFocused())
            {
                if (keyData == Keys.Escape)
                {
                    FocusVideo();
                    return true;
                }
                // The filter box consumes typing and editing keys itself and deliberately passes on
                // F-keys and non-editing Ctrl/Alt chords (F11, Alt+Enter, Ctrl+O): run those.
                if (_playlistPanel.IsEditingFilter && IsForwardedWhileTyping(keyData))
                {
                    _keyIsAutoRepeat = IsAutoRepeat(ref msg);
                    try
                    {
                        if (HandleKey(keyData) != null) return true;
                    }
                    finally
                    {
                        _keyIsAutoRepeat = false;
                    }
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            _keyIsAutoRepeat = IsAutoRepeat(ref msg);
            try
            {
                if (HandleKey(keyData) != null) return true;
            }
            finally
            {
                _keyIsAutoRepeat = false;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // lParam bit 30: the key was already down (auto-repeat).
        private static bool IsAutoRepeat(ref Message msg)
        {
            return (msg.Msg == Native.WM_KEYDOWN || msg.Msg == Native.WM_SYSKEYDOWN) && (msg.LParam.ToInt64() & (1L << 30)) != 0;
        }

        // F-keys and Ctrl/Alt chords, minus the text-editing chords a text box needs.
        private static bool IsForwardedWhileTyping(Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            if (code >= Keys.F1 && code <= Keys.F24) return true;
            if ((mods & Keys.Alt) != 0) return true;
            if ((mods & Keys.Control) == 0) return false;
            switch (code)
            {
                case Keys.A: case Keys.C: case Keys.V: case Keys.X: case Keys.Z: case Keys.Y:
                case Keys.Insert: case Keys.Back: case Keys.Delete:
                case Keys.Left: case Keys.Right: case Keys.Home: case Keys.End:
                    return false;
                default:
                    return true;
            }
        }

        // Runs the action bound to a key; returns its id, or null when the key is unbound.
        // Auto-repeat simply calls this again (holding "." keeps stepping).
        private string HandleKey(Keys keyData)
        {
            // In Cull mode the cull layer (P, X, U, 0-5, N) wins over the global keys.
            string id = (_settings.CullMode ? _actions.FindActionForKey(keyData, ActionCatalog.LayerCull) : null)
                        ?? _actions.FindActionForKey(keyData);
            if (id == null) return null;
            _actions.Execute(id);
            return id;
        }

        private bool IsTextInputFocused()
        {
            if (_playlistPanel.IsEditingFilter) return true;
            Control c = ActiveControl;
            while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
            return c is TextBoxBase && c.Focused;
        }

        private void FocusVideo()
        {
            ActiveControl = null;
            if (!Focused) Focus();
        }

        protected override void WndProc(ref Message m)
        {
            bool featureHandled = false;
            FeatureWndProc(ref m, ref featureHandled);
            if (featureHandled) return;
            switch (m.Msg)
            {
                case Native.WM_MOUSEWHEEL:
                    if (ForwardWheel(ref m)) return;
                    break;
                case Native.WM_SYSCOMMAND:
                    // A lone Alt or F10 would enter system-menu mode and swallow the next shortcut.
                    if (((int)m.WParam.ToInt64() & 0xFFF0) == Native.SC_KEYMENU && m.LParam == IntPtr.Zero) return;
                    break;
                case Native.WM_APPCOMMAND:
                    if (HandleAppCommand(m.LParam))
                    {
                        m.Result = (IntPtr)1;
                        return;
                    }
                    break;
                case Native.WM_DPICHANGED:
                {
                    base.WndProc(ref m);
                    // Without WinForms' per-monitor mode (app.config) nobody applies the suggested
                    // bounds, and OnDpiChanged never runs: do both here.
                    var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                    var suggested = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    if (!_fullscreen && WindowState == FormWindowState.Normal && suggested.Width > 0 && Bounds != suggested)
                        Native.SetWindowPos(Handle, IntPtr.Zero, suggested.X, suggested.Y, suggested.Width, suggested.Height,
                            Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_NOACTIVATE);
                    if (!_closing) BeginInvoke((MethodInvoker)ApplyDpiLayout);
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // WM_APPCOMMAND only arrives for key presses ProcessCmdKey did not consume (or devices that
        // send nothing else), so mapping it through the same key table cannot double-fire.
        // Mouse X buttons arrive here too, from the video, the bar or the playlist alike.
        private bool HandleAppCommand(IntPtr lParam)
        {
            int hi = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
            int cmd = hi & ~Native.FAPPCOMMAND_MASK;
            bool fromMouse = (hi & Native.FAPPCOMMAND_MASK) == Native.FAPPCOMMAND_MOUSE;
            switch (cmd)
            {
                case 1: // APPCOMMAND_BROWSER_BACKWARD
                    if (fromMouse) { Run(ActionIds.PrevFile); return true; }
                    return HandleKey(Keys.BrowserBack) != null;
                case 2: // APPCOMMAND_BROWSER_FORWARD
                    if (fromMouse) { Run(ActionIds.NextFile); return true; }
                    return HandleKey(Keys.BrowserForward) != null;
                case 11: return HandleKey(Keys.MediaNextTrack) != null;
                case 12: return HandleKey(Keys.MediaPreviousTrack) != null;
                case 13: return HandleKey(Keys.MediaStop) != null;
                case 14: return HandleKey(Keys.MediaPlayPause) != null;
                case 46: // APPCOMMAND_MEDIA_PLAY
                    if (!HasMedia || _st.Paused) Run(ActionIds.PlayPause);
                    return true;
                case 47: // APPCOMMAND_MEDIA_PAUSE
                    if (HasMedia && !_st.Paused) SetPause(true);
                    return true;
                default:
                    return false;
            }
        }

        // ---- mouse over the video ---------------------------------------------------------

        private void WireVideoHost()
        {
            _host.MouseDown += (s, e) => VideoMouseDown(e.Button, e.Location, e.Clicks, false);
            _host.MouseMove += (s, e) => VideoMouseMove(e.Location, false);
            _host.MouseUp += (s, e) => VideoMouseUp(e.Button, e.Location, false);
            _host.MouseWheel += (s, e) => VideoWheel(e.Location, e.Delta, ModifierKeys);
            _host.MouseLeave += (s, e) => SetHotRecent(-1);
            _host.Resize += (s, e) => UpdateZoomText(); // the displayed % depends on the fit size
            _host.MouseCaptureChanged += (s, e) =>
            {
                // Capture lost mid-gesture (Alt+Tab, a popup): abandon it rather than act on a stale press.
                if (!_host.Capture && _downButton != MouseButtons.None && !_simulating) ResetGesture();
            };
        }

        private bool _simulating; // automation is driving the handlers (no real capture involved)

        private void ResetGesture()
        {
            _downButton = MouseButtons.None;
            _dragging = false;
            _dragMode = DragMode.None;
        }

        private void VideoMouseDown(MouseButtons button, Point p, int clicks, bool simulated)
        {
            if (button != MouseButtons.Left && button != MouseButtons.Right && button != MouseButtons.Middle) return; // X buttons: WM_APPCOMMAND
            _cursorIdleSince = Environment.TickCount;
            ShowCursorNow();
            _preview.Hide();
            if (button == MouseButtons.Left && clicks >= 2)
            {
                OnVideoDoubleClick(p, simulated);
                return;
            }
            if (_downButton != MouseButtons.None) return;
            _downButton = button;
            _downPoint = _lastDragPoint = p;
            _dragging = false;
            _dragMode = DragMode.None;
            _suppressUp = false;
            if (!simulated) _host.Capture = true;
        }

        private void VideoMouseMove(Point p, bool simulated)
        {
            if (_downButton == MouseButtons.None)
            {
                UpdateRecentHover(p);
                return;
            }
            if (!_dragging)
            {
                Size ds = SystemInformation.DragSize;
                if (Math.Abs(p.X - _downPoint.X) <= ds.Width / 2 && Math.Abs(p.Y - _downPoint.Y) <= ds.Height / 2) return;
                _dragging = true;
                if (_downButton == MouseButtons.Middle || (_downButton == MouseButtons.Left && HasVideo && !IsZoomReset))
                    _dragMode = DragMode.Pan;
                else if (_downButton == MouseButtons.Left && _settings.DragMovesWindow && !_fullscreen && WindowState == FormWindowState.Normal)
                    _dragMode = DragMode.MoveWindow;
                else
                    _dragMode = DragMode.None;

                if (_dragMode == DragMode.MoveWindow && !simulated)
                {
                    BeginSystemMove();
                    return;
                }
            }
            switch (_dragMode)
            {
                case DragMode.Pan:
                    if (HasVideo) PanByPixels(p.X - _lastDragPoint.X, p.Y - _lastDragPoint.Y);
                    break;
                case DragMode.MoveWindow: // automation only; real drags use the system move loop
                    Location = new Point(Location.X + p.X - _lastDragPoint.X, Location.Y + p.Y - _lastDragPoint.Y);
                    break;
            }
            _lastDragPoint = p;
        }

        // Hands the drag to Windows' own move loop (smooth, supports snapping). It returns on release.
        private void BeginSystemMove()
        {
            ResetGesture();
            _host.Capture = false;
            Native.ReleaseCapture();
            Point s = Cursor.Position;
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION,
                (IntPtr)((s.Y << 16) | (s.X & 0xFFFF)));
            _cursorIdleSince = Environment.TickCount;
        }

        private void VideoMouseUp(MouseButtons button, Point p, bool simulated)
        {
            if (button != MouseButtons.Left && button != MouseButtons.Right && button != MouseButtons.Middle) return;
            MouseButtons down = _downButton;
            bool wasDrag = _dragging;
            ResetGesture();
            if (!simulated && _host.Capture) _host.Capture = false;
            if (_suppressUp)
            {
                _suppressUp = false; // release of a double-click's second press
                return;
            }
            if (down != button || wasDrag) return;
            switch (button)
            {
                case MouseButtons.Left:
                    OnVideoClick(p);
                    break;
                case MouseButtons.Middle:
                    if (HasVideo && !IsZoomReset) Run(ActionIds.ZoomReset);
                    break;
                case MouseButtons.Right:
                {
                    // After the mouse-up finishes, so the host's capture release cannot touch the menu.
                    Point screen = _host.PointToScreen(p);
                    BeginInvoke((MethodInvoker)(() => ShowContextMenu(screen)));
                    break;
                }
            }
        }

        private void OnVideoClick(Point p)
        {
            _clickToggled = false;
            if (_host.Surface.Mode == SurfaceMode.Idle)
            {
                int recent = _host.Surface.HitTestRecent(p);
                if (recent >= 0)
                {
                    SetHotRecent(-1);
                    OpenRecentPath(_host.Surface.RecentPathAt(recent)); // the entry shown, not a list position
                    return;
                }
            }
            if (_settings.LeftClick != ClickAction.PlayPause) return;
            _pausedBeforeClick = _st.Paused;
            bool hadMedia = HasMedia;
            if (Run(ActionIds.PlayPause) && hadMedia)
            {
                _clickToggled = true;
                _clickToggledAt = Environment.TickCount;
            }
        }

        private void OnVideoDoubleClick(Point p, bool simulated)
        {
            if (!_settings.DoubleClickFullscreen)
            {
                // Treat the second press as an ordinary one.
                _downButton = MouseButtons.Left;
                _downPoint = _lastDragPoint = p;
                _dragging = false;
                _suppressUp = false;
                if (!simulated) _host.Capture = true;
                return;
            }
            _suppressUp = true;
            if (_clickToggled && HasMedia && unchecked(Environment.TickCount - _clickToggledAt) <= SystemInformation.DoubleClickTime + 200)
                SetPause(_pausedBeforeClick);
            _clickToggled = false;
            Run(ActionIds.ToggleFullscreen);
        }

        private void UpdateRecentHover(Point p)
        {
            int idx = _host.Surface.Mode == SurfaceMode.Idle ? _host.Surface.HitTestRecent(p) : -1;
            SetHotRecent(idx);
        }

        private void SetHotRecent(int idx)
        {
            _host.Surface.HotRecent = idx;
            Cursor want = idx >= 0 ? Cursors.Hand : Cursors.Default;
            if (_host.Cursor != want) _host.Cursor = want;
        }

        // ---- wheel ------------------------------------------------------------------------

        private bool _forwardingWheel;

        // With "Scroll inactive windows" off, Windows sends the wheel to the focus window, which is
        // the form (none of our controls take focus): hand it to the control under the cursor
        // (bar, seek bar, playlist, overlay). The video host is served by OnMouseWheel below.
        private bool ForwardWheel(ref Message m)
        {
            if (_forwardingWheel) return false; // a child that did not want it passed it back up
            long lp = m.LParam.ToInt64();
            var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            IntPtr target = Native.WindowAt(screen);
            if (target == IntPtr.Zero || target == Handle || (_host.IsHandleCreated && target == _host.Handle)) return false;
            if (OwnControlOf(target) == null) return false;
            _forwardingWheel = true;
            try { m.Result = Native.SendMessage(target, m.Msg, m.WParam, m.LParam); }
            finally { _forwardingWheel = false; }
            return true;
        }

        // Wheel delivered to the form (focus-follows wheel off): forward when over the video.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Point screen = Cursor.Position;
            if (!_host.IsHandleCreated || Native.WindowAt(screen) != _host.Handle) return;
            VideoWheel(_host.PointToClient(screen), e.Delta, ModifierKeys);
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }

        private void VideoWheel(Point p, int delta, Keys mods)
        {
            mods &= Keys.Modifiers;
            if (mods != _wheelMods)
            {
                _wheelAccum = 0;
                _wheelMods = mods;
            }
            // High-resolution wheels send small deltas: act per full notch.
            _wheelAccum += delta;
            int notches = _wheelAccum / 120;
            if (notches == 0) return;
            _wheelAccum -= notches * 120;
            int dir = Math.Sign(notches);
            for (int i = 0; i < Math.Abs(notches); i++) WheelStep(p, dir, mods);
        }

        private void WheelStep(Point p, int dir, Keys mods)
        {
            if ((mods & Keys.Control) != 0) { if (HasVideo) ZoomBy(dir * Presets.ZoomStepLog2, p); return; }
            // Through the actions: the feature layer re-registers frame steps so they stay
            // absolute during backward playback.
            if ((mods & Keys.Shift) != 0) { if (HasMedia) Run(dir > 0 ? ActionIds.FrameStep : ActionIds.FrameBackStep); return; }
            if ((mods & Keys.Alt) != 0) { SetSpeed(Math.Round(_st.Speed + dir * Presets.FineSpeedStep, 2)); return; }
            switch (_settings.Wheel)
            {
                case WheelAction.Seek:
                    if (HasMedia) SeekRelative(dir * _settings.SeekStep);
                    break;
                case WheelAction.FrameStep:
                    if (HasMedia) Run(dir > 0 ? ActionIds.FrameStep : ActionIds.FrameBackStep);
                    break;
                case WheelAction.Zoom:
                    if (HasVideo) ZoomBy(dir * Presets.ZoomStepLog2, p);
                    break;
                default:
                    SetVolume(_st.Volume + dir * Presets.VolumeStep, true);
                    break;
            }
        }

        // ---- menus ------------------------------------------------------------------------

        private void ShowContextMenu(Point screen)
        {
            if (_contextMenu == null) return;
            ShowCursorNow();
            _preview.Hide();
            MenuBuilder.ShowAt(_contextMenu, screen); // sized for the monitor it opens on, not the cursor's
        }

        // Keyboard (Apps / Shift+F10): at the mouse when it is over the video, else the video centre.
        private void ShowContextMenuAtCursor(bool keyboard)
        {
            Point screen = Cursor.Position;
            Rectangle host = _host.RectangleToScreen(_host.ClientRectangle);
            if (keyboard && !host.Contains(screen)) screen = new Point(host.Left + host.Width / 2, host.Top + host.Height / 2);
            ShowContextMenu(screen);
        }

        private bool AnyMenuOpen()
        {
            return (_contextMenu != null && _contextMenu.Visible) || (_speedMenu != null && _speedMenu.Visible) ||
                   (_repeatMenu != null && _repeatMenu.Visible) || (_zoomMenu != null && _zoomMenu.Visible);
        }

        private void OnMenuClosed()
        {
            _cursorIdleSince = Environment.TickCount;
        }

        // ---- cursor auto-hide -------------------------------------------------------------

        private void OnAnyMouseMove()
        {
            Point p = Cursor.Position;
            if (p == _lastMousePos) return; // Windows synthesises moves on window changes; ignore those
            _lastMousePos = p;
            _cursorIdleSince = Environment.TickCount;
            if (_cursorHidden) ShowCursorNow();
            if (_fullscreen) CheckOverlayTrigger(p);
        }

        private bool ShouldHideCursor()
        {
            if (_closing || !_shown || _modalDepth > 0 || AnyMenuOpen() || MouseButtons != MouseButtons.None) return false;
            if (!_fullscreen && !(_settings.HideCursorWhenIdle && HasMedia && !_st.Paused)) return false;
            if (unchecked(Environment.TickCount - _cursorIdleSince) < CursorHideDelayMs) return false;
            if (ActiveForm != this || !_host.IsHandleCreated || !_host.Visible) return false;
            Point p = Cursor.Position;
            if (!_host.RectangleToScreen(_host.ClientRectangle).Contains(p)) return false;
            // Only when the video itself is under the cursor (not a popup, dialog or the overlay).
            return Native.WindowAt(p) == _host.Handle;
        }

        private void UpdateCursorVisibility()
        {
            if (_cursorTimer == null) return;
            if (ShouldHideCursor()) HideCursorNow();
            else if (_cursorHidden && !CanStayHidden()) ShowCursorNow();
        }

        // Once hidden the cursor stays hidden until it moves, unless the context changed.
        private bool CanStayHidden()
        {
            if (_closing || _modalDepth > 0 || AnyMenuOpen() || ActiveForm != this) return false;
            return _fullscreen || (_settings.HideCursorWhenIdle && HasMedia && !_st.Paused);
        }

        // Cursor.Hide/Show keep a per-thread counter: never show more often than we hid.
        private void HideCursorNow()
        {
            if (_cursorHidden) return;
            Cursor.Hide();
            _cursorHidden = true;
            if (EngineAlive) _mpv.CommandAsync("set", "cursor-autohide", "always");
        }

        private void ShowCursorNow()
        {
            if (!_cursorHidden) return;
            Cursor.Show();
            _cursorHidden = false;
            if (EngineAlive) _mpv.CommandAsync("set", "cursor-autohide", "no");
        }
    }
}
