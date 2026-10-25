using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Dark replacement for the native Undo / Cut / Copy / Paste / Delete / Select All menu of
    // text fields (Windows always draws that one light). One shared menu serves every field:
    // each time it opens it targets the native edit window it was opened on and enables only
    // what applies there (read-only, selection, clipboard, undo buffer). Working on the edit
    // window itself means editable combo boxes are covered as well.
    internal static class EditMenu
    {
        private const int WM_CUT = 0x0300, WM_COPY = 0x0301, WM_PASTE = 0x0302, WM_CLEAR = 0x0303, WM_UNDO = 0x0304;
        private const int EM_GETSEL = 0x00B0, EM_SETSEL = 0x00B1, EM_CANUNDO = 0x00C6;
        private const int GWL_STYLE = -16, ES_PASSWORD = 0x0020, ES_READONLY = 0x0800;

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, out int wParam, out int lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(IntPtr hwnd);

        private static Menu _menu;
        private static IntPtr _target;

        // The shared menu (created on first use, on the UI thread, and kept for the process).
        public static ContextMenuStrip Shared
        {
            get
            {
                if (_menu == null || _menu.IsDisposed) _menu = new Menu();
                return _menu;
            }
        }

        public static void Attach(TextBox box)
        {
            if (box != null) box.ContextMenuStrip = Shared;
        }

        // Native edit window behind a menu source: a TextBox itself, or the edit child of an
        // editable DarkComboBox.
        private static IntPtr EditWindowOf(Control c)
        {
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return IntPtr.Zero;
            if (c is TextBox) return c.Handle;
            var combo = c as DarkComboBox;
            return combo != null ? combo.EditWindow : IntPtr.Zero;
        }

        private sealed class Menu : ContextMenuStrip
        {
            private const int PaddingY = 4; // logical px above and below the items, as in the app's menus
            private readonly ToolStripMenuItem _undo, _cut, _copy, _paste, _delete, _selectAll;
            private int _dpi = 96;

            public Menu()
            {
                _undo = Item("Undo", "Ctrl+Z", () => Send(WM_UNDO));
                _cut = Item("Cut", "Ctrl+X", () => Send(WM_CUT));
                _copy = Item("Copy", "Ctrl+C", () => Send(WM_COPY));
                _paste = Item("Paste", "Ctrl+V", () => Send(WM_PASTE));
                _delete = Item("Delete", "Del", () => Send(WM_CLEAR));
                _selectAll = Item("Select All", "Ctrl+A", () => SendMessage(_target, EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)));
                Items.AddRange(new ToolStripItem[] { _undo, new ToolStripSeparator(), _cut, _copy, _paste, _delete, new ToolStripSeparator(), _selectAll });
                // Subscribed before the theme, so the target and DPI are known when it measures.
                Opening += OnOpening;
                MenuBuilder.ApplyTheme(this);
            }

            // ToolStripDropDownMenu re-applies DefaultPadding on every layout.
            protected override Padding DefaultPadding
            {
                get
                {
                    var p = base.DefaultPadding;
                    int y = (int)Math.Round(PaddingY * _dpi / 96.0);
                    return new Padding(p.Left, y, p.Right, y);
                }
            }

            private static ToolStripMenuItem Item(string text, string keys, Action run)
            {
                var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, ShowShortcutKeys = true };
                item.Click += (s, e) => { if (_target != IntPtr.Zero) run(); };
                return item;
            }

            private static void Send(int msg)
            {
                SendMessage(_target, msg, IntPtr.Zero, IntPtr.Zero);
            }

            private void OnOpening(object sender, CancelEventArgs e)
            {
                Control source = SourceControl;
                _target = EditWindowOf(source);
                if (_target == IntPtr.Zero)
                {
                    e.Cancel = true;
                    return;
                }
                _dpi = DarkControls.DpiOf(source);
                // Like the native menu: the field takes focus, so a selection shows and the
                // caret is where the commands act.
                if (!source.ContainsFocus && source.CanFocus) source.Focus();

                long style = GetWindowLongPtr(_target, GWL_STYLE).ToInt64();
                bool readOnly = (style & ES_READONLY) != 0, password = (style & ES_PASSWORD) != 0;
                int start, end;
                SendMessage(_target, EM_GETSEL, out start, out end);
                int length = GetWindowTextLength(_target);
                bool selection = end > start;
                _undo.Enabled = !readOnly && SendMessage(_target, EM_CANUNDO, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;
                _cut.Enabled = selection && !readOnly && !password;
                _copy.Enabled = selection && !password;
                _paste.Enabled = !readOnly && ClipboardHasText();
                _delete.Enabled = selection && !readOnly;
                _selectAll.Enabled = length > 0 && !(start == 0 && end >= length);
            }

            private static bool ClipboardHasText()
            {
                try { return Clipboard.ContainsText(); }
                catch (ExternalException) { return true; } // clipboard busy: let the edit control decide
            }

            protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
            {
                base.OnClosed(e);
                // Item clicks run before Closed; drop the window handle so nothing outlives its field.
                BeginInvoke((Action)(() => { if (!Visible) _target = IntPtr.Zero; }));
            }
        }
    }
}
