using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using VidFlow.Media;

namespace VidFlow.Ui
{
    // Side panel listing the playlist (dark, owner-drawn). Docked right by MainForm.
    // Only its filter text box may take keyboard focus; the list itself is mouse-driven.
    // Indices raised in events are always real Playlist indices, whatever the filter shows.
    public sealed class PlaylistPanel : Control
    {
        // Layout, in logical (96-dpi) pixels.
        private const int HeaderH = 44, TitleX = 14, BtnSize = 30, BtnGap = 2, EdgePad = 8;
        private const int FilterMarginX = 10, FilterH = 30, FilterIconW = 30, FilterClearW = 28, ListGap = 8;
        private const int RowH = 30, RowInsetX = 6, BottomPad = 6, Radius = 4;
        private const int ScrollZoneW = 12, ThumbThin = 4, ThumbWide = 8, ThumbMin = 28, ThumbRightPad = 3;
        private const int UserScrollQuietMs = 1500, TipDelayMs = 650, TipDurationMs = 10000, TipMaxW = 460;
        private const string TitleText = "Playlist";
        private const string SortGlyph = "";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Color RowText = Blend(Theme.TextDim, Theme.Text, 0.62f);
        private static readonly Color NumText = Blend(Theme.TextDisabled, Theme.TextDim, 0.45f);
        private static readonly Color GlyphText = Blend(Theme.TextDim, Theme.Text, 0.35f);
        private static readonly Color SelBack = Blend(Theme.Window, Theme.AccentDim, 0.40f);
        private static readonly Color SelHotBack = Blend(Theme.Window, Theme.AccentDim, 0.55f);
        private static readonly Color BorderHot = Blend(Theme.Border, Theme.TextDim, 0.35f);

        private const TextFormatFlags LineFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
        private const TextFormatFlags CenterFlags = LineFlags | TextFormatFlags.HorizontalCenter;

        private enum Part { None, Sort, Close, Filter, FilterClear, Row, List, Thumb, Track }

        private static readonly char[] PathSeparators = { '\\', '/' };
        private static readonly object DescendingTag = new object(); // marks the "Descending" sort menu item

        // Cull decision badges: the palette has no green/red, these match the control bar's cull chip.
        private static readonly Color PickColor = Color.FromArgb(76, 194, 120);
        private static readonly Color RejectColor = Color.FromArgb(232, 88, 88);
        private static readonly Color StarColor = Color.FromArgb(255, 196, 64);
        private const string StarGlyph = "", PickGlyph = "", RejectGlyph = "";
        private static readonly string[] CullFilters = { "all", "picked", "rejected", "undecided", "rated" };

        private Playlist _playlist;
        private readonly FilterBox _filter;
        private readonly ToolTip _tip;
        private readonly Timer _tipTimer, _animTimer, _dragTimer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Action _syncCallback;
        private ContextMenuStrip _rowMenu, _sortMenu;
        private ToolStripMenuItem _miPlay, _miRemove, _miDelete, _miShow, _miSortBy, _miSave, _miClear, _miCullShow;

        // Cull decisions (null provider = no badges, no decision filter).
        private Func<string, CullMark> _markProvider;
        private bool _cullMode;
        private string _cullFilter = "all";
        private int _picked, _rejected, _undecided, _rated;
        private bool _countsDirty = true;

        // Rows: a snapshot of the playlist items, their display names, and the filtered view.
        // Playlist.Changed only marks the snapshot dirty and posts one resync; EnsureSynced() runs it early
        // wherever rows are read to raise indices, so events never carry indices from a stale snapshot.
        private string[] _snapshot = new string[0];
        private string[] _names = new string[0];
        private bool _dirty, _syncPosted;
        // Display names by item, valid for one naming mode (_nameRoot: null = plain file names, else the
        // common folder that rows are shown relative to, "" when the items share none).
        private Dictionary<string, string> _nameCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _nameRoot;
        private readonly List<int> _visible = new List<int>();
        private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.Ordinal);
        private string _anchor;
        private string _lastCurrent;
        private string _menuItem;   // row under the context menu, by path so list changes cannot retarget it

        // Scrolling (device px). _scroll eases towards _scrollTarget.
        private double _scroll, _scrollTarget;
        private long _lastAnimMs, _lastUserScrollMs = -100000, _sortMenuClosedMs = -100000;
        private bool _revealPending;

        // Pointer state.
        private bool _hovering, _filterHover, _sortMenuOpen, _externalDrop;
        private Part _hoverPart, _pressPart;
        private int _hoverRow = -1;
        private bool _leftDown, _collapseOnUp, _reordering, _thumbDrag;
        private Point _downPt;
        private int _downRow = -1, _dropPos = -1, _thumbGrab;
        private double _dragSpeed;

        // Tooltip.
        private string _tipPending, _tipText;
        private Point _tipAt;
        private bool _tipShown;

        // Per-DPI resources.
        private int _dpi;
        private Font _fTitle, _fCount, _fRow, _fRowCur, _fNum, _fIcon, _fIconSmall, _fIconTiny, _fEmptyGlyph, _fBadge;
        private Pen _pDrop;
        private int _numDigits = -1, _numWidth;
        private readonly SolidBrush _bHover = new SolidBrush(Theme.Hover), _bPressed = new SolidBrush(Theme.Pressed);
        private readonly SolidBrush _bRowHot = new SolidBrush(Theme.PanelAlt), _bSel = new SolidBrush(SelBack), _bSelHot = new SolidBrush(SelHotBack);
        private readonly SolidBrush _bField = new SolidBrush(Theme.PanelAlt), _bAccent = new SolidBrush(Theme.Accent);
        private readonly SolidBrush _bThumb = new SolidBrush(Theme.Track), _bThumbHot = new SolidBrush(Theme.Buffered);
        private readonly SolidBrush _bThumbDrag = new SolidBrush(Theme.TextDim), _bTrack = new SolidBrush(Theme.Panel);
        private readonly SolidBrush _bDropTint = new SolidBrush(Color.FromArgb(22, Theme.Accent));
        private readonly SolidBrush _bSeparator = new SolidBrush(Theme.Panel);
        private readonly Pen _pBorder = new Pen(Theme.Border), _pBorderHot = new Pen(BorderHot), _pAccent = new Pen(Theme.Accent);

        public PlaylistPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            AllowDrop = true;

