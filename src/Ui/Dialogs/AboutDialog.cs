using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui.Dialogs
{
    public static class AboutDialog
    {
        public static void Show(IWin32Window owner, string mpvVersion, string ffmpegVersion, string enginePath, string settingsPath)
        {
            using (var w = new Window(mpvVersion, ffmpegVersion, enginePath, settingsPath))
                w.ShowDialog(owner);
        }

        internal sealed class Window : DarkDialog
        {
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int SHDefExtractIconW(string iconFile, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);

            [DllImport("user32.dll")]
            private static extern bool DestroyIcon(IntPtr icon);

            private readonly PictureBox _icon;

            public Window(string mpvVersion, string ffmpegVersion, string enginePath, string settingsPath)
            {
                Text = "About VidFlow Player";
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var root = CreateTable(1);
                root.RowCount = 2;
                var content = CreateTable(2);
                content.RowCount = 4;
                OnDpi(() => content.Padding = Pad(24, 24, 28, 20));

                _icon = new PictureBox { SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left };
                OnDpi(() =>
                {
                    _icon.Size = new Size(S(56), S(56));
                    _icon.Margin = Pad(0, 2, 20, 0);
                    var old = _icon.Image;
                    _icon.Image = LoadAppIcon(S(48));
                    if (old != null) old.Dispose();
                });
                content.Controls.Add(_icon, 0, 0);
                content.SetRowSpan(_icon, 4);

                var title = CreateLabel(AppInfo.Name + " " + AppInfo.Version + (AppInfo.IsPackaged ? " (Microsoft Store)" : ""), false, 16f, FontStyle.Bold);
                OnDpi(() => title.Margin = Pad(0, 0, 0, 0));
                content.Controls.Add(title, 1, 0);
                var tagline = CreateLabel("A lightweight video player for quickly reviewing short clips.", true);
                OnDpi(() => tagline.Margin = Pad(1, 2, 0, 14));
                content.Controls.Add(tagline, 1, 1);

                var grid = CreateTable(2);
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                AddRow(grid, "mpv", mpvVersion);
                AddRow(grid, "FFmpeg", ffmpegVersion);
                AddRow(grid, "Engine", enginePath);
                AddRow(grid, "Settings", settingsPath);
                content.Controls.Add(grid, 1, 2);

                var credits = CreateWrappingLabel(AppInfo.Name + " is free software under the GNU General Public License v3, with no warranty. " +
                    "Playback by mpv (libmpv) and FFmpeg, also GPL. Source code for this build, including its engine: " + AppInfo.ProjectUrl + ".",
                    400, true, 8.5f);
                OnDpi(() => credits.Margin = Pad(1, 16, 0, 0));
                content.Controls.Add(credits, 1, 3);

                var ok = CreateButton("OK", true, DialogResult.OK);
                root.Controls.Add(content, 0, 0);
                root.Controls.Add(CreateFooter(null, ok), 0, 1);
                Controls.Add(root);
                AcceptButton = ok;
                CancelButton = ok;
                ActiveControl = ok;
            }

            // Selectable value text (versions and paths can be copied); long paths wrap.
            private void AddRow(DarkTable grid, string name, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) value = "—";
                int row = grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var key = CreateLabel(name, true);
                OnDpi(() => key.Margin = Pad(0, 2, 16, 4));
                var box = new TextBox
                {
                    Text = value,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    Multiline = true,
                    WordWrap = true,
                    BackColor = Theme.Window,
                    ForeColor = Theme.Text,
                    TabStop = false,
                    ScrollBars = ScrollBars.None,
                };
                // No inner margins, so the measured wrap matches the edit control's own.
                box.HandleCreated += (s, e) => DarkControls.SendMessage(box.Handle, DarkControls.EM_SETMARGINS,
                    (IntPtr)(DarkControls.EC_LEFTMARGIN | DarkControls.EC_RIGHTMARGIN), IntPtr.Zero);
                EditMenu.Attach(box);
                OnDpi(() =>
                {
                    int w = S(330);
                    var size = TextRenderer.MeasureText(value, Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                    box.Size = new Size(w, size.Height + S(2));
                    box.Margin = Pad(0, 2, 0, 4);
                });
                grid.Controls.Add(key, 0, row);
                grid.Controls.Add(box, 1, row);
            }

            private static Bitmap LoadAppIcon(int size)
            {
                string exe = Application.ExecutablePath;
                try
                {
                    IntPtr large, small;
                    if (SHDefExtractIconW(exe, 0, 0, out large, out small, (uint)(size | (16 << 16))) == 0 && large != IntPtr.Zero)
                    {
                        try
                        {
                            using (var ico = Icon.FromHandle(large)) return ico.ToBitmap();
                        }
                        finally
                        {
                            DestroyIcon(large);
                            if (small != IntPtr.Zero) DestroyIcon(small);
                        }
                    }
                }
                catch (Exception) { }
                try
                {
                    using (var ico = Icon.ExtractAssociatedIcon(exe))
                    {
                        if (ico == null) return null;
                        using (var bmp = ico.ToBitmap()) return new Bitmap(bmp, size, size);
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _icon.Image != null)
                {
                    _icon.Image.Dispose();
                    _icon.Image = null;
                }
                base.Dispose(disposing);
            }
        }
    }
}
