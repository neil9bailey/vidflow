using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui.Dialogs
{
    // What "Apply Decisions" is about to do, as CullCommitDialog shows it.
    public sealed class CullCommitSummary
    {
        public int PickCount { get; set; }
        public long PickBytes { get; set; }
        public string PickAction { get; set; } = "";     // e.g. "Move into a “Keep” folder next to each clip"
        public int RejectCount { get; set; }
        public long RejectBytes { get; set; }
        public string RejectAction { get; set; } = "";   // e.g. "Send to the Recycle Bin"
        public int PermanentDeletes { get; set; }        // rejects on drives without a Recycle Bin
        public int AlreadyKept { get; set; }             // picks already in their Keep folder (not counted above, left alone)
        public int AlreadyRejected { get; set; }         // rejects already in their Rejected folder (likewise)
        public IList<string> Notes { get; } = new List<string>(); // name conflicts, skipped clips (one line each)
    }

    public static class CullCommitDialog
    {
        // Shows the summary. Apply runs prepare() on the UI thread (release the playing file), then
        // work(progress, stopRequested) on a background thread while a progress bar runs; progress takes
        // (fraction 0..1, status text) and may be called from that thread. work returns a report: null or
        // empty when everything went through (the dialog closes), otherwise text that stays on screen
        // until the user closes it. Returns true when the batch ran (completely or until stopped), false
        // when the user dismissed the summary.
        public static bool Show(IWin32Window owner, CullCommitSummary summary, Action prepare,
                                Func<Action<double, string>, Func<bool>, string> work)
        {
            if (summary == null) throw new ArgumentNullException("summary");
            using (var w = new Window(summary, prepare, work))
            {
                w.ShowDialog(owner);
                return w.Ran;
            }
        }

        internal sealed class Window : DarkDialog
        {
            private const string PickGlyph = "", RejectGlyph = "", WarnGlyph = "", InfoGlyph = "";
            private static readonly Color PickColor = Color.FromArgb(76, 194, 120);
            private static readonly Color RejectColor = Color.FromArgb(232, 88, 88);

            private readonly Action _prepare;
            private readonly Func<Action<double, string>, Func<bool>, string> _work;
            private readonly DarkButton _apply, _cancel;
            private readonly ProgressStrip _bar;
            private readonly Label _status;
            private volatile bool _stop;
            private bool _running;
            private int _progressPosted;
            private double _progress;
            private string _progressText = "";
            private readonly object _gate = new object();

            public bool Ran { get; private set; }

            public Window(CullCommitSummary s, Action prepare, Func<Action<double, string>, Func<bool>, string> work)
            {
                _prepare = prepare;
                _work = work;
                Text = "Apply Decisions";
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var root = CreateTable(1);
                root.RowCount = 2;
                var content = CreateTable(2);
                OnDpi(() => content.Padding = Pad(24, 20, 28, 18));
                int row = 0;

                AddLine(content, ref row, PickGlyph, PickColor,
                    Count(s.PickCount, "pick", "picks") + (s.PickCount > 0 ? " · " + Fmt.Bytes(s.PickBytes) : ""),
                    s.PickCount > 0 ? s.PickAction : "Nothing to keep", true);
                // Clips already where Apply would put them are not counted above; say so on their own line.
                if (s.AlreadyKept > 0)
                    AddLine(content, ref row, null, Theme.TextDim, Count(s.AlreadyKept, "pick is", "picks are") + " already in the Keep folder: " +
                            (s.AlreadyKept == 1 ? "left where it is." : "left where they are."), null, false);
                AddLine(content, ref row, RejectGlyph, RejectColor,
                    Count(s.RejectCount, "reject", "rejects") + (s.RejectCount > 0 ? " · " + Fmt.Bytes(s.RejectBytes) : ""),
                    s.RejectCount > 0 ? s.RejectAction : "Nothing to remove", true);
                if (s.AlreadyRejected > 0)
                    AddLine(content, ref row, null, Theme.TextDim, Count(s.AlreadyRejected, "reject is", "rejects are") + " already in the Rejected folder: " +
                            (s.AlreadyRejected == 1 ? "left where it is." : "left where they are."), null, false);
                if (s.PermanentDeletes > 0)
                {
                    string msg = (s.PermanentDeletes == 1 ? "1 reject is" : s.PermanentDeletes + " rejects are") +
                                 " on a drive without a Recycle Bin (a network share, removable or optical drive) and will be " +
                                 "deleted permanently. This cannot be undone.";
                    AddLine(content, ref row, WarnGlyph, Theme.Warning, msg, null, false);
                }
                int shown = 0;
                foreach (var note in s.Notes)
                {
                    if (string.IsNullOrEmpty(note)) continue;
                    if (shown == 6)
                    {
                        AddLine(content, ref row, null, Theme.TextDim, "…and " + (s.Notes.Count - shown) + " more", null, false);
                        break;
                    }
                    AddLine(content, ref row, shown == 0 ? InfoGlyph : null, Theme.TextDim, note, null, false);
                    shown++;
                }

                var undo = CreateWrappingLabel("Moves can be undone afterwards with Ctrl+Z. Files sent to the Recycle Bin can only be " +
                                               "restored from the Recycle Bin itself.", 400, true);
                OnDpi(() => undo.Margin = Pad(0, 12, 0, 0));
                content.Controls.Add(undo, 1, row++);

                _bar = new ProgressStrip { Visible = false };
                OnDpi(() =>
                {
                    _bar.Size = new Size(S(400), S(6));
                    _bar.Margin = Pad(0, 16, 0, 0);
                });
                content.Controls.Add(_bar, 1, row++);
                _status = CreateWrappingLabel("", 400, true);
                _status.Visible = false;
                OnDpi(() => _status.Margin = Pad(0, 6, 0, 0));
                content.Controls.Add(_status, 1, row++);
                content.RowCount = row;

                bool nothing = s.PickCount == 0 && s.RejectCount == 0;
                _apply = CreateButton(s.PermanentDeletes > 0 ? "Apply and Delete" : "Apply", true);
                _apply.Enabled = !nothing;
                _apply.Click += (o, e) => Start();
                _cancel = CreateButton("Cancel", false);
                _cancel.Click += (o, e) => OnCancelClick();
                root.Controls.Add(content, 0, 0);
                root.Controls.Add(CreateFooter(null, _apply, _cancel), 0, 1);
                Controls.Add(root);
                AcceptButton = _apply;
                ActiveControl = nothing ? _cancel : _apply;
            }

            private static string Count(int n, string one, string many)
            {
                return n == 1 ? "1 " + one : n + " " + many;
            }

            // One glyph + text row; detail (dim) goes on a second line under the text.
            private void AddLine(DarkTable t, ref int row, string glyph, Color color, string text, string detail, bool strong)
            {
                bool first = row == 0;
                if (glyph != null)
                {
                    var g = new Label
                    {
                        Text = glyph,
                        AutoSize = true,
                        ForeColor = color,
                        BackColor = Color.Transparent,
                        UseMnemonic = false,
                    };
                    OnDpi(() =>
                    {
                        g.Font = GetFont(12f, FontStyle.Regular, Theme.IconFontFamily);
                        g.Margin = Pad(0, first ? 2 : 10, 12, 0);
                    });
                    t.Controls.Add(g, 0, row);
                }
                int top = first ? 0 : glyph == null ? 2 : 10;
                var main = strong ? CreateLabel(text, false, 10f, FontStyle.Bold) : CreateWrappingLabel(text, 400, color == Theme.TextDim);
                if (!strong && color == Theme.Warning) main.ForeColor = Theme.Warning;
                OnDpi(() =>
                {
                    main.Margin = Pad(0, top, 0, 0);
                    if (strong) main.MaximumSize = new Size(S(400), 0);
                });
                t.Controls.Add(main, 1, row++);
                if (!string.IsNullOrEmpty(detail))
                {
                    var d = CreateWrappingLabel(detail, 400, true);
                    OnDpi(() => d.Margin = Pad(0, 2, 0, 0));
                    t.Controls.Add(d, 1, row++);
                }
            }

            private void Start()
            {
                if (_running || Ran) return;
                _running = true;
                _apply.Enabled = false;
                _cancel.Text = "Stop";
                _bar.Visible = true;
                _status.Visible = true;
                _status.Text = "Starting…";
                if (_prepare != null) _prepare();
                var t = new Thread(Worker) { IsBackground = true, Name = "VidFlow cull apply" };
                t.SetApartmentState(ApartmentState.STA); // the shell's delete (Recycle Bin) wants STA
                t.Start();
            }

            private void Worker()
            {
                string report;
                try
                {
                    report = _work != null ? _work(Report, () => _stop) : null;
                }
                catch (Exception ex)
                {
                    Log.Error("cull apply failed", ex);
                    report = "Stopped by an error: " + ex.Message;
                }
                try { BeginInvoke((MethodInvoker)(() => Finish(report))); }
                catch (InvalidOperationException) { } // window already gone
            }

            // Worker thread: remember the latest values, repaint at most once per UI message.
            private void Report(double fraction, string text)
            {
                lock (_gate)
                {
                    _progress = Math.Max(0, Math.Min(1, fraction));
                    _progressText = text ?? "";
                }
                if (Interlocked.Exchange(ref _progressPosted, 1) == 1) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        Interlocked.Exchange(ref _progressPosted, 0);
                        double p;
                        string s;
                        lock (_gate) { p = _progress; s = _progressText; }
                        _bar.Value = p;
                        if (!_stop) _status.Text = s;
                    }));
                }
                catch (InvalidOperationException) { }
            }

            private void Finish(string report)
            {
                _running = false;
                Ran = true;
                _bar.Value = 1;
                if (string.IsNullOrEmpty(report))
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                _status.ForeColor = Theme.Text;
                _status.Text = report;
                _apply.Visible = false;
                _cancel.Text = "Close";
                AcceptButton = _cancel;
                ActiveControl = _cancel;
            }

            private void OnCancelClick()
            {
                if (_running)
                {
                    RequestStop();
                    return;
                }
                DialogResult = Ran ? DialogResult.OK : DialogResult.Cancel;
                Close();
            }

            private void RequestStop()
            {
                if (_stop) return;
                _stop = true;
                _cancel.Enabled = false;
                _status.Text = "Stopping after the current file…";
            }

            protected override bool HandleEscape()
            {
                if (!_running) return false;
                RequestStop();
                return true;
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                // The batch must finish (or stop between files) before the window goes.
                if (_running)
                {
                    e.Cancel = true;
                    RequestStop();
                }
                base.OnFormClosing(e);
            }
        }

        // Thin rounded progress bar in the app's accent colour.
        private sealed class ProgressStrip : Control
        {
            private double _value;

            public ProgressStrip()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                BackColor = Color.Transparent;
            }

            public double Value
            {
                get { return _value; }
                set
                {
                    double v = double.IsNaN(value) ? 0 : Math.Max(0, Math.Min(1, value));
                    if (Math.Abs(v - _value) < 0.0005) return;
                    _value = v;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var r = new RectangleF(0, 0, Width, Height);
                float radius = Height / 2f;
                using (var track = DarkControls.RoundRect(r, radius)) g.FillPath(DarkControls.Brush(Theme.Track), track);
                float w = (float)(Width * _value);
                if (w < 1) return;
                using (var fill = DarkControls.RoundRect(new RectangleF(0, 0, Math.Max(w, Height), Height), radius))
                    g.FillPath(DarkControls.Brush(Theme.Accent), fill);
            }
        }
    }
}