            _filter = new FilterBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Text,
                Placeholder = "Filter",
                PlaceholderColor = Theme.TextDim,
                TabStop = false,
            };
            _filter.TextChanged += OnFilterTextChanged;
            _filter.Command += OnFilterCommand;
            _filter.GotFocus += (s, e) => Invalidate(FilterRect);
            _filter.LostFocus += (s, e) => Invalidate(FilterRect);
            _filter.MouseEnter += (s, e) => { _filterHover = true; Invalidate(FilterRect); };
            _filter.MouseLeave += (s, e) => { _filterHover = false; Invalidate(FilterRect); };
            Controls.Add(_filter);

            _tip = new ToolTip { OwnerDraw = true, UseAnimation = false, UseFading = false, ShowAlways = true };
            _tip.Popup += OnTipPopup;
            _tip.Draw += OnTipDraw;
            _tipTimer = new Timer { Interval = TipDelayMs };
            _tipTimer.Tick += OnTipTimer;
            _animTimer = new Timer { Interval = 15 };
            _animTimer.Tick += OnAnimTick;
            _dragTimer = new Timer { Interval = 15 };
            _dragTimer.Tick += OnDragTick;
            _syncCallback = OnSyncPosted;

            ApplyDpi();
        }

        public Playlist Playlist
        {
            get { return _playlist; }
            set
            {
                if (ReferenceEquals(_playlist, value)) return;
                if (_playlist != null) _playlist.Changed -= OnPlaylistChanged;
                _playlist = value;
                if (_playlist != null) _playlist.Changed += OnPlaylistChanged;
                _selected.Clear();
                _anchor = null;
                _snapshot = new string[0];
                _lastCurrent = null;
                Sync();
            }
        }

        public event Action<int> ItemActivated;
        public event Action<int[]> RemoveRequested;     // ascending real indices
        public event Action<int[]> DeleteRequested;     // ascending real indices (local files only)
        public event Action<int> ShowInFolderRequested;
        public event Action<string[]> AppendRequested;
        public event Action CloseRequested;
        public event Action SortChanged;                // user picked a sort/order here (Playlist already updated)
        public event Action SaveRequested;              // "Save List As…" in the row menu

        public bool IsEditingFilter { get { return _filter.Focused; } }

        // Gives keyboard focus back the way Esc in the filter does (no-op when not editing).
        public void EndFilterEditing()
        {
            if (_filter.Focused) OnFilterCommand(Keys.Escape);
        }

        // ---- cull decisions ----

        // Asked for each row's decision while painting and filtering (must be a cheap lookup).
        // Null hides the badges and the decision filter.
        public Func<string, CullMark> MarkProvider
        {
            get { return _markProvider; }
            set
            {
                if (ReferenceEquals(_markProvider, value)) return;
                _markProvider = value;
                RefreshMarks();
            }
        }

        // Cull mode: the header shows "12 ✓ · 5 ✕ · 23 left" instead of "3 / 40".
        public bool CullMode
        {
            get { return _cullMode; }
            set
            {
                if (_cullMode == value) return;
                _cullMode = value;
                // The decision filter only applies in cull mode.
                if (_cullFilter != "all") RefreshMarks();
                else Invalidate(new Rectangle(0, 0, Width, S(HeaderH)));
            }
        }

        // Decision filter: all | picked | rejected | undecided | rated (applied together with the text filter).
        public string CullFilter
        {
            get { return _cullFilter; }
            set
            {
                string v = NormalizeCullFilter(value);
                if (v == _cullFilter) return;
                _cullFilter = v;
                _filter.Placeholder = v == "all" ? "Filter" : "Filter · " + CullFilterLabel(v);
                _filter.Invalidate();
                RefreshMarks();
                if (_playlist != null)
                {
                    int pos = _visible.IndexOf(_playlist.Index);
                    if (pos >= 0) EnsureRowVisible(pos, true, false);
                    else ScrollTo(0, false);
                }
            }
        }

        // The user picked a decision filter in the panel's menu (MainForm applies it via CullFilter).
        public event Action<string> CullFilterRequested;

        // Decisions changed: re-filter, recount and repaint.
        public void RefreshMarks()
        {
            _countsDirty = true;
            if (IsDisposed) return;
            EnsureSynced();
            RebuildVisible();
            ClampScroll();
            RefreshHover();
            Invalidate();
        }

        // Rows currently shown (after the text and decision filters).
        public int VisibleCount
        {
            get
            {
                EnsureSynced();
                return _visible.Count;
            }
        }

        // The cull summary the header shows in cull mode, e.g. "12 ✓ · 5 ✕ · 23 left".
        public string SummaryText
        {
            get
            {
                EnsureSynced();
                EnsureCounts();
                return _picked.ToString(Inv) + " ✓ · " + _rejected.ToString(Inv) + " ✕ · " + _undecided.ToString(Inv) + " left";
            }
        }

        private static string NormalizeCullFilter(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(CullFilters, v) >= 0 ? v : "all";
        }

        private static string CullFilterLabel(string filter)
        {
            switch (filter)
            {
                case "picked": return "picks only";
                case "rejected": return "rejects only";
                case "undecided": return "undecided only";
                case "rated": return "rated only";
                default: return "all clips";
            }
        }

        private CullMark MarkOf(int real)
        {
            if (_markProvider == null || real < 0 || real >= _snapshot.Length) return CullMark.None;
            try { return _markProvider(_snapshot[real]); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return CullMark.None;
            }
        }

        // The decision filter in effect: none outside cull mode or without decisions.
        private string ActiveCullFilter
        {
            get { return _cullMode && _markProvider != null ? _cullFilter : "all"; }
        }

        private bool PassesCullFilter(int real)
        {
            string filter = ActiveCullFilter;
            if (filter == "all") return true;
            var m = MarkOf(real);
            switch (filter)
            {
                case "picked": return m.IsPick;
                case "rejected": return m.IsReject;
                case "undecided": return m.Flag == 0;
                case "rated": return m.Rating > 0;
                default: return true;
            }
        }

        private void EnsureCounts()
        {
            if (!_countsDirty) return;
            _countsDirty = false;
            _picked = _rejected = _undecided = _rated = 0;
            for (int i = 0; i < _snapshot.Length; i++)
            {
                var m = MarkOf(i);
                if (m.IsPick) _picked++;
                else if (m.IsReject) _rejected++;
                else _undecided++;
                if (m.Rating > 0) _rated++;
            }
        }

        // ---- playlist sync ----

        private void OnPlaylistChanged()
        {
            if (IsDisposed) return;
            _dirty = true;
            if (_syncPosted || !IsHandleCreated) return; // without a handle: synced on creation or first use
            try
            {
                BeginInvoke(_syncCallback);
                _syncPosted = true;
            }
            catch (InvalidOperationException)
            {
                Sync(); // handle being destroyed
            }
        }

        private void OnSyncPosted()
        {
            _syncPosted = false;
            EnsureSynced();
        }

        private void EnsureSynced()
        {
            if (_dirty && !IsDisposed) Sync();
        }

        private void Sync()
        {
            _dirty = false;
            string[] items = _playlist != null ? ToArray(_playlist.Items) : new string[0];
            string current = _playlist != null ? _playlist.Current : null;
            bool fresh = current != null && Array.IndexOf(_snapshot, current) < 0;
            if (!SameItems(items, _snapshot))
            {
                _snapshot = items;
                RebuildNames(items);
                _countsDirty = true;
            }
            RebuildVisible();

            if (!string.Equals(current, _lastCurrent, StringComparison.Ordinal))
            {
                _lastCurrent = current;
                // A new list jumps straight to its current item; otherwise follow playback gently,
                // but never yank the view while the user is pointing at or scrolling the list.
                if (fresh) RevealCurrent(true);
                else if (!UserBusy) RevealCurrent(false);
            }
            ClampScroll();
            RefreshHover();
            Invalidate();
        }

        private void RebuildVisible()
        {
            _visible.Clear();
            string f = _filter.Text.Trim();
            for (int i = 0; i < _names.Length; i++)
                if ((f.Length == 0 || _names[i].IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) && PassesCullFilter(i)) _visible.Add(i);

            // Selection never includes hidden rows, so menu actions only touch what the user can see.
            if (_selected.Count > 0 || _anchor != null)
            {
                var shown = new HashSet<string>(StringComparer.Ordinal);
                foreach (int i in _visible) shown.Add(_snapshot[i]);
                _selected.IntersectWith(shown);
                if (_anchor != null && !shown.Contains(_anchor)) _anchor = null;
            }
        }

        // Names come from the cache when the naming mode is unchanged, so an edit to a large list only
        // derives names for new items; the cache is rebuilt each time so removed items do not linger.
        private void RebuildNames(string[] items)
        {
            string root = CommonRoot(items);
            var old = string.Equals(root, _nameRoot, StringComparison.Ordinal) ? _nameCache : null;
            var cache = new Dictionary<string, string>(items.Length, StringComparer.Ordinal);
            var names = new string[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i], name;
                if (!cache.TryGetValue(item, out name))
                {
                    if (old == null || !old.TryGetValue(item, out name)) name = DisplayName(item, root);
                    cache[item] = name;
                }
                names[i] = name;
            }
            _names = names;
            _nameCache = cache;
            _nameRoot = root;
        }

        private void RevealCurrent(bool center)
        {
            if (_playlist == null) return;
            int pos = _visible.IndexOf(_playlist.Index);
            if (pos < 0) return;
            if (ListRect.Height <= 0) { _revealPending = true; return; }
            EnsureRowVisible(pos, center, !center);
        }

        private bool UserBusy
        {
            get
            {
                return _hovering || _reordering || _thumbDrag ||
                       _clock.ElapsedMilliseconds - _lastUserScrollMs < UserScrollQuietMs;
            }
        }

        // ---- geometry ----

        private int S(int px) { return Theme.Scale(this, px); }
        private int RowHeight { get { return Math.Max(1, S(RowH)); } }
        private int ListTop { get { return S(HeaderH) + S(FilterH) + S(ListGap); } }
        private Rectangle ListRect { get { return new Rectangle(0, ListTop, Width, Math.Max(0, Height - ListTop)); } }
        private int ContentHeight { get { return _visible.Count == 0 ? 0 : _visible.Count * RowHeight + S(BottomPad); } }
        private double MaxScroll { get { return Math.Max(0, ContentHeight - ListRect.Height); } }
        private bool ScrollBarShown { get { return MaxScroll > 0; } }
        private int RowLeft { get { return S(RowInsetX); } }
        private int RowRight { get { return Width - (ScrollBarShown ? S(ScrollZoneW) + S(2) : S(RowInsetX)); } }

        private Rectangle CloseRect
        {
            get { int b = S(BtnSize); return new Rectangle(Width - S(EdgePad) - b, (S(HeaderH) - b) / 2, b, b); }
        }

        private Rectangle SortRect
        {
            get { var c = CloseRect; return new Rectangle(c.X - S(BtnGap) - c.Width, c.Y, c.Width, c.Height); }
        }

        private Rectangle FilterRect
        {
            get { int m = S(FilterMarginX); return new Rectangle(m, S(HeaderH), Math.Max(0, Width - 2 * m), S(FilterH)); }
        }

        private Rectangle ClearRect
        {
            get { var f = FilterRect; int w = S(FilterClearW); return new Rectangle(f.Right - w, f.Y, w, f.Height); }
        }

        private Rectangle TrackRect
        {
            get
            {
                var l = ListRect;
                int w = S(ScrollZoneW);
                return new Rectangle(Width - w, l.Top + S(2), w, Math.Max(0, l.Height - S(4)));
            }
        }

        private Rectangle ThumbRect
        {
            get
            {
                var t = TrackRect;
                double max = MaxScroll;
                if (max <= 0 || t.Height <= 0) return Rectangle.Empty;
                int h = (int)Math.Round(t.Height * (double)ListRect.Height / ContentHeight);
                h = Math.Min(t.Height, Math.Max(S(ThumbMin), h));
                int y = t.Y + (int)Math.Round((t.Height - h) * Math.Min(1, Math.Max(0, _scroll / max)));
                return new Rectangle(t.X, y, t.Width, h);
            }
        }

        private int RowAt(int y)
        {
            int pos = (int)Math.Floor((y - ListTop + _scroll) / RowHeight);
            return pos >= 0 && pos < _visible.Count ? pos : -1;
        }

        private Part HitTest(Point p, out int row)
        {
            row = -1;
            if (CloseRect.Contains(p)) return Part.Close;
            if (SortRect.Contains(p)) return Part.Sort;
            var f = FilterRect;
            if (f.Contains(p)) return _filter.TextLength > 0 && ClearRect.Contains(p) ? Part.FilterClear : Part.Filter;
            var l = ListRect;
            if (!l.Contains(p)) return Part.None;
            if (ScrollBarShown && p.X >= Width - S(ScrollZoneW))
            {
                var t = ThumbRect;
                return p.Y >= t.Top && p.Y < t.Bottom ? Part.Thumb : Part.Track;
            }
            row = RowAt(p.Y);
            if (row >= 0 && p.X >= RowLeft && p.X < RowRight) return Part.Row;
            row = -1;
            return Part.List;
        }

        // ---- scrolling ----

        private void ScrollTo(double target, bool animate)
        {
            _scrollTarget = Math.Max(0, Math.Min(MaxScroll, target));
            if (!animate || !IsHandleCreated || !Visible)
            {
                _animTimer.Stop();
                _scroll = _scrollTarget;
                RefreshHover();
                Invalidate();
                return;
            }
            if (!_animTimer.Enabled)
            {
                _lastAnimMs = _clock.ElapsedMilliseconds;
                _animTimer.Start();
            }
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            long now = _clock.ElapsedMilliseconds;
            double dt = Math.Max(1, now - _lastAnimMs);
            _lastAnimMs = now;
            double diff = _scrollTarget - _scroll;
            if (Math.Abs(diff) < 0.5)
            {
                _scroll = _scrollTarget;
                _animTimer.Stop();
            }
            else
            {
                // Frame-rate independent exponential ease (~70 ms time constant).
                _scroll += diff * (1 - Math.Exp(-dt / 70.0));
            }
            RefreshHover();
            Invalidate();
        }

        private void ClampScroll()
        {
            double max = MaxScroll;
            if (_scrollTarget > max) _scrollTarget = max;
            if (_scroll > max) _scroll = max;
            if (_scrollTarget < 0) _scrollTarget = 0;
            if (_scroll < 0) _scroll = 0;
        }

        private void EnsureRowVisible(int pos, bool center, bool animate)
        {
            int rh = RowHeight;
            double view = ListRect.Height;
            double top = pos * (double)rh, bottom = top + rh;
            double cur = _scrollTarget;
            if (top >= cur && bottom <= cur + view) return;
            double target;
            if (center) target = top - (view - rh) / 2;
            else
            {
                // Leave a row of context when it fits, so the item is not glued to the edge.
                double margin = Math.Max(0, Math.Min(rh, (view - rh) / 2));
                target = top < cur ? top - margin : bottom - view + margin;
            }
            ScrollTo(target, animate);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            EnsureSynced();
            if (!ScrollBarShown) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            double step = lines > 0 ? lines * RowHeight : ListRect.Height - RowHeight;
            _lastUserScrollMs = _clock.ElapsedMilliseconds;
            ScrollTo(_scrollTarget - e.Delta / 120.0 * step, true);
        }

        // ---- painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureSynced();
            var g = e.Graphics;
            g.Clear(Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            PaintHeader(g);
            PaintFilterFrame(g);
            PaintList(g);
        }

        private void PaintHeader(Graphics g)
        {
            int h = S(HeaderH);
            var sort = SortRect;
            int x = S(TitleX), limit = sort.X - S(6);
            if (limit > x)
            {
                int tw = TextRenderer.MeasureText(g, TitleText, _fTitle, Size.Empty, LineFlags).Width;
                TextRenderer.DrawText(g, TitleText, _fTitle, new Rectangle(x, 0, Math.Min(tw, limit - x), h), Theme.Text,
                    LineFlags | TextFormatFlags.EndEllipsis);
                // The count is shown whole or not at all; "3 /…" reads as noise.
                int cx = x + tw + S(8);
                if (_cullMode && _markProvider != null && _snapshot.Length > 0)
                {
                    PaintCullSummary(g, cx, limit, h);
                }
                else
                {
                    string count = CountText();
                    if (count.Length > 0)
                    {
                        int cw = TextRenderer.MeasureText(g, count, _fCount, Size.Empty, LineFlags).Width;
                        if (cx + cw <= limit)
                            TextRenderer.DrawText(g, count, _fCount, new Rectangle(cx, S(1), cw, h), Theme.TextDim, LineFlags);
                    }
                }
            }
            PaintButton(g, sort, SortGlyph, _fIcon, Part.Sort, _sortMenuOpen);
            PaintButton(g, CloseRect, Glyphs.Close, _fIconSmall, Part.Close, false);
        }

        // Icon glyphs use grayscale anti-aliasing: ClearType leaves colour fringes on their thin strokes.
        // (TextRenderer picks its font quality from the Graphics' TextRenderingHint.)
        private static void DrawGlyph(Graphics g, string glyph, Font font, Rectangle r, Color color)
        {
            var hint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            TextRenderer.DrawText(g, glyph, font, r, color, CenterFlags);
            g.TextRenderingHint = hint;
        }

        // "12 ✓ · 5 ✕ · 23 left" with coloured glyphs; tighter without separators when narrow; whole or not at all.
        private void PaintCullSummary(Graphics g, int x, int limit, int h)
        {
            EnsureCounts();
            var parts = new List<KeyValuePair<string, Color>>
            {
                new KeyValuePair<string, Color>(_picked.ToString(Inv), Theme.TextDim),
                new KeyValuePair<string, Color>(PickGlyph, PickColor),
                new KeyValuePair<string, Color>(null, Theme.TextDisabled),
                new KeyValuePair<string, Color>(_rejected.ToString(Inv), Theme.TextDim),
                new KeyValuePair<string, Color>(RejectGlyph, RejectColor),
                new KeyValuePair<string, Color>(null, Theme.TextDisabled),
                new KeyValuePair<string, Color>(_undecided.ToString(Inv) + " left", Theme.TextDim),
            };
            for (int pass = 0; pass < 2; pass++)
            {
                string sep = pass == 0 ? "  ·  " : " ";
                int total = 0;
                var widths = new int[parts.Count];
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    bool glyph = p.Key == PickGlyph || p.Key == RejectGlyph;
                    string text = p.Key ?? sep;
                    widths[i] = TextRenderer.MeasureText(g, text, glyph ? _fBadge : _fCount, Size.Empty, LineFlags).Width + (glyph ? S(3) : 0);
                    total += widths[i];
                }
                if (x + total > limit) continue;
                int cx = x;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    bool glyph = p.Key == PickGlyph || p.Key == RejectGlyph;
                    var r = new Rectangle(cx + (glyph ? S(3) : 0), S(1), widths[i] - (glyph ? S(3) : 0), h);
                    if (glyph) DrawGlyph(g, p.Key, _fBadge, r, p.Value);
                    else TextRenderer.DrawText(g, p.Key ?? sep, _fCount, r, p.Value, LineFlags);
                    cx += widths[i];
                }
                return;
            }
        }

        private string CountText()
        {
            if (_playlist == null || _playlist.Count == 0) return "";
            return (_playlist.Index + 1).ToString(Inv) + " / " + _playlist.Count.ToString(Inv);
        }

        private void PaintButton(Graphics g, Rectangle r, string glyph, Font font, Part part, bool active)
        {
            if (r.X < S(TitleX)) return;
            bool hot = _hoverPart == part;
            bool down = active || (hot && _pressPart == part);
            if (hot || down)
                using (var path = RoundRect(r, S(Radius))) g.FillPath(down ? _bPressed : _bHover, path);
            DrawGlyph(g, glyph, font, r, hot || down ? Theme.Text : GlyphText);
        }

        private void PaintFilterFrame(Graphics g)
        {
            var f = FilterRect;
            if (f.Width < S(FilterIconW)) return;
            float radius = S(Radius);
            using (var path = RoundRect(f, radius))
            {
                g.FillPath(_bField, path);
                bool hot = _filterHover || _hoverPart == Part.Filter || _hoverPart == Part.FilterClear;
                using (var edge = RoundRect(new RectangleF(f.X + 0.5f, f.Y + 0.5f, f.Width - 1, f.Height - 1), radius))
                    g.DrawPath(hot && !_filter.Focused ? _pBorderHot : _pBorder, edge);
                if (_filter.Focused)
                {
                    // Fluent-style focus: an accent underline over the bottom edge, clipped to the rounded field.
                    var state = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    int u = Math.Max(2, S(2));
                    g.FillRectangle(_bAccent, f.X, f.Bottom - u, f.Width, u);
                    g.Restore(state);
                }
            }

            DrawGlyph(g, Glyphs.Search, _fIconSmall, new Rectangle(f.X, f.Y, S(FilterIconW), f.Height), Theme.TextDim);

            if (_filter.TextLength > 0)
            {
                var c = ClearRect;
                bool clearHot = _hoverPart == Part.FilterClear;
                if (clearHot)
                {
                    int d = S(20);
                    var dot = new Rectangle(c.X + (c.Width - d) / 2, c.Y + (c.Height - d) / 2, d, d);
                    using (var path = RoundRect(dot, S(Radius))) g.FillPath(_pressPart == Part.FilterClear ? _bPressed : _bHover, path);
                }
                DrawGlyph(g, Glyphs.Close, _fIconTiny, c, clearHot ? Theme.Text : Theme.TextDim);
            }
        }

        private void PaintList(Graphics g)
        {
            var l = ListRect;
            if (l.Height <= 0 || l.Width <= 0) return;
            if (_scroll >= 1)
            {
                // Hairline under the filter while rows are scrolled beneath it.
                int y = l.Top - S(ListGap) / 2;
                g.FillRectangle(_bSeparator, S(RowInsetX), y, Width - 2 * S(RowInsetX), 1);
            }
            var state = g.Save();
            g.SetClip(l);
            if (_visible.Count == 0)
            {
                PaintEmpty(g, l);
            }
            else
            {
                int rh = RowHeight;
                int sc = (int)Math.Round(_scroll);
                int first = Math.Max(0, sc / rh), last = Math.Min(_visible.Count - 1, (sc + l.Height) / rh);
                int left = RowLeft, right = RowRight;
                int numW = NumberWidth(g);
                int current = _playlist != null ? _playlist.Index : -1;
                for (int pos = first; pos <= last; pos++)
                    PaintRow(g, pos, new Rectangle(left, l.Top + pos * rh - sc, right - left, rh), numW, current);

                if (_reordering && _dropPos >= 0)
                {
                    int y = l.Top + _dropPos * rh - sc;
                    int d = S(6);
                    g.DrawLine(_pDrop, left + d, y, right, y);
                    g.FillEllipse(_bAccent, left, y - d / 2, d, d);
                }
            }
            if (_externalDrop)
            {
                var r = new Rectangle(l.X + S(4), l.Y + S(2), l.Width - S(8), l.Height - S(6));
                using (var path = RoundRect(r, S(6))) g.FillPath(_bDropTint, path);
                using (var path = RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(6))) g.DrawPath(_pAccent, path);
            }
            g.Restore(state);
            PaintScrollBar(g);
        }

        private void PaintRow(Graphics g, int pos, Rectangle r, int numW, int current)
        {
            if (r.Width <= 0) return;
            int real = _visible[pos];
            bool isCurrent = real == current;
            bool selected = _selected.Contains(_snapshot[real]);
            bool hot = !_reordering && !_thumbDrag && _hoverPart == Part.Row && _hoverRow == pos;

            Brush back = selected ? (hot ? _bSelHot : _bSel) : hot ? _bRowHot : null;
            if (back != null)
                using (var path = RoundRect(new Rectangle(r.X, r.Y + S(1), r.Width, r.Height - S(2)), S(Radius)))
                    g.FillPath(back, path);

            if (isCurrent)
            {
                int bh = S(16), bw = Math.Max(2, S(3));
                var bar = new Rectangle(r.X, r.Y + (r.Height - bh) / 2, bw, bh);
                using (var path = RoundRect(bar, bw / 2f)) g.FillPath(_bAccent, path);
            }

            var numRect = new Rectangle(r.X + S(10), r.Y, numW, r.Height);
            TextRenderer.DrawText(g, (real + 1).ToString(Inv), _fNum, numRect, isCurrent ? Theme.Accent : NumText,
                LineFlags | TextFormatFlags.Right);

            int nx = numRect.Right + S(10);
            int right = r.Right - S(8);
            var mark = MarkOf(real);
            if (!mark.IsEmpty) right = PaintBadges(g, mark, r, right, nx);
            var nameRect = new Rectangle(nx, r.Y, right - nx, r.Height);
            if (nameRect.Width > 0)
                TextRenderer.DrawText(g, _names[real], isCurrent ? _fRowCur : _fRow, nameRect,
                    isCurrent || selected ? Theme.Text : RowText, LineFlags | TextFormatFlags.EndEllipsis);
        }

        // Right-aligned decision badges (stars, then the pick/reject glyph); returns where the name must end.
        // Badges give way first to a readable name when the panel is narrow.
        private int PaintBadges(Graphics g, CullMark mark, Rectangle row, int right, int nameLeft)
        {
            int flagW = mark.Flag != 0 ? S(16) : 0;
            int starW = S(10);
            int stars = mark.Rating;
            int minName = S(60);
            while (stars > 0 && right - flagW - stars * starW - S(4) - nameLeft < minName) stars--;
            if (flagW > 0 && right - flagW - S(4) - nameLeft < minName) flagW = 0;
            int x = right;
            if (flagW > 0)
            {
                x -= flagW;
                DrawGlyph(g, mark.IsPick ? PickGlyph : RejectGlyph, _fBadge, new Rectangle(x, row.Y, flagW, row.Height), mark.IsPick ? PickColor : RejectColor);
            }
            if (stars > 0)
            {
                x -= S(2);
                for (int i = 0; i < stars; i++)
                {
                    x -= starW;
                    DrawGlyph(g, StarGlyph, _fIconTiny, new Rectangle(x, row.Y, starW, row.Height), StarColor);
                }
            }
            return x == right ? right : x - S(6);
        }

        private void PaintEmpty(Graphics g, Rectangle l)
        {
            bool filtered = _filter.TextLength > 0 && _snapshot.Length > 0;
            string line1 = filtered ? "No matches" : "Playlist is empty";
            string line2 = filtered ? "Nothing here contains “" + _filter.Text.Trim() + "”" : "Drop videos here to add them";
            if (!filtered && _snapshot.Length > 0 && ActiveCullFilter != "all")
            {
                filtered = true;
                switch (_cullFilter)
                {
                    case "picked": line1 = "No picks yet"; break;
                    case "rejected": line1 = "No rejects"; break;
                    case "undecided": line1 = "Every clip is decided"; break;
                    default: line1 = "No rated clips"; break;
                }
                line2 = "Showing " + CullFilterLabel(_cullFilter) + " · right-click to show all";
            }
            int gh = filtered ? 0 : S(40), h1 = S(22), h2 = S(20);
            int top = l.Top + Math.Max(S(16), (Math.Min(l.Height, S(260)) - gh - h1 - h2) / 2);
            int w = l.Width - 2 * S(16);
            if (w <= 0) return;
            if (!filtered)
                DrawGlyph(g, Glyphs.Playlist, _fEmptyGlyph, new Rectangle(S(16), top, w, gh), Theme.TextDisabled);
            TextRenderer.DrawText(g, line1, _fRow, new Rectangle(S(16), top + gh, w, h1), Theme.TextDim, CenterFlags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, line2, _fNum, new Rectangle(S(16), top + gh + h1, w, h2), NumText, CenterFlags | TextFormatFlags.EndEllipsis);
        }

        private void PaintScrollBar(Graphics g)
        {
            if (!ScrollBarShown) return;
            var t = TrackRect;
            var th = ThumbRect;
            if (t.Height <= 0 || th.Height <= 0) return;
            bool wide = _thumbDrag || _hoverPart == Part.Thumb || _hoverPart == Part.Track;
            int w = S(wide ? ThumbWide : ThumbThin);
            int x = t.Right - S(ThumbRightPad) - w;
            if (wide)
                using (var path = RoundRect(new Rectangle(x, t.Y, w, t.Height), w / 2f)) g.FillPath(_bTrack, path);
            var thumb = new Rectangle(x, th.Y, w, th.Height);
            using (var path = RoundRect(thumb, w / 2f))
                g.FillPath(_thumbDrag ? _bThumbDrag : wide ? _bThumbHot : _bThumb, path);
        }

        private int NumberWidth(Graphics g)
        {
            int digits = Math.Max(2, _snapshot.Length.ToString(Inv).Length);
            if (digits != _numDigits)
            {
                _numDigits = digits;
                _numWidth = TextRenderer.MeasureText(g, new string('0', digits), _fNum, Size.Empty, LineFlags).Width;
            }
            return _numWidth;
        }

        // ---- mouse ----

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovering = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            EnsureSynced();
            _hovering = true;
            if (_thumbDrag)
            {
                DragThumb(e.Y);
                return;
            }
            if (_leftDown && !_reordering && _downRow >= 0 && _downRow < _visible.Count &&
                _selected.Contains(_snapshot[_visible[_downRow]]))
            {
                var drag = SystemInformation.DragSize;
                if (Math.Abs(e.X - _downPt.X) > drag.Width || Math.Abs(e.Y - _downPt.Y) > drag.Height)
                {
                    _reordering = true;
                    _collapseOnUp = false;
                    HideTip();
                }
            }
            if (_reordering)
            {
                UpdateDrop(e.Location);
                return;
            }
            UpdateHover(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_reordering || _thumbDrag) return;
            _hovering = false;
            _hoverPart = Part.None;
            _hoverRow = -1;
            HideTip();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            EnsureSynced();
            int row;
            var part = HitTest(e.Location, out row);
            if (part != Part.Filter && part != Part.FilterClear) ReleaseFilterFocus();
            HideTip();

            if (e.Button == MouseButtons.Left)
            {
                switch (part)
                {
                    case Part.Sort:
                        // The press that dismissed the open drop-down must not reopen it: the button toggles.
                        if (_clock.ElapsedMilliseconds - _sortMenuClosedMs < 250) break;
                        _pressPart = part;
                        Invalidate();
                        break;
                    case Part.Close:
                    case Part.FilterClear:
                        _pressPart = part;
                        Invalidate();
                        break;
                    case Part.Filter:
                        _filter.Focus();
                        _filter.SelectAll();
                        break;
                    case Part.Thumb:
                        _thumbDrag = true;
                        _thumbGrab = e.Y - ThumbRect.Y;
                        _lastUserScrollMs = _clock.ElapsedMilliseconds;
                        Invalidate();
                        break;
                    case Part.Track:
                        _lastUserScrollMs = _clock.ElapsedMilliseconds;
                        double page = Math.Max(RowHeight, ListRect.Height - RowHeight);
                        ScrollTo(_scrollTarget + (e.Y < ThumbRect.Y ? -page : page), true);
                        break;
                    case Part.Row:
                        SelectOnPress(row, ModifierKeys);
                        _leftDown = true;
                        _downPt = e.Location;
                        _downRow = row;
                        break;
                    case Part.List:
                        if ((ModifierKeys & (Keys.Control | Keys.Shift)) == 0 && _selected.Count > 0)
                        {
                            _selected.Clear();
                            Invalidate();
                        }
                        break;
                }
            }
            else if (e.Button == MouseButtons.Right && part == Part.Row)
            {
                string item = _snapshot[_visible[row]];
                if (!_selected.Contains(item))
                {
                    _selected.Clear();
                    _selected.Add(item);
                    _anchor = item;
                    Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            EnsureSynced();
            int row;
            var part = HitTest(e.Location, out row);
            if (e.Button == MouseButtons.Left)
            {
                if (_thumbDrag)
                {
                    _thumbDrag = false;
                    Invalidate();
                }
                else if (_reordering)
                {
                    FinishReorder();
                }
                else if (_collapseOnUp && _downRow >= 0 && _downRow < _visible.Count)
                {
                    string item = _snapshot[_visible[_downRow]];
                    _selected.Clear();
                    _selected.Add(item);
                    _anchor = item;
                    Invalidate();
                }
                else if (_pressPart != Part.None)
                {
                    var pressed = _pressPart;
                    _pressPart = Part.None;
                    Invalidate();
                    if (part == pressed) ClickPart(pressed);
                }
                ResetPointerDrag();
                UpdateHover(e.Location);
            }
            else if (e.Button == MouseButtons.Right && (part == Part.Row || part == Part.List))
            {
                ShowRowMenu(e.Location, part == Part.Row ? _visible[row] : -1);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            EnsureSynced();
            int row;
            if (HitTest(e.Location, out row) != Part.Row) return;
            ResetPointerDrag();
            var h = ItemActivated;
            if (h != null) h(_visible[row]);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // Capture lost mid-gesture (Alt+Tab, a popup): abandon it rather than apply a half drag.
            if (!Capture && (_reordering || _thumbDrag || _leftDown || _pressPart != Part.None))
            {
                ResetPointerDrag();
                _thumbDrag = false;
                _pressPart = Part.None;
                Invalidate();
            }
        }

        private void ResetPointerDrag()
        {
            _leftDown = false;
            _collapseOnUp = false;
            _reordering = false;
            _downRow = -1;
            _dropPos = -1;
            _dragTimer.Stop();
        }

        private void ClickPart(Part part)
        {
            switch (part)
            {
                case Part.Sort:
                    ShowSortMenu();
                    break;
                case Part.Close:
                    var h = CloseRequested;
                    if (h != null) h();
                    break;
                case Part.FilterClear:
                    _filter.Clear();
                    break;
            }
        }

        private void SelectOnPress(int row, Keys mods)
        {
            string item = _snapshot[_visible[row]];
            bool ctrl = (mods & Keys.Control) != 0, shift = (mods & Keys.Shift) != 0;
            _collapseOnUp = false;
            if (shift && _anchor != null)
            {
                int a = VisiblePosOf(_anchor);
                if (a < 0) a = row;
                if (!ctrl) _selected.Clear();
                for (int i = Math.Min(a, row); i <= Math.Max(a, row); i++) _selected.Add(_snapshot[_visible[i]]);
            }
            else if (ctrl)
            {
                if (!_selected.Remove(item)) _selected.Add(item);
                _anchor = item;
            }
            else
            {
                // Pressing inside a multi-selection keeps it until release, so the whole block can be dragged.
                if (_selected.Contains(item) && _selected.Count > 1) _collapseOnUp = true;
                else
                {
                    _selected.Clear();
                    _selected.Add(item);
                }
                _anchor = item;
            }
            Invalidate();
        }

        private int VisiblePosOf(string item)
        {
            for (int i = 0; i < _visible.Count; i++)
                if (string.Equals(_snapshot[_visible[i]], item, StringComparison.Ordinal)) return i;
            return -1;
        }

        private int[] SelectedIndices()
        {
            var list = new List<int>();
            foreach (int i in _visible) if (_selected.Contains(_snapshot[i])) list.Add(i);
            return list.ToArray(); // _visible is ascending, so this is too
        }

        private void UpdateHover(Point p)
        {
            int row;
            var part = HitTest(p, out row);
            if (part != _hoverPart || row != _hoverRow)
            {
                _hoverPart = part;
                _hoverRow = row;
                Invalidate();
            }
            SetHoverTip(part, row);
        }

        private void RefreshHover()
        {
            if (!_hovering || !IsHandleCreated || _reordering) return;
            var p = PointToClient(Cursor.Position);
            if (ClientRectangle.Contains(p)) UpdateHover(p);
        }

        private void DragThumb(int y)
        {
            var t = TrackRect;
            var th = ThumbRect;
            int range = t.Height - th.Height;
            if (range <= 0) return;
            _lastUserScrollMs = _clock.ElapsedMilliseconds;
            ScrollTo((y - _thumbGrab - t.Y) / (double)range * MaxScroll, false);
        }

        // ---- row reordering ----

        private void UpdateDrop(Point p)
        {
            var l = ListRect;
            int rh = RowHeight;
            int pos = (int)Math.Floor((p.Y - l.Top + _scroll + rh / 2.0) / rh);
            pos = Math.Max(0, Math.Min(_visible.Count, pos));
            if (p.X < 0 || p.X >= Width) pos = -1; // dragged off the side: releasing there cancels
            if (pos != _dropPos)
            {
                _dropPos = pos;
                Invalidate();
            }
            // Auto-scroll when the pointer nears (or passes) the list edges.
            int zone = S(28);
            if (p.Y < l.Top + zone) _dragSpeed = -(l.Top + zone - p.Y);
            else if (p.Y > l.Bottom - zone) _dragSpeed = p.Y - (l.Bottom - zone);
            else _dragSpeed = 0;
            if (_dragSpeed != 0 && pos >= 0 && ScrollBarShown)
            {
                if (!_dragTimer.Enabled)
                {
                    _lastAnimMs = _clock.ElapsedMilliseconds;
                    _dragTimer.Start();
                }
            }
            else _dragTimer.Stop();
        }

        private void OnDragTick(object sender, EventArgs e)
        {
            if (!_reordering) { _dragTimer.Stop(); return; }
            EnsureSynced();
            long now = _clock.ElapsedMilliseconds;
            double dt = Math.Max(1, now - _lastAnimMs);
            _lastAnimMs = now;
            double speed = Math.Max(-S(60), Math.Min(S(60), _dragSpeed)) * 14; // px per second
            ScrollTo(_scroll + speed * dt / 1000.0, false);
            UpdateDrop(PointToClient(Cursor.Position));
        }

        private void FinishReorder()
        {
            int drop = _dropPos;
            ResetPointerDrag();
            Invalidate();
            if (_playlist == null || drop < 0 || _visible.Count == 0) return;
            int target = drop < _visible.Count ? _visible[drop] : _visible[_visible.Count - 1] + 1;
            var moving = SelectedIndices();
            // One batch move (one Changed): the selection lands, in order, just before the row it was dropped on.
            if (moving.Length > 0) _playlist.MoveBlock(moving, target);
        }

        // ---- menus ----

        private void EnsureMenus()
        {
            if (_rowMenu != null) return;
            _rowMenu = new ContextMenuStrip();
            _miPlay = new ToolStripMenuItem("Play", null, (s, e) => RaiseMenuItem(ItemActivated));
            _miRemove = new ToolStripMenuItem("Remove from List", null, (s, e) => RaiseRows(RemoveRequested, SelectedIndices));
            _miDelete = new ToolStripMenuItem("Delete File to Recycle Bin…", null, (s, e) => RaiseRows(DeleteRequested, () => LocalOnly(SelectedIndices())));
            _miShow = new ToolStripMenuItem("Show in Folder", null, (s, e) => RaiseMenuItem(ShowInFolderRequested));
            _miSortBy = new ToolStripMenuItem("Sort by");
            AddSortItems(_miSortBy.DropDownItems);
            _miSave = new ToolStripMenuItem("Save List As…", null, (s, e) => Raise(SaveRequested));
            _miClear = new ToolStripMenuItem("Clear List", null, (s, e) => RaiseRows(RemoveRequested, AllIndices));
            _miCullShow = new ToolStripMenuItem("Show");
            foreach (string f in CullFilters)
            {
                string id = f;
                string text = id == "all" ? "All Clips" : id == "picked" ? "Picks" : id == "rejected" ? "Rejects" : id == "undecided" ? "Undecided" : "Rated";
                _miCullShow.DropDownItems.Add(new ToolStripMenuItem(text, null, (s, e) => RaiseCullFilter(id)) { Tag = id });
            }
            _rowMenu.Items.AddRange(new ToolStripItem[]
            {
                _miPlay, new ToolStripSeparator(), _miRemove, _miDelete, _miShow, new ToolStripSeparator(),
                _miSortBy, _miCullShow, _miSave, new ToolStripSeparator(), _miClear,
            });
            MenuBuilder.ApplyTheme(_rowMenu);
            MenuBuilder.ApplyTheme(_miSortBy.DropDown);
            MenuBuilder.ApplyTheme(_miCullShow.DropDown);

            _sortMenu = new ContextMenuStrip();
            AddSortItems(_sortMenu.Items);
            MenuBuilder.ApplyTheme(_sortMenu);
            _sortMenu.Opened += (s, e) => { _sortMenuOpen = true; Invalidate(); };
            _sortMenu.Closed += (s, e) =>
            {
                _sortMenuOpen = false;
                _sortMenuClosedMs = _clock.ElapsedMilliseconds;
                _hoverPart = Part.None;
                RefreshHover();
                Invalidate();
            };
        }

        private void AddSortItems(ToolStripItemCollection items)
        {
            AddSortItem(items, "Name", PlaylistSort.Name);
            AddSortItem(items, "Date modified", PlaylistSort.DateModified);
            AddSortItem(items, "Size", PlaylistSort.Size);
            AddSortItem(items, "Type", PlaylistSort.Type);
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem("Descending", null, (s, e) => PickOrder()) { Tag = DescendingTag });
        }

        private void AddSortItem(ToolStripItemCollection items, string text, PlaylistSort sort)
        {
            items.Add(new ToolStripMenuItem(text, null, (s, e) => PickSort(sort)) { Tag = sort });
        }

        private void PickSort(PlaylistSort sort)
        {
            if (_playlist == null) return;
            _playlist.Sort = sort;
            Raise(SortChanged);
        }

        private void PickOrder()
        {
            if (_playlist == null) return;
            _playlist.Descending = !_playlist.Descending;
            Raise(SortChanged);
        }

        private void UpdateSortChecks(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                if (mi.Tag is PlaylistSort) mi.Checked = _playlist != null && (PlaylistSort)mi.Tag == _playlist.Sort;
                else if (mi.Tag == DescendingTag) mi.Checked = _playlist != null && _playlist.Descending;
                else continue;
                mi.Enabled = _playlist != null;
            }
        }

        private void ShowRowMenu(Point at, int real)
        {
            EnsureMenus();
            _menuItem = real >= 0 ? _snapshot[real] : null;
            var sel = SelectedIndices();
            bool remote = real >= 0 && Playlist.IsUrl(_snapshot[real]);
            _miPlay.Enabled = real >= 0;
            _miRemove.Enabled = sel.Length > 0;
            _miDelete.Enabled = LocalOnly(sel).Length > 0;
            _miShow.Enabled = real >= 0 && !remote;
            _miClear.Enabled = _snapshot.Length > 0;
            _miSave.Enabled = _snapshot.Length > 0;
            UpdateSortChecks(_miSortBy.DropDownItems);
            _miSortBy.Enabled = _playlist != null;
            _miCullShow.Visible = _markProvider != null && _cullMode;
            foreach (ToolStripItem it in _miCullShow.DropDownItems)
            {
                var mi = it as ToolStripMenuItem;
                if (mi != null) mi.Checked = string.Equals(mi.Tag as string, _cullFilter, StringComparison.Ordinal);
            }
            MenuBuilder.ShowAt(_rowMenu, PointToScreen(at));
        }

        private void RaiseCullFilter(string filter)
        {
            var h = CullFilterRequested;
            if (h != null) h(filter);
            else CullFilter = filter;
        }

        private void ShowSortMenu()
        {
            EnsureMenus();
            UpdateSortChecks(_sortMenu.Items);
            // Right-aligned under the button: its left edge comes from the menu's current width.
            var r = SortRect;
            var corner = PointToScreen(new Point(r.Right, r.Bottom + S(2)));
            MenuBuilder.ShowAt(_sortMenu, new Point(corner.X - _sortMenu.PreferredSize.Width, corner.Y));
        }

        private int[] LocalOnly(int[] indices)
        {
            var list = new List<int>();
            foreach (int i in indices) if (i >= 0 && i < _snapshot.Length && !Playlist.IsUrl(_snapshot[i])) list.Add(i);
            return list.ToArray();
        }

        private int[] AllIndices()
        {
            var all = new int[_snapshot.Length];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            return all;
        }

        // Menu actions run after the menu loop, during which the playlist may have changed: resync first and
        // resolve rows by path, never by a position remembered from when the menu opened.
        private void RaiseMenuItem(Action<int> handler)
        {
            EnsureSynced();
            int index = _menuItem != null ? Array.IndexOf(_snapshot, _menuItem) : -1;
            if (index >= 0) Raise(handler, index);
        }

        private void RaiseRows(Action<int[]> handler, Func<int[]> rows)
        {
            EnsureSynced();
            RaiseIndices(handler, rows());
        }

        private static void Raise(Action<int> handler, int index)
        {
            if (handler != null) handler(index);
        }

        private static void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        private static void RaiseIndices(Action<int[]> handler, int[] indices)
        {
            if (handler != null && indices.Length > 0) handler(indices);
        }

        // ---- filter box ----

        private void OnFilterTextChanged(object sender, EventArgs e)
        {
            EnsureSynced();
            RebuildVisible();
            _selected.Clear();
            _anchor = null;
            if (_filter.TextLength > 0)
            {
                // The top match is pre-selected: Enter plays it, Up/Down move from it.
                if (_visible.Count > 0)
                {
                    _anchor = _snapshot[_visible[0]];
                    _selected.Add(_anchor);
                }
                ScrollTo(0, false);
            }
            else
            {
                ClampScroll();
                if (_playlist != null)
                {
                    int pos = _visible.IndexOf(_playlist.Index);
                    if (pos >= 0) EnsureRowVisible(pos, true, false);
                }
            }
            RefreshHover();
            Invalidate();
        }

        private void OnFilterCommand(Keys key)
        {
            EnsureSynced();
            switch (key)
            {
                case Keys.Escape:
                    _filter.Clear();
                    ReleaseFilterFocus();
                    break;
                case Keys.Enter:
                    var sel = SelectedIndices();
                    int real = sel.Length > 0 ? sel[0] : _visible.Count > 0 ? _visible[0] : -1;
                    if (real < 0) break;
                    ReleaseFilterFocus();
                    Raise(ItemActivated, real);
                    break;
                case Keys.Up: MoveKeySelection(-1); break;
                case Keys.Down: MoveKeySelection(1); break;
                case Keys.PageUp: MoveKeySelection(-Math.Max(1, ListRect.Height / RowHeight - 1)); break;
                case Keys.PageDown: MoveKeySelection(Math.Max(1, ListRect.Height / RowHeight - 1)); break;
            }
        }

        private void MoveKeySelection(int delta)
        {
            if (_visible.Count == 0) return;
            int cur = _anchor != null ? VisiblePosOf(_anchor) : -1;
            if (cur < 0) cur = delta > 0 ? -1 : _visible.Count;
            int next = Math.Max(0, Math.Min(_visible.Count - 1, cur + delta));
            _anchor = _snapshot[_visible[next]];
            _selected.Clear();
            _selected.Add(_anchor);
            EnsureRowVisible(next, false, true);
            Invalidate();
        }

        // Hands keyboard focus back to whatever had it before the filter box (normally the video).
        private void ReleaseFilterFocus()
        {
            if (!_filter.Focused) return;
            var form = FindForm();
            var prev = _filter.PreviousFocus != IntPtr.Zero ? Control.FromHandle(_filter.PreviousFocus) : null;
            if (prev != null && prev != _filter && prev.CanFocus && prev.FindForm() == form)
            {
                prev.Focus();
                if (!_filter.Focused) return;
            }
            if (form == null) return;
            form.ActiveControl = null;
            if (_filter.Focused) form.Focus();
        }

        private void LayoutFilter()
        {
            var f = FilterRect;
            int x = f.X + S(FilterIconW);
            int w = Math.Max(0, f.Right - S(FilterClearW) - x);
            int h = _filter.Height;
            _filter.SetBounds(x, f.Y + (f.Height - h) / 2, w, h);
            _filter.Visible = w > S(16);
        }

        // ---- external drag & drop ----

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            _externalDrop = DropPaths(e.Data) != null;
            e.Effect = _externalDrop ? DragDropEffects.Copy : DragDropEffects.None;
            Invalidate();
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = _externalDrop ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _externalDrop = false;
            Invalidate();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var paths = DropPaths(e.Data);
            _externalDrop = false;
            Invalidate();
            if (paths == null) return;
            // Let Explorer finish the drop before we enumerate folders.
            if (IsHandleCreated) BeginInvoke((Action)(() => RaiseAppend(paths)));
            else RaiseAppend(paths);
        }

        private void RaiseAppend(string[] paths)
        {
            var h = AppendRequested;
            if (h != null && !IsDisposed) h(paths);
        }

        private static string[] DropPaths(IDataObject data)
        {
            if (data == null) return null;
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    return files != null && files.Length > 0 ? files : null;
                }
                if (data.GetDataPresent(DataFormats.UnicodeText))
                {
                    var urls = new List<string>();
                    foreach (var line in (data.GetData(DataFormats.UnicodeText) as string ?? "").Split('\r', '\n'))
                        if (Playlist.IsUrl(line.Trim())) urls.Add(line.Trim());
                    return urls.Count > 0 ? urls.ToArray() : null;
                }
            }
            catch (ExternalException)
            {
                // Some drag sources fail to render their data on request; treat as nothing droppable.
            }
            return null;
        }

        // ---- tooltip ----

        private void SetHoverTip(Part part, int row)
        {
            string text = null;
            Point at = Point.Empty;
            switch (part)
            {
                case Part.Row:
                    text = _snapshot[_visible[row]];
                    var mark = MarkOf(_visible[row]);
                    if (!mark.IsEmpty)
                        text += "\n" + (mark.IsPick ? "✓ Pick" : mark.IsReject ? "✕ Reject" : "Undecided") +
                                (mark.Rating > 0 ? "  " + new string('★', mark.Rating) + new string('☆', 5 - mark.Rating) : "");
                    at = new Point(RowLeft + S(10) + _numWidth + S(10) - S(8), ListTop + (row + 1) * RowHeight - (int)Math.Round(_scroll) + S(2));
                    break;
                case Part.Sort:
                    text = "Sort (" + SortName(_playlist != null ? _playlist.Sort : PlaylistSort.Name) +
                           (_playlist != null && _playlist.Descending ? ", descending)" : ")");
                    at = new Point(SortRect.X, SortRect.Bottom + S(4));
                    break;
                case Part.Close:
                    text = "Hide playlist";
                    at = new Point(CloseRect.X, CloseRect.Bottom + S(4));
                    break;
                case Part.FilterClear:
                    text = "Clear filter";
                    at = new Point(ClearRect.X, ClearRect.Bottom + S(4));
                    break;
            }
            if (part == Part.Sort && _sortMenuOpen) text = null;
            if (string.Equals(text, _tipPending, StringComparison.Ordinal)) return;
            HideTip();
            _tipPending = text;
            _tipAt = at;
            if (text != null) _tipTimer.Start();
        }

        private void OnTipTimer(object sender, EventArgs e)
        {
            _tipTimer.Stop();
            if (_tipPending == null || !_hovering || _reordering || !Visible) return;
            _tipText = WrapPath(_tipPending);
            int x = Math.Max(0, Math.Min(_tipAt.X, Width - S(40)));
            _tip.Show(_tipText, this, x, _tipAt.Y, TipDurationMs);
            _tipShown = true;
        }

        private void HideTip()
        {
            _tipTimer.Stop();
            _tipPending = null;
            if (_tipShown)
            {
                _tip.Hide(this);
                _tipShown = false;
            }
        }

        private void OnTipPopup(object sender, PopupEventArgs e)
        {
            var size = TextRenderer.MeasureText(_tipText ?? "", _fRow, Size.Empty, TextFormatFlags.NoPrefix);
            e.ToolTipSize = new Size(size.Width + S(18), size.Height + S(10));
        }

        private void OnTipDraw(object sender, DrawToolTipEventArgs e)
        {
            var r = e.Bounds;
            e.Graphics.FillRectangle(_bField, r);
            e.Graphics.DrawRectangle(_pBorder, r.X, r.Y, r.Width - 1, r.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, _fRow, Rectangle.Inflate(r, -S(9), -S(5)), Theme.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Breaks long paths after separators so the tooltip stays a readable width.
        private string WrapPath(string text)
        {
            int max = S(TipMaxW);
            if (TextRenderer.MeasureText(text, _fRow, Size.Empty, TextFormatFlags.NoPrefix).Width <= max) return text;
            var sb = new StringBuilder();
            var line = new StringBuilder();
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\\' && text[i] != '/') continue;
                string piece = text.Substring(start, Math.Min(text.Length, i + 1) - start);
                start = i + 1;
                if (line.Length > 0 && TextRenderer.MeasureText(line + piece, _fRow, Size.Empty, TextFormatFlags.NoPrefix).Width > max)
                {
                    sb.Append(line).Append('\n');
                    line.Clear();
                }
                line.Append(piece);
            }
            sb.Append(line);
            return sb.ToString();
        }

        private static string SortName(PlaylistSort sort)
        {
            switch (sort)
            {
                case PlaylistSort.DateModified: return "Date modified";
                case PlaylistSort.Size: return "Size";
                case PlaylistSort.Type: return "Type";
                default: return "Name";
            }
        }

        // ---- lifecycle / DPI ----

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpi();
            _syncPosted = false; // a callback posted to an earlier handle is gone
            EnsureSynced();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpi();
        }

        // Fonts follow the same DPI that Theme.Scale uses for layout, so the two can never disagree.
        // Also re-checked on layout: a monitor change always relayouts, even where WinForms raises no DPI event.
        private void ApplyDpi()
        {
            int dpi = Theme.Scale(this, 96);
            if (dpi == _dpi && _fRow != null) return;
            if (_dpi > 0 && dpi != _dpi)
            {
                double k = dpi / (double)_dpi;
                _scroll *= k;
                _scrollTarget *= k;
            }
            _dpi = dpi;
            var old = new IDisposable[] { _fTitle, _fCount, _fRow, _fRowCur, _fNum, _fIcon, _fIconSmall, _fIconTiny, _fEmptyGlyph, _fBadge, _pDrop };
            _fTitle = MakeFont("Segoe UI Semibold", 10.5f);
            _fCount = MakeFont("Segoe UI", 9f);
            _fRow = MakeFont("Segoe UI", 9f);
            _fRowCur = MakeFont("Segoe UI Semibold", 9f);
            _fNum = MakeFont("Segoe UI", 8f);
            _fIcon = MakeFont(Theme.IconFontFamily, 10.5f);
            _fIconSmall = MakeFont(Theme.IconFontFamily, 9f);
            _fIconTiny = MakeFont(Theme.IconFontFamily, 7f);
            _fEmptyGlyph = MakeFont(Theme.IconFontFamily, 22f);
            _fBadge = MakeFont(Theme.IconFontFamily, 8f);
            _pDrop = new Pen(Theme.Accent, Math.Max(2, S(2)));
            _filter.Font = _fRow;
            foreach (var d in old) if (d != null) d.Dispose();
            _numDigits = -1;
            LayoutFilter();
            ClampScroll();
            Invalidate();
        }

        // Pixel-sized fonts, so text follows this control's DPI rather than the Graphics' system DPI.
        private Font MakeFont(string family, float pt)
        {
            return new Font(family, pt * _dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            ApplyDpi();
            LayoutFilter();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
            if (_revealPending && ListRect.Height > 0)
            {
                _revealPending = false;
                RevealCurrent(true);
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) return;
            HideTip();
            ResetPointerDrag();
            _animTimer.Stop();
            _scroll = _scrollTarget;
            _hovering = false;
            _hoverPart = Part.None;
            _hoverRow = -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_playlist != null) _playlist.Changed -= OnPlaylistChanged;
                _playlist = null;
                _tipTimer.Dispose();
                _animTimer.Dispose();
                _dragTimer.Dispose();
                _tip.Dispose();
                _filter.Dispose(); // before the font it uses
                if (_rowMenu != null) _rowMenu.Dispose();
                if (_sortMenu != null) _sortMenu.Dispose();
                foreach (var d in new IDisposable[]
                {
                    _fTitle, _fCount, _fRow, _fRowCur, _fNum, _fIcon, _fIconSmall, _fIconTiny, _fEmptyGlyph, _fBadge, _pDrop,
                    _bHover, _bPressed, _bRowHot, _bSel, _bSelHot, _bField, _bAccent, _bThumb, _bThumbHot, _bThumbDrag,
                    _bTrack, _bDropTint, _bSeparator, _pBorder, _pBorderHot, _pAccent,
                })
                    if (d != null) d.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- helpers ----

        // The folder the local items have in common (with its trailing separator) when they are not all
        // directly in one folder; null when they are (or there are no local items), "" when nothing is shared.
        private static string CommonRoot(string[] items)
        {
            string first = null;
            int len = 0;
            foreach (var item in items)
            {
                if (Playlist.IsUrl(item)) continue;
                int dirLen = item.LastIndexOfAny(PathSeparators) + 1;
                if (first == null)
                {
                    first = item;
                    len = dirLen;
                    continue;
                }
                int max = Math.Min(len, dirLen), k = 0;
                while (k < max && char.ToUpperInvariant(item[k]) == char.ToUpperInvariant(first[k])) k++;
                if (k < len) len = k > 0 ? first.LastIndexOfAny(PathSeparators, k - 1) + 1 : 0;
            }
            if (first == null) return null;
            // Part of a root only ("\\" of two different servers, "\\server\" of two shares) is no shared folder.
            if (len > 0 && len < RootLength(first)) len = 0;
            foreach (var item in items)
                if (!Playlist.IsUrl(item) && item.LastIndexOfAny(PathSeparators) + 1 != len) return first.Substring(0, len);
            return null;
        }

        // File name; relative to 'root' ("sub\name.ext") when the list spans folders; parent folder plus
        // name when the items share no folder at all. URLs verbatim.
        private static string DisplayName(string item, string root)
        {
            if (Playlist.IsUrl(item)) return item;
            int sep = item.LastIndexOfAny(PathSeparators);
            if (root == null) return sep >= 0 && sep < item.Length - 1 ? item.Substring(sep + 1) : item;
            if (root.Length > 0) return item.Substring(root.Length);
            int parent = sep > 0 ? item.LastIndexOfAny(PathSeparators, sep - 1) : -1;
            return parent >= 0 ? item.Substring(parent + 1) : item;
        }

        private static int RootLength(string path)
        {
            try { return (System.IO.Path.GetPathRoot(path) ?? "").Length; }
            catch (ArgumentException) { return 0; }
        }

        private static string[] ToArray(IReadOnlyList<string> items)
        {
            var a = new string[items.Count];
            for (int i = 0; i < a.Length; i++) a[i] = items[i];
            return a;
        }

        private static bool SameItems(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Dark single-line text box: draws its own placeholder, keeps typing away from the form's
        // shortcuts, and remembers which window had focus before it so focus can be handed back.
        private sealed class FilterBox : TextBox
        {
            private const int WM_SETFOCUS = 0x0007, WM_PAINT = 0x000F, EM_GETMARGINS = 0x00D4;

            [DllImport("user32.dll")]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            public string Placeholder { get; set; }
            public Color PlaceholderColor { get; set; }
            public IntPtr PreviousFocus { get; private set; }
            public event Action<Keys> Command;

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Escape:
                    case Keys.Enter:
                    case Keys.Up:
                    case Keys.Down:
                    case Keys.PageUp:
                    case Keys.PageDown:
                        var h = Command;
                        if (h != null) h(keyData);
                        return true;
                    case Keys.Control | Keys.A:
                        SelectAll();
                        return true;
                    case Keys.Control | Keys.Back:
                        DeletePreviousWord();
                        return true;
                }
                Keys code = keyData & Keys.KeyCode;
                bool chord = (keyData & (Keys.Control | Keys.Alt)) != 0;
                bool fkey = code >= Keys.F1 && code <= Keys.F24;
                // Typing and editing chords stay in the box; real shortcuts continue to the form.
                if (fkey || (chord && !IsEditingChord(keyData))) return base.ProcessCmdKey(ref msg, keyData);
                return false;
            }

            private static bool IsEditingChord(Keys keyData)
            {
                if ((keyData & Keys.Alt) != 0) return false;
                switch (keyData & Keys.KeyCode)
                {
                    case Keys.C: case Keys.V: case Keys.X: case Keys.Z: case Keys.Y:
                    case Keys.Left: case Keys.Right: case Keys.Home: case Keys.End:
                    case Keys.Delete: case Keys.Back: case Keys.Insert:
                        return true;
                }
                return false;
            }

            private void DeletePreviousWord()
            {
                if (SelectionLength > 0)
                {
                    SelectedText = "";
                    return;
                }
                int end = SelectionStart, start = end;
                string t = Text;
                while (start > 0 && char.IsWhiteSpace(t[start - 1])) start--;
                while (start > 0 && char.IsLetterOrDigit(t[start - 1])) start--;
                if (start == end && start > 0) start--;
                if (start == end) return;
                Select(start, end - start);
                SelectedText = "";
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                if (TextLength == 0) Invalidate(); // the edit control does not repaint when emptied by typing
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_SETFOCUS) PreviousFocus = m.WParam;
                base.WndProc(ref m);
                if (m.Msg == WM_PAINT && TextLength == 0 && !string.IsNullOrEmpty(Placeholder)) DrawPlaceholder();
            }

            private void DrawPlaceholder()
            {
                int left = (int)((long)SendMessage(Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero) & 0xFFFF);
                using (var g = Graphics.FromHwnd(Handle))
                {
                    var r = ClientRectangle;
                    r.X += left;
                    r.Width -= left;
                    TextRenderer.DrawText(g, Placeholder, Font, r, PlaceholderColor, BackColor,
                        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
                }
            }
        }
    }
}
