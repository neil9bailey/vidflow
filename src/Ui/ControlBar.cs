using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    public enum BarButton
    {
        PrevFile, FrameBack, PlayPause, FrameForward, NextFile,
        Repeat, AbLoop, Shuffle, Speed, Zoom, Mute, Playlist, Fullscreen, Menu,
        Cull // the cull chip (only present while CullMode)
    }

    public enum AbLoopState { None, ASet, Active }

    // Bottom control strip (custom drawn, dark). Hosts the SeekBar along its top edge.
    // Purely a view: MainForm pushes state in through the properties and reacts to events.
    // Never takes keyboard focus.
    public sealed class ControlBar : Control
    {
        // ---- logical (96-dpi) metrics ----
        private const int RowHeight = 40;           // button row below the seek bar
        private const int FallbackSeekHeight = 20;  // only if the seek bar reports no preference
        private const int SeekMargin = 10;
        private const int RowMargin = 6;
        private const int ButtonWidth = 36, ButtonHeight = 32;
        private const int PlayWidth = 44, PlayHeight = 36;
        private const int TextButtonPad = 9;
        private const int ReadoutPad = 6;
        private const int VolumeWidth = 86, VolumePad = 7;
        private const int TightGap = 2, GroupGap = 8, ClusterGap = 12;
        private const int CornerRadius = 4;
        private const int MinTimeTextWidth = 14;    // below this the readout is hidden rather than drawn as an ellipsis
        private const float IconPx = 16f, PlayIconPx = 20f, TextPx = 12f;
        private const float FadeInMs = 90f, FadeOutMs = 170f;
        private const int TipDelayMs = 500, TipReshowMs = 40, TipReshowWindowMs = 400;
        private const double VolumeWheelStep = 5;
        private const int MaxHideLevel = 8;
        // While the cull chip is shown it outlasts everything but play/pause and the time readout:
        // the otherwise permanent right-hand buttons give way first (levels 9-13), the chip last.
        private const int CullMaxHideLevel = 14;
        // Cull chip (a pill inside its cell) and the VFR badge.
        private const int ChipHeight = 24, ChipPadX = 8, ChipGap = 5, StarGap = 1;
        private const int BadgeHeight = 16, BadgePadX = 5;
        private const float StarPx = 12f, FlagPx = 14f, BadgeTextPx = 10f;
        // While playing, the frame readout repaints at most this often (about 15 Hz) although its value
        // changes every video frame; paused (stepping), every change repaints at once.
        private const int FrameRepaintMs = 66;

        private const string AbText = "A\u2013B", AbHead = "A\u2013", AbTail = "B";

        // Solid transport glyphs (present in both Segoe Fluent Icons and Segoe MDL2 Assets): the
        // primary controls read stronger filled, next to the outline style of everything else.
        private const string PlaySolid = "\uF5B0", PauseSolid = "\uF8AE", PreviousSolid = "\uF8AC", NextSolid = "\uF8AD";

        // Cull chip: check / cancel / filled star from the icon font, an en dash (text font) when undecided.
        private const string StarGlyph = "\uE735", UndecidedText = "\u2013";
        private const string VfrText = "VFR";
        // Decision colours (the palette has no green/red); the pill takes a faint tint of them.
        private static readonly Color PickColor = Color.FromArgb(88, 200, 120);
        private static readonly Color RejectColor = Color.FromArgb(238, 92, 92);
        private static readonly Color StarColor = Color.FromArgb(255, 196, 64);

        private enum PartKind { Button, Time, Frame, PlaylistInfo, Volume, Badge }

        private sealed class Part
        {
            public readonly PartKind Kind;
            public readonly BarButton Button;
            public readonly bool Right;      // laid out from the right edge
            public readonly int Group;       // neighbours in one group sit closer together
            public readonly int HideLevel;   // hidden once the responsive level exceeds this; -1 = never
            public readonly int CullHideLevel; // the same while the cull chip is shown
            public Rectangle Bounds;
            public bool Visible;
            public float Hover;              // 0..1, animated

            public Part(PartKind kind, BarButton button, bool right, int group, int hideLevel, int? cullHideLevel = null)
            {
                Kind = kind; Button = button; Right = right; Group = group; HideLevel = hideLevel;
                CullHideLevel = cullHideLevel ?? hideLevel;
            }
        }

        private readonly SeekBar _seekBar;
        private readonly Part[] _parts;
        private readonly Part[] _buttons;   // indexed by (int)BarButton
        private readonly Part _time, _frame, _volume, _badge, _cull;

        // ---- state ----
        private bool _hasMedia;
        private bool _paused = true;
        private double _position, _duration;
        private long? _frameNumber, _frameCount;
        private bool _showFrameNumber = true, _showRemaining;
        private double _speed = 1.0, _volumeValue = 100, _volumeMax = 150;
        private bool _muted, _shuffle, _fullscreen, _playlistVisible;
        private RepeatMode _repeat;
        private AbLoopState _abLoop;
        private string _zoomText = "Fit", _playlistText = "";
        private bool _frameApproximate, _variableFrameRate, _cullMode;
        private bool _cullOrder;   // the current layout keeps the chip (cull responsive order)
        private int _cullFlag, _cullRating;
        private string _cullProgress = "";

        // Readout strings plus their digit "shapes"; the layout reserves the shape's width so
        // per-frame text changes only repaint the readout instead of moving neighbours.
        // The time readout has three forms, widest first: full, total without millis, position only.
        // The layout uses the full form unless even the essentials do not fit.
        private const int TimeForms = 3;
        private readonly string[] _timeTexts = new string[TimeForms], _timeShapes = new string[TimeForms];
        private readonly string[] _laidTimeShapes = new string[TimeForms];
        private int _timeForm;
        private string _frameText = "", _frameShape = "";
        private string _laidFrameShape;

        // ---- interaction ----
        private Part _hover, _pressed;
        private MouseButtons _pressedButton;
        private bool _volumeDragging;
        private int _wheelRemainder;

        // ---- per-DPI resources ----
        private int _resDpi;
        private Font _iconFont, _playFont, _textFont, _buttonFont, _starFont, _flagFont, _badgeFont;
        private char _wideDigit = '0';
        // Measured widths per font (cleared whenever the fonts are rebuilt).
        private readonly Dictionary<Font, Dictionary<string, float>> _widths = new Dictionary<Font, Dictionary<string, float>>();
        private readonly SolidBrush _brush;
        private readonly Pen _pen;
        private readonly Bitmap _measureBitmap;
        private readonly Graphics _measure;
        private readonly StringFormat _fmtMeasure, _fmtLeft, _fmtCenter;

        private readonly Timer _animTimer, _tipTimer, _frameTimer;
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private long _lastFrameRepaintMs = -FrameRepaintMs;
        private int _animLastTick;
        private readonly ToolTip _tip;
        private Part _tipPart;
        private bool _tipVisible;
        private int? _tipHiddenTick;   // when the last visible tip was hidden; null = never shown yet
        private Size _tipSize;

        public ControlBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Panel;   // ambient: the hosted seek bar inherits it
            ForeColor = Theme.Text;

            _brush = new SolidBrush(Theme.Text);
            _pen = new Pen(Theme.Border);
            _measureBitmap = new Bitmap(1, 1);
            _measure = Graphics.FromImage(_measureBitmap);
            _measure.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _fmtMeasure = MakeFormat(StringAlignment.Near, StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces, StringTrimming.None);
            _fmtLeft = MakeFormat(StringAlignment.Near, StringFormatFlags.NoWrap | StringFormatFlags.LineLimit, StringTrimming.EllipsisCharacter);
            _fmtCenter = MakeFormat(StringAlignment.Center, StringFormatFlags.NoWrap | StringFormatFlags.NoClip, StringTrimming.None);
            _parts = new[]
            {
                new Part(PartKind.Button, BarButton.PrevFile, false, 0, 8),
                new Part(PartKind.Button, BarButton.FrameBack, false, 0, 7),
                new Part(PartKind.Button, BarButton.PlayPause, false, 0, -1),
                new Part(PartKind.Button, BarButton.FrameForward, false, 0, 7),
                new Part(PartKind.Button, BarButton.NextFile, false, 0, 8),
                _time = new Part(PartKind.Time, default(BarButton), false, 1, -1),
                // The frame number outranks shuffle/zoom/A-B: this is a reviewing tool.
                _frame = new Part(PartKind.Frame, default(BarButton), false, 1, 4),
                _badge = new Part(PartKind.Badge, default(BarButton), false, 1, 4),
                // The cull chip sits in a group of its own, beside the clip counter it describes.
                _cull = new Part(PartKind.Button, BarButton.Cull, false, 6, CullMaxHideLevel),
                new Part(PartKind.PlaylistInfo, default(BarButton), false, 1, 1),
                new Part(PartKind.Button, BarButton.Repeat, true, 2, 6),
                new Part(PartKind.Button, BarButton.AbLoop, true, 2, 3),
                new Part(PartKind.Button, BarButton.Shuffle, true, 2, 0),
                new Part(PartKind.Button, BarButton.Speed, true, 3, -1, 12),
                new Part(PartKind.Button, BarButton.Zoom, true, 3, 2),
                new Part(PartKind.Button, BarButton.Mute, true, 4, -1, 11),
                _volume = new Part(PartKind.Volume, default(BarButton), true, 4, 5),
                new Part(PartKind.Button, BarButton.Playlist, true, 5, -1, 9),
                new Part(PartKind.Button, BarButton.Fullscreen, true, 5, -1, 10),
                new Part(PartKind.Button, BarButton.Menu, true, 5, -1, 13),
            };
            _buttons = new Part[Enum.GetValues(typeof(BarButton)).Length];
            foreach (var p in _parts)
                if (p.Kind == PartKind.Button) _buttons[(int)p.Button] = p;

            _animTimer = new Timer { Interval = 15 };
            _animTimer.Tick += OnAnimTick;
            _tipTimer = new Timer { Interval = TipDelayMs };
            _tipTimer.Tick += OnTipTimer;
            _frameTimer = new Timer { Interval = FrameRepaintMs };
            _frameTimer.Tick += OnFrameTimer;
            _tip = new ToolTip { OwnerDraw = true, ShowAlways = true, UseAnimation = false, UseFading = false };
            _tip.Popup += (s, e) => e.ToolTipSize = _tipSize;
            _tip.Draw += OnTipDraw;

            RefreshTimeText();
            RefreshFrameText();

            _seekBar = new SeekBar();
            Controls.Add(_seekBar);
            Height = PreferredBarHeight;
        }

        public SeekBar SeekBar { get { return _seekBar; } }

        // ---- state (setting a property repaints only if the value changed) ----

        public bool HasMedia
        {
            get { return _hasMedia; }
            set
            {
                if (_hasMedia == value) return;
                _hasMedia = value;
                RefreshTimeText();
                RefreshFrameText();
                Relayout();
            }
        }

        public bool Paused
        {
            get { return _paused; }
            set
            {
                if (_paused == value) return;
                _paused = value;
                InvalidatePart(_buttons[(int)BarButton.PlayPause]);
                UpdateTime();   // millis appear/disappear
                if (_paused && _frameTimer.Enabled) OnFrameTimer(this, EventArgs.Empty); // show the frame it stopped on
            }
        }

        // Seconds, for the time readout (SeekBar is set separately).
        public double Position
        {
            get { return _position; }
            set
            {
                value = Finite(value);
                if (_position == value) return;
                _position = value;
                UpdateTime();
            }
        }

        public double Duration
        {
            get { return _duration; }
            set
            {
                value = Finite(value);
                if (_duration == value) return;
                _duration = value;
                UpdateTime();
            }
        }

        // 0-based current frame; null = unknown.
        public long? FrameNumber
        {
            get { return _frameNumber; }
            set
            {
                if (_frameNumber == value) return;
                bool wasShown = FrameShown;
                _frameNumber = value;
                UpdateFrame(wasShown);
            }
        }

        public long? FrameCount
        {
            get { return _frameCount; }
            set
            {
                if (_frameCount == value) return;
                bool wasShown = FrameShown;
                _frameCount = value;
                UpdateFrame(wasShown);
            }
        }

        public bool ShowFrameNumber
        {
            get { return _showFrameNumber; }
            set
            {
                if (_showFrameNumber == value) return;
                bool wasShown = FrameShown;
                _showFrameNumber = value;
                UpdateFrame(wasShown);
            }
        }

        // "-0:03" instead of elapsed.
        public bool ShowRemaining
        {
            get { return _showRemaining; }
            set
            {
                if (_showRemaining == value) return;
                _showRemaining = value;
                UpdateTime();
                RefreshVisibleTip(_time);
            }
        }

        public double Speed
        {
            get { return _speed; }
            set
            {
                value = Finite(value, 1.0);
                if (_speed == value) return;
                _speed = value;
                Relayout();   // the label width can change ("1x" -> "1.25x")
            }
        }

        // 0..VolumeMax
        public double Volume
        {
            get { return _volumeValue; }
            set
            {
                value = Finite(value);
                if (_volumeValue == value) return;
                _volumeValue = value;
                InvalidatePart(_volume);
                InvalidatePart(_buttons[(int)BarButton.Mute]);
                RefreshVisibleTip(_volume);
            }
        }

        public double VolumeMax
        {
            get { return _volumeMax; }
            set
            {
                value = Finite(value, 100);
                if (_volumeMax == value) return;
                _volumeMax = value;
                InvalidatePart(_volume);
            }
        }

        public bool Muted
        {
            get { return _muted; }
            set
            {
                if (_muted == value) return;
                _muted = value;
                InvalidatePart(_volume);
                InvalidatePart(_buttons[(int)BarButton.Mute]);
                RefreshVisibleTip(_volume);
            }
        }

        public RepeatMode Repeat
        {
            get { return _repeat; }
            set
            {
                if (_repeat == value) return;
                _repeat = value;
                InvalidatePart(_buttons[(int)BarButton.Repeat]);
            }
        }

        public bool Shuffle
        {
            get { return _shuffle; }
            set
            {
                if (_shuffle == value) return;
                _shuffle = value;
                InvalidatePart(_buttons[(int)BarButton.Shuffle]);
            }
        }

        public AbLoopState AbLoop
        {
            get { return _abLoop; }
            set
            {
                if (_abLoop == value) return;
                _abLoop = value;
                InvalidatePart(_buttons[(int)BarButton.AbLoop]);
            }
        }

        // e.g. "Fit", "250%"
        public string ZoomText
        {
            get { return _zoomText; }
            set
            {
                value = value ?? "Fit";
                if (_zoomText == value) return;
                _zoomText = value;
                Relayout();
            }
        }

        public bool Fullscreen
        {
            get { return _fullscreen; }
            set
            {
                if (_fullscreen == value) return;
                _fullscreen = value;
                InvalidatePart(_buttons[(int)BarButton.Fullscreen]);
            }
        }

        public bool PlaylistVisible
        {
            get { return _playlistVisible; }
            set
            {
                if (_playlistVisible == value) return;
                _playlistVisible = value;
                InvalidatePart(_buttons[(int)BarButton.Playlist]);
            }
        }

        // e.g. "3 / 12"; empty hides.
        public string PlaylistText
        {
            get { return _playlistText; }
            set
            {
                value = value ?? "";
                if (_playlistText == value) return;
                _playlistText = value;
                Relayout();   // also re-evaluates Prev/Next availability without media
            }
        }

        // Frame readout honesty: FrameApproximate draws "\u2248F 61"; VariableFrameRate adds a small "VFR"
        // badge (tooltip explains that frame numbers are estimates on variable-frame-rate video).
        public bool FrameApproximate
        {
            get { return _frameApproximate; }
            set
            {
                if (_frameApproximate == value) return;
                bool wasShown = FrameShown;
                _frameApproximate = value;
                UpdateFrame(wasShown);
                RefreshVisibleTip(_frame);
            }
        }

        public bool VariableFrameRate
        {
            get { return _variableFrameRate; }
            set
            {
                if (_variableFrameRate == value) return;
                _variableFrameRate = value;
                Relayout();
            }
        }

        // Cull mode chip after the time readout: shown while CullMode; flag 1 = Pick (green check),
        // -1 = Reject (red cross), 0 = undecided (dim dash); Rating 0..5 drawn as stars. Clicking it
        // raises ButtonClicked(BarButton.Cull, ...).
        public bool CullMode
        {
            get { return _cullMode; }
            set
            {
                if (_cullMode == value) return;
                _cullMode = value;
                Relayout();   // the chip appears and the responsive order changes
            }
        }

        public int CullFlag
        {
            get { return _cullFlag; }
            set
            {
                value = Math.Sign(value);
                if (_cullFlag == value) return;
                _cullFlag = value;
                InvalidatePart(_cull);   // the flag slot is as wide as its widest glyph: no relayout
                RefreshVisibleTip(_cull);
            }
        }

        public int CullRating
        {
            get { return _cullRating; }
            set
            {
                value = Math.Max(0, Math.Min(5, value));
                if (_cullRating == value) return;
                _cullRating = value;
                Relayout();   // one star per point
                RefreshVisibleTip(_cull);
            }
        }

        // e.g. "12 / 40 decided"; empty hides.
        public string CullProgressText
        {
            get { return _cullProgress; }
            set
            {
                value = value ?? "";
                if (_cullProgress == value) return;
                bool reshaped = !string.Equals(CounterShape(value), CounterShape(_cullProgress), StringComparison.Ordinal);
                _cullProgress = value;
                if (reshaped) Relayout();
                else InvalidatePart(_cull);
                RefreshVisibleTip(_cull);
            }
        }

        // What the bar currently shows (for automation dumps and tests).
        public string FrameText { get { return _frameText; } }
        public bool CullChipVisible { get { return _cull.Visible; } }
        public bool VfrBadgeVisible { get { return _badge.Visible; } }

        // Tooltip text per button (MainForm supplies names + shortcuts). Null = no tooltip.
        public Func<BarButton, string> TooltipProvider { get; set; }

        // ---- events ----

        // A button was clicked. MouseButtons tells left vs right click; the rectangle is the
        // button's bounds in screen coordinates (to anchor drop-down menus).
        public event Action<BarButton, MouseButtons, Rectangle> ButtonClicked;
        public event Action<double> VolumeChanged;    // user moved the volume slider (click, drag or wheel)
        public event Action TimeDisplayClicked;       // user clicked the time readout (toggle remaining)

        // DPI-scaled total height incl. the seek bar.
        public int PreferredBarHeight { get { return SeekBarHeight() + S(RowHeight); } }

        // Screen bounds of a button, e.g. to anchor a menu opened from the keyboard at the matching
        // button. Empty when the button is currently hidden (narrow bar) or the bar has no handle.
        public Rectangle GetButtonScreenBounds(BarButton button)
        {
            int i = (int)button;
            if (i < 0 || i >= _buttons.Length || !IsHandleCreated) return Rectangle.Empty;
            var p = _buttons[i];
            return p.Visible ? RectangleToScreen(p.Bounds) : Rectangle.Empty;
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private int S(int logicalPx) { return Theme.Scale(this, logicalPx); }
        private float SF(float logicalPx) { return Theme.ScaleF(this, logicalPx); }
        // Whatever DPI Theme.Scale uses for this control, so cached fonts always match the layout.
        private int Dpi { get { return (int)Math.Round(Theme.ScaleF(this, 96f)); } }

        private int SeekBarHeight()
        {
            int h = _seekBar != null ? _seekBar.PreferredBarHeight : 0;
            return h > 0 ? h : S(FallbackSeekHeight);
        }

        private void Relayout()
        {
            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_parts == null) return;   // base constructor layout, before fields exist
            // A DPI change noticed here (e.g. a resize after a monitor move) also needs a new height,
            // which must not be set from inside our own layout pass.
            if (EnsureResources() && IsHandleCreated) BeginInvoke((Action)ApplyDpi);
            int seekH = Math.Min(SeekBarHeight(), Height);
            int m = S(SeekMargin);
            if (_seekBar != null) _seekBar.SetBounds(m, 0, Math.Max(0, Width - 2 * m), seekH);
            LayoutRow(seekH);
            Invalidate();
        }

        private void LayoutRow(int rowTop)
        {
            int rowH = Math.Max(0, Height - rowTop);
            int cy = rowTop + rowH / 2;
            int margin = S(RowMargin);
            int avail = Math.Max(0, Width - 2 * margin);

            int n = _parts.Length;
            var widths = new int[n];
            var wanted = new bool[n];
            for (int i = 0; i < n; i++)
            {
                widths[i] = PartWidth(_parts[i]);
                wanted[i] = PartWanted(_parts[i]);
            }

            // Responsive: hide optional parts in priority order until the row fits; if even the
            // essentials do not fit, the time readout gives up width last: first a shorter form,
            // then an ellipsis. The cull chip outlasts everything but play/pause and the time; when
            // even that does not fit, the chip goes and the rest follows the normal order.
            _cullOrder = _cullMode;
            int level = FitLevel(widths, wanted, avail);
            if (_cullOrder && level > CullMaxHideLevel)
            {
                _cullOrder = false;
                wanted[Array.IndexOf(_parts, _cull)] = false;
                level = FitLevel(widths, wanted, avail);
            }
            int maxLevel = MaxLevel;
            int timeIndex = Array.IndexOf(_parts, _time);
            _timeForm = 0;
            if (level > maxLevel)
            {
                int pad2 = 2 * S(ReadoutPad);
                int room = avail - RowWidth(level, widths, wanted, timeIndex);
                _timeForm = TimeForms - 1;
                int timeWidth = room;
                // The last level also hid parts, so even the full form may fit now.
                for (int f = 0; f < TimeForms; f++)
                {
                    int w = f == 0 ? widths[timeIndex] : Ceil(Measure(_textFont, _timeShapes[f])) + pad2;
                    if (w <= room) { _timeForm = f; timeWidth = w; break; }
                }
                if (room < S(MinTimeTextWidth) + pad2) { wanted[timeIndex] = false; timeWidth = 0; }
                widths[timeIndex] = timeWidth;
            }

            for (int i = 0; i < n; i++)
            {
                var p = _parts[i];
                int hide = HideLevelOf(p);
                p.Visible = wanted[i] && (hide < 0 || hide >= level);
            }

            int x = margin;
            Part prev = null;
            for (int i = 0; i < n; i++)
            {
                var p = _parts[i];
                if (p.Right || !p.Visible) continue;
                if (prev != null) x += Gap(prev, p);
                p.Bounds = PartRect(p, x, widths[i], cy, rowH);
                x += widths[i];
                prev = p;
            }

            int r = Width - margin;
            prev = null;
            for (int i = n - 1; i >= 0; i--)
            {
                var p = _parts[i];
                if (!p.Right || !p.Visible) continue;
                if (prev != null) r -= Gap(p, prev);
                p.Bounds = PartRect(p, r - widths[i], widths[i], cy, rowH);
                r -= widths[i];
                prev = p;
            }

            foreach (var p in _parts)
                if (!p.Visible) { p.Bounds = Rectangle.Empty; p.Hover = 0; }

            Array.Copy(_timeShapes, _laidTimeShapes, TimeForms);
            _laidFrameShape = _frameShape;

            if (_hover != null && !_hover.Visible) SetHover(null);
            if (_pressed != null && !_pressed.Visible) _pressed = null;
            if (_volumeDragging && !_volume.Visible) _volumeDragging = false;
        }

        // Total row width at a responsive level (excluding the part at skipIndex, gaps included).
        private int RowWidth(int level, int[] widths, bool[] wanted, int skipIndex)
        {
            int total = 0;
            Part prevLeft = null, prevRight = null;
            for (int i = 0; i < _parts.Length; i++)
            {
                var p = _parts[i];
                int hide = HideLevelOf(p);
                if (!wanted[i] || (hide >= 0 && hide < level)) continue;
                ref Part prev = ref (p.Right ? ref prevRight : ref prevLeft);
                if (prev != null) total += Gap(prev, p);
                if (i != skipIndex) total += widths[i];
                prev = p;
            }
            if (prevLeft != null && prevRight != null) total += S(ClusterGap);
            return total;
        }

        private int Gap(Part a, Part b) { return a.Group == b.Group ? S(TightGap) : S(GroupGap); }

        // The lowest responsive level at which the row fits (MaxLevel + 1 when even that is too wide).
        private int FitLevel(int[] widths, bool[] wanted, int avail)
        {
            int level = 0, max = MaxLevel;
            while (level <= max && RowWidth(level, widths, wanted, -1) > avail) level++;
            return level;
        }

        // The chip changes the responsive order (see CullMaxHideLevel) while it is being laid out.
        private int MaxLevel { get { return _cullOrder ? CullMaxHideLevel : MaxHideLevel; } }
        private int HideLevelOf(Part p) { return _cullOrder ? p.CullHideLevel : p.HideLevel; }

        private Rectangle PartRect(Part p, int x, int w, int cy, int rowH)
        {
            int h = p.Kind == PartKind.Button && p.Button == BarButton.PlayPause ? S(PlayHeight) : S(ButtonHeight);
            h = Math.Min(h, rowH);
            return new Rectangle(x, cy - h / 2, w, h);
        }

        private bool FrameShown { get { return _hasMedia && _showFrameNumber && _frameNumber.HasValue; } }

        private bool PartWanted(Part p)
        {
            switch (p.Kind)
            {
                case PartKind.Frame: return FrameShown;
                case PartKind.PlaylistInfo: return _playlistText.Length > 0;
                case PartKind.Badge: return _hasMedia && _variableFrameRate;
                case PartKind.Button: return p.Button != BarButton.Cull || _cullMode;
                default: return true;
            }
        }

        private int PartWidth(Part p)
        {
            int pad2 = 2 * S(ReadoutPad);
            switch (p.Kind)
            {
                case PartKind.Time: return Ceil(Measure(_textFont, _timeShapes[0])) + pad2;
                case PartKind.Frame: return Ceil(Measure(_textFont, _frameShape)) + pad2;
                case PartKind.PlaylistInfo: return Ceil(Measure(_textFont, CounterShape(_playlistText))) + pad2;
                case PartKind.Volume: return S(VolumeWidth);
                case PartKind.Badge: return Ceil(Measure(_badgeFont, VfrText)) + 2 * S(BadgePadX);
            }
            switch (p.Button)
            {
                case BarButton.Cull: return ChipWidth();
                case BarButton.PlayPause: return S(PlayWidth);
                case BarButton.AbLoop: return TextButtonWidth(AbText, null);
                case BarButton.Speed: return TextButtonWidth(Fmt.Speed(_speed), "0.00\u00d7");
                case BarButton.Zoom: return TextButtonWidth(ZoomLabel, "000%");
                default: return S(ButtonWidth);
            }
        }

        // Pill: flag slot (wide enough for any flag, so deciding never moves the row), one star per
        // rating point, then the progress text (digit-shaped like the other readouts).
        private int ChipWidth()
        {
            float w = 2 * S(ChipPadX) + FlagSlotWidth();
            if (_cullRating > 0) w += S(ChipGap) + _cullRating * Measure(_starFont, StarGlyph) + (_cullRating - 1) * S(StarGap);
            if (_cullProgress.Length > 0) w += S(ChipGap) + Measure(_textFont, CounterShape(_cullProgress));
            return Ceil(w);
        }

        private float FlagSlotWidth()
        {
            return Math.Max(Measure(_textFont, UndecidedText), Math.Max(Measure(_flagFont, Glyphs.Check), Measure(_flagFont, Glyphs.Close)));
        }

        // Text buttons reserve a typical label's width so e.g. 1x -> 1.5x does not shift the row.
        private int TextButtonWidth(string text, string reserve)
        {
            float w = Measure(_buttonFont, text);
            if (reserve != null) w = Math.Max(w, Measure(_buttonFont, Shape(reserve)));
            return Math.Max(S(ButtonWidth), Ceil(w) + 2 * S(TextButtonPad));
        }

        // =====================================================================================
        // Readout text
        // =====================================================================================

        private void UpdateTime()
        {
            string before = _timeTexts[_timeForm];
            RefreshTimeText();
            bool reshaped = false;
            for (int f = 0; f < TimeForms; f++)
                if (!string.Equals(_timeShapes[f], _laidTimeShapes[f], StringComparison.Ordinal)) reshaped = true;
            if (reshaped) Relayout();
            else if (!string.Equals(_timeTexts[_timeForm], before, StringComparison.Ordinal)) InvalidatePart(_time);
        }

        private void RefreshTimeText()
        {
            if (!_hasMedia)
            {
                SetTimeForms("0:00", Shape("0:00"), "0:00", "0:00");
                return;
            }
            bool millis = _paused;
            double dur = Math.Max(0, _duration), pos = Math.Max(0, _position);
            bool hours = Math.Max(dur, pos) >= 3599.9995;
            bool remaining = _showRemaining && dur > 0;
            string durText = Fmt.Time(dur, millis, hours);
            string durShort = millis ? Fmt.Time(dur, false, hours) : durText;
            string posText = remaining ? "-" + Fmt.Time(Math.Max(0, dur - pos), millis, hours) : Fmt.Time(pos, millis, hours);

            // Elapsed and total share a shape (same h/m digits) so the readout only resizes when
            // the format itself changes, never as the clock ticks.
            string posShape = Shape(posText);
            string fromDur = (remaining ? "-" : "") + Shape(durText);
            if (fromDur.Length > posShape.Length) posShape = fromDur;
            SetTimeForms(posText, posShape, durText, durShort);
        }

        private void SetTimeForms(string pos, string posShape, string dur, string durShort)
        {
            _timeTexts[0] = pos + " / " + dur;
            _timeTexts[1] = pos + " / " + durShort;
            _timeTexts[2] = pos;
            _timeShapes[0] = posShape + " / " + Shape(dur);
            _timeShapes[1] = posShape + " / " + Shape(durShort);
            _timeShapes[2] = posShape;
        }

        private void UpdateFrame(bool wasShown)
        {
            string before = _frameText;
            RefreshFrameText();
            if (wasShown != FrameShown || (FrameShown && _frameShape != _laidFrameShape)) Relayout();
            else if (FrameShown && _frameText != before) InvalidateFrame();
        }

        // Throttled repaint of the frame readout (see FrameRepaintMs). The text is read when painting,
        // so a deferred repaint always shows the latest frame.
        private void InvalidateFrame()
        {
            long now = _frameClock.ElapsedMilliseconds;
            long wait = _lastFrameRepaintMs + FrameRepaintMs - now;
            if (_paused || wait <= 0)
            {
                _frameTimer.Stop();
                _lastFrameRepaintMs = now;
                InvalidatePart(_frame);
                return;
            }
            if (_frameTimer.Enabled) return;
            _frameTimer.Interval = (int)Math.Max(1, wait);
            _frameTimer.Start();
        }

        private void OnFrameTimer(object sender, EventArgs e)
        {
            _frameTimer.Stop();
            _lastFrameRepaintMs = _frameClock.ElapsedMilliseconds;
            InvalidatePart(_frame);
        }

        private void RefreshFrameText()
        {
            if (!FrameShown) { _frameText = ""; _frameShape = ""; return; }
            long n = Math.Max(0, _frameNumber.Value);
            // "\u2248F": the number is estimated from the timestamp (variable frame rate).
            string text = (_frameApproximate ? "\u2248F " : "F ") + n.ToString(CultureInfo.InvariantCulture);
            if (_frameCount.HasValue && _frameCount.Value > 0)
                text += " / " + _frameCount.Value.ToString(CultureInfo.InvariantCulture);
            _frameText = text;
            _frameShape = CounterShape(text);
        }

        // Every digit replaced by the font's widest digit.
        private string Shape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] >= '0' && chars[i] <= '9') chars[i] = _wideDigit;
            return new string(chars);
        }

        // Like Shape, but every digit run is widened to the longest run, so "9 / 240" reserves
        // room for "240 / 240" and counting up never resizes the readout.
        private string CounterShape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int longest = 0, run = 0;
            foreach (char c in s)
            {
                run = c >= '0' && c <= '9' ? run + 1 : 0;
                if (run > longest) longest = run;
            }
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length;)
            {
                if (s[i] >= '0' && s[i] <= '9')
                {
                    while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                    sb.Append(_wideDigit, longest);
                }
                else sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        private string ZoomLabel { get { return _zoomText.Length == 0 ? "Fit" : _zoomText; } }
        private bool ZoomIsFit { get { return string.Equals(ZoomLabel, "Fit", StringComparison.OrdinalIgnoreCase); } }
        private bool SpeedIsNormal { get { return Math.Round(_speed, 2) == 1.0; } }
        private double VolumeScaleMax { get { return _volumeMax > 0 ? _volumeMax : 100; } }

        // =====================================================================================
        // Resources
        // =====================================================================================

        private static StringFormat MakeFormat(StringAlignment align, StringFormatFlags flags, StringTrimming trimming)
        {
            // GenericTypographic: no GDI+ side padding, so measured widths match what is drawn.
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.Alignment = align;
            f.LineAlignment = StringAlignment.Center;
            f.FormatFlags = flags;
            f.Trimming = trimming;
            return f;
        }

        // Returns true when the fonts were (re)built for a different DPI than before.
        private bool EnsureResources()
        {
            int dpi = Dpi;
            if (_iconFont != null && _resDpi == dpi) return false;
            bool rebuilt = _iconFont != null;
            DisposeFonts();
            _resDpi = dpi;
            _iconFont = new Font(Theme.IconFontFamily, SF(IconPx), FontStyle.Regular, GraphicsUnit.Pixel);
            _playFont = new Font(Theme.IconFontFamily, SF(PlayIconPx), FontStyle.Regular, GraphicsUnit.Pixel);
            _textFont = new Font("Segoe UI", SF(TextPx), FontStyle.Regular, GraphicsUnit.Pixel);
            _buttonFont = SemiboldFont(TextPx);
            _starFont = new Font(Theme.IconFontFamily, SF(StarPx), FontStyle.Regular, GraphicsUnit.Pixel);
            _flagFont = new Font(Theme.IconFontFamily, SF(FlagPx), FontStyle.Regular, GraphicsUnit.Pixel);
            _badgeFont = SemiboldFont(BadgeTextPx);
            _widths.Clear();

            // Digits are tabular in Segoe UI, but measure rather than assume.
            char best = '0';
            float bestW = 0;
            for (char c = '0'; c <= '9'; c++)
            {
                float w = Measure(_textFont, new string(c, 8));
                if (w > bestW + 0.01f) { bestW = w; best = c; }
            }
            if (best != _wideDigit)
            {
                _wideDigit = best;
                RefreshTimeText();
                RefreshFrameText();
            }
            return rebuilt;
        }

        private Font SemiboldFont(float logicalPx)
        {
            var f = new Font("Segoe UI Semibold", SF(logicalPx), FontStyle.Regular, GraphicsUnit.Pixel);
            if (string.Equals(f.Name, "Segoe UI Semibold", StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
            return new Font("Segoe UI", SF(logicalPx), FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            if (_iconFont != null) { _iconFont.Dispose(); _iconFont = null; }
            if (_playFont != null) { _playFont.Dispose(); _playFont = null; }
            if (_textFont != null) { _textFont.Dispose(); _textFont = null; }
            if (_buttonFont != null) { _buttonFont.Dispose(); _buttonFont = null; }
            if (_starFont != null) { _starFont.Dispose(); _starFont = null; }
            if (_flagFont != null) { _flagFont.Dispose(); _flagFont = null; }
            if (_badgeFont != null) { _badgeFont.Dispose(); _badgeFont = null; }
        }

        private float Measure(Font font, string s)
        {
            if (string.IsNullOrEmpty(s) || font == null) return 0;
            Dictionary<string, float> cache;
            if (!_widths.TryGetValue(font, out cache)) _widths[font] = cache = new Dictionary<string, float>(StringComparer.Ordinal);
            float w;
            if (cache.TryGetValue(s, out w)) return w;
            w = _measure.MeasureString(s, font, PointF.Empty, _fmtMeasure).Width;
            if (cache.Count > 256) cache.Clear();
            cache[s] = w;
            return w;
        }

        private static int Ceil(float v) { return (int)Math.Ceiling(v - 0.01f); }

        private void ApplyDpi()
        {
            if (IsDisposed) return;
            EnsureResources();
            int h = PreferredBarHeight;
            if (Height != h) Height = h;
            Relayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpi();   // DeviceDpi is only meaningful once the handle exists
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            CancelTip();
            ApplyDpi();
            // The seek bar may learn its new DPI after us; re-read its preferred height once it has.
            if (IsHandleCreated) BeginInvoke((Action)ApplyDpi);
        }

        // =====================================================================================
        // Painting
        // =====================================================================================

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            _brush.Color = Theme.Panel;
            e.Graphics.FillRectangle(_brush, e.ClipRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (EnsureResources() && IsHandleCreated) BeginInvoke((Action)ApplyDpi);
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            foreach (var p in _parts)
            {
                if (!p.Visible || p.Bounds.Width <= 0 || !p.Bounds.IntersectsWith(clip)) continue;
                switch (p.Kind)
                {
                    case PartKind.Button: PaintButton(g, p); break;
                    case PartKind.Time: PaintTime(g, p); break;
                    case PartKind.Frame: PaintReadout(g, p, _frameText); break;
                    case PartKind.PlaylistInfo: PaintReadout(g, p, _playlistText); break;
                    case PartKind.Volume: PaintVolume(g, p); break;
                    case PartKind.Badge: PaintBadge(g, p); break;
                }
            }
        }

        private void PaintButton(Graphics g, Part p)
        {
            if (p.Button == BarButton.Cull)
            {
                PaintCullChip(g, p);   // its pill is its own hover surface
                return;
            }
            bool enabled = IsEnabled(p);
            PaintHoverBack(g, p, enabled);
            Color normal = Theme.Text;
            Color dim = Lerp(Theme.TextDim, Theme.Text, p.Hover);
            switch (p.Button)
            {
                case BarButton.AbLoop:
                    PaintAbLoop(g, p, enabled, dim);
                    return;
                case BarButton.Speed:
                    DrawCenteredText(g, Fmt.Speed(_speed), p.Bounds, !enabled ? Theme.TextDisabled : SpeedIsNormal ? normal : Theme.Accent);
                    return;
                case BarButton.Zoom:
                    DrawCenteredText(g, ZoomLabel, p.Bounds, !enabled ? Theme.TextDisabled : ZoomIsFit ? normal : Theme.Accent);
                    return;
            }

            string glyph;
            Color color = normal;
            switch (p.Button)
            {
                case BarButton.PrevFile: glyph = PreviousSolid; break;
                case BarButton.FrameBack: glyph = Glyphs.StepBack; break;
                case BarButton.PlayPause: glyph = _paused || !_hasMedia ? PlaySolid : PauseSolid; break;
                case BarButton.FrameForward: glyph = Glyphs.StepForward; break;
                case BarButton.NextFile: glyph = NextSolid; break;
                case BarButton.Repeat:
                    glyph = _repeat == RepeatMode.One ? Glyphs.RepeatOne : Glyphs.RepeatAll;
                    color = _repeat == RepeatMode.Off ? dim : Theme.Accent;
                    break;
                case BarButton.Shuffle:
                    glyph = Glyphs.Shuffle;
                    color = _shuffle ? Theme.Accent : dim;
                    break;
                case BarButton.Mute: glyph = VolumeGlyph(); break;
                case BarButton.Playlist:
                    glyph = Glyphs.Playlist;
                    if (_playlistVisible) color = Theme.Accent;
                    break;
                case BarButton.Fullscreen: glyph = _fullscreen ? Glyphs.BackToWindow : Glyphs.FullScreen; break;
                default: glyph = Glyphs.More; break;
            }
            if (!enabled) color = Theme.TextDisabled;

            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            _brush.Color = color;
            g.DrawString(glyph, p.Button == BarButton.PlayPause ? _playFont : _iconFont, _brush, p.Bounds, _fmtCenter);
        }

        private string VolumeGlyph()
        {
            if (_muted || _volumeValue <= 0) return Glyphs.Mute;
            if (_volumeValue < 34) return Glyphs.Volume1;
            if (_volumeValue < 67) return Glyphs.Volume2;
            return Glyphs.Volume3;
        }

        private void PaintAbLoop(Graphics g, Part p, bool enabled, Color dim)
        {
            float full = Measure(_buttonFont, AbText);
            float x = p.Bounds.X + (p.Bounds.Width - full) / 2f;
            if (!enabled || _abLoop != AbLoopState.ASet)
            {
                Color c = !enabled ? Theme.TextDisabled : _abLoop == AbLoopState.Active ? Theme.Accent : dim;
                DrawText(g, AbText, _buttonFont, c, new RectangleF(x, p.Bounds.Y, full + p.Bounds.Width, p.Bounds.Height));
                return;
            }
            // A set, waiting for B: the "A-" half lights up.
            float head = Measure(_buttonFont, AbHead);
            DrawText(g, AbHead, _buttonFont, Theme.Accent, new RectangleF(x, p.Bounds.Y, head + p.Bounds.Width, p.Bounds.Height));
            DrawText(g, AbTail, _buttonFont, dim, new RectangleF(x + head, p.Bounds.Y, p.Bounds.Width, p.Bounds.Height));
        }

        // The chip: a pill tinted by the decision, holding the flag, the stars and the progress.
        private void PaintCullChip(Graphics g, Part p)
        {
            Rectangle pill = CenteredRect(p.Bounds, p.Bounds.Width, S(ChipHeight));
            Color tint = _cullFlag > 0 ? Lerp(Theme.PanelAlt, PickColor, 0.2f)
                : _cullFlag < 0 ? Lerp(Theme.PanelAlt, RejectColor, 0.2f) : Theme.PanelAlt;
            Color fill = p == _pressed && p == _hover ? Lerp(tint, Color.White, 0.16f)
                : Lerp(tint, Lerp(tint, Color.White, 0.09f), p.Hover);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            FillRounded(g, pill, pill.Height / 2f, fill);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;

            float x = pill.X + S(ChipPadX);
            float flagW = FlagSlotWidth();
            var flagRect = new RectangleF(x, pill.Y, flagW, pill.Height);
            if (_cullFlag != 0)
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                _brush.Color = _cullFlag > 0 ? PickColor : RejectColor;
                string glyph = _cullFlag > 0 ? Glyphs.Check : Glyphs.Close;
                // The icon font's strokes are hairlines at this size: draw twice, a fraction of a pixel apart.
                float bold = Math.Max(0.6f, SF(0.6f));
                g.DrawString(glyph, _flagFont, _brush, new RectangleF(flagRect.X - bold / 2f, flagRect.Y, flagRect.Width, flagRect.Height), _fmtCenter);
                g.DrawString(glyph, _flagFont, _brush, new RectangleF(flagRect.X + bold / 2f, flagRect.Y, flagRect.Width, flagRect.Height), _fmtCenter);
            }
            else
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                _brush.Color = Theme.TextDim;
                g.DrawString(UndecidedText, _textFont, _brush, flagRect, _fmtCenter);
            }
            x += flagW;

            if (_cullRating > 0)
            {
                x += S(ChipGap);
                float starW = Measure(_starFont, StarGlyph);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                _brush.Color = StarColor;
                for (int i = 0; i < _cullRating; i++)
                {
                    if (i > 0) x += S(StarGap);
                    g.DrawString(StarGlyph, _starFont, _brush, new RectangleF(x, pill.Y, starW, pill.Height), _fmtCenter);
                    x += starW;
                }
            }

            if (_cullProgress.Length > 0)
            {
                x += S(ChipGap);
                // +1 like the readouts: the reserved width is ceil'd from the measurement.
                DrawText(g, _cullProgress, _textFont, Theme.TextDim, new RectangleF(x, pill.Y, Math.Max(0, pill.Right - x + 1), pill.Height));
            }
        }

        // Small amber "VFR" tag: frame numbers on this file are estimates.
        private void PaintBadge(Graphics g, Part p)
        {
            Rectangle r = CenteredRect(p.Bounds, p.Bounds.Width, S(BadgeHeight));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            FillRounded(g, r, SF(3), Lerp(Theme.Panel, Theme.Warning, 0.16f));
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _brush.Color = Theme.Warning;
            g.DrawString(VfrText, _badgeFont, _brush, r, _fmtCenter);
        }

        private static Rectangle CenteredRect(Rectangle bounds, int w, int h)
        {
            w = Math.Min(w, bounds.Width);
            h = Math.Min(h, bounds.Height);
            return new Rectangle(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
        }

        private void PaintTime(Graphics g, Part p)
        {
            bool enabled = IsEnabled(p);
            PaintHoverBack(g, p, enabled);
            DrawText(g, _timeTexts[_timeForm], _textFont, enabled ? Theme.Text : Theme.TextDisabled, ReadoutTextRect(p));
        }

        private void PaintReadout(Graphics g, Part p, string text)
        {
            DrawText(g, text, _textFont, Theme.TextDim, ReadoutTextRect(p));
        }

        private RectangleF ReadoutTextRect(Part p)
        {
            int pad = S(ReadoutPad);
            // +1: the reserved width is ceil'd from the measurement; keep sub-pixel overhang from trimming.
            return new RectangleF(p.Bounds.X + pad, p.Bounds.Y, Math.Max(0, p.Bounds.Width - 2 * pad + 1), p.Bounds.Height);
        }

        private void PaintVolume(Graphics g, Part p)
        {
            RectangleF track = VolumeTrack(p);
            if (track.Width <= 0) return;
            double max = VolumeScaleMax;
            float frac = (float)Math.Max(0, Math.Min(1, _volumeValue / max));
            float radius = track.Height / 2f;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            FillRounded(g, track, radius, Theme.Track);
            if (frac > 0)
                FillRounded(g, new RectangleF(track.X, track.Y, track.Width * frac, track.Height), radius, _muted ? Theme.TextDisabled : Theme.Accent);

            if (max > 100)
            {
                // Subtle cut in the track at 100%: everything to its right is amplification.
                float cut = Math.Max(1f, (float)Math.Round(SF(2)));
                float mx = (float)Math.Round(track.X + track.Width * (100.0 / max) - cut / 2f);
                _brush.Color = Theme.Panel;
                g.FillRectangle(_brush, mx, track.Y - 1, cut, track.Height + 2);
            }

            float d = SF(10f + 3f * Math.Max(p.Hover, _volumeDragging ? 1f : 0f));
            float cx = track.X + track.Width * frac, cy = track.Y + track.Height / 2f;
            _brush.Color = _muted ? Theme.TextDim : Theme.Text;
            g.FillEllipse(_brush, cx - d / 2f, cy - d / 2f, d, d);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }

        private RectangleF VolumeTrack(Part p)
        {
            int pad = S(VolumePad);
            float h = Math.Max(2f, (float)Math.Round(SF(4)));
            float top = (float)Math.Round(p.Bounds.Y + (p.Bounds.Height - h) / 2f);
            return new RectangleF(p.Bounds.X + pad, top, Math.Max(0, p.Bounds.Width - 2 * pad), h);
        }

        private void PaintHoverBack(Graphics g, Part p, bool enabled)
        {
            if (!enabled) return;
            Color c;
            if (p == _pressed && p == _hover) c = Theme.Pressed;
            else if (p.Hover > 0.001f) c = Lerp(Theme.Panel, Theme.Hover, p.Hover);
            else return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            FillRounded(g, p.Bounds, SF(CornerRadius), c);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }

        private void FillRounded(Graphics g, RectangleF r, float radius, Color color)
        {
            _brush.Color = color;
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d < 1f) { g.FillRectangle(_brush, r); return; }
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(_brush, path);
            }
        }

        private void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect)
        {
            if (string.IsNullOrEmpty(text) || rect.Width <= 0) return;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _brush.Color = color;
            g.DrawString(text, font, _brush, rect, _fmtLeft);
        }

        private void DrawCenteredText(Graphics g, string text, Rectangle rect, Color color)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _brush.Color = color;
            g.DrawString(text, _buttonFont, _brush, rect, _fmtCenter);
        }

        private static Color Lerp(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        private void InvalidatePart(Part p)
        {
            if (p == null || !p.Visible || p.Bounds.IsEmpty) return;
            var r = p.Bounds;
            r.Inflate(S(2), S(2));   // anti-aliased edges and the growing volume thumb
            Invalidate(r);
        }

        // =====================================================================================
        // Input
        // =====================================================================================

        private static bool IsMediaButton(BarButton b)
        {
            switch (b)
            {
                case BarButton.PrevFile: case BarButton.FrameBack: case BarButton.PlayPause:
                case BarButton.FrameForward: case BarButton.NextFile: case BarButton.AbLoop:
                case BarButton.Speed: case BarButton.Zoom:
                    return true;
                default:
                    return false;
            }
        }

        private bool IsEnabled(Part p)
        {
            switch (p.Kind)
            {
                case PartKind.Time: return _hasMedia;
                case PartKind.Volume: return true;
                case PartKind.Button:
                    if (_hasMedia || !IsMediaButton(p.Button)) return true;
                    // Without a playable file, previous/next still make sense while a playlist exists
                    // (e.g. after a file failed to open).
                    return (p.Button == BarButton.PrevFile || p.Button == BarButton.NextFile) && _playlistText.Length > 0;
                default: return false;
            }
        }

        private Part HitTest(Point pt)
        {
            foreach (var p in _parts)
            {
                // Readouts are inert; an estimated frame number and the VFR badge only explain themselves (tooltip).
                if (!p.Visible || p.Kind == PartKind.PlaylistInfo || (p.Kind == PartKind.Frame && !_frameApproximate)) continue;
                if (p.Bounds.Contains(pt)) return p;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_volumeDragging)
            {
                SetVolumeFromX(e.X);
                return;
            }
            SetHover(HitTest(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_volumeDragging && _pressed == null) SetHover(null);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_pressed != null || _volumeDragging) { CancelTip(); return; }   // another button is already held
            var p = HitTest(e.Location);
            SetHover(p);
            CancelTip();   // like native buttons: no tip for the part just clicked until the mouse moves on
            if (p == null || !IsEnabled(p)) return;
            if (p.Kind == PartKind.Volume)
            {
                if (e.Button != MouseButtons.Left) return;
                _volumeDragging = true;
                InvalidatePart(p);
                SetVolumeFromX(e.X);
                return;
            }
            if (e.Button != MouseButtons.Left && (e.Button != MouseButtons.Right || p.Kind != PartKind.Button)) return;
            _pressed = p;
            _pressedButton = e.Button;
            InvalidatePart(p);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_volumeDragging)
            {
                if (e.Button != MouseButtons.Left) return;
                _volumeDragging = false;
                CancelTip();
                InvalidatePart(_volume);
                SetHover(HitTest(e.Location));
                return;
            }
            var p = _pressed;
            if (p == null || e.Button != _pressedButton) return;
            _pressed = null;
            InvalidatePart(p);
            if (HitTest(e.Location) != p || !IsEnabled(p)) return;

            if (p.Kind == PartKind.Time) TimeDisplayClicked?.Invoke();
            else ButtonClicked?.Invoke(p.Button, e.Button, RectangleToScreen(p.Bounds));
            if (IsDisposed || !IsHandleCreated) return;

            // A handler may have opened a menu or changed state; resync hover with the real cursor.
            Point now = PointToClient(MousePosition);
            SetHover(ClientRectangle.Contains(now) ? HitTest(now) : null);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture) return;
            // Capture stolen (menu, Alt+Tab, ...): abandon any press or drag in progress.
            if (_pressed != null) { InvalidatePart(_pressed); _pressed = null; }
            if (_volumeDragging) { _volumeDragging = false; CancelTip(); InvalidatePart(_volume); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var p = HitTest(e.Location);
            if (p != _volume && p != _buttons[(int)BarButton.Mute]) return;   // let the parent have it
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;

            // Accumulate partial deltas from precision touchpads; one notch = 120.
            _wheelRemainder += e.Delta;
            int notches = _wheelRemainder / SystemInformation.MouseWheelScrollDelta;
            if (notches == 0) return;
            _wheelRemainder -= notches * SystemInformation.MouseWheelScrollDelta;
            SetVolumeFromUser(_volumeValue + notches * VolumeWheelStep);
            if (p == _volume) ShowTip(_volume);
        }

        private void SetVolumeFromX(int x)
        {
            RectangleF track = VolumeTrack(_volume);
            if (track.Width <= 0) return;
            double frac = Math.Max(0, Math.Min(1, (x - track.X) / track.Width));
            SetVolumeFromUser(frac * VolumeScaleMax);
            ShowTip(_volume);
        }

        private void SetVolumeFromUser(double v)
        {
            v = Math.Max(0, Math.Min(VolumeScaleMax, Math.Round(v)));
            if (v == _volumeValue) return;
            _volumeValue = v;
            InvalidatePart(_volume);
            InvalidatePart(_buttons[(int)BarButton.Mute]);
            VolumeChanged?.Invoke(v);
        }

        private void SetHover(Part p)
        {
            if (p == _hover) return;
            _hover = p;
            bool tipWasRecent = _tipVisible ||
                (_tipHiddenTick.HasValue && unchecked(Environment.TickCount - _tipHiddenTick.Value) < TipReshowWindowMs);
            CancelTip();
            if (p != null && _pressed == null)
            {
                _tipPart = p;
                // Like native toolbars: once one tip is up, moving to a neighbour shows its tip at once.
                _tipTimer.Interval = tipWasRecent ? TipReshowMs : TipDelayMs;
                _tipTimer.Start();
            }
            StartAnimation();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) return;
            CancelTip();
            _hover = null;
            _pressed = null;
            _volumeDragging = false;
            foreach (var p in _parts) p.Hover = 0;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            CancelTip();
            Invalidate();
        }

        // =====================================================================================
        // Hover animation
        // =====================================================================================

        private void StartAnimation()
        {
            if (_animTimer.Enabled) return;
            _animLastTick = Environment.TickCount;
            _animTimer.Start();
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            int now = Environment.TickCount;
            float dt = Math.Max(1, Math.Min(100, unchecked(now - _animLastTick)));
            _animLastTick = now;
            bool active = false;
            foreach (var p in _parts)
            {
                float target = p == _hover && p.Visible && IsEnabled(p) ? 1f : 0f;
                if (p.Hover == target) continue;
                p.Hover = target > p.Hover
                    ? Math.Min(target, p.Hover + dt / FadeInMs)
                    : Math.Max(target, p.Hover - dt / FadeOutMs);
                InvalidatePart(p);
                if (p.Hover != target) active = true;
            }
            if (!active) _animTimer.Stop();
        }

        // =====================================================================================
        // Tooltips (one owner-drawn ToolTip, shown above the bar so it never covers the controls)
        // =====================================================================================

        private string TipText(Part p)
        {
            switch (p.Kind)
            {
                case PartKind.Button:
                    var provider = TooltipProvider;
                    string tip = provider != null ? provider(p.Button) : null;
                    // Without a text from MainForm the chip still describes the decision it shows.
                    return p.Button == BarButton.Cull && string.IsNullOrEmpty(tip) ? CullDescription() : tip;
                case PartKind.Frame:
                    return "Estimated frame number (variable frame rate)\nThe time is exact";
                case PartKind.Badge:
                    return "Variable frame rate: frame numbers are estimates (\u2248F)\nThe time is exact";
                case PartKind.Time:
                    string hint = _showRemaining ? "Show elapsed time" : "Show remaining time";
                    // A narrow bar shows a shortened readout; the tip carries the full one.
                    return _timeForm == 0 ? hint : _timeTexts[0] + "\n" + hint;
                case PartKind.Volume:
                    string v = "Volume " + ((int)Math.Round(_volumeValue)).ToString(CultureInfo.InvariantCulture) + "%";
                    return _muted ? v + " (muted)" : v;
                default:
                    return null;
            }
        }

        private string CullDescription()
        {
            string text = "Cull: " + (_cullFlag > 0 ? "Picked" : _cullFlag < 0 ? "Rejected" : "Undecided");
            if (_cullRating > 0)
                text += " \u00B7 " + _cullRating.ToString(CultureInfo.InvariantCulture) + (_cullRating == 1 ? " star" : " stars");
            if (_cullProgress.Length > 0) text += " \u00B7 " + _cullProgress;
            return text;
        }

        private void OnTipTimer(object sender, EventArgs e)
        {
            _tipTimer.Stop();
            var p = _tipPart;
            if (p != null && p == _hover && _pressed == null && !_volumeDragging) ShowTip(p);
        }

        private void ShowTip(Part p)
        {
            string text = p != null && p.Visible ? TipText(p) : null;
            if (string.IsNullOrEmpty(text) || !IsHandleCreated || !Visible)
            {
                CancelTip();
                return;
            }
            EnsureResources();
            SizeF sz = _measure.MeasureString(text, _textFont, PointF.Empty, _fmtMeasure);
            _tipSize = new Size(Ceil(sz.Width) + 2 * S(8), Ceil(sz.Height) + 2 * S(5));

            int anchor = p.Bounds.X + p.Bounds.Width / 2;
            if (p == _volume)
            {
                RectangleF track = VolumeTrack(p);
                anchor = (int)Math.Round(track.X + track.Width * Math.Max(0, Math.Min(1, _volumeValue / VolumeScaleMax)));
            }
            int x = Math.Max(0, Math.Min(Width - _tipSize.Width, anchor - _tipSize.Width / 2));
            int y = -_tipSize.Height - S(6);
            _tipPart = p;
            _tipVisible = true;
            _tip.Show(text, this, x, y);
        }

        // Re-show a visible tip whose text depends on state that just changed.
        private void RefreshVisibleTip(Part p)
        {
            if (_tipVisible && _tipPart == p) ShowTip(p);
        }

        private void CancelTip()
        {
            _tipTimer.Stop();
            if (!_tipVisible) return;
            _tipVisible = false;
            _tipHiddenTick = Environment.TickCount;
            if (IsHandleCreated) _tip.Hide(this);
        }

        private void OnTipDraw(object sender, DrawToolTipEventArgs e)
        {
            var g = e.Graphics;
            var b = e.Bounds;
            _brush.Color = Theme.PanelAlt;
            g.FillRectangle(_brush, b);
            _pen.Color = Theme.Border;
            g.DrawRectangle(_pen, b.X, b.Y, b.Width - 1, b.Height - 1);
            if (_textFont == null) return;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _brush.Color = Theme.Text;
            g.DrawString(e.ToolTipText, _textFont, _brush, b, _fmtCenter);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            CancelTip();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer.Dispose();
                _tipTimer.Dispose();
                _frameTimer.Dispose();
                _tip.Dispose();
                DisposeFonts();
                _brush.Dispose();
                _pen.Dispose();
                _measure.Dispose();
                _measureBitmap.Dispose();
                _fmtMeasure.Dispose();
                _fmtLeft.Dispose();
                _fmtCenter.Dispose();
            }
            base.Dispose(disposing);
        }

        private static double Finite(double v, double fallback = 0)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;
        }
    }
}
