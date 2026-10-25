using System;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    public static class TextInputDialog
    {
        // Single-line prompt (used for "Open URL or Path"). Returns null if cancelled.
        public static string Show(IWin32Window owner, string title, string prompt, string initialText)
        {
            return Show(owner, title, prompt, initialText, -1, 0);
        }

        // Same, with an initial selection (e.g. the name part of "clip.mp4" for a rename). A negative
        // selectionStart (or one outside the text) selects everything, like the overload above.
        public static string Show(IWin32Window owner, string title, string prompt, string initialText, int selectionStart, int selectionLength)
        {
            using (var w = new Window(title, prompt, initialText, selectionStart, selectionLength))
                return w.ShowDialog(owner) == DialogResult.OK ? w.Value : null;
        }

        internal sealed class Window : DarkDialog
        {
            private readonly DarkTextBox _text;
            private readonly DarkButton _ok;

            private readonly int _selStart, _selLength;

            public Window(string title, string prompt, string initialText, int selectionStart = -1, int selectionLength = 0)
            {
                _selStart = selectionStart;
                _selLength = selectionLength;
                Text = string.IsNullOrEmpty(title) ? "VidFlow Player" : title;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var root = CreateTable(1);
                root.RowCount = 2;
                var content = CreateTable(1);
                content.RowCount = 2;
                OnDpi(() => content.Padding = Pad(24, 20, 24, 22));

                if (!string.IsNullOrEmpty(prompt))
                {
                    var label = CreateWrappingLabel(prompt, 460);
                    OnDpi(() => label.Margin = Pad(0, 0, 0, 8));
                    content.Controls.Add(label, 0, 0);
                }
                _text = CreateTextBox(initialText ?? "", 460);
                content.Controls.Add(_text, 0, 1);

                _ok = CreateButton("OK", true, DialogResult.OK);
                var cancel = CreateButton("Cancel", false, DialogResult.Cancel);
                root.Controls.Add(content, 0, 0);
                root.Controls.Add(CreateFooter(null, _ok, cancel), 0, 1);
                Controls.Add(root);
                AcceptButton = _ok;
                CancelButton = cancel;

                _text.TextChanged += (s, e) => _ok.Enabled = Value.Length > 0;
                _ok.Enabled = Value.Length > 0;
                ActiveControl = _text.TextBox;
                Shown += (s, e) => ApplySelection();
            }

            private void ApplySelection()
            {
                var box = _text.TextBox;
                int length = box.TextLength;
                if (_selStart < 0 || _selStart > length) box.SelectAll();
                else box.Select(_selStart, Math.Max(0, Math.Min(_selLength, length - _selStart)));
            }

            public string Value { get { return (_text.Text ?? "").Trim(); } }
        }
    }
}
