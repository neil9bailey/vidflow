using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VidFlow.Ui
{
    // Custom-drawn timeline: a thin rounded track centred in a taller hit area. Played part in
    // the accent colour, buffered band, chapter gaps, A-B loop range and (draggable) markers,
    // review markers above the track, a thumb and a hover indicator while the pointer is over
    // it. Never takes keyboard focus.
    public sealed class SeekBar : Control
    {
        // Logical (96-dpi) sizes.
        private const int BarLogical = 18, TrackLogical = 4, TrackHoverLogical = 6, ThumbLogical = 12;
        private const int InsetLogical = 8, ChapterGapLogical = 2, DragThresholdLogical = 3;
        private const int LoopBandExtraLogical = 4, LoopMarkerWidthLogical = 2, LoopMarkerExtraLogical = 4;
        private const int LoopGrabLogical = 5;                     // a press this close to an A/B marker drags it
        private const int MarkWidthLogical = 7, MarkHeightLogical = 5; // review marker: a small triangle above the track
        private const int HoverLineHeightLogical = 14;
        private const float ThumbShadowLogical = 1.5f;
        private const int AntiAliasPx = 2;                         // repaint margin for soft edges

        private const int ThrottleMs = 33;       // <= ~30 scrub seeks / loop-drag events per second
        private const int HoldMs = 600;          // keep showing a requested time until the player catches up
        private const double HoldTolerance = 0.25;
        private const float AnimMs = 110f;

        private double _duration, _position, _bufferedEnd;
        private IList<double> _chapters, _markers;
        private double? _loopA, _loopB;
        private double _wheelStep = 5;

        // Pointer state. _pressed spans the whole left-button press; _scrubbing starts once the
        // pointer has moved past the drag threshold. A press on an A-B loop marker (_loopDrag)
        // moves that marker instead (_loopMoved once past the threshold) and never seeks.
        private bool _pressed, _scrubbing, _hovering, _hoverRaised;
        private bool _loopDrag, _loopDragB, _loopMoved;
        private int _pressX, _mouseX;
        private float _grabOffset;               // pointer x minus the grabbed marker's x at the press
        private double _scrubTime, _loopDragTime;
        private double _lastSentTime = double.NaN, _lastSentLoop = double.NaN;
        private long _lastSentAt = long.MinValue / 2;
        private int _wheelRemainder;

        // After a click/scrub/wheel the player needs a moment to report the new position;
        // show the requested time meanwhile so the playhead does not jump back. Likewise a
        // dropped loop marker stays where it was dropped until the owner reports the new value.
        private double? _hold, _loopHoldA, _loopHoldB;

        private float _anim;                      // 0 = idle, 1 = hovered/scrubbing
        private long _animLast;
        // What the last paint drew, so a moving playhead or hover line repaints only the strip
        // between its old and new place instead of the whole bar.
        private float _paintedPlayX = float.NaN, _paintedHoverX = float.NaN;
        private bool _paintedBuffered;
        // Set by every invalidation that covers the playhead's move. A paint that is not (an
        // exposed strip, the hover line) redraws the playhead where the screen already shows it.
        private bool _playheadDirty = true;

        // A drop is answered by the owner pushing that end's property while the final
        // LoopPointDragged event runs (0 = not dispatching, 1 = A, 2 = B); such an answer
        // replaces the hold at once, even when the owner adjusted the time.
        private int _finalLoopEnd;
        private bool _finalAnswered;

        // Our own off-screen surface instead of OptimizedDoubleBuffer: the stock one shares a
        // buffer of at most 225x96 px (by area), so a bar wider than ~1000 px got a freshly
        // allocated bar-sized DIB on every paint, even for a strip a few px wide, i.e. once per
        // video frame while playing. This context keeps its surface (MaximumBuffer follows the size).
        private readonly BufferedGraphicsContext _buffer = new BufferedGraphicsContext();

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Timer _throttle = new Timer();
        private readonly Timer _holdTimer = new Timer { Interval = HoldMs };
        private readonly Timer _loopHoldTimer = new Timer { Interval = HoldMs };
        private readonly Timer _animTimer = new Timer { Interval = 15 };

        private readonly SolidBrush _trackBrush = new SolidBrush(Theme.Track);
        private readonly SolidBrush _bufferedBrush = new SolidBrush(Theme.Buffered);
        private readonly SolidBrush _playedBrush = new SolidBrush(Theme.Accent);
        private readonly SolidBrush _loopRangeBrush = new SolidBrush(Theme.LoopRange);
        private readonly SolidBrush _loopMarkerBrush = new SolidBrush(Theme.LoopMarker);
        // Review markers: the accent pulled halfway to white, so they read apart from the orange
        // loop markers, the grey chapter notches and the white thumb.
        private readonly SolidBrush _markBrush = new SolidBrush(Color.FromArgb(
            (Theme.Accent.R + 255) / 2, (Theme.Accent.G + 255) / 2, (Theme.Accent.B + 255) / 2));
        // Alpha of these follows the hover animation; their colour is updated in place.
        private const int ThumbShadowAlpha = 90, HoverLineAlpha = 150;
        private readonly SolidBrush _thumbBrush = new SolidBrush(Theme.Text);
        private readonly SolidBrush _thumbShadowBrush = new SolidBrush(Color.FromArgb(ThumbShadowAlpha, Color.Black));
        private readonly SolidBrush _hoverLineBrush = new SolidBrush(Color.FromArgb(HoverLineAlpha, Theme.Text));
        private SolidBrush _disabledBrush;

        public SeekBar()
        {
            // Double buffered through _buffer (see there), so not OptimizedDoubleBuffer.
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Panel;
            AccessibleRole = AccessibleRole.Slider;
            AccessibleName = "Seek";
            _throttle.Tick += (s, e) => { _throttle.Stop(); SendDragEvent(); };
            _holdTimer.Tick += (s, e) => { ClearHold(); InvalidateIfPlayheadMoved(); };
            _loopHoldTimer.Tick += (s, e) => { ClearLoopHolds(); InvalidateAll(); UpdateCursor(); };
            _animTimer.Tick += (s, e) => StepAnimation();
        }

        // ---- public surface ---------------------------------------------------------------

        public double Duration
        {
            get { return _duration; }
            set
            {
                double v = Finite(value);
                if (v < 0) v = 0;
                if (v == _duration) return;
                _duration = v;
                if (v <= 0)
                {
                    CancelPointer();
                    ClearHold();
                    ClearLoopHolds();
                }
                else if (_pressed)
                {
                    // New scale under a (possibly motionless) pointer: whatever is being dragged
                    // stays under it, and the release commits that time, not a stale one.
                    if (_scrubbing)
                    {
                        _scrubTime = TimeAt(_mouseX);
                        QueueDragEvent();
                    }
                    else if (_loopDrag)
                    {
                        if (_loopMoved)
                        {
                            _loopDragTime = LoopTimeAtPointer();
                            QueueDragEvent();
                        }
                    }
                    else
                    {
                        _scrubTime = Clamp(_scrubTime);
                    }
                    RaiseHover();
                }
                else if (_hoverRaised)
                {
                    RaiseHover(); // same pixel, new time
                }
                UpdateAnimationTarget();
                InvalidateAll();
            }
        }

        public double Position
        {
            get { return _position; }
            set
            {
                double v = Finite(value);
                if (v == _position) return;
                _position = v;
                if (_hold.HasValue && Math.Abs(v - _hold.Value) <= HoldTolerance) ClearHold();
                InvalidateIfPlayheadMoved();
            }
        }

        public double BufferedEnd
        {
            get { return _bufferedEnd; }
            set
            {
                double v = Finite(value);
                if (v == _bufferedEnd) return;
                double old = _bufferedEnd;
                _bufferedEnd = v;
                if (!Active) return;
                // The band shows only ahead of the playhead; while it stays visible just its
                // moving end needs a repaint.
                bool shown = v > DisplayPosition;
                if (shown != _paintedBuffered || float.IsNaN(_paintedPlayX)) InvalidateAll();
                else if (shown) InvalidateSpan(XOf(old), XOf(v), CapReach());
            }
        }

        public IList<double> Chapters
        {
            get { return _chapters; }
            set { _chapters = value; InvalidateAll(); }
        }

        // Review markers (seconds), drawn as small triangles just above the track.
        public IList<double> Markers
        {
            get { return _markers; }
            set { _markers = value; InvalidateAll(); }
        }

        public double? LoopA
        {
            get { return _loopA; }
            set
            {
                if (_finalLoopEnd == 1) _finalAnswered = true;
                if (_loopA == value) return;
                _loopA = value;
                if (!value.HasValue && _loopDrag && !_loopDragB) CancelPointer(); // the marker went away under the drag
                LoopChanged(value, false);
            }
        }

        public double? LoopB
        {
            get { return _loopB; }
            set
            {
                if (_finalLoopEnd == 2) _finalAnswered = true;
                if (_loopB == value) return;
                _loopB = value;
                if (!value.HasValue && _loopDrag && _loopDragB) CancelPointer();
                LoopChanged(value, true);
            }
        }

        public double WheelStepSeconds
        {
            get { return _wheelStep; }
            set { _wheelStep = value; }
        }

        public bool IsScrubbing { get { return _scrubbing; } }

        public event Action<double, bool> SeekRequested;
        public event Action ScrubStarted;
        public event Action ScrubEnded;
        public event Action<double, int, Rectangle> HoverChanged;
        public event Action HoverEnded;
        // (isB, seconds, final): an A-B loop marker was dragged; final=false while dragging
        // (throttled to <= ~30/s), true on release. Such a press never raises SeekRequested.
        public event Action<bool, double, bool> LoopPointDragged;

        public int PreferredBarHeight { get { return Theme.Scale(this, BarLogical); } }

        // ---- geometry ---------------------------------------------------------------------

        protected override Size DefaultSize { get { return new Size(300, BarLogical); } }

        private bool Active { get { return _duration > 0 && Enabled; } }

        private float TrackLeft { get { return Theme.Scale(this, InsetLogical); } }

        private float TrackWidth { get { return Math.Max(0f, Width - 2f * Theme.Scale(this, InsetLogical)); } }

        private float XOf(double t)
        {
            if (_duration <= 0) return TrackLeft;
            double f = Math.Max(0.0, Math.Min(1.0, t / _duration));
            return TrackLeft + (float)(f * TrackWidth);
        }

        private double TimeAt(float x)
        {
            float w = TrackWidth;
            if (w <= 0 || _duration <= 0) return 0;
            double f = (x - TrackLeft) / w;
            return Math.Max(0.0, Math.Min(1.0, f)) * _duration;
        }

        private double Clamp(double t)
        {
            return Math.Max(0.0, Math.Min(_duration, t));
        }

        private static double Finite(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
        }

        // What the playhead shows: the scrub position while pressed, a just-requested time
        // until the player reports it, otherwise the reported position.
        private double DisplayPosition
        {
            get
            {
                if (_pressed && !_loopDrag) return _scrubTime;
                if (_hold.HasValue) return _hold.Value;
                return _position;
            }
        }

        // Where each loop marker is drawn: under the pointer while dragged, then where it was
        // dropped until the owner pushes the new value, otherwise the property.
        private double? ShownLoopA { get { return _loopDrag && !_loopDragB ? _loopDragTime : ValidTime(_loopHoldA ?? _loopA); } }

        private double? ShownLoopB { get { return _loopDrag && _loopDragB ? _loopDragTime : ValidTime(_loopHoldB ?? _loopB); } }

        private static double? ValidTime(double? t)
        {
            return t.HasValue && !double.IsNaN(t.Value) && !double.IsInfinity(t.Value) ? t : null;
        }

        // Horizontal reach around the playhead of everything drawn with it: the full-size thumb
        // and its shadow (wider than the played band's round end), plus anti-aliasing.
        private int PlayheadReach()
        {
            float thumb = Theme.ScaleF(this, ThumbLogical) / 2f + Theme.ScaleF(this, ThumbShadowLogical);
            float cap = Theme.Scale(this, TrackHoverLogical) / 2f;
            return (int)Math.Ceiling(Math.Max(thumb, cap)) + AntiAliasPx;
        }

        // Reach of a capsule's round end (the buffered band's moving edge).
        private int CapReach()
        {
            return (int)Math.Ceiling(Theme.Scale(this, TrackHoverLogical) / 2f) + AntiAliasPx;
        }

        // Full repaint (anything that moves more than the playhead, e.g. a new scale).
        private void InvalidateAll()
        {
            _playheadDirty = true;
            Invalidate();
        }

        private void InvalidateSpan(float a, float b, int reach)
        {
            if (float.IsNaN(a) || float.IsNaN(b)) { InvalidateAll(); return; }
            int x0 = (int)Math.Floor(Math.Min(a, b)) - reach, x1 = (int)Math.Ceiling(Math.Max(a, b)) + reach;
            Invalidate(new Rectangle(x0, 0, x1 - x0, Height));
        }

        // Called on every position push (once per video frame while playing): repaints only the
        // strip the playhead left and entered.
        private void InvalidateIfPlayheadMoved()
        {
            if (!Active) return; // the disabled look does not show the position
            double pos = DisplayPosition;
            if (float.IsNaN(_paintedPlayX) || (_bufferedEnd > pos) != _paintedBuffered)
            {
                InvalidateAll(); // first paint, or the buffered band appears/disappears as a whole
                return;
            }
            float x = XOf(pos);
            if (Math.Abs(x - _paintedPlayX) < 0.25f) return;
            _playheadDirty = true;
            InvalidateSpan(_paintedPlayX, x, PlayheadReach());
        }

        private float HoverLineX(float left, float right, float lw)
        {
            return (float)Math.Round(Math.Max(left, Math.Min(right, _mouseX)) - lw / 2f);
        }

        // The hover line follows the pointer: repaint just its old and new columns.
        private void InvalidateHoverLine()
        {
            if (_animTimer.Enabled) return; // the animation repaints the whole bar anyway
            int lw = Math.Max(1, Theme.Scale(this, 1));
            float x = HoverLineX(TrackLeft, TrackLeft + TrackWidth, lw);
            if (!float.IsNaN(_paintedHoverX)) InvalidateSpan(_paintedHoverX, _paintedHoverX + lw, AntiAliasPx);
            InvalidateSpan(x, x + lw, AntiAliasPx);
        }

        // ---- painting ---------------------------------------------------------------------

        // OnPaint covers every pixel (through the buffer); erasing first would only flicker.
        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle client = ClientRectangle, clip = Rectangle.Intersect(e.ClipRectangle, client);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            // Compared by area: anything larger would go through a throw-away surface again.
            Size max = _buffer.MaximumBuffer;
            if ((long)max.Width * max.Height < (long)client.Width * client.Height) _buffer.MaximumBuffer = client.Size;
            using (BufferedGraphics bg = _buffer.Allocate(e.Graphics, client))
            {
                // Only the clip is redrawn, and only the clip reaches the screen: the paint DC is
                // clipped to the update region.
                bg.Graphics.SetClip(clip);
                PaintBar(bg.Graphics, clip);
                bg.Render(e.Graphics);
            }
        }

        private void PaintBar(Graphics g, Rectangle clip)
        {
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            _paintedHoverX = float.NaN;

            float left = TrackLeft, width = TrackWidth, right = left + width;
            if (width <= 0) return;

            int thinPx = Theme.Scale(this, TrackLogical), thickPx = Theme.Scale(this, TrackHoverLogical);
            // Integer thickness steps keep the track edges on whole pixels (crisp) mid-animation.
            int th = Math.Max(1, (int)Math.Round(thinPx + (thickPx - thinPx) * _anim));
            float top = (float)Math.Floor((Height - th) / 2.0);
            float cy = top + th / 2f;

            if (!Active)
            {
                _paintedPlayX = float.NaN;
                FillCapsule(g, DisabledBrush, left, right, top, th);
                return;
            }

            double pos = DisplayPosition;
            float playX = XOf(pos);
            if (!_playheadDirty && !float.IsNaN(_paintedPlayX)) playX = _paintedPlayX; // < 0.25 px off
            _playheadDirty = false;
            _paintedPlayX = playX;
            _paintedBuffered = _bufferedEnd > pos;

            // A-B loop range: a translucent band slightly taller than the track, behind it, so
            // the track keeps its colours and the range reads as a highlight around it.
            double? loopA = ShownLoopA, loopB = ShownLoopB;
            float bandH = th + Theme.Scale(this, LoopBandExtraLogical);
            float bandTop = (float)Math.Floor(cy - bandH / 2f);
            if (loopA.HasValue && loopB.HasValue)
            {
                float ax = XOf(Math.Min(loopA.Value, loopB.Value)), bx = XOf(Math.Max(loopA.Value, loopB.Value));
                if (bx - ax >= 1f) FillRoundRect(g, _loopRangeBrush, ax, bandTop, bx - ax, bandH, Theme.ScaleF(this, 2f));
            }

            // Saved and restored rather than reset: the paint clip is usually a narrow strip.
            GraphicsState saved = g.Save();
            ExcludeChapterGaps(g, left, right);
            FillCapsule(g, _trackBrush, left, right, top, th);
            if (_paintedBuffered) FillCapsule(g, _bufferedBrush, left, XOf(_bufferedEnd), top, th);
            if (playX > left) FillCapsule(g, _playedBrush, left, playX, top, th);
            g.Restore(saved);

            DrawReviewMarkers(g, clip, thickPx);

            if (loopA.HasValue || loopB.HasValue)
            {
                float mw = Math.Max(1f, Theme.Scale(this, LoopMarkerWidthLogical));
                float mh = bandH + Theme.Scale(this, LoopMarkerExtraLogical);
                float mt = (float)Math.Floor(cy - mh / 2f);
                if (loopA.HasValue) DrawLoopMarker(g, XOf(loopA.Value), mt, mw, mh);
                if (loopB.HasValue) DrawLoopMarker(g, XOf(loopB.Value), mt, mw, mh);
            }

            float eased = EaseOut(_anim);
            if (_hovering && !_pressed && eased > 0.01f)
            {
                float lw = Math.Max(1, Theme.Scale(this, 1));
                float lh = Theme.Scale(this, HoverLineHeightLogical);
                float lx = HoverLineX(left, right, lw);
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                _hoverLineBrush.Color = Color.FromArgb((int)(HoverLineAlpha * eased), Theme.Text);
                g.FillRectangle(_hoverLineBrush, lx, (float)Math.Floor(cy - lh / 2f), lw, lh);
                g.SmoothingMode = old;
                _paintedHoverX = lx;
            }

            if (eased > 0.01f)
            {
                float d = Theme.ScaleF(this, ThumbLogical) * (0.35f + 0.65f * eased);
                float shadow = Theme.ScaleF(this, ThumbShadowLogical);
                _thumbShadowBrush.Color = Color.FromArgb((int)(ThumbShadowAlpha * eased), Color.Black);
                g.FillEllipse(_thumbShadowBrush, playX - d / 2f - shadow, cy - d / 2f - shadow * 0.5f, d + 2 * shadow, d + 2 * shadow);
                _thumbBrush.Color = Color.FromArgb((int)(255 * eased), Theme.Text);
                g.FillEllipse(_thumbBrush, playX - d / 2f, cy - d / 2f, d, d);
            }
        }

        // Chapter starts as small gaps cut through the track (YouTube-style notches). Clipped
        // out rather than painted over, so the loop band behind the track shows through.
        private void ExcludeChapterGaps(Graphics g, float left, float right)
        {
            var list = _chapters;
            if (list == null || list.Count == 0) return;
            int gw = Math.Max(1, Theme.Scale(this, ChapterGapLogical));
            for (int i = 0; i < list.Count; i++)
            {
                double c = list[i];
                if (double.IsNaN(c) || c <= 0 || c >= _duration) continue;
                int x = (int)Math.Round(XOf(c) - gw / 2f);
                if (x - left < gw || right - (x + gw) < gw) continue; // would just nick the rounded ends
                g.SetClip(new Rectangle(x, 0, gw, Height), CombineMode.Exclude);
            }
        }

        // Downward triangles whose tips sit just above the hovered (thick) track, so they keep
        // still while the track animates.
        private void DrawReviewMarkers(Graphics g, Rectangle clip, int thickPx)
        {
            var list = _markers;
            if (list == null || list.Count == 0) return;
            int w = Math.Max(3, Theme.Scale(this, MarkWidthLogical)) | 1; // odd: the tip lands on a pixel centre
            float h = Math.Max(2, Theme.Scale(this, MarkHeightLogical));
            float tip = (float)Math.Floor((Height - thickPx) / 2.0) - Math.Max(1, Theme.Scale(this, 1));
            float top = Math.Max(0f, tip - h);
            if (tip - top < 1f) return; // no room above the track
            var pts = new PointF[3];
            for (int i = 0; i < list.Count; i++)
            {
                double m = list[i];
                if (double.IsNaN(m) || double.IsInfinity(m) || m < 0 || m > _duration) continue;
                float x = (float)Math.Floor(XOf(m)) + 0.5f;
                if (x + w < clip.Left || x - w > clip.Right) continue;
                pts[0] = new PointF(x - w / 2f, top);
                pts[1] = new PointF(x + w / 2f, top);
                pts[2] = new PointF(x, tip);
                g.FillPolygon(_markBrush, pts);
            }
        }

        private void DrawLoopMarker(Graphics g, float x, float top, float w, float h)
        {
            float mx = (float)Math.Round(x - w / 2f);
            FillRoundRect(g, _loopMarkerBrush, mx, top, w, h, w / 2f);
        }

        private static void FillCapsule(Graphics g, Brush b, float x0, float x1, float y, float h)
        {
            float w = x1 - x0;
            if (w <= 0.5f || h <= 0) return;
            if (w <= h) { g.FillEllipse(b, x0, y, w, h); return; }
            using (var p = new GraphicsPath())
            {
                p.AddArc(x0, y, h, h, 90, 180);
                p.AddArc(x1 - h, y, h, h, 270, 180);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        private static void FillRoundRect(Graphics g, Brush b, float x, float y, float w, float h, float r)
        {
            if (w <= 0 || h <= 0) return;
            r = Math.Min(r, Math.Min(w, h) / 2f);
            if (r < 0.5f) { g.FillRectangle(b, x, y, w, h); return; }
            float d = 2 * r;
            using (var p = new GraphicsPath())
            {
                p.AddArc(x, y, d, d, 180, 90);
                p.AddArc(x + w - d, y, d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                p.AddArc(x, y + h - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        private static float EaseOut(float t)
        {
            float u = 1f - t;
            return 1f - u * u * u;
        }

        // Track colour pulled halfway towards the background: visibly "off" without a new colour.
        private SolidBrush DisabledBrush
        {
            get
            {
                if (_disabledBrush == null)
                {
                    Color a = Theme.Track, b = BackColor;
                    _disabledBrush = new SolidBrush(Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2));
                }
                return _disabledBrush;
            }
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            if (_disabledBrush != null) { _disabledBrush.Dispose(); _disabledBrush = null; }
            base.OnBackColorChanged(e);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            _paintedPlayX = _paintedHoverX = float.NaN;
            InvalidateAll();
        }

        protected override void OnResize(EventArgs e)
        {
            // A new width moves every x; the base class repaints the whole bar (ResizeRedraw).
            _paintedPlayX = _paintedHoverX = float.NaN;
            _playheadDirty = true;
            base.OnResize(e);
        }

        // ---- hover animation -------------------------------------------------------------

        private void UpdateAnimationTarget()
        {
            float target = (_hovering || _pressed) && Active ? 1f : 0f;
            if (_anim == target) { _animTimer.Stop(); return; }
            if (!_animTimer.Enabled)
            {
                _animLast = _clock.ElapsedMilliseconds;
                _animTimer.Start();
            }
        }

        private void StepAnimation()
        {
            long now = _clock.ElapsedMilliseconds;
            float step = Math.Max(1, now - _animLast) / AnimMs;
            _animLast = now;
            float target = (_hovering || _pressed) && Active ? 1f : 0f;
            _anim = target > _anim ? Math.Min(target, _anim + step) : Math.Max(target, _anim - step);
            if (_anim == target) _animTimer.Stop();
            InvalidateAll();
        }

        // ---- A-B loop markers --------------------------------------------------------------

        // The A or B marker within grabbing distance of x (the nearer one when both are; on a
        // tie the one on the pointer's side, so coincident markers can still be pulled apart).
        private bool HitLoopMarker(int x, out bool isB, out double t)
        {
            isB = false;
            t = 0;
            double? a = ShownLoopA, b = ShownLoopB;
            float reach = Theme.ScaleF(this, LoopGrabLogical);
            float da = a.HasValue ? Math.Abs(x - XOf(a.Value)) : float.MaxValue;
            float db = b.HasValue ? Math.Abs(x - XOf(b.Value)) : float.MaxValue;
            if (da > reach && db > reach) return false;
            if (db < da || (db == da && x >= XOf(b.Value)))
            {
                isB = true;
                t = b.Value;
            }
            else
            {
                t = a.Value;
            }
            return true;
        }

        // The dragged marker keeps the offset at which it was grabbed, so taking hold of it
        // never makes it jump. The ends never cross: the dragged one stops at the other (the
        // owner may keep them a little further apart, e.g. a frame).
        private double LoopTimeAtPointer()
        {
            double t = TimeAt(_mouseX - _grabOffset);
            double? other = _loopDragB ? ShownLoopA : ShownLoopB;
            if (other.HasValue) t = _loopDragB ? Math.Max(t, other.Value) : Math.Min(t, other.Value);
            return t;
        }

        private void LoopChanged(double? value, bool isB)
        {
            if (!value.HasValue)
            {
                if (isB) _loopHoldB = null;
                else _loopHoldA = null;
            }
            else
            {
                // The owner reported a dropped marker (possibly as the other end, if it swapped
                // an inverted range): stop holding it.
                double tol = Math.Max(0.05, TrackWidth > 0 ? 2.0 * _duration / TrackWidth : 0);
                if (_loopHoldA.HasValue && Math.Abs(value.Value - _loopHoldA.Value) <= tol) _loopHoldA = null;
                if (_loopHoldB.HasValue && Math.Abs(value.Value - _loopHoldB.Value) <= tol) _loopHoldB = null;
            }
            if (!_loopHoldA.HasValue && !_loopHoldB.HasValue) _loopHoldTimer.Stop();
            InvalidateAll();
            UpdateCursor();
        }

        private void SetLoopHold(bool isB, double t)
        {
            if (isB) _loopHoldB = t;
            else _loopHoldA = t;
            _loopHoldTimer.Stop();
            _loopHoldTimer.Start();
        }

        private void ClearLoopHolds()
        {
            _loopHoldA = _loopHoldB = null;
            _loopHoldTimer.Stop();
        }

        // Resize arrow over a grabbable marker (and while dragging one), the normal arrow elsewhere.
        private void UpdateCursor()
        {
            bool isB;
            double t;
            bool resize = _loopDrag || (_hovering && !_pressed && Active && HitLoopMarker(_mouseX, out isB, out t));
            Cursor want = resize ? Cursors.SizeWE : Cursors.Default;
            if (Cursor != want) Cursor = want;
        }

        // ---- mouse ------------------------------------------------------------------------

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!Active) return;
            _hovering = true;
            UpdateAnimationTarget();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_pressed) return; // still dragging with the mouse captured; mouse-up decides
            EndHover();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Active || _pressed) return;
            _pressed = true;
            _scrubbing = false;
            _pressX = _mouseX = e.X;
            _lastSentTime = _lastSentLoop = double.NaN;
            Capture = true;
            _hovering = true;
            UpdateAnimationTarget();
            bool isB;
            double loopT;
            if (HitLoopMarker(e.X, out isB, out loopT))
            {
                // Grabbed an A-B marker: it follows the pointer, nothing seeks.
                _loopDrag = true;
                _loopDragB = isB;
                _loopMoved = false;
                _loopDragTime = loopT;
                _grabOffset = e.X - XOf(loopT);
                if (isB) _loopHoldB = null;
                else _loopHoldA = null;
                UpdateCursor();
                InvalidateAll();
                RaiseHover();
                return;
            }
            _scrubTime = TimeAt(e.X);
            ClearHold();
            InvalidateAll();
            // A click seeks at once (exact); a drag that follows turns into a scrub.
            var h = SeekRequested;
            if (h != null) h(_scrubTime, true);
            if (_pressed) RaiseHover();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Active) return;
            _mouseX = e.X;
            if (_pressed)
            {
                bool past = Math.Abs(e.X - _pressX) >= Theme.Scale(this, DragThresholdLogical);
                if (_loopDrag)
                {
                    if (past) _loopMoved = true;
                    if (_loopMoved)
                    {
                        double t = LoopTimeAtPointer();
                        if (t != _loopDragTime)
                        {
                            _loopDragTime = t;
                            InvalidateAll(); // the marker and the loop band move
                            QueueDragEvent();
                        }
                    }
                }
                else
                {
                    if (!_scrubbing && past)
                    {
                        _scrubbing = true;
                        var started = ScrubStarted;
                        if (started != null) started();
                        if (!_pressed) return; // a handler cancelled the press
                    }
                    if (_scrubbing)
                    {
                        double t = TimeAt(e.X);
                        if (t != _scrubTime)
                        {
                            _scrubTime = t;
                            InvalidateIfPlayheadMoved();
                            QueueDragEvent();
                        }
                    }
                }
            }
            else
            {
                if (!_hovering)
                {
                    _hovering = true;
                    UpdateAnimationTarget();
                }
                InvalidateHoverLine();
                UpdateCursor();
            }
            RaiseHover();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_pressed) return;
            EndPress(e.Location);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // Capture lost mid-drag (Alt+Tab, a dialog): finish the drag where it was.
            if (_pressed && !Capture)
                EndPress(IsHandleCreated ? PointToClient(Cursor.Position) : new Point(-1, -1));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var he = e as HandledMouseEventArgs;
            if (he != null) he.Handled = true; // the bar consumed it; don't let the window also act on it
            if (!Active || _pressed) return;
            double step = _wheelStep;
            if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0) return;
            _wheelRemainder += e.Delta;
            int notches = _wheelRemainder / 120;
            if (notches == 0) return;
            _wheelRemainder -= notches * 120;
            double from = _hold ?? _position; // successive ticks accumulate before the player catches up
            double t = Clamp(from + notches * step);
            if (t == Clamp(from)) return;
            SetHold(t);
            InvalidateIfPlayheadMoved();
            var h = SeekRequested;
            if (h != null) h(t, true);
        }

        // Throttles the in-drag events (scrub seeks or loop-marker moves); the latest value
        // always goes out, at most ThrottleMs after the previous one.
        private void QueueDragEvent()
        {
            long wait = ThrottleMs - (_clock.ElapsedMilliseconds - _lastSentAt);
            if (wait <= 0) SendDragEvent();
            else if (!_throttle.Enabled)
            {
                _throttle.Interval = (int)Math.Max(1, wait);
                _throttle.Start();
            }
            // else: the running timer sends the latest value when it fires
        }

        private void SendDragEvent()
        {
            _throttle.Stop();
            if (_loopDrag)
            {
                if (!_loopMoved || _loopDragTime == _lastSentLoop) return;
                _lastSentLoop = _loopDragTime;
                _lastSentAt = _clock.ElapsedMilliseconds;
                var lh = LoopPointDragged;
                if (lh != null) lh(_loopDragB, _loopDragTime, false);
                return;
            }
            if (!_scrubbing || _scrubTime == _lastSentTime) return;
            _lastSentTime = _scrubTime;
            _lastSentAt = _clock.ElapsedMilliseconds;
            var h = SeekRequested;
            if (h != null) h(_scrubTime, false);
        }

        // Releases a press: commits a scrub with a final exact seek, or a loop-marker drag with
        // its final value. pointer: client coords.
        private void EndPress(Point pointer)
        {
            if (!_pressed) return;
            bool wasScrub = _scrubbing, wasLoop = _loopDrag, loopB = _loopDragB;
            double t = _scrubTime, loopT = _loopDragTime;
            _pressed = false;
            _scrubbing = false;
            _loopDrag = _loopMoved = false;
            _throttle.Stop();
            _lastSentTime = _lastSentLoop = double.NaN;
            if (Capture) Capture = false;
            if (wasLoop)
            {
                if (_duration > 0)
                {
                    // Held until the owner reports the new value; an owner that answers right
                    // away (possibly with an adjusted time) is shown at once.
                    SetLoopHold(loopB, loopT);
                    var lh = LoopPointDragged;
                    if (lh != null)
                    {
                        _finalLoopEnd = loopB ? 2 : 1;
                        _finalAnswered = false;
                        try { lh(loopB, loopT, true); }
                        finally { _finalLoopEnd = 0; }
                        if (_finalAnswered)
                        {
                            if (loopB) _loopHoldB = null;
                            else _loopHoldA = null;
                            if (!_loopHoldA.HasValue && !_loopHoldB.HasValue) _loopHoldTimer.Stop();
                        }
                    }
                }
            }
            else
            {
                if (_duration > 0) SetHold(t);
                if (wasScrub)
                {
                    if (_duration > 0)
                    {
                        var h = SeekRequested;
                        if (h != null) h(t, true);
                    }
                    var ended = ScrubEnded;
                    if (ended != null) ended();
                }
            }
            if (ClientRectangle.Contains(pointer) && Active)
            {
                _mouseX = pointer.X;
                RaiseHover();
            }
            else
            {
                EndHover();
            }
            UpdateAnimationTarget();
            UpdateCursor();
            InvalidateAll();
        }

        // Abandons any press/hover without seeking or committing a loop drag (media unloaded,
        // control hidden/disabled, the dragged marker cleared).
        private void CancelPointer()
        {
            if (_pressed)
            {
                bool wasScrub = _scrubbing;
                _pressed = false;
                _scrubbing = false;
                _loopDrag = _loopMoved = false;
                _throttle.Stop();
                _lastSentTime = _lastSentLoop = double.NaN;
                if (Capture) Capture = false;
                if (wasScrub)
                {
                    var ended = ScrubEnded;
                    if (ended != null) ended();
                }
                InvalidateAll();
            }
            EndHover();
            UpdateAnimationTarget();
        }

        private void RaiseHover()
        {
            if (!Active || !IsHandleCreated) return;
            // While dragging, the preview follows what is dragged (scrub time or loop marker).
            double t = !_pressed ? TimeAt(_mouseX) : _loopDrag ? _loopDragTime : _scrubTime;
            int x = (int)Math.Round(XOf(t));
            int screenX = PointToScreen(new Point(x, 0)).X;
            Rectangle bounds = RectangleToScreen(ClientRectangle);
            _hoverRaised = true;
            var h = HoverChanged;
            if (h != null) h(t, screenX, bounds);
        }

        private void EndHover()
        {
            bool wasHovering = _hovering;
            _hovering = false;
            if (wasHovering) UpdateAnimationTarget();
            if (wasHovering) InvalidateAll();
            UpdateCursor();
            if (!_hoverRaised) return;
            _hoverRaised = false;
            var h = HoverEnded;
            if (h != null) h();
        }

        private void SetHold(double t)
        {
            _hold = t;
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private void ClearHold()
        {
            _hold = null;
            _holdTimer.Stop();
        }

        // ---- lifecycle -----------------------------------------------------------------

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) CancelPointer();
            UpdateAnimationTarget();
            InvalidateAll();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // Hidden (e.g. entering fullscreen) while hovered: the preview must go away too.
            if (!Visible)
            {
                CancelPointer();
                _anim = 0;
                _animTimer.Stop();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            CancelPointer();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _throttle.Dispose();
                _holdTimer.Dispose();
                _loopHoldTimer.Dispose();
                _animTimer.Dispose();
                _buffer.Dispose();
                _trackBrush.Dispose();
                _bufferedBrush.Dispose();
                _playedBrush.Dispose();
                _loopRangeBrush.Dispose();
                _loopMarkerBrush.Dispose();
                _markBrush.Dispose();
                _thumbBrush.Dispose();
                _thumbShadowBrush.Dispose();
                _hoverLineBrush.Dispose();
                if (_disabledBrush != null) _disabledBrush.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
