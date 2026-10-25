using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Base for all dialogs: dark theme, Segoe UI 9pt, DPI-aware layout, Esc = cancel,
    // centred on owner, no taskbar button.
    //
    // Sizes are written in logical 96-dpi pixels (S(), Pad(), OnDpi()) and resolved against
    // the DPI of the monitor the window is actually on (GetDpiForWindow / WM_DPICHANGED).
    // WinForms' own auto-scaling is switched off so nothing is scaled twice; fonts are
    // created in pixel units for the current DPI and re-created when it changes.
    public class DarkDialog : Form
    {
        private const int WM_DPICHANGED = 0x02E0;

        private int _dpi;
        private readonly List<Action> _dpiActions = new List<Action>();
        private Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        public DarkDialog()
        {
            _dpi = Theme.SystemDpi;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            KeyPreview = true;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = GetFont(9f);
        }

        // DPI this dialog is currently laid out for.
        public int Dpi { get { return _dpi; } }

        public int S(int logicalPx) { return (int)Math.Round(logicalPx * _dpi / 96.0); }
        public Padding Pad(int left, int top, int right, int bottom) { return new Padding(S(left), S(top), S(right), S(bottom)); }

        // Font for the current DPI, cached until the DPI changes. Callers must (re)assign
        // fonts inside OnDpi() blocks: the previous DPI's fonts are disposed after a change.
        public Font GetFont(float pt, FontStyle style = FontStyle.Regular, string family = "Segoe UI")
        {
            string key = family + "|" + pt.ToString(CultureInfo.InvariantCulture) + "|" + (int)style;
            Font f;
            if (!_fonts.TryGetValue(key, out f))
            {
                f = new Font(family, pt * _dpi / 72f, style, GraphicsUnit.Pixel);
                _fonts[key] = f;
            }
            return f;
        }

        // Registers DPI-dependent layout code; runs it now and again after every DPI change.
        protected void OnDpi(Action apply)
        {
            if (apply == null) return;
            _dpiActions.Add(apply);
            apply();
        }

        // Called after a DPI change has been applied (fonts, paddings, sizes).
        protected virtual void OnDpiApplied() { }

        // Return true to consume Esc (e.g. while recording a shortcut).
        protected virtual bool HandleEscape() { return false; }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            // ProcessDialogKey runs after controls had their chance (an open combo list
            // closes on Esc first), unlike ProcessCmdKey.
            if (keyData == Keys.Escape)
            {
                if (!HandleEscape())
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkControls.UseDarkTitleBar(Handle, Theme.Window, Theme.Border);
            ApplyDpi(DarkControls.WindowDpi(Handle, _dpi));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                base.WndProc(ref m);
                if (dpi > 0 && dpi != _dpi)
                {
                    ApplyDpi(dpi);
                    if (AutoSize) Location = new Point(r.Left, r.Top);
                    else SetBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // Re-lays the dialog out for a new DPI. Internal so harnesses can preview other scales.
        internal void ApplyDpi(int dpi)
        {
            if (dpi <= 0 || dpi == _dpi) return;
            _dpi = dpi;
            var old = _fonts;
            _fonts = new Dictionary<string, Font>();
            SuspendLayout();
            try
            {
                Font = GetFont(9f);
                foreach (var a in _dpiActions) a();
                OnDpiApplied();
            }
            finally
            {
                ResumeLayout(true);
            }
            foreach (var f in old.Values) f.Dispose();
            Invalidate(true);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                foreach (var f in _fonts.Values) f.Dispose();
                _fonts.Clear();
            }
        }

        // ---- themed control factories ----------------------------------------------

        protected DarkButton CreateButton(string text, bool accent = false, DialogResult result = DialogResult.None)
        {
            var b = new DarkButton { Text = text, Accent = accent, DialogResult = result };
            OnDpi(() =>
            {
                b.MinimumSize = new Size(S(88), 0);
                b.Margin = Pad(4, 0, 0, 0);
            });
            return b;
        }

        protected Label CreateLabel(string text, bool dim = false, float pt = 9f, FontStyle style = FontStyle.Regular)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = dim ? Theme.TextDim : Theme.Text,
                BackColor = Color.Transparent,
                UseMnemonic = false,
            };
            if (pt != 9f || style != FontStyle.Regular) OnDpi(() => l.Font = GetFont(pt, style));
            OnDpi(() => l.Margin = Pad(0, 3, 0, 3));
            return l;
        }

        // Label that wraps at maxWidth logical pixels.
        protected Label CreateWrappingLabel(string text, int maxWidth, bool dim = false, float pt = 9f)
        {
            var l = CreateLabel(text, dim, pt);
            OnDpi(() => l.MaximumSize = new Size(S(maxWidth), 0));
            return l;
        }

        // Small, semibold heading above a group of settings.
        protected Label CreateSectionLabel(string text)
        {
            var l = CreateLabel(text, false, 9.5f, FontStyle.Bold);
            OnDpi(() => l.Margin = Pad(0, 14, 0, 6));
            return l;
        }

        protected DarkCheckBox CreateCheckBox(string text, bool isChecked, string note = null)
        {
            var c = new DarkCheckBox { Text = text, Checked = isChecked, Note = note };
            OnDpi(() => c.Margin = Pad(0, 3, 0, 3));
            return c;
        }

        protected DarkTextBox CreateTextBox(string text, int width = 0, string cue = null)
        {
            var t = new DarkTextBox { Text = text ?? "", Cue = cue };
            OnDpi(() =>
            {
                if (width > 0) t.Width = S(width);
                t.Margin = Pad(0, 3, 0, 3);
            });
            return t;
        }

        protected DarkComboBox CreateComboBox(int width, params string[] items)
        {
            var c = new DarkComboBox();
            if (items != null) c.Items.AddRange(items);
            OnDpi(() =>
            {
                c.Width = S(width);
                c.Margin = Pad(0, 3, 0, 3);
            });
            return c;
        }

        protected DarkNumericBox CreateNumericBox(decimal min, decimal max, decimal value, decimal increment, int decimals, string suffix, int width = 120)
        {
            var n = new DarkNumericBox { Minimum = min, Maximum = max, Increment = increment, DecimalPlaces = decimals, Suffix = suffix };
            n.Value = value;
            OnDpi(() =>
            {
                n.Width = S(width);
                n.Margin = Pad(0, 3, 0, 3);
            });
            return n;
        }

        protected DarkTable CreateTable(int columns)
        {
            var t = new DarkTable { ColumnCount = Math.Max(1, columns), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.Margin = Padding.Empty;
            t.Padding = Padding.Empty;
            return t;
        }

        // Bottom strip: optional left-hand content, buttons right-aligned in the given order.
        protected DarkTable CreateFooter(Control left, params Control[] buttons)
        {
            var footer = new DarkTable
            {
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Panel,
                TopBorder = true,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (left != null)
            {
                left.Anchor = AnchorStyles.Left;
                footer.Controls.Add(left, 0, 0);
            }
            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Right,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent,
            };
            foreach (var b in buttons) flow.Controls.Add(b);
            footer.Controls.Add(flow, 1, 0);
            OnDpi(() => footer.Padding = Pad(16, 12, 16, 12));
            return footer;
        }
    }
}
