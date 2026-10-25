using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace VidFlow.Ui
{
    // The video area. mpv renders into Surface (a disabled child), so every mouse message over
    // the picture lands here instead; MainForm subscribes to the standard mouse events.
    // Presses on the quick start card's buttons are handled here and never reach MainForm.
    public sealed class VideoHost : Panel
    {
        private const int WM_SETCURSOR = 0x0020, HTCLIENT = 1;

        private QuickStartButton _cardPress = QuickStartButton.None;
        private bool _cardPressActive;

        public VideoSurface Surface { get; private set; }

        // A quick start card button was clicked (pressed and released on it).
        public event Action<QuickStartButton> QuickStartClicked;

        public VideoHost()
        {
            BackColor = Theme.Video;
            TabStop = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            Surface = new VideoSurface { Dock = DockStyle.Fill };
            Controls.Add(Surface);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // Consumed here: an unhandled wheel would bubble to the form and act twice.
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }

        private Point ToSurface(Point hostPoint)
        {
            return new Point(hostPoint.X - Surface.Left, hostPoint.Y - Surface.Top);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _cardPressActive = false; // a press whose release never arrived
                var hit = Surface.HitTestQuickStart(ToSurface(e.Location));
                if (hit != QuickStartButton.None)
                {
                    _cardPressActive = true;
                    _cardPress = hit;
                    Surface.PressedQuickStart = hit;
                    return; // a button press is not a click on the video (no play/pause, no drag)
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_cardPressActive && e.Button == MouseButtons.Left)
            {
                var down = _cardPress;
                _cardPressActive = false;
                _cardPress = QuickStartButton.None;
                Surface.PressedQuickStart = QuickStartButton.None;
                if (Capture) Capture = false;
                if (down != QuickStartButton.None && Surface.HitTestQuickStart(ToSurface(e.Location)) == down)
                {
                    var h = QuickStartClicked;
                    if (h != null) h(down);
                }
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = Surface.HitTestQuickStart(ToSurface(e.Location));
            if (_cardPressActive) Surface.PressedQuickStart = hit == _cardPress ? _cardPress : QuickStartButton.None;
            if (Surface.HotQuickStart == hit) return;
            Surface.HotQuickStart = hit;
            // WM_SETCURSOR for this move already ran with the old hover state.
            if (ClientRectangle.Contains(e.Location)) Cursor.Current = hit != QuickStartButton.None ? Cursors.Hand : Cursor;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Surface.HotQuickStart = QuickStartButton.None;
            if (!_cardPressActive) Surface.PressedQuickStart = QuickStartButton.None;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _cardPressActive)
            {
                // Capture taken away mid-press (Alt+Tab, a popup): drop the press.
                _cardPressActive = false;
                _cardPress = QuickStartButton.None;
                Surface.PressedQuickStart = QuickStartButton.None;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // A hand over the card's buttons; MainForm owns the cursor everywhere else.
            if (m.Msg == WM_SETCURSOR && ((int)m.LParam.ToInt64() & 0xFFFF) == HTCLIENT && Surface.HotQuickStart != QuickStartButton.None)
            {
                Cursor.Current = Cursors.Hand;
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        // The monitor DPI of a control's window (see Theme.DpiOf).
        public static int DpiOf(Control c)
        {
            return Theme.DpiOf(c);
        }
    }

    public enum SurfaceMode { Idle, Video, AudioOnly, Error }

    public enum QuickStartButton { None, OpenFolder, GotIt }

    // One line of the quick start card: key caps ("Ctrl+Enter", "1–5") and what they do.
    public sealed class QuickStartRow
    {
        public QuickStartRow(string text, params string[] keys)
        {
            Text = text ?? "";
            Keys = keys ?? new string[0];
        }

        public string Text { get; private set; }
        public string[] Keys { get; private set; }
    }

    // Black panel whose handle is mpv's "wid". Disabled so mouse input falls through to the host.
    // While no video covers it, it paints the idle hint (with clickable recent files), the
    // audio-only title or an error message, centred. On first run the idle view also shows the
    // quick start card (open a folder, the cull keys, "Got it"), hit-tested through the host.
    public sealed class VideoSurface : Panel
    {
        private const string IdleGlyph = "\uE714", ErrorGlyph = "\uE783"; // Segoe MDL2 / Fluent: video, warning
        private const string OpenFolderText = "Open a folder", GotItText = "Got it", ShortCardTitle = "Quick start";
        private const int MaxRecent = 5;
        private const int RecentHeader = -2; // Line.RecentIndex marker for the "Recent" caption (-1 = ordinary line)

        // Quick start card geometry, logical px.
        private const int CardMaxWidth = 360, CardMinWidth = 240, CardPad = 20, CardRadius = 10, TitleGap = 14,
            OpenButtonH = 36, RowsGap = 14, RowH = 28, CapH = 22, CapPadX = 7, CapGap = 4, CapLabelGap = 14,
            DismissGap = 14, DismissH = 30, DismissMinW = 88, HintGapAfterCard = 16;

        private enum LineKind { Text, Glyph, Hint, Card }

        private SurfaceMode _mode = SurfaceMode.Idle;
        private string _title = "", _hint = "", _detail = "";
        private List<string> _recent = new List<string>();
        private int _hotRecent = -1;
        private readonly List<Rectangle> _recentRects = new List<Rectangle>();

        private bool _quickStartVisible;
        private string _quickStartTitle = "Quick start";
        private List<QuickStartRow> _quickStartRows = new List<QuickStartRow>();
        private QuickStartButton _hotCard = QuickStartButton.None, _pressedCard = QuickStartButton.None;
        private Rectangle _cardRect, _openRect, _dismissRect;
        private int _cardRowsShown;

        private int _fontDpi;
        private Font _glyphFont, _titleFont, _hintFont, _smallFont;
        private Font _cardTitleFont, _cardTextFont, _capFont, _buttonFont, _buttonGlyphFont;

        public VideoSurface()
        {
            BackColor = Theme.Video;
            TabStop = false;
            Enabled = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public SurfaceMode Mode
        {
            get { return _mode; }
            set
            {
                if (_mode == value) return;
                _mode = value;
                _hotRecent = -1;
                _hotCard = _pressedCard = QuickStartButton.None;
                if (_mode != SurfaceMode.Idle) ClearCardRects();
                Invalidate();
            }
        }

        // Audio-only: the media title. Error: the headline ("Cannot play clip.mp4").
        public string Title
        {
            get { return _title; }
            set { value = value ?? ""; if (_title != value) { _title = value; Invalidate(); } }
        }

        // Error: the reason line under the headline.
        public string Detail
        {
            get { return _detail; }
            set { value = value ?? ""; if (_detail != value) { _detail = value; Invalidate(); } }
        }

        // Idle: e.g. "Drop videos here · Ctrl+O to open".
        public string Hint
        {
            get { return _hint; }
            set { value = value ?? ""; if (_hint != value) { _hint = value; Invalidate(); } }
        }

        // Idle: recent files (full paths/URLs); the first few are listed dimly.
        public void SetRecentFiles(IList<string> files)
        {
            var list = new List<string>();
            if (files != null)
                foreach (var f in files)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    list.Add(f);
                    if (list.Count == MaxRecent) break;
                }
            bool same = list.Count == _recent.Count;
            for (int i = 0; same && i < list.Count; i++) same = list[i] == _recent[i];
            if (same) return;
            _recent = list;
            _hotRecent = -1;
            Invalidate();
        }

        public int RecentCount { get { return _recent.Count; } }

        // The path shown at a recent-entry index (null when out of range): what a click opens.
        public string RecentPathAt(int index)
        {
            return index >= 0 && index < _recent.Count ? _recent[index] : null;
        }

        // Index of the recent entry under a point (surface client coords), or -1.
        public int HitTestRecent(Point p)
        {
            if (_mode != SurfaceMode.Idle) return -1;
            for (int i = 0; i < _recentRects.Count; i++)
                if (_recentRects[i].Contains(p)) return i;
            return -1;
        }

        public int HotRecent
        {
            get { return _hotRecent; }
            set { if (_hotRecent != value) { _hotRecent = value; Invalidate(); } }
        }

        // ---- quick start card ---------------------------------------------------------------

        // Whether the idle view shows the card (it is still left out when the area is too small).
        public bool QuickStartVisible
        {
            get { return _quickStartVisible; }
            set
            {
                if (_quickStartVisible == value) return;
                _quickStartVisible = value;
                _hotCard = _pressedCard = QuickStartButton.None;
                if (!value) ClearCardRects();
                Invalidate();
            }
        }

        public string QuickStartTitle
        {
            get { return _quickStartTitle; }
            set { value = value ?? ""; if (_quickStartTitle != value) { _quickStartTitle = value; Invalidate(); } }
        }

        public void SetQuickStartRows(IList<QuickStartRow> rows)
        {
            _quickStartRows = rows == null ? new List<QuickStartRow>() : new List<QuickStartRow>(rows);
            Invalidate();
        }

        public int QuickStartRowCount { get { return _quickStartRows.Count; } }

        // Where the card and its buttons were last painted (surface coords); empty when not shown.
        public Rectangle QuickStartBounds { get { return _cardRect; } }
        public Rectangle QuickStartOpenBounds { get { return _openRect; } }
        public Rectangle QuickStartDismissBounds { get { return _dismissRect; } }
        public int QuickStartRowsShown { get { return _cardRect.IsEmpty ? 0 : _cardRowsShown; } }

        // The card button under a point (surface client coords).
        public QuickStartButton HitTestQuickStart(Point p)
        {
            if (_mode != SurfaceMode.Idle || !_quickStartVisible || _cardRect.IsEmpty) return QuickStartButton.None;
            if (_openRect.Contains(p)) return QuickStartButton.OpenFolder;
            if (_dismissRect.Contains(p)) return QuickStartButton.GotIt;
            return QuickStartButton.None;
        }

        public QuickStartButton HotQuickStart
        {
            get { return _hotCard; }
            set { if (_hotCard != value) { _hotCard = value; InvalidateCard(); } }
        }

        public QuickStartButton PressedQuickStart
        {
            get { return _pressedCard; }
            set { if (_pressedCard != value) { _pressedCard = value; InvalidateCard(); } }
        }

        private void InvalidateCard()
        {
            if (_cardRect.IsEmpty) return;
            Rectangle r = _cardRect;
            r.Inflate(2, 2);
            Invalidate(r);
        }

        private void ClearCardRects()
        {
            _cardRect = _openRect = _dismissRect = Rectangle.Empty;
        }

        // ---- fonts & DPI --------------------------------------------------------------------

        private void EnsureFonts()
        {
            int dpi = VideoHost.DpiOf(this);
            if (_glyphFont != null && dpi == _fontDpi) return;
            DisposeFonts();
            _fontDpi = dpi;
            // Pixel units: point fonts would follow the process DPI, not this monitor's.
            _glyphFont = new Font(Theme.IconFontFamily, PxF(40f), FontStyle.Regular, GraphicsUnit.Pixel);
            _titleFont = new Font("Segoe UI Semibold", PxF(17f), FontStyle.Regular, GraphicsUnit.Pixel);
            _hintFont = new Font("Segoe UI", PxF(14f), FontStyle.Regular, GraphicsUnit.Pixel);
            _smallFont = new Font("Segoe UI", PxF(12f), FontStyle.Regular, GraphicsUnit.Pixel);
            _cardTitleFont = new Font("Segoe UI Semibold", PxF(16f), FontStyle.Regular, GraphicsUnit.Pixel);
            _cardTextFont = new Font("Segoe UI", PxF(13f), FontStyle.Regular, GraphicsUnit.Pixel);
            _capFont = new Font("Segoe UI Semibold", PxF(12f), FontStyle.Regular, GraphicsUnit.Pixel);
            _buttonFont = new Font("Segoe UI Semibold", PxF(13f), FontStyle.Regular, GraphicsUnit.Pixel);
            _buttonGlyphFont = new Font(Theme.IconFontFamily, PxF(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            foreach (var f in new[] { _glyphFont, _titleFont, _hintFont, _smallFont, _cardTitleFont, _cardTextFont, _capFont, _buttonFont, _buttonGlyphFont })
                if (f != null) f.Dispose();
            _glyphFont = _titleFont = _hintFont = _smallFont = null;
            _cardTitleFont = _cardTextFont = _capFont = _buttonFont = _buttonGlyphFont = null;
        }

        private int Px(int logical) { return (int)Math.Round(logical * VideoHost.DpiOf(this) / 96.0); }
        private float PxF(float logical) { return logical * VideoHost.DpiOf(this) / 96f; }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
        }

        // ---- painting -----------------------------------------------------------------------

        private struct Line
        {
            public string Text;
            public Font Font;
            public Color Color;
            public int Height;
            public int GapBefore;
            public int RecentIndex;
            public LineKind Kind;
        }

        private const TextFormatFlags CenterFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                                                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        private const TextFormatFlags LeftFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Video);
            _recentRects.Clear();
            ClearCardRects();
            if (_mode == SurfaceMode.Video || Width < 40 || Height < 30) return;
            EnsureFonts();

            int maxW = Math.Max(10, Width - Px(48));
            int avail = Height - Px(16);
            int cardW = Math.Min(Px(CardMaxWidth), Width - Px(32));
            List<Line> lines = null;
            int cardRows = 0;
            if (_mode == SurfaceMode.Idle && _quickStartVisible && cardW >= Px(CardMinWidth))
            {
                cardRows = _quickStartRows.Count;
                lines = BuildLines(true, cardW, cardRows);
                if (!FitCard(g, lines, avail, cardW, ref cardRows)) lines = null;
            }
            if (lines == null)
            {
                lines = BuildLines(false, 0, 0);
                // Drop optional lines (recent files first, then the glyph) until the block fits.
                while (lines.Count > 1 && Total(lines) > avail)
                {
                    int drop = lines.FindLastIndex(l => l.RecentIndex >= 0 || l.RecentIndex == RecentHeader);
                    if (drop < 0) drop = lines.FindIndex(l => l.Kind == LineKind.Glyph);
                    if (drop < 0) break;
                    lines.RemoveAt(drop);
                }
            }
            // A "Recent" caption whose entries were all left out says nothing.
            if (!lines.Exists(l => l.RecentIndex >= 0)) lines.RemoveAll(l => l.RecentIndex == RecentHeader);

            int y = (Height - Total(lines)) / 2;
            foreach (var l in lines)
            {
                y += l.GapBefore;
                if (l.Kind == LineKind.Card)
                {
                    PaintCard(g, new Rectangle((Width - cardW) / 2, y, cardW, l.Height), cardRows);
                    y += l.Height;
                    continue;
                }
                var rect = new Rectangle((Width - maxW) / 2, y, maxW, l.Height);
                Color c = l.Color;
                if (l.RecentIndex >= 0)
                {
                    var sz = TextRenderer.MeasureText(g, l.Text, l.Font, new Size(maxW, l.Height), CenterFlags);
                    int w = Math.Min(maxW, sz.Width + Px(16));
                    var hit = new Rectangle((Width - w) / 2, y, w, l.Height);
                    while (_recentRects.Count < l.RecentIndex) _recentRects.Add(Rectangle.Empty);
                    _recentRects.Add(hit);
                    if (l.RecentIndex == _hotRecent)
                    {
                        using (var b = new SolidBrush(Theme.PanelAlt)) g.FillRectangle(b, hit);
                        c = Theme.Text;
                    }
                }
                TextRenderer.DrawText(g, l.Text, l.Font, rect, c, CenterFlags);
                y += l.Height;
            }
        }

        // Card layout: drops recent files, then the hint below the card, then key rows from the
        // end, until the block fits. False when even the bare card (no rows) does not fit.
        private bool FitCard(Graphics g, List<Line> lines, int avail, int cardW, ref int rows)
        {
            while (Total(lines) > avail)
            {
                int drop = lines.FindLastIndex(l => l.RecentIndex >= 0 || l.RecentIndex == RecentHeader);
                if (drop < 0) drop = lines.FindIndex(l => l.Kind == LineKind.Hint);
                if (drop >= 0)
                {
                    lines.RemoveAt(drop);
                    continue;
                }
                if (rows == 0) return false;
                rows--;
                int card = lines.FindIndex(l => l.Kind == LineKind.Card);
                var cl = lines[card];
                cl.Height = CardHeight(rows);
                lines[card] = cl;
            }
            return true;
        }

        private static int Total(List<Line> lines)
        {
            int t = 0;
            foreach (var l in lines) t += l.GapBefore + l.Height;
            return t;
        }

        private List<Line> BuildLines(bool withCard, int cardW, int cardRows)
        {
            var lines = new List<Line>();
            int glyphH = _glyphFont.Height + Px(4);
            Func<string, Font, Color, int, int, Line> mk = (t, f, c, gap, idx) =>
                new Line { Text = t, Font = f, Color = c, Height = f.Height, GapBefore = gap, RecentIndex = idx, Kind = LineKind.Text };
            switch (_mode)
            {
                case SurfaceMode.AudioOnly:
                {
                    var gl = mk(Glyphs.Audio, _glyphFont, Theme.TextDim, 0, -1);
                    gl.Height = glyphH;
                    gl.Kind = LineKind.Glyph;
                    lines.Add(gl);
                    lines.Add(mk(_title.Length > 0 ? _title : "Audio", _titleFont, Theme.Text, Px(10), -1));
                    break;
                }
                case SurfaceMode.Error:
                {
                    var gl = mk(ErrorGlyph, _glyphFont, Theme.Warning, 0, -1);
                    gl.Height = glyphH;
                    gl.Kind = LineKind.Glyph;
                    lines.Add(gl);
                    lines.Add(mk(_title.Length > 0 ? _title : "Cannot play this file", _titleFont, Theme.Text, Px(10), -1));
                    if (_detail.Length > 0) lines.Add(mk(_detail, _hintFont, Theme.TextDim, Px(4), -1));
                    break;
                }
                default:
                {
                    if (withCard)
                    {
                        lines.Add(new Line { Kind = LineKind.Card, Height = CardHeight(cardRows), RecentIndex = -1 });
                        var hint = mk(_hint, _smallFont, Theme.TextDim, Px(HintGapAfterCard), -1);
                        hint.Kind = LineKind.Hint;
                        if (_hint.Length > 0) lines.Add(hint);
                    }
                    else
                    {
                        var gl = mk(IdleGlyph, _glyphFont, Theme.TextDisabled, 0, -1);
                        gl.Height = glyphH;
                        gl.Kind = LineKind.Glyph;
                        lines.Add(gl);
                        lines.Add(mk(_hint, _hintFont, Theme.TextDim, Px(8), -1));
                    }
                    if (_recent.Count > 0)
                    {
                        lines.Add(mk("Recent", _smallFont, Theme.TextDisabled, Px(withCard ? 18 : 22), RecentHeader));
                        int rowH = _smallFont.Height + Px(8);
                        for (int i = 0; i < _recent.Count; i++)
                        {
                            var l = mk(DisplayName(_recent[i]), _smallFont, Theme.TextDim, i == 0 ? Px(4) : 0, i);
                            l.Height = rowH;
                            lines.Add(l);
                        }
                    }
                    break;
                }
            }
            return lines;
        }

        private int CardHeight(int rows)
        {
            int h = Px(CardPad) + _cardTitleFont.Height + Px(TitleGap) + Px(OpenButtonH);
            if (rows > 0) h += Px(RowsGap) + rows * Px(RowH);
            return h + Px(DismissGap) + Px(DismissH) + Px(CardPad);
        }

        private void PaintCard(Graphics g, Rectangle card, int rows)
        {
            _cardRect = card;
            _cardRowsShown = rows;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                FillRound(g, card, Px(CardRadius), Theme.Panel, Theme.Border);

                int pad = Px(CardPad);
                int x = card.X + pad, w = card.Width - 2 * pad, y = card.Y + pad;
                // A title that counts the keys would be wrong once rows were left out for space.
                string title = rows < _quickStartRows.Count ? ShortCardTitle : _quickStartTitle;
                TextRenderer.DrawText(g, title, _cardTitleFont, new Rectangle(x, y, w, _cardTitleFont.Height), Theme.Text, LeftFlags);
                y += _cardTitleFont.Height + Px(TitleGap);

                // Primary action.
                _openRect = new Rectangle(x, y, w, Px(OpenButtonH));
                Color fill = _pressedCard == QuickStartButton.OpenFolder ? Theme.AccentDim
                           : _hotCard == QuickStartButton.OpenFolder ? Lighten(Theme.Accent, 0.18f) : Theme.Accent;
                FillRound(g, _openRect, Px(6), fill, Color.Empty);
                Color onAccent = Color.FromArgb(14, 14, 14);
                if (_pressedCard == QuickStartButton.OpenFolder) onAccent = Theme.Text;
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                Size glyphSz = TextRenderer.MeasureText(g, Glyphs.Folder, _buttonGlyphFont, Size.Empty, flags);
                Size textSz = TextRenderer.MeasureText(g, OpenFolderText, _buttonFont, Size.Empty, flags);
                int gap = Px(8);
                int contentW = Math.Min(w - Px(16), glyphSz.Width + gap + textSz.Width);
                int cx = _openRect.X + (_openRect.Width - contentW) / 2;
                TextRenderer.DrawText(g, Glyphs.Folder, _buttonGlyphFont, new Rectangle(cx, _openRect.Y, glyphSz.Width, _openRect.Height), onAccent,
                    flags | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, OpenFolderText, _buttonFont,
                    new Rectangle(cx + glyphSz.Width + gap, _openRect.Y, Math.Max(1, contentW - glyphSz.Width - gap), _openRect.Height), onAccent,
                    flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += Px(OpenButtonH);

                // The key rows: caps in a left column, what they do beside them.
                if (rows > 0)
                {
                    y += Px(RowsGap);
                    int capsW = 0;
                    for (int i = 0; i < rows; i++) capsW = Math.Max(capsW, CapsWidth(g, _quickStartRows[i]));
                    capsW = Math.Min(capsW, w / 2);
                    int labelX = x + capsW + Px(CapLabelGap);
                    for (int i = 0; i < rows; i++)
                    {
                        var row = _quickStartRows[i];
                        int capX = x;
                        int capY = y + (Px(RowH) - Px(CapH)) / 2;
                        foreach (var key in row.Keys)
                        {
                            int cw = TextRenderer.MeasureText(g, key, _capFont, Size.Empty, flags).Width + 2 * Px(CapPadX);
                            if (capX + cw > x + capsW) break;
                            var capRect = new Rectangle(capX, capY, cw, Px(CapH));
                            FillRound(g, capRect, Px(4), Theme.PanelAlt, Theme.Border);
                            TextRenderer.DrawText(g, key, _capFont, capRect, Theme.Text, CenterFlags);
                            capX += cw + Px(CapGap);
                        }
                        TextRenderer.DrawText(g, row.Text, _cardTextFont, new Rectangle(labelX, y, Math.Max(1, x + w - labelX), Px(RowH)), Theme.TextDim, LeftFlags);
                        y += Px(RowH);
                    }
                }
                y += Px(DismissGap);

                // Dismiss, bottom right.
                int dw = Math.Max(Px(DismissMinW), TextRenderer.MeasureText(g, GotItText, _buttonFont, Size.Empty, flags).Width + Px(32));
                _dismissRect = new Rectangle(x + w - dw, y, dw, Px(DismissH));
                Color dfill = _pressedCard == QuickStartButton.GotIt ? Theme.Pressed : _hotCard == QuickStartButton.GotIt ? Theme.Hover : Theme.PanelAlt;
                FillRound(g, _dismissRect, Px(6), dfill, Theme.Border);
                TextRenderer.DrawText(g, GotItText, _buttonFont, _dismissRect, Theme.Text, CenterFlags);
            }
            finally
            {
                g.SmoothingMode = oldMode;
            }
        }

        private int CapsWidth(Graphics g, QuickStartRow row)
        {
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int w = 0;
            foreach (var key in row.Keys)
            {
                if (w > 0) w += Px(CapGap);
                w += TextRenderer.MeasureText(g, key, _capFont, Size.Empty, flags).Width + 2 * Px(CapPadX);
            }
            return w;
        }

        private static Color Lighten(Color c, float amount)
        {
            return Color.FromArgb(c.A, c.R + (int)((255 - c.R) * amount), c.G + (int)((255 - c.G) * amount), c.B + (int)((255 - c.B) * amount));
        }

        // Rounded rectangle, filled and (unless border is empty) outlined inside its bounds.
        private static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color border)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            using (var path = RoundRect(new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1), radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (!border.IsEmpty)
                    using (var p = new Pen(border)) g.DrawPath(p, path);
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static string DisplayName(string path)
        {
            if (path.Contains("://")) return path;
            try
            {
                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch (ArgumentException)
            {
                return path;
            }
        }
    }
}
