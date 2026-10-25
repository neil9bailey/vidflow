using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    public static class ConfirmDialog
    {
        private const string WarningGlyph = "";

        // Dark Yes/No confirmation. Returns true for Yes. If `dontAskAgain` is non-null a
        // "Don't ask again" checkbox is shown and its final state written back.
        public static bool Show(IWin32Window owner, string title, string message, string yesText, string noText, ref bool? dontAskAgain)
        {
            using (var w = new Window(title, message, yesText, noText, dontAskAgain))
            {
                bool yes = w.ShowDialog(owner) == DialogResult.Yes;
                if (dontAskAgain.HasValue) dontAskAgain = w.DontAskAgain;
                return yes;
            }
        }

        internal sealed class Window : DarkDialog
        {
            private readonly DarkCheckBox _dontAsk;

            public Window(string title, string message, string yesText, string noText, bool? dontAskAgain)
            {
                Text = string.IsNullOrEmpty(title) ? "VidFlow Player" : title;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var root = CreateTable(1);
                root.RowCount = 2;

                var content = CreateTable(2);
                content.RowCount = 2;
                var glyph = new Label
                {
                    Text = WarningGlyph,
                    AutoSize = true,
                    ForeColor = Theme.Warning,
                    BackColor = Color.Transparent,
                    UseMnemonic = false,
                };
                OnDpi(() =>
                {
                    glyph.Font = GetFont(20f, FontStyle.Regular, Theme.IconFontFamily);
                    glyph.Margin = Pad(0, 2, 16, 0);
                    content.Padding = Pad(24, 22, 28, 20);
                });
                content.Controls.Add(glyph, 0, 0);
                content.SetRowSpan(glyph, 2);

                var text = CreateWrappingLabel(message ?? "", 380, false, 10f);
                OnDpi(() => text.Margin = Pad(0, 4, 0, 4));
                content.Controls.Add(text, 1, 0);

                if (dontAskAgain.HasValue)
                {
                    _dontAsk = CreateCheckBox("Don't ask again", dontAskAgain.Value);
                    OnDpi(() => _dontAsk.Margin = Pad(0, 14, 0, 0));
                    content.Controls.Add(_dontAsk, 1, 1);
                }

                var yes = CreateButton(string.IsNullOrEmpty(yesText) ? "Yes" : yesText, true, DialogResult.Yes);
                var no = CreateButton(string.IsNullOrEmpty(noText) ? "No" : noText, false, DialogResult.No);
                root.Controls.Add(content, 0, 0);
                root.Controls.Add(CreateFooter(null, yes, no), 0, 1);
                Controls.Add(root);
                AcceptButton = yes;
                CancelButton = no;
                ActiveControl = yes;
            }

            public bool DontAskAgain { get { return _dontAsk != null && _dontAsk.Checked; } }

            protected override bool HandleEscape()
            {
                DialogResult = DialogResult.No;
                Close();
                return true;
            }
        }
    }
}
