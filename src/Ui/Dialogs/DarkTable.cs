using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Double-buffered TableLayoutPanel (no flicker while resizing) with an optional
    // hairline along its top edge (dialog footers).
    public class DarkTable : TableLayoutPanel
    {
        private bool _topBorder;

        public DarkTable()
        {
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
        }

        public bool TopBorder
        {
            get { return _topBorder; }
            set { _topBorder = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_topBorder) e.Graphics.FillRectangle(DarkControls.Brush(Theme.Border), 0, 0, Width, 1);
        }
    }
}
