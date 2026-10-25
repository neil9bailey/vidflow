using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;
using VidFlow.Ui.Dialogs;

namespace VidFlow.Ui
{
    // Opening media, recent files, the delete-to-Recycle-Bin flow, screenshots, clipboard and drops.
    public sealed partial class MainForm
    {
        private sealed class PendingDelete
        {
            public string Path;
            public bool Permanent;    // the user agreed to a permanent delete (no Recycle Bin on that drive)
            public int Attempts;
        }

        private const int DeleteRetryMs = 250, DeleteMaxAttempts = 20; // ~5 s for a lingering file handle

        private readonly List<PendingDelete> _deleteAfterEnd = new List<PendingDelete>(); // waiting for mpv to release them
        private readonly List<PendingDelete> _deleteQueue = new List<PendingDelete>();
        private Timer _deleteTimer;
        // Screenshot files requested but not yet written (mpv saves asynchronously).
        private readonly HashSet<string> _pendingShots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- open -------------------------------------------------------------------------

        // Replaces (or appends to) the playlist. Subtitle files go to the playing video instead.
        private void OpenPaths(IEnumerable<string> paths, bool append)
        {
            var list = new List<string>();
            if (paths != null)
                foreach (var raw in paths)
                {
                    string p = (raw ?? "").Trim().Trim('"').Trim();
                    if (p.Length > 0) list.Add(p);
                }
            if (list.Count == 0) return;
            if (!EngineAlive)
            {
                // The core is gone (failed or quit): nothing can play, keep the error on screen.
                if (_lastError == null) _lastError = "Playback engine stopped";
                UpdateSurface();
                return;
            }

            if (HasMedia)
            {
                list.RemoveAll(p =>
                {
                    if (Playlist.IsUrl(p) || !Playlist.IsSubtitleFile(p) || !File.Exists(p)) return false;
                    AddSubtitle(p);
                    return true;
                });
                if (list.Count == 0) return;
            }

            string first = list[0];
            if (!Playlist.IsUrl(first))
            {
                string dir = Directory.Exists(first) ? first : SafeDirectoryName(first);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) _settings.LastFolder = dir;
            }

            if (append)
            {
                int before = _playlist.Count;
                _playlist.Append(list);
                int added = _playlist.Count - before;
                if (added <= 0) { ShowOsd("Nothing playable to add", true); return; }
                if (HasMedia)
                {
                    ShowOsd("Added " + added + (added == 1 ? " file" : " files") +
                            (_playlist.LastLoadTruncated ? " · " + TruncatedMessage : ""), _playlist.LastLoadTruncated);
                    return;
                }
                PlayCurrent(true);
                if (_playlist.LastLoadTruncated) ShowOsd(TruncatedMessage, true, 3000);
                return;
            }

            if (!_playlist.Load(list, _settings.AutoLoadFolder))
            {
                string name = FileNameOf(first);
                ShowOsd("Error: nothing playable in " + name, true);
                if (!HasMedia)
                {
                    _lastError = "Nothing to play";
                    _lastErrorDetail = name;
                    UpdateSurface();
                }
                return;
            }
            PlayCurrent(true);
            if (_playlist.LastLoadTruncated) ShowOsd(TruncatedMessage, true, 3000);
        }

        // Playlist caps a folder walk at 5000 files.
        private const string TruncatedMessage = "Only the first 5000 files were added";

