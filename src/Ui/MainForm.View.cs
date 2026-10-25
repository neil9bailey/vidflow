using System;
using System.Drawing;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    // Fullscreen, the fullscreen hover overlay, zoom/pan maths and window sizing.
    public sealed partial class MainForm
    {
        private bool _fullscreen;
        private Rectangle _fsRestoreBounds;
        private bool _fsRestoreMaximized;
        private Native.WINDOWPLACEMENT _fsPlacement;
        private int _fsStyle;
        private OverlayForm _overlay;
        private int _overlayLastInside;

        // ---- fullscreen -------------------------------------------------------------------

        // Pure Win32 style/placement changes: nothing here recreates a handle, so mpv's "wid"
        // (the surface handle) stays valid.
        private void SetFullscreen(bool on)
        {
            if (on == _fullscreen || !IsHandleCreated || _closing) return;
            IntPtr h = Handle;
            if (on)
            {
                if (WindowState == FormWindowState.Minimized) WindowState = _lastNonMinimizedState;
                _fsRestoreMaximized = WindowState == FormWindowState.Maximized;
                _fsRestoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                _fsPlacement = Native.GetPlacement(h);
                _fsStyle = Native.GetWindowLong(h, Native.GWL_STYLE);
                _fullscreen = true;
                Native.SetWindowLong(h, Native.GWL_STYLE, _fsStyle & ~Native.WS_OVERLAPPEDWINDOW);
                ApplyLayout();
                CoverMonitor();
            }
            else
            {
                _fullscreen = false;
                HideOverlay();
                int cur = Native.GetWindowLong(h, Native.GWL_STYLE);
                Native.SetWindowLong(h, Native.GWL_STYLE, (cur & ~Native.WS_OVERLAPPEDWINDOW) | (_fsStyle & Native.WS_OVERLAPPEDWINDOW));
                Native.SetPlacement(h, _fsPlacement);
                Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_FRAMECHANGED);
                ApplyDpiLayout();
            }
            _bar.Fullscreen = _fullscreen;
            _preview.Hide();
            _cursorIdleSince = Environment.TickCount;
            UpdateCursorVisibility();
            FeatureFullscreenChanged();
            if (_surfaceHandleAtStart != IntPtr.Zero && _host.Surface.Handle != _surfaceHandleAtStart)
                Log.Error("video surface handle changed after a fullscreen toggle");
        }

        private void CoverMonitor()
        {
            if (!_fullscreen || !IsHandleCreated) return;
            Rectangle mon = Screen.FromHandle(Handle).Bounds;
            Native.SetWindowPos(Handle, IntPtr.Zero, mon.X, mon.Y, mon.Width, mon.Height,
                Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_FRAMECHANGED);
        }

        // ---- fullscreen hover overlay -----------------------------------------------------

        // Borderless, never-activating owned window that borrows the control bar.
        private sealed class OverlayForm : Form
        {
            public OverlayForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Theme.Panel;
                Text = AppName;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_MOUSEACTIVATE)
                {
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        private bool OverlayVisible
        {
            get { return _overlay != null && _overlay.Visible; }
        }

        // Called on mouse movement while fullscreen.
        private void CheckOverlayTrigger(Point screen)
        {
            if (!_fullscreen || !_settings.FullscreenControlsOnHover || _modalDepth > 0 || OverlayVisible) return;
            Rectangle mon = Screen.FromHandle(Handle).Bounds;
            if (mon.Contains(screen) && screen.Y >= mon.Bottom - mon.Height * 0.12) ShowOverlay();
        }

        private void ShowOverlay()
        {
            if (_overlay == null) _overlay = new OverlayForm();
            if (_overlay.Owner != this) _overlay.Owner = this;
            Rectangle mon = Screen.FromHandle(Handle).Bounds;
            int h = Math.Max(Px(48), _bar.PreferredBarHeight);
            if (_bar.Parent != _overlay)
            {
                _overlay.Controls.Add(_bar);
                _bar.Dock = DockStyle.Fill;
            }
            _bar.Visible = true;
            _overlay.Bounds = new Rectangle(mon.Left, mon.Bottom - h, mon.Width, h);
            _overlayLastInside = Environment.TickCount;
            if (!_overlay.Visible) _overlay.Show(); // already owned; Show(owner) would re-parent
            ShowCursorNow();
        }

        private void HideOverlay()
        {
            if (_overlay == null) return;
            if (_overlay.Visible) _overlay.Hide();
            if (_bar.Parent == this) return;
            SuspendLayout();
            Controls.Add(_bar);
            _bar.Dock = DockStyle.Bottom;
            // Restore the docking order (see the constructor): host 0, bar 1, splitter 2, playlist 3.
            Controls.SetChildIndex(_bar, 1);
            ResumeLayout(false);
            ApplyLayout();
        }

        // Timer tick: hide the overlay 1 s after the mouse left it (menus from the bar keep it up).
        private void UpdateOverlay()
        {
            if (!OverlayVisible) return;
            if (!_fullscreen || !_settings.FullscreenControlsOnHover) { HideOverlay(); return; }
            Point p = Cursor.Position;
            Rectangle mon = Screen.FromHandle(Handle).Bounds;
            bool inside = _overlay.Bounds.Contains(p) || (mon.Contains(p) && p.Y >= mon.Bottom - mon.Height * 0.12) || AnyMenuOpen();
            if (inside) _overlayLastInside = Environment.TickCount;
            else if (unchecked(Environment.TickCount - _overlayLastInside) > 1000) HideOverlay();
        }

        // ---- zoom & pan -------------------------------------------------------------------

        // Video size in device pixels as displayed at 1:1 (aspect override and rotation applied).
        private Size VideoPixelSize()
        {
            int w = _st.VideoW, h = _st.VideoH;
            if (w <= 0 || h <= 0) return Size.Empty;
            return _st.ParamsRotate % 180 == 90 ? new Size(h, w) : new Size(w, h);
        }

        // Size of the video at video-zoom 0 inside the host (contain, or cover with panscan).
        private SizeF FitSize()
        {
            Size v = VideoPixelSize();
            Size c = _host.ClientSize;
            if (v.IsEmpty || c.Width <= 0 || c.Height <= 0) return SizeF.Empty;
            double sx = c.Width / (double)v.Width, sy = c.Height / (double)v.Height;
            double s = _st.Panscan > 0.5 ? Math.Max(sx, sy) : Math.Min(sx, sy);
            return new SizeF((float)(v.Width * s), (float)(v.Height * s));
        }

        // Displayed width as a percentage of the video's own pixels (0 when unknown).
        private double DisplayedPercent()
        {
            Size v = VideoPixelSize();
            SizeF fit = FitSize();
            if (v.IsEmpty || fit.Width <= 0) return 0;
            return Math.Pow(2, _st.Zoom) * fit.Width / v.Width * 100;
        }

        private bool IsZoomReset
        {
            get { return Math.Abs(_st.Zoom) < 1e-6 && Math.Abs(_st.PanX) < 1e-6 && Math.Abs(_st.PanY) < 1e-6; }
        }

        private void UpdateZoomText()
        {
            string text = "Fit";
            if (!IsZoomReset)
            {
                double pct = DisplayedPercent();
                if (pct <= 0) pct = Math.Pow(2, _st.Zoom) * 100;
                text = Math.Round(pct).ToString("0", Inv) + "%";
            }
            _bar.ZoomText = text;
        }

        // Our copy is updated at once so wheel bursts and drags build on the latest values.
        private void SetZoomPan(double zoom, double panX, double panY)
        {
            if (double.IsNaN(zoom) || double.IsNaN(panX) || double.IsNaN(panY)) return;
            if (Math.Abs(zoom - _st.Zoom) > 1e-9) _mpv.SetProperty("video-zoom", zoom);
            if (Math.Abs(panX - _st.PanX) > 1e-9) _mpv.SetProperty("video-pan-x", panX);
            if (Math.Abs(panY - _st.PanY) > 1e-9) _mpv.SetProperty("video-pan-y", panY);
            _st.Zoom = zoom;
            _st.PanX = panX;
            _st.PanY = panY;
            UpdateZoomText();
        }

        // Zooms by a log2 step, keeping the video point under hostPoint fixed (centre when null).
        private void ZoomBy(double deltaLog2, Point? hostPoint)
        {
            SizeF fit = FitSize();
            if (fit.Width <= 0 || fit.Height <= 0) return;
            double z = _st.Zoom;
            double z2 = Math.Max(Presets.MinZoomLog2, Math.Min(Presets.MaxZoomLog2, z + deltaLog2));
            // Snap accumulated floating error back onto the 25% grid (and exactly onto "fit").
            double steps = z2 / Presets.ZoomStepLog2;
            if (Math.Abs(steps - Math.Round(steps)) < 1e-6) z2 = Math.Round(steps) * Presets.ZoomStepLog2;
            if (Math.Abs(z2 - z) < 1e-9)
            {
                ShowOsd("Zoom " + Math.Round(DisplayedPercent()).ToString("0", Inv) + "% (limit)");
                return;
            }
            double px = 0, py = 0;
            if (hostPoint.HasValue)
            {
                Size c = _host.ClientSize;
                px = hostPoint.Value.X - c.Width / 2.0;
                py = hostPoint.Value.Y - c.Height / 2.0;
            }
            double s = Math.Pow(2, z), s2 = Math.Pow(2, z2);
            double panX = px / (s2 * fit.Width) - px / (s * fit.Width) + _st.PanX;
            double panY = py / (s2 * fit.Height) - py / (s * fit.Height) + _st.PanY;
            SetZoomPan(z2, panX, panY);
            ShowOsd("Zoom " + Math.Round(DisplayedPercent()).ToString("0", Inv) + "%");
        }

        private void ResetZoom()
        {
            SetZoomPan(0, 0, 0);
            ShowOsd("Zoom: fit");
        }

        // percent of the video's own pixels (100 = 1:1). The view centre is kept.
        private void SetPixelZoom(double percent)
        {
            if (!(percent > 0)) throw new ArgumentException("invalid zoom");
            Size v = VideoPixelSize();
            SizeF fit = FitSize();
            if (v.IsEmpty || fit.Width <= 0) return;
            double z = Math.Log(percent / 100.0 * v.Width / fit.Width, 2);
            z = Math.Max(Presets.MinZoomLog2, Math.Min(Presets.MaxZoomLog2, z));
            SetZoomPan(z, _st.PanX, _st.PanY);
            ShowOsd("Zoom " + Math.Round(DisplayedPercent()).ToString("0", Inv) + "%");
        }

        private bool IsPixelZoom(double percent)
        {
            if (!HasVideo || !(percent > 0)) return false;
            return Math.Abs(DisplayedPercent() - percent) < 0.5;
        }

        // PanLeft = (1, 0): the picture moves right so more of its left side shows (mpv convention).
        private void PanStep(int dx, int dy)
        {
            Size c = _host.ClientSize;
            PanByPixels(dx * c.Width * 0.1, dy * c.Height * 0.1);
        }

        private void PanByPixels(double dx, double dy)
        {
            SizeF fit = FitSize();
            if (fit.Width <= 0 || fit.Height <= 0) return;
            double s = Math.Pow(2, _st.Zoom);
            SetZoomPan(_st.Zoom, _st.PanX + dx / (s * fit.Width), _st.PanY + dy / (s * fit.Height));
        }

        // ---- window sizing ----------------------------------------------------------------

        // Client-area space that is not video (control bar, playlist + splitter).
        private Size NonVideoClientSize()
        {
            int w = _playlistPanel.Visible ? _playlistPanel.Width + _splitter.Width : 0;
            int h = _bar.Parent == this && _bar.Visible ? _bar.Height : 0;
            return new Size(w, h);
        }

        // Resizes the window so the video area is vw x vh device px (scaled down to fit
        // maxFraction of the work area, or scaled up to it when upscale is set), keeping the
        // window centre and staying on screen.
        private void ResizeToVideoArea(double vw, double vh, double maxFraction, bool upscale)
        {
            if (vw <= 0 || vh <= 0 || _fullscreen || WindowState != FormWindowState.Normal) return;
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            Size nonClient = Size - ClientSize;
            Size extra = NonVideoClientSize();
            double maxW = wa.Width * maxFraction - nonClient.Width - extra.Width;
            double maxH = wa.Height * maxFraction - nonClient.Height - extra.Height;
            if (maxW < 16 || maxH < 16) return;
            double f = Math.Min(maxW / vw, maxH / vh);
            if (!upscale) f = Math.Min(1, f);
            int w = (int)Math.Round(vw * f) + extra.Width + nonClient.Width;
            int h = (int)Math.Round(vh * f) + extra.Height + nonClient.Height;
            w = Math.Max(w, MinimumSize.Width);
            h = Math.Max(h, MinimumSize.Height);
            Rectangle cur = Bounds;
            int x = cur.Left + (cur.Width - w) / 2, y = cur.Top + (cur.Height - h) / 2;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            Bounds = new Rectangle(x, y, w, h);
        }

        private void MaybeAutoFit()
        {
            Size v = VideoPixelSize();
            bool differs = v.Width != _lastFitW || v.Height != _lastFitH;
            _lastFitW = v.Width;
            _lastFitH = v.Height;
            if (!differs || !_settings.AutoFitWindow || _fullscreen || WindowState != FormWindowState.Normal) return;
            double max = Math.Max(10, Math.Min(100, _settings.AutoFitMaxPercent)) / 100.0;
            ResizeToVideoArea(v.Width, v.Height, max, false);
        }

        private static bool IsWindowSizeArg(string arg)
        {
            return string.Equals(arg, "fit", StringComparison.OrdinalIgnoreCase) || ParseDouble(arg, -1) > 0;
        }

        private void SetWindowSize(string arg)
        {
            if (!IsWindowSizeArg(arg)) throw new ArgumentException("invalid window size '" + arg + "'");
            Size v = VideoPixelSize();
            if (v.IsEmpty)
            {
                // Asked while the file is still opening (e.g. right after Next), or in the moment
                // between files when mpv has dropped the old size and not reported the new one:
                // apply it once the video size is known, unless another file has started by then.
                if (!MayGetVideo) return;
                _deferredWindowSize = arg;
                _deferredWindowSizeGen = _loadGeneration + (FileStartPending ? 1 : 0);
                return;
            }
            if (_fullscreen) SetFullscreen(false);
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            bool fit = string.Equals(arg, "fit", StringComparison.OrdinalIgnoreCase);
            double pct = fit ? 100 : ParseDouble(arg, 100);
            ResizeToVideoArea(v.Width * pct / 100, v.Height * pct / 100, 1.0, fit);
            ShowOsd(fit ? "Window: fit to screen" : "Window " + Math.Round(pct).ToString("0", Inv) + "%");
        }

        // A video size is known, or may still arrive for the file that is loading/loaded
        // (its track list is not in yet, or it has a video track whose parameters are pending).
        private bool MayGetVideo
        {
            // Any video track counts: mpv lists the tracks before it marks one "selected", which
            // used to leave WindowSize disabled for the first ~0.5 s of every clip.
            get { return HasVideo || VideoPending || (HasMedia && (!_tracksSeen || HasAnyVideoTrack())); }
        }

        private bool HasAnyVideoTrack()
        {
            foreach (var t in _st.Tracks)
                if (t.Type == "video" && !t.AlbumArt) return true;
            return false;
        }

        private string _deferredWindowSize;  // WindowSize argument waiting for the video size
        private int _deferredWindowSizeGen;   // load generation of the file it is meant for

        // OnVideoParams / OnStartFileEntry: apply or drop a deferred WindowSize.
        private void ApplyDeferredWindowSize(bool fileStarted)
        {
            if (_deferredWindowSize == null) return;
            if (_loadGeneration > _deferredWindowSizeGen) { _deferredWindowSize = null; return; }
            if (fileStarted || !HasVideo || _loadGeneration != _deferredWindowSizeGen) return;
            string arg = _deferredWindowSize;
            _deferredWindowSize = null;
            SetWindowSize(arg);
        }

        private bool IsWindowSize(string arg)
        {
            if (!HasVideo || _fullscreen || WindowState != FormWindowState.Normal || !IsWindowSizeArg(arg)) return false;
            Size v = VideoPixelSize();
            Size c = _host.ClientSize;
            double tw, th;
            if (string.Equals(arg, "fit", StringComparison.OrdinalIgnoreCase))
            {
                Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                Size nonClient = Size - ClientSize, extra = NonVideoClientSize();
                double f = Math.Min((wa.Width - nonClient.Width - extra.Width) / (double)v.Width,
                                    (wa.Height - nonClient.Height - extra.Height) / (double)v.Height);
                tw = v.Width * f;
                th = v.Height * f;
            }
            else
            {
                double pct = ParseDouble(arg, 0) / 100;
                tw = v.Width * pct;
                th = v.Height * pct;
            }
            return Math.Abs(c.Width - tw) <= 2 && Math.Abs(c.Height - th) <= 2;
        }
    }
}
