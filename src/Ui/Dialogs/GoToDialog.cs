using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui.Dialogs
{
    public static class GoToDialog
    {
        // Lets the user type a time ("1:23.456", "83.4", "-2" = 2 s before end) or a frame number
        // (frame mode / "f123"). Returns the target time in seconds, or null if cancelled.
        public static double? Show(IWin32Window owner, double position, double duration, double fps)
        {
            using (var w = new Window(position, duration, fps))
                return w.ShowDialog(owner) == DialogResult.OK ? w.Result : null;
        }

        internal sealed class Window : DarkDialog
        {
            // mpv's hr-seek keeps frames up to 5 ms before the target, so a target time shows
            // the first frame whose pts >= target - 5 ms.
            private const double HrSeekTolerance = 0.005;

            private readonly double _position, _duration, _fps;
            private readonly DarkButton _timeMode, _frameMode, _ok;
            private readonly DarkTextBox _input;
            private readonly Label _status;
            private bool _frame;
            private string _timeText, _frameText;

            public Window(double position, double duration, double fps)
            {
                _duration = IsUsable(duration) && duration > 0 ? duration : 0;
                _fps = IsUsable(fps) && fps > 0 ? fps : 0;
                _position = IsUsable(position) ? Math.Max(0, position) : 0;
                if (_duration > 0) _position = Math.Min(_position, _duration);
                _timeText = Fmt.Time(_position, true);
                _frameText = _fps > 0 ? ((long)Math.Round(_position * _fps)).ToString(CultureInfo.InvariantCulture) : "";

                Text = "Go To";
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var root = CreateTable(1);
                root.RowCount = 2;
                var content = CreateTable(1);
                content.RowCount = 5;
                OnDpi(() => content.Padding = Pad(24, 18, 24, 20));

                var modes = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    WrapContents = false,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty,
                    BackColor = Color.Transparent,
                };
                _timeMode = new DarkButton { Text = "&Time", Glyph = "" };   // Stopwatch
                _frameMode = new DarkButton { Text = "&Frame", Glyph = "" }; // Film strip
                foreach (var b in new[] { _timeMode, _frameMode })
                {
                    var btn = b;
                    OnDpi(() =>
                    {
                        btn.MinimumSize = new Size(S(96), 0);
                        btn.Margin = Pad(0, 0, 6, 0);
                    });
                    modes.Controls.Add(btn);
                }
                _frameMode.Enabled = _fps > 0;
                _timeMode.Click += (s, e) => SetMode(false);
                _frameMode.Click += (s, e) => SetMode(true);
                content.Controls.Add(modes, 0, 0);

                _input = CreateTextBox(_timeText, 320);
                OnDpi(() =>
                {
                    _input.Font = GetFont(11f);
                    _input.Margin = Pad(0, 14, 0, 0);
                });
                _input.TextChanged += (s, e) => UpdateStatus();
                content.Controls.Add(_input, 0, 1);

                _status = CreateLabel("", true);
                OnDpi(() => _status.Margin = Pad(1, 8, 0, 0));
                content.Controls.Add(_status, 0, 2);

                var hint = CreateWrappingLabel("e.g.  1:23.5  ·  83.4  ·  -2 (from the end)  ·  f123 (frame)", 320, true, 8.5f);
                OnDpi(() => hint.Margin = Pad(1, 14, 0, 0));
                content.Controls.Add(hint, 0, 3);

                var info = CreateWrappingLabel(InfoText(), 320, true, 8.5f);
                OnDpi(() => info.Margin = Pad(1, 4, 0, 0));
                content.Controls.Add(info, 0, 4);

                _ok = CreateButton("Go", true, DialogResult.None);
                _ok.Click += (s, e) => Accept();
                var cancel = CreateButton("Cancel", false, DialogResult.Cancel);
                root.Controls.Add(content, 0, 0);
                root.Controls.Add(CreateFooter(null, _ok, cancel), 0, 1);
                Controls.Add(root);
                AcceptButton = _ok;
                CancelButton = cancel;

                SetMode(false);
                ActiveControl = _input.TextBox;
            }

            public double? Result { get; private set; }

            private static bool IsUsable(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

            private string InfoText()
            {
                string s = "Now " + Fmt.Time(_position, true);
                if (_duration > 0) s += "  ·  Length " + Fmt.Time(_duration, true);
                if (_fps > 0)
                {
                    s += "  ·  " + Fmt.Num(_fps) + " fps";
                    if (_duration > 0) s += "  ·  " + TotalFrames.ToString("N0", CultureInfo.InvariantCulture) + " frames";
                }
                return s;
            }

            // Frames that start before the end (matches mpv's estimated-frame-count; rounding up
            // would count a frame past EOF, e.g. 12.023 s at 24 fps = 288, not 289).
            private long TotalFrames { get { return Math.Max(1, (long)Math.Floor(_duration * _fps + 1e-6)); } }

            // Without a frame rate (audio-only files), stay this far before the end.
            private const double EndMargin = 0.05;

            // Latest seekable time: the start of the last frame when the rate is known, else a
            // little before the end (seeking to the very end trips end-of-file handling: the
            // file loops or the next one starts).
            private double MaxTime
            {
                get
                {
                    if (_duration <= 0) return double.MaxValue;
                    return _fps > 0 ? Math.Max(0, (TotalFrames - 1) / _fps) : Math.Max(0, _duration - EndMargin);
                }
            }

            private long FrameAt(double t)
            {
                return Math.Max(0, (long)Math.Ceiling((t - HrSeekTolerance) * _fps - 1e-9));
            }

            private void SetMode(bool frame)
            {
                if (frame && _fps <= 0) frame = false;
                if (_frame) _frameText = _input.Text; else _timeText = _input.Text;
                _frame = frame;
                _timeMode.Checked = !frame;
                _frameMode.Checked = frame;
                _input.Text = frame ? _frameText : _timeText;
                _input.Cue = frame ? "Frame number" : "Time or frame";
                _input.Focus();
                _input.SelectAll();
                UpdateStatus();
            }

            private bool Resolve(out double seconds, out string error, out bool clamped)
            {
                seconds = 0;
                error = null;
                clamped = false;
                string s = (_input.Text ?? "").Trim();
                if (s.Length == 0)
                {
                    error = _frame ? "Enter a frame number." : "Enter a time or a frame number.";
                    return false;
                }
                bool asFrame = _frame;
                if (s[0] == 'f' || s[0] == 'F' || s[0] == '#')
                {
                    asFrame = true;
                    s = s.Substring(1).Trim();
                }
                bool fromEnd = s.StartsWith("-");
                if (asFrame)
                {
                    if (_fps <= 0) { error = "Frame numbers need a known frame rate."; return false; }
                    long n;
                    string digits = fromEnd ? s.Substring(1).Trim() : s;
                    if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n > 100000000)
                    {
                        error = "Enter a whole frame number, e.g. 123.";
                        return false;
                    }
                    if (fromEnd)
                    {
                        if (_duration <= 0) { error = "The length is unknown, so frames can't count from the end."; return false; }
                        n = TotalFrames - n;
                    }
                    seconds = n / _fps;
                }
                else
                {
                    double t;
                    if (!Fmt.TryParseTime(s, out t))
                    {
                        error = "Use a time like 1:23.5, 83.4 or -2, or a frame like f123.";
                        return false;
                    }
                    if (fromEnd)
                    {
                        if (_duration <= 0) { error = "The length is unknown, so times can't count from the end."; return false; }
                        t = _duration + t;
                    }
                    seconds = t;
                }
                double max = MaxTime;
                clamped = seconds < 0 || seconds > max + 1e-9;
                seconds = Math.Max(0, Math.Min(max, seconds));
                return true;
            }

            private void UpdateStatus()
            {
                double t;
                string error;
                bool clamped;
                bool ok = Resolve(out t, out error, out clamped);
                _ok.Enabled = ok;
                _input.Invalid = !ok && (_input.Text ?? "").Trim().Length > 0;
                if (!ok)
                {
                    _status.ForeColor = Theme.Warning;
                    _status.Text = error;
                    return;
                }
                string text = "→ " + Fmt.Time(t, true);
                if (_fps > 0) text += "  (F " + FrameAt(t).ToString(CultureInfo.InvariantCulture) + ")";
                bool atEnd = _duration > 0 && t >= MaxTime - 1e-9;
                if (clamped && t <= 0) text += "  (start)";
                else if (atEnd) text += "  (end)";
                _status.ForeColor = Theme.TextDim;
                _status.Text = text;
            }

            private void Accept()
            {
                double t;
                string error;
                bool clamped;
                if (!Resolve(out t, out error, out clamped)) return;
                Result = t;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