        private static string SafeDirectoryName(string path)
        {
            try { return Path.GetDirectoryName(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException) { return null; }
        }

        private void OpenFileDialogAction()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Open";
                dlg.Multiselect = true;
                dlg.Filter = NonEmptyFilter(Playlist.MediaFileFilter);
                dlg.RestoreDirectory = true;
                if (Directory.Exists(_settings.LastFolder)) dlg.InitialDirectory = _settings.LastFolder;
                DialogResult r = DialogResult.Cancel;
                RunModal(() => r = dlg.ShowDialog(this));
                if (r == DialogResult.OK && dlg.FileNames.Length > 0) OpenPaths(dlg.FileNames, false);
            }
        }

        private static string NonEmptyFilter(string filter)
        {
            return string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter;
        }

        private void OpenFolderAction()
        {
            string folder = null;
            RunModal(() => folder = FolderPicker.Show(this, "Open Folder", _settings.LastFolder));
            if (!string.IsNullOrEmpty(folder)) OpenPaths(new[] { folder }, false);
        }

        private void OpenUrlAction()
        {
            string initial = "";
            try
            {
                string clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
                // Cheap checks only: a File.Exists on an unreachable share blocks for the SMB timeout.
                if (clip.Length > 0 && clip.IndexOf('\n') < 0 && (Playlist.IsUrl(clip) || LooksLikePath(clip.Trim('"'))))
                    initial = clip;
            }
            catch (ExternalException) { }
            string text = null;
            RunModal(() => text = TextInputDialog.Show(this, "Open URL or Path", "Enter a URL, or the path of a file or folder:", initial));
            if (!string.IsNullOrWhiteSpace(text)) OpenPaths(new[] { text }, false);
        }

        private void LoadSubtitleAction()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Load Subtitle File";
                dlg.Filter = NonEmptyFilter(Playlist.SubtitleFileFilter);
                dlg.RestoreDirectory = true;
                string dir = Playlist.IsUrl(_st.Path) ? null : SafeDirectoryName(_st.Path ?? "");
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                DialogResult r = DialogResult.Cancel;
                RunModal(() => r = dlg.ShowDialog(this));
                if (r == DialogResult.OK && HasMedia) AddSubtitle(dlg.FileName);
            }
        }

        private void AddSubtitle(string path)
        {
            int rc = _mpv.Command("sub-add", path, "select");
            if (rc < 0) ShowOsd("Error: could not load subtitle " + FileNameOf(path), true);
            else ShowOsd("Subtitles: " + FileNameOf(path));
        }

        private void OpenRecent(int index)
        {
            if (index < 0 || index >= _settings.RecentFiles.Count) return;
            OpenRecentPath(_settings.RecentFiles[index]);
        }

        // By path, not position: the list reorders whenever a file loads, possibly while a menu is open.
        private void OpenRecentPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!Playlist.IsUrl(path) && !File.Exists(path) && !Directory.Exists(path))
            {
                _settings.RecentFiles.RemoveAll(p => SamePath(p, path));
                _settings.Save();
                UpdateSurface();
                ShowOsd("Error: file not found: " + FileNameOf(path), true);
                return;
            }
            OpenPaths(new[] { path }, false);
        }

        private void CopyPathAction()
        {
            if (SetClipboard(() => Clipboard.SetDataObject(_st.Path, true, 5, 100))) ShowOsd("Path copied");
        }

        private void ShowInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path) || Playlist.IsUrl(path)) return;
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else ShowOsd("Error: file not found: " + FileNameOf(path), true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                ShowOsd("Error: " + ex.Message, true);
            }
        }

        // ---- delete to Recycle Bin --------------------------------------------------------

        private void InitDeleteTimer()
        {
            _deleteTimer = new Timer { Interval = DeleteRetryMs };
            _deleteTimer.Tick += (s, e) => ProcessDeleteQueue();
        }

        private void DeleteCurrentFile(bool confirm)
        {
            if (!HasMedia || Playlist.IsUrl(_st.Path)) return;
            DeleteItems(new List<string> { _st.Path }, confirm);
        }

        private void DeletePlaylistItems(int[] indices, bool confirm)
        {
            if (indices == null) return;
            var paths = new List<string>();
            foreach (int i in indices)
                if (i >= 0 && i < _playlist.Count) paths.Add(_playlist.Items[i]);
            DeleteItems(paths, confirm);
        }

        // Confirm, drop the files from the playlist, move on to the next item, then delete each
        // file once mpv has let go of it. Whether a file can be recycled is decided here, while the
        // user is looking: a drive without a Recycle Bin always gets an explicit "permanently
        // delete" confirmation (even for Shift+Delete or with confirmations turned off).
        private void DeleteItems(List<string> paths, bool confirm)
        {
            var files = new List<string>();
            foreach (var p in paths)
                if (!string.IsNullOrEmpty(p) && !Playlist.IsUrl(p) && !files.Exists(f => SamePath(f, p))) files.Add(p);
            if (files.Count == 0) return;

            var permanent = new List<string>();
            foreach (var f in files) if (!CanRecycle(f)) permanent.Add(f);

            if (permanent.Count > 0)
            {
                string msg;
                if (files.Count == 1)
                    msg = "\"" + FileNameOf(files[0]) + "\" is on a drive without a Recycle Bin (a network share, removable or optical drive), " +
                          "so it will be deleted permanently.";
                else if (permanent.Count == files.Count)
                    msg = "These " + files.Count + " files are on a drive without a Recycle Bin (a network share, removable or optical drive), " +
                          "so they will be deleted permanently.";
                else
                    msg = permanent.Count + " of the " + files.Count + " files are on a drive without a Recycle Bin and will be deleted " +
                          "permanently. The others go to the Recycle Bin.";
                msg += "\n\nThis cannot be undone.";
                bool? noCheckbox = null;
                bool yes = false;
                RunModal(() => yes = ConfirmDialog.Show(this, "Permanently Delete?", msg, "Delete Permanently", "Cancel", ref noCheckbox));
                if (!yes) return;
            }
            else if (confirm)
            {
                string msg = files.Count == 1
                    ? "Move \"" + FileNameOf(files[0]) + "\" to the Recycle Bin?"
                    : "Move " + files.Count + " files to the Recycle Bin?";
                bool? dontAsk = false;
                bool yes = false;
                RunModal(() => yes = ConfirmDialog.Show(this, "Delete File", msg, "Delete", "Cancel", ref dontAsk));
                if (!yes) return;
                if (dontAsk == true)
                {
                    _settings.ConfirmDelete = false;
                    _settings.Save();
                }
            }

            var indices = new List<int>();
            foreach (var f in files)
            {
                int idx = _playlist.IndexOf(f);
                if (idx >= 0) indices.Add(idx);
            }
            if (indices.Count > 0) _playlist.RemoveRange(indices); // one Changed event

            bool reloadCurrent = false;
            foreach (var f in files)
            {
                var d = new PendingDelete { Path = f, Permanent = permanent.Exists(p => SamePath(p, f)) };
                // Still open in mpv (playing, or loaded and about to start): wait for its END_FILE.
                if (IsOpenInPlayer(f))
                {
                    _deleteAfterEnd.Add(d);
                    reloadCurrent = true;
                }
                else QueueDelete(d);
            }
            if (reloadCurrent)
            {
                // The seek-bar preview runs its own player on the same file; let it go too.
                _preview.SetSource(null);
                if (_playlist.Current != null) PlayCurrent(false);
                else StopPlayback();
            }
        }

        // A playback entry ended (or was superseded): delete what no live entry holds any more.
        private void ReleaseDeletes()
        {
            if (_deleteAfterEnd.Count == 0) return;
            var ready = new List<PendingDelete>();
            for (int i = _deleteAfterEnd.Count - 1; i >= 0; i--)
            {
                if (IsOpenInPlayer(_deleteAfterEnd[i].Path)) continue;
                ready.Add(_deleteAfterEnd[i]);
                _deleteAfterEnd.RemoveAt(i);
            }
            foreach (var d in ready) QueueDelete(d);
        }

        private void QueueDelete(PendingDelete d)
        {
            _deleteQueue.Add(d);
            ProcessDeleteQueue();
            if (_deleteQueue.Count > 0 && !_closing) _deleteTimer.Start();
        }

        private bool _processingDeletes;

        // Shell error/warning dialogs pump messages (timer ticks, mpv events): never re-enter.
        private void ProcessDeleteQueue()
        {
            if (_processingDeletes) return;
            _processingDeletes = true;
            try
            {
                var ready = new List<PendingDelete>();
                for (int i = _deleteQueue.Count - 1; i >= 0; i--)
                {
                    var d = _deleteQueue[i];
                    if (!File.Exists(d.Path)) { _deleteQueue.RemoveAt(i); continue; }
                    if (IsLocked(d.Path) && ++d.Attempts < DeleteMaxAttempts) continue;
                    _deleteQueue.RemoveAt(i);
                    ready.Add(d);
                }
                foreach (var d in ready) DeleteNow(d);
            }
            finally
            {
                _processingDeletes = false;
            }
            if (_deleteQueue.Count == 0) _deleteTimer.Stop();
        }

        private void DeleteNow(PendingDelete d)
        {
            string name = FileNameOf(d.Path);
            string root = d.Permanent ? null : LocalDriveRoot(d.Path);
            long binBefore = root != null ? Native.RecycleBinItemCount(root) : -1;
            int rc;
            bool aborted;
            // The shell may show a warning or error dialog; keep the idle-cursor logic out of its way.
            _modalDepth++;
            try { rc = Native.ShellDelete(IsHandleCreated ? Handle : IntPtr.Zero, d.Path, !d.Permanent, out aborted); }
            finally { _modalDepth--; }
            if (aborted)
            {
                ShowOsd("Delete cancelled: " + name, true);
                Log.Info("delete cancelled: " + d.Path);
                return;
            }
            if (rc != 0 || File.Exists(d.Path))
            {
                string why = rc != 0 ? "shell error 0x" + rc.ToString("X", Inv) : "the file is still there";
                ShowOsd("Error: could not delete " + name + " (" + why + ")", true);
                Log.Warn("delete failed for " + d.Path + ": " + why);
                return;
            }
            _settings.RecentFiles.RemoveAll(p => SamePath(p, d.Path));
            FeatureFileMoved(d.Path, null); // its markers go with it
            UpdateSurface();
            // FOF_ALLOWUNDO only recycles "if possible"; the bin's item count tells what happened
            // (e.g. the user accepted the shell's warning for a file too big for the bin).
            bool recycled = binBefore >= 0 && Native.RecycleBinItemCount(root) > binBefore;
            if (recycled) ShowOsd("Moved to Recycle Bin: " + name);
            else if (d.Permanent || binBefore >= 0) ShowOsd("Deleted permanently: " + name);
            else ShowOsd("Deleted: " + name);
            Log.Info((recycled ? "recycled " : "deleted ") + d.Path);
        }

        // Last chance at exit: mpv is gone, so files it held can usually be deleted now.
        private void FlushDeletesOnExit()
        {
            _deleteQueue.AddRange(_deleteAfterEnd);
            _deleteAfterEnd.Clear();
            foreach (var d in _deleteQueue.ToArray())
                if (File.Exists(d.Path) && !IsLocked(d.Path)) DeleteNow(d);
            _deleteQueue.Clear();
        }

        private static bool IsLocked(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return false; } // read-only: let the shell report it
        }

        // "C:\" for a path on a local drive letter; null for UNC/device paths or bad input.
        private static string LocalDriveRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path;
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = p.Substring(4);
            if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return null; // UNC share or device
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(p));
                return root != null && root.Length >= 2 && root[1] == ':' && char.IsLetter(root[0]) ? root.Substring(0, 2) + "\\" : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        // Only a local fixed drive whose Recycle Bin answers can take the file. Network, removable,
        // optical and RAM drives have no bin: the shell would delete permanently without asking.
        // (A file too big for the bin is caught by the shell's own warning at delete time.)
        private static bool CanRecycle(string path)
        {
            string root = LocalDriveRoot(path);
            if (root == null) return false;
            try
            {
                if (new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
            return Native.RecycleBinItemCount(root) >= 0;
        }

        // ---- screenshots & clipboard ------------------------------------------------------

        private string ScreenshotFormat()
        {
            string f = (_settings.ScreenshotFormat ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (f == "jpeg") f = "jpg";
            return f == "jpg" || f == "webp" || f == "png" ? f : "png";
        }

        // <folder>\<name>_<hh-mm-ss.fff>.<fmt>; displayed = as shown (subs, zoom, filters).
        private void SaveScreenshot(bool displayed)
        {
            string folder = _settings.EffectiveScreenshotFolder;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                ShowOsd("Error: cannot use screenshot folder " + folder, true);
                return;
            }
            string baseName = Playlist.IsUrl(_st.Path) ? (_st.MediaTitle ?? "stream") : Path.GetFileNameWithoutExtension(_st.Path);
            baseName = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "frame" : baseName);
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, _st.Position));
            string stamp = string.Format(Inv, "{0:00}-{1:00}-{2:00}.{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
            string ext = ScreenshotFormat();
            string file = Path.Combine(folder, baseName + "_" + stamp + "." + ext);
            for (int n = 2; File.Exists(file) || _pendingShots.Contains(file); n++)
                file = Path.Combine(folder, baseName + "_" + stamp + " (" + n + ")." + ext);
            string shown = Path.GetFileName(file);
            _pendingShots.Add(file);
            _mpv.CommandAsync((err, result) =>
            {
                _pendingShots.Remove(file);
                if (err < 0) ShowOsd("Error: screenshot failed (" + MpvErrorText(err) + ")", true);
                else ShowOsd("Saved " + shown);
            }, "screenshot-to-file", file, displayed ? "window" : "video");
        }

        private static string SanitizeFileName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (Array.IndexOf(bad, chars[i]) >= 0) chars[i] = '_';
            string s = new string(chars).Trim();
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }

        // screenshot-raw gives bgr0 rows; Format32bppRgb has the same byte layout.
        private void CopyFrameToClipboard()
        {
            var r = _mpv.CommandRet("screenshot-raw", "video") as Dictionary<string, object>;
            int w = (int)ToDouble(Get(r, "w")), h = (int)ToDouble(Get(r, "h")), stride = (int)ToDouble(Get(r, "stride"));
            string format = Get(r, "format") as string;
            var data = Get(r, "data") as byte[];
            if (w <= 0 || h <= 0 || data == null || stride < w * 4 || data.Length < (long)stride * h || (format != "bgr0" && format != "bgra"))
            {
                ShowOsd("Error: could not capture the frame", true);
                return;
            }
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(data, y * stride, IntPtr.Add(bd.Scan0, y * bd.Stride), w * 4);
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
                // copy=true renders the bitmap into the clipboard now, so disposing it afterwards is safe.
                var obj = new DataObject();
                obj.SetData(DataFormats.Bitmap, true, bmp);
                if (SetClipboard(() => Clipboard.SetDataObject(obj, true, 5, 100)))
                    ShowOsd("Frame copied (" + w + "×" + h + ")");
            }
        }

        private bool SetClipboard(Action set)
        {
            try
            {
                set();
                return true;
            }
            catch (ExternalException ex)
            {
                ShowOsd("Error: clipboard is busy (" + ex.Message + ")", true);
                return false;
            }
        }

        private void OpenScreenshotFolder()
        {
            string folder = _settings.EffectiveScreenshotFolder;
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                       ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
            {
                ShowOsd("Error: " + ex.Message, true);
            }
        }

        // ---- drag & drop ------------------------------------------------------------------

        private bool _dragAccepted; // decided once per drag, in DragEnter

        private void WireDragDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnterAny;
            c.DragOver += OnDragOverAny;
            c.DragDrop += OnDragDropAny;
        }

        // Without touching the file system: DragEnter/DragOver run on the UI thread while the drag
        // source waits, and an existence check on an unreachable share blocks for the SMB timeout.
        private static bool LooksDroppable(IDataObject data)
        {
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop)) return true;
                if (!data.GetDataPresent(DataFormats.UnicodeText) && !data.GetDataPresent(DataFormats.Text)) return false;
                foreach (var s in TextLines(data))
                    if (Playlist.IsUrl(s) || LooksLikePath(s)) return true;
            }
            catch (Exception ex) when (ex is ExternalException || ex is InvalidCastException || ex is ArgumentException)
            {
                Log.Warn("drag data unreadable: " + ex.Message);
            }
            return false;
        }

        // Rooted local or UNC path syntax ("C:\x", "\\server\share\x"); no file-system access.
        private static bool LooksLikePath(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3 || s.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            bool drive = char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/');
            bool unc = s.StartsWith(@"\\", StringComparison.Ordinal) && s.Length > 2 && s[2] != '\\';
            return drive || unc;
        }

        private static bool IsUncPath(string s)
        {
            return s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal);
        }

        private static List<string> TextLines(IDataObject data)
        {
            var lines = new List<string>();
            string text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string ?? "";
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                string s = line.Trim().Trim('"');
                if (s.Length > 0) lines.Add(s);
                if (lines.Count >= 5000) break;
            }
            return lines;
        }

        // Text lines that are URLs or existing paths. UNC paths pass through unchecked (the playlist
        // skips what it cannot read), so a dead share cannot hang the UI.
        private static List<string> PathsFromLines(List<string> lines)
        {
            var list = new List<string>();
            foreach (var s in lines)
            {
                if (Playlist.IsUrl(s)) list.Add(s);
                else if (LooksLikePath(s) && (IsUncPath(s) || File.Exists(s) || Directory.Exists(s))) list.Add(s);
            }
            return list;
        }

        private void OnDragEnterAny(object sender, DragEventArgs e)
        {
            _dragAccepted = LooksDroppable(e.Data);
            OnDragOverAny(sender, e);
        }

        private void OnDragOverAny(object sender, DragEventArgs e)
        {
            if (!_dragAccepted) { e.Effect = DragDropEffects.None; return; }
            DragDropEffects allowed = e.AllowedEffect;
            e.Effect = (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                     : (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link
                     : allowed & DragDropEffects.Move;
        }

        private void OnDragDropAny(object sender, DragEventArgs e)
        {
            if (!_dragAccepted) return;
            _dragAccepted = false;
            bool append = (e.KeyState & 8) != 0; // Ctrl held
            IDataObject data = e.Data;
            // Read the data now (it is only valid during the drop), but finish the OLE drop before
            // checking or opening anything so Explorer is not blocked while the files open.
            List<string> files = null;
            List<string> lines = null;
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop)) files = new List<string>(data.GetData(DataFormats.FileDrop) as string[] ?? new string[0]);
                else lines = TextLines(data);
            }
            catch (Exception ex) when (ex is ExternalException || ex is InvalidCastException || ex is ArgumentException)
            {
                Log.Warn("drop data unreadable: " + ex.Message);
                return;
            }
            BeginInvoke((MethodInvoker)(() =>
            {
                var paths = files ?? PathsFromLines(lines);
                if (paths.Count == 0) return;
                OpenPaths(paths, append);
                Activate();
            }));
        }
    }
}
