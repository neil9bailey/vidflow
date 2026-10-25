using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Fully owner-drawn check box (the stock flat one paints a light box): rounded box,
    // accent fill with a dark tick when checked, hover/pressed/focus states, and an
    // optional dim note after the text, e.g. "(restart)".
    public class DarkCheckBox : CheckBox
    {
        private bool _hover, _pressed;
        private string _note;

        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            AutoSize = true;
            UseMnemonic = false;
        }

        // Dim text drawn after the label in parentheses.
        public string Note
        {
            get { return _note; }
            set { _note = value; Invalidate(); if (AutoSize && Parent != null) Parent.PerformLayout(this, "Note"); }
        }

        private int BoxSize { get { return DarkControls.Scale(this, 18); } }
        private int Gap { get { return DarkControls.Scale(this, 9); } }

        private string NoteText { get { return string.IsNullOrEmpty(_note) ? null : "(" + _note + ")"; } }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var ts = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, flags);
            int w = BoxSize + Gap + ts.Width + DarkControls.Scale(this, 2);
            if (NoteText != null) w += TextRenderer.MeasureText(" " + NoteText, Font, Size.Empty, flags).Width;
            int h = Math.Max(BoxSize, ts.Height) + DarkControls.Scale(this, 6);
            return DarkControls.Constrain(this, new Size(w, h));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_pressed) { _pressed = false; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(DarkControls.BackOf(this));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DarkControls.BackOf(this));
            bool enabled = Enabled;
            int box = BoxSize;
            var br = new Rectangle(DarkControls.Scale(this, 1), (Height - box) / 2, box, box);
            float radius = DarkControls.ScaleF(this, 4f);
            bool on = CheckState != CheckState.Unchecked;

            if (on)
            {
                Color fill = !enabled ? DarkControls.AccentDisabled : _pressed ? DarkControls.AccentPressed : _hover ? DarkControls.AccentHover : Theme.Accent;
                DarkControls.RoundedBox(g, br, radius, fill, null);
                Color mark = enabled ? DarkControls.OnAccent : DarkControls.OnAccentDisabled;
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var pen = DarkControls.Pen(mark, Math.Max(1.5f, DarkControls.ScaleF(this, 1.7f)));
                if (CheckState == CheckState.Indeterminate)
                {
                    float y = br.Top + box / 2f;
                    g.DrawLine(pen, br.Left + box * 0.28f, y, br.Right - box * 0.28f, y);
                }
                else
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(br.Left + box * 0.26f, br.Top + box * 0.52f),
                        new PointF(br.Left + box * 0.43f, br.Top + box * 0.69f),
                        new PointF(br.Left + box * 0.75f, br.Top + box * 0.34f),
                    });
                }
                g.SmoothingMode = old;
            }
            else
            {
                Color fill = !enabled ? Theme.Panel : _pressed ? Theme.Pressed : _hover ? Theme.Hover : Theme.PanelAlt;
                Color border = !enabled ? Theme.Border : _hover ? DarkControls.CheckBorderHover : DarkControls.CheckBorder;
                DarkControls.RoundedBox(g, br, radius, fill, border);
            }

            if (Focused && ShowFocusCues && enabled)
            {
                var fr = Rectangle.Inflate(br, DarkControls.Scale(this, 2), DarkControls.Scale(this, 2));
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = DarkControls.RoundRect(new RectangleF(fr.X + 0.5f, fr.Y + 0.5f, fr.Width - 1f, fr.Height - 1f), radius + 2))
                    g.DrawPath(DarkControls.Pen(Theme.Text), path);
                g.SmoothingMode = old;
            }

            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            int x = br.Right + Gap;
            var textColor = enabled ? ForeColor : Theme.TextDisabled;
            var tr = new Rectangle(x, 0, Math.Max(0, Width - x), Height);
            TextRenderer.DrawText(g, Text, Font, tr, textColor, flags);
            if (NoteText != null)
            {
                int tw = TextRenderer.MeasureText(g, Text ?? "", Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                var nr = new Rectangle(x + tw, 0, Math.Max(0, Width - x - tw), Height);
                TextRenderer.DrawText(g, " " + NoteText, Font, nr, enabled ? Theme.TextDim : Theme.TextDisabled, flags);
            }
        }
    }
}
