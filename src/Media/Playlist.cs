using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace VidFlow.Media
{
    // Sorts are ascending (A→Z, oldest→newest, smallest→largest) unless Playlist.Descending is set,
    // matching what a reviewer expects when stepping forward through a folder. URLs sort after local
    // files either way. Name order groups items by folder (a folder's own files before its subfolders).
    public enum PlaylistSort { Name, DateModified, Size, Type }

    // Ordered list of media paths/URLs with a current index. UI-agnostic, not thread-safe
    // (UI thread only). Items are full paths (or URLs, kept verbatim).
    public sealed class Playlist
    {
        public static readonly string[] VideoExtensions =
        {
            ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".f4v", ".mpg", ".mpeg", ".m2v",
            ".m2ts", ".mts", ".ts", ".vob", ".ogv", ".3gp", ".3g2", ".asf", ".rm", ".rmvb", ".divx", ".y4m",
            ".mxf", ".dv", ".gif", ".hevc", ".h264", ".264", ".265", ".av1", ".ivf",
        };

        public static readonly string[] AudioExtensions =
        {
            ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".oga", ".opus", ".wma", ".ac3", ".eac3", ".dts",
            ".mka", ".ape", ".wv", ".aiff", ".aif", ".alac", ".amr", ".mpc",
        };

        public static readonly string[] SubtitleExtensions =
        {
            ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi", ".lrc",
        };

        // Playlist files are expanded by Load/Append themselves; mpv never receives one.
        public static readonly string[] PlaylistExtensions = { ".m3u", ".m3u8", ".pls" };

        // Recursive folder walks (IncludeSubfolders): levels below the opened folder, and the most files
        // one Load/Append collects from subfolders. Nested playlist files are followed this deep.
        private const int MaxFolderDepth = 8, MaxTreeFiles = 5000, MaxPlaylistNesting = 4;
        private const long MaxPlaylistBytes = 64L << 20;

        // Declared after the arrays: static initializers run in textual order.
        private static readonly HashSet<string> MediaSet =
            new HashSet<string>(VideoExtensions.Concat(AudioExtensions), StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> SubtitleSet =
            new HashSet<string>(SubtitleExtensions, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PlaylistSet =
            new HashSet<string>(PlaylistExtensions, StringComparer.OrdinalIgnoreCase);
        private static readonly char[] Separators = { '\\', '/' };
        private static readonly char[] LineBreaks = { '\r', '\n' };
        private static readonly Random Rng = new Random();

        private static readonly string MediaFilter =
            "Media files|" + Patterns(VideoExtensions.Concat(AudioExtensions).Concat(PlaylistExtensions)) +
            "|Video files|" + Patterns(VideoExtensions) +
            "|Audio files|" + Patterns(AudioExtensions) +
            "|Playlists|" + Patterns(PlaylistExtensions) +
            "|All files|*.*";
        private static readonly string SubtitleFilter =
            "Subtitle files|" + Patterns(SubtitleExtensions) + "|All files|*.*";

        private static class NativeMethods
        {
            internal const string SystemDefaultLocale = "!x-sys-default-locale";
            internal const uint LocaleIDefaultAnsiCodePage = 0x1004;

            [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            internal static extern int StrCmpLogicalW(string a, string b);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            internal static extern int GetLocaleInfoEx(string localeName, uint lcType, StringBuilder data, int cchData);
        }

        public static bool IsMediaFile(string path) { return MediaSet.Contains(ExtensionOf(path)); }
        public static bool IsSubtitleFile(string path) { return SubtitleSet.Contains(ExtensionOf(path)); }
        public static bool IsPlaylistFile(string path) { return PlaylistSet.Contains(ExtensionOf(path)); }

        // "scheme://..." with a scheme of 2+ characters, so "C:\..." and "C://..." are not URLs.
        public static bool IsUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int end = s.IndexOf("://", StringComparison.Ordinal);
            if (end < 2 || !IsAsciiLetter(s[0])) return false;
            for (int i = 1; i < end; i++)
            {
                char c = s[i];
                if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '+' && c != '-' && c != '.') return false;
            }
            return true;
        }

        // Explorer order: case-insensitive, digit runs compared by value ("clip2" < "clip10").
        public static int NaturalCompare(string a, string b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            return NativeMethods.StrCmpLogicalW(a, b);
        }

        public static string MediaFileFilter { get { return MediaFilter; } }
        public static string SubtitleFileFilter { get { return SubtitleFilter; } }

        // Entries of an .m3u/.m3u8/.pls file, in order: relative entries resolved against the list's
        // folder, file:// URIs decoded to paths, other URLs kept verbatim. Comment/#EXT lines are skipped.
        // Text is UTF-8 (BOM-detected, also UTF-16 with a BOM); a .m3u/.pls that is not valid UTF-8 is read
        // in the system ANSI code page. An unreadable file yields an empty list.
        public static List<string> ReadPlaylistFile(string path)
        {
            var result = new List<string>();
            string full = FullPath(Clean(path) ?? "");
            if (full == null) return result;
            string text = ReadPlaylistText(full);
            if (text == null) return result;
            string dir = Path.GetDirectoryName(full) ?? "";

            bool pls = string.Equals(ExtensionOf(full), ".pls", StringComparison.OrdinalIgnoreCase) ||
                       text.TrimStart().StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase);
            var numbered = new List<KeyValuePair<int, string>>();
            foreach (var raw in text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (pls)
                {
                    // "File3=clip.mp4"; Title/Length/NumberOfEntries lines are ignored.
                    int eq = line.IndexOf('=');
                    int n;
                    if (eq <= 4 || !line.StartsWith("File", StringComparison.OrdinalIgnoreCase) ||
                        !int.TryParse(line.Substring(4, eq - 4), NumberStyles.None, CultureInfo.InvariantCulture, out n)) continue;
                    string e = ResolveEntry(line.Substring(eq + 1), dir);
                    if (e != null) numbered.Add(new KeyValuePair<int, string>(n, e));
                }
                else
                {
                    if (line[0] == '#') continue;
                    string e = ResolveEntry(line, dir);
                    if (e != null) result.Add(e);
                }
            }
            // OrderBy is stable: duplicate or missing numbers keep their file order.
            if (pls) result.AddRange(numbered.OrderBy(kv => kv.Key).Select(kv => kv.Value));
            return result;
        }

        // Writes a UTF-8 .m3u8 (no BOM, #EXTM3U header). Local items on the same drive (or share) as the
        // list become paths relative to its folder; others stay absolute, URLs verbatim. Throws on I/O errors.
        public static void WritePlaylistFile(string path, IEnumerable<string> items)
        {
            if (path == null) throw new ArgumentNullException("path");
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? "";
            var sb = new StringBuilder("#EXTM3U\r\n");
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    sb.Append(IsUrl(item) ? item : RelativeEntry(item, dir)).Append("\r\n");
                }
            }
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
        }

        private readonly List<string> _items = new List<string>();
        private readonly ReadOnlyCollection<string> _view;
        private int _index = -1;
        private bool _shuffle, _descending, _includeSubfolders, _truncated;
        private PlaylistSort _sort = PlaylistSort.Name;

        // Shuffle walk: a permutation of item indices; _order[_orderPos] is always the current item.
        // Kept (remapped) across edits so Previous still retraces what was actually played.
        private readonly List<int> _order = new List<int>();
        private int _orderPos;
        private int _generation;

        public Playlist()
        {
            _view = _items.AsReadOnly();
        }

        public IReadOnlyList<string> Items { get { return _view; } }
        public int Count { get { return _items.Count; } }
        public int Index { get { return _index; } }
        public string Current { get { return _index >= 0 ? _items[_index] : null; } }

        public event Action Changed;

        public bool Shuffle
        {
            get { return _shuffle; }
            set
            {
                if (_shuffle == value) return;
                _shuffle = value;
                Reshuffle();
            }
        }

        public PlaylistSort Sort
        {
            get { return _sort; }
            set
            {
                // Always re-sort, even for the same key: it restores the order after manual drags.
                _sort = value;
                Resort();
            }
        }

        // Reverses the sort key; ties still fall back to ascending natural name order.
        public bool Descending
        {
            get { return _descending; }
            set
            {
                if (_descending == value) return;
                _descending = value;
                Resort();
            }
        }

        // Folders given to Load/Append (or listed in a playlist file) are walked recursively. The folder
        // auto-loaded around a single opened file is never recursive.
        public bool IncludeSubfolders
        {
            get { return _includeSubfolders; }
            set { _includeSubfolders = value; }
        }

        // The last Load/Append stopped collecting subfolder files at the cap (MaxTreeFiles).
        public bool LastLoadTruncated { get { return _truncated; } }

        // Folders for which this returns true (given the folder's full path) are not entered when a folder
        // is walked recursively, e.g. a cull session's Keep / Rejected / bin folders. The folder that was
        // opened itself is always listed. Null = walk everything.
        public Func<string, bool> SkipFolder { get; set; }

        // Goes up each time Load replaces the list with different items, so a caller can tell whether the
        // list it remembered positions in is still the one shown.
        public int Generation { get { return _generation; } }

        public bool Load(IEnumerable<string> inputs, bool autoLoadFolder)
        {
            _truncated = false;
            var list = CleanInputs(inputs);
            List<string> items;
            int current = 0;
            string single = list.Count == 1 && !IsUrl(list[0]) ? list[0] : null;
            if (autoLoadFolder && single != null && IsMediaFile(single) && FileExists(single))
            {
                string dir = Path.GetDirectoryName(single);
                items = dir != null ? ListFolder(dir) : new List<string>();
                current = IndexIn(items, single);
                if (current < 0)
                {
                    // A hidden file opened explicitly is not part of the folder listing; slot it in.
                    items.Add(single);
                    items = SortItems(items);
                    current = IndexIn(items, single);
                }
            }
            else
            {
                items = Expand(list, null);
            }
            if (items.Count == 0) return false; // keep the previous list: nothing here could play

            bool changed = current != _index || !items.SequenceEqual(_items, StringComparer.Ordinal);
            _items.Clear();
            _items.AddRange(items);
            _index = current;
            Reshuffle();
            if (changed)
            {
                _generation++;
                OnChanged();
            }
            return true;
        }

        public void Append(IEnumerable<string> inputs)
        {
            _truncated = false;
            var added = Expand(CleanInputs(inputs), _items);
            if (added.Count == 0) return;
            int first = _items.Count;
            _items.AddRange(added);
            if (_index < 0)
            {
                _index = 0;
                Reshuffle();
            }
            else if (_shuffle)
            {
                // Random insertion into the unplayed tail keeps that tail a uniform permutation.
                for (int i = first; i < _items.Count; i++)
                    _order.Insert(Rng.Next(_orderPos + 1, _order.Count + 1), i);
            }
            OnChanged();
        }

        public bool HasNext(bool wrap) { return _items.Count > 0 && (wrap || Position < _items.Count - 1); }
        public bool HasPrevious(bool wrap) { return _items.Count > 0 && (wrap || Position > 0); }

        public bool MoveNext(bool wrap)
        {
            int n = _items.Count;
            if (n == 0) return false;
            int p = Position + 1;
            if (p >= n)
            {
                if (!wrap) return false;
                p = 0;
            }
            GoToPosition(p);
            return true;
        }

        public bool MovePrevious(bool wrap)
        {
            int n = _items.Count;
            if (n == 0) return false;
            int p = Position - 1;
            if (p < 0)
            {
                if (!wrap) return false;
                p = n - 1;
            }
            GoToPosition(p);
            return true;
        }

        // A jump while shuffling becomes the next step of the walk, so Previous returns to where we were.
        public bool MoveTo(int index)
        {
            if (index < 0 || index >= _items.Count) return false;
            if (index == _index) return true;
            if (_shuffle && _order.Count == _items.Count)
            {
                int p = _order.IndexOf(index);
                _order.RemoveAt(p);
                if (p < _orderPos) _orderPos--;
                _orderPos++;
                _order.Insert(_orderPos, index);
            }
            _index = index;
            OnChanged();
            return true;
        }

        public bool MoveFirst() { return MoveTo(0); }
        public bool MoveLast() { return MoveTo(_items.Count - 1); }

        // One item, one Changed; RemoveRange removes several with a single Changed.
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            bool wasCurrent = index == _index;
            _items.RemoveAt(index);
            if (_items.Count == 0) _index = -1;
            else if (index < _index) _index--;
            else if (wasCurrent && _index >= _items.Count) _index = _items.Count - 1;

            if (_shuffle)
            {
                if (_items.Count == 0 || _order.Count != _items.Count + 1)
                {
                    Reshuffle();
                }
                else
                {
                    int p = _order.IndexOf(index);
                    _order.RemoveAt(p);
                    if (p < _orderPos) _orderPos--;
                    for (int k = 0; k < _order.Count; k++) if (_order[k] > index) _order[k]--;
                    if (wasCurrent)
                    {
                        // The new current item takes over the removed item's step of the walk.
                        int q = _order.IndexOf(_index);
                        _order.RemoveAt(q);
                        if (q < _orderPos) _orderPos--;
                        if (_orderPos > _order.Count) _orderPos = _order.Count;
                        _order.Insert(_orderPos, _index);
                    }
                }
            }
            OnChanged();
        }

        // Removes every listed index (any order; duplicates and out-of-range values ignored) with one
        // Changed. Removing the current item makes the next surviving item current, or the new last item.
        public void RemoveRange(IList<int> indices)
        {
            int n = _items.Count;
            if (indices == null || n == 0) return;
            var remove = new bool[n];
            int count = 0;
            foreach (int i in indices)
            {
                if (i < 0 || i >= n || remove[i]) continue;
                remove[i] = true;
                count++;
            }
            if (count == 0) return;
            if (count == n)
            {
                Clear();
                return;
            }

            var map = new int[n];
            var kept = new List<string>(n - count);
            for (int i = 0; i < n; i++)
            {
                if (remove[i]) map[i] = -1;
                else
                {
                    map[i] = kept.Count;
                    kept.Add(_items[i]);
                }
            }
            bool currentRemoved = _index >= 0 && remove[_index];
            int newIndex = -1;
            if (_index >= 0)
            {
                if (!currentRemoved) newIndex = map[_index];
                else
                {
                    newIndex = kept.Count - 1;
                    for (int i = _index + 1; i < n; i++)
                        if (!remove[i]) { newIndex = map[i]; break; }
                }
            }
            bool walk = _shuffle && _order.Count == n;
            _items.Clear();
            _items.AddRange(kept);
            _index = newIndex;
            if (walk) RemapOrderAfterRemove(remove, map, currentRemoved);
            else if (_shuffle) Reshuffle();
            OnChanged();
        }

        public bool Remove(string path)
        {
            int i = IndexOf(path);
            if (i < 0) return false;
            RemoveAt(i);
            return true;
        }

        // Moves the item at 'from' so that it ends up at index 'to' ('to' is clamped to the list).
        public void Move(int from, int to)
        {
            int n = _items.Count;
            if (from < 0 || from >= n) return;
            to = Math.Max(0, Math.Min(n - 1, to));
            if (from == to) return;
            string item = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, item);

            var map = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (i == from) map[i] = to;
                else if (from < to && i > from && i <= to) map[i] = i - 1;
                else if (from > to && i >= to && i < from) map[i] = i + 1;
                else map[i] = i;
            }
            if (_index >= 0) _index = map[_index];
            RemapOrder(map);
            OnChanged();
        }

        // Moves the listed rows (kept in their current order) so they sit together just before the item
        // now at 'target' (Count = the end), with one Changed. When 'target' is itself one of the moved
        // rows, the block lands before the next row that stays put. The current item follows.
        public void MoveBlock(IList<int> indices, int target)
        {
            int n = _items.Count;
            if (indices == null || n < 2) return;
            var moving = new bool[n];
            int count = 0;
            foreach (int i in indices)
            {
                if (i < 0 || i >= n || moving[i]) continue;
                moving[i] = true;
                count++;
            }
            if (count == 0 || count == n) return;
            target = Math.Max(0, Math.Min(n, target));
            int dest = 0; // rows staying put that end up above the block
            for (int i = 0; i < target; i++) if (!moving[i]) dest++;

            var map = new int[n];
            int stay = 0, block = 0;
            bool same = true;
            for (int i = 0; i < n; i++)
            {
                if (moving[i]) map[i] = dest + block++;
                else map[i] = stay < dest ? stay++ : count + stay++;
                if (map[i] != i) same = false;
            }
            if (same) return;
            var reordered = new string[n];
            for (int i = 0; i < n; i++) reordered[map[i]] = _items[i];
            _items.Clear();
            _items.AddRange(reordered);
            if (_index >= 0) _index = map[_index];
            RemapOrder(map);
            OnChanged();
        }

        // Replaces one item in place (a rename or move of that file): its position, the current item and
        // the shuffle walk stay as they are; one Changed. A path that is already listed elsewhere is not
        // duplicated: the item is removed instead (the usual RemoveAt rules apply).
        public void Replace(int index, string path)
        {
            if (index < 0 || index >= _items.Count) return;
            string p = Clean(path);
            if (p == null) return;
            if (!IsUrl(p)) p = FullPath(p) ?? p;
            int other = IndexOf(p);
            if (other >= 0 && other != index)
            {
                RemoveAt(index);
                return;
            }
            if (string.Equals(_items[index], p, StringComparison.Ordinal)) return;
            _items[index] = p;
            OnChanged();
        }

        public void Clear()
        {
            if (_items.Count == 0) return;
            _items.Clear();
            _index = -1;
            _order.Clear();
            _orderPos = 0;
            OnChanged();
        }

        public int IndexOf(string path)
        {
            string key = Clean(path);
            if (key == null) return -1;
            if (IsUrl(key))
            {
                for (int i = 0; i < _items.Count; i++)
                    if (string.Equals(_items[i], key, StringComparison.Ordinal)) return i;
                return -1;
            }
            key = FullPath(key) ?? key;
            for (int i = 0; i < _items.Count; i++)
                if (string.Equals(_items[i], key, StringComparison.OrdinalIgnoreCase) && !IsUrl(_items[i])) return i;
            return -1;
        }

        // ---- internals ----

        private int Position { get { return _shuffle && _order.Count == _items.Count ? _orderPos : _index; } }

        private void GoToPosition(int p)
        {
            int index = p;
            if (_shuffle && _order.Count == _items.Count)
            {
                _orderPos = p;
                index = _order[p];
            }
            if (index == _index) return;
            _index = index;
            OnChanged();
        }

        private void Resort()
        {
            if (_items.Count < 2) return;
            var sorted = SortItems(_items);
            if (sorted.SequenceEqual(_items, StringComparer.Ordinal)) return;
            var newIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Count; i++) newIndex[sorted[i]] = i;
            var map = new int[_items.Count];
            for (int i = 0; i < _items.Count; i++) map[i] = newIndex[_items[i]];
            _items.Clear();
            _items.AddRange(sorted);
            if (_index >= 0) _index = map[_index];
            RemapOrder(map);
            OnChanged();
        }

        // Fisher-Yates over every item but the current one, which leads the walk.
        private void Reshuffle()
        {
            _order.Clear();
            _orderPos = 0;
            if (!_shuffle || _index < 0) return;
            for (int i = 0; i < _items.Count; i++) if (i != _index) _order.Add(i);
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                int t = _order[i]; _order[i] = _order[j]; _order[j] = t;
            }
            _order.Insert(0, _index);
        }

        private void RemapOrder(int[] map)
        {
            if (!_shuffle) return;
            if (_order.Count != map.Length) { Reshuffle(); return; }
            for (int k = 0; k < _order.Count; k++) _order[k] = map[_order[k]];
        }

        // Drops removed items from the walk (map: old index -> new index). If the current item went, the new
        // current item takes over its step, so the history before it and the unplayed tail stay as they were.
        private void RemapOrderAfterRemove(bool[] removed, int[] map, bool currentRemoved)
        {
            var order = new List<int>(_items.Count);
            int pos = _orderPos;
            for (int k = 0; k < _order.Count; k++)
            {
                int old = _order[k];
                if (removed[old])
                {
                    if (k < _orderPos) pos--;
                    continue;
                }
                order.Add(map[old]);
            }
            if (currentRemoved)
            {
                int q = order.IndexOf(_index);
                order.RemoveAt(q);
                if (q < pos) pos--;
                pos = Math.Max(0, Math.Min(pos, order.Count));
                order.Insert(pos, _index);
            }
            _order.Clear();
            _order.AddRange(order);
            _orderPos = pos;
        }

        private void OnChanged()
        {
            var h = Changed;
            if (h != null) h();
        }

        private static List<string> CleanInputs(IEnumerable<string> inputs)
        {
            var list = new List<string>();
            if (inputs == null) return list;
            foreach (var raw in inputs)
            {
                string s = Clean(raw);
                if (s == null) continue;
                if (!IsUrl(s))
                {
                    s = FullPath(s);
                    if (s == null) continue;
                }
                list.Add(s);
            }
            return list;
        }

        // Trims whitespace and quotes; file:// URLs become local paths.
        private static string Clean(string raw)
        {
            if (raw == null) return null;
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2).Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) s = FileUriToPath(s);
            return s.Length == 0 ? null : s;
        }

        // file:///C:/a%20b.mp4, file://localhost/C:/x, file://server/share/x, file:////server/share/x.
        // Decoded by hand rather than via Uri.LocalPath, which drops everything after an unescaped '#'.
        private static string FileUriToPath(string uri)
        {
            string rest = uri.Substring("file://".Length);
            string host = "";
            if (!rest.StartsWith("/", StringComparison.Ordinal))
            {
                int slash = rest.IndexOf('/');
                host = slash < 0 ? rest : rest.Substring(0, slash);
                rest = slash < 0 ? "" : rest.Substring(slash);
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) host = "";
            }
            string p = Uri.UnescapeDataString(rest).Replace('/', '\\');
            if (host.Length > 0) return @"\\" + host + p;
            if (p.Length >= 3 && p[0] == '\\' && IsAsciiLetter(p[1]) && (p[2] == ':' || p[2] == '|'))
                return p[1] + ":" + p.Substring(3); // drive path (also the old "C|" spelling)
            return p;
        }

        private static string FullPath(string s)
        {
            try { return Path.GetFullPath(s); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException)
            {
                return null;
            }
        }

        private static bool FileExists(string path)
        {
            try { return File.Exists(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) { return false; }
        }

        private static bool DirectoryExists(string path)
        {
            try { return Directory.Exists(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) { return false; }
        }

        // ---- expansion of Load/Append inputs ----

        // State of one Load/Append expansion: de-duplication, playlist files already read (cycles), and
        // the number of files collected from subfolders so far (for the cap).
        private sealed class Expansion
        {
            public readonly HashSet<string> SeenLocal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> SeenUrl = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Lists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Result = new List<string>();
            public int TreeFiles;

            public void Add(string item)
            {
                if ((IsUrl(item) ? SeenUrl : SeenLocal).Add(item)) Result.Add(item);
            }
        }

        // Cleaned inputs -> new items: URLs verbatim, folders -> their media files (sorted; recursive when
        // IncludeSubfolders), playlist files -> their entries, files as given. A file with an unknown extension
        // is accepted only when it is the sole input or a playlist entry (picked deliberately; mpv may still
        // play it); in a multi-file drop such files are just noise. Duplicates of each other or of 'existing'
        // are skipped.
        private List<string> Expand(List<string> inputs, List<string> existing)
        {
            var x = new Expansion();
            if (existing != null)
                foreach (var e in existing) (IsUrl(e) ? x.SeenUrl : x.SeenLocal).Add(e);
            bool sole = inputs.Count == 1;
            foreach (var s in inputs) ExpandInput(s, sole, 0, x);
            return x.Result;
        }

        private void ExpandInput(string s, bool deliberate, int nesting, Expansion x)
        {
            if (IsUrl(s))
            {
                x.Add(s);
                return;
            }
            if (DirectoryExists(s))
            {
                foreach (var f in _includeSubfolders ? ListTree(s, x) : ListFolder(s)) x.Add(f);
                return;
            }
            if (!FileExists(s) || IsSubtitleFile(s)) return;
            if (IsPlaylistFile(s))
            {
                // Too deep, or a list that (indirectly) includes itself: stop there.
                if (nesting >= MaxPlaylistNesting || !x.Lists.Add(s)) return;
                foreach (var entry in ReadPlaylistFile(s)) ExpandInput(entry, true, nesting + 1, x);
                return;
            }
            if (deliberate || IsMediaFile(s)) x.Add(s);
        }

        // Media files directly inside 'dir' (hidden/system skipped), sorted by the current sort.
        // An unreadable folder yields whatever could be listed before the failure.
        private List<string> ListFolder(string dir)
        {
            var entries = new List<Entry>();
            AddMediaFiles(new DirectoryInfo(dir), entries, int.MaxValue);
            return Sorted(entries);
        }

        // Media files in 'root' and its subfolders, at most MaxFolderDepth levels down; hidden or system
        // folders and reparse points (junctions, symlinks) are not entered. The root's own files are always
        // listed in full; files from subfolders stop at the per-load cap, which sets LastLoadTruncated.
        // Folders are walked in natural order, files before subfolders, so a truncated list keeps the first
        // part of the sorted tree.
        private List<string> ListTree(string root, Expansion x)
        {
            var entries = new List<Entry>();
            var rootInfo = new DirectoryInfo(root);
            AddMediaFiles(rootInfo, entries, int.MaxValue);
            var stack = new Stack<KeyValuePair<DirectoryInfo, int>>();
            var skip = SkipFolder;
            PushSubfolders(rootInfo, 1, stack, skip);
            while (stack.Count > 0 && !_truncated)
            {
                var top = stack.Pop();
                int before = entries.Count;
                if (!AddMediaFiles(top.Key, entries, MaxTreeFiles - x.TreeFiles)) _truncated = true;
                x.TreeFiles += entries.Count - before;
                if (top.Value < MaxFolderDepth) PushSubfolders(top.Key, top.Value + 1, stack, skip);
            }
            return Sorted(entries);
        }

        // Adds up to 'room' media files of one folder; false when more were left over. Errors end the
        // listing of that folder quietly.
        private static bool AddMediaFiles(DirectoryInfo dir, List<Entry> entries, int room)
        {
            try
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (!IsMediaFile(f.Name)) continue;
                    if (room <= 0) return false;
                    entries.Add(Entry.FromInfo(f));
                    room--;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
            }
            return true;
        }

        private static void PushSubfolders(DirectoryInfo dir, int depth, Stack<KeyValuePair<DirectoryInfo, int>> stack, Func<string, bool> skip)
        {
            var subs = new List<DirectoryInfo>();
            try
            {
                foreach (var d in dir.EnumerateDirectories())
                {
                    if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    if (skip != null && SkipSafe(skip, d.FullName)) continue;
                    subs.Add(d);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
            }
            subs.Sort((a, b) => NaturalCompare(a.Name, b.Name));
            for (int i = subs.Count - 1; i >= 0; i--) stack.Push(new KeyValuePair<DirectoryInfo, int>(subs[i], depth));
        }

        // A throwing filter skips nothing (the walk itself must not fail because of it).
        private static bool SkipSafe(Func<string, bool> skip, string folder)
        {
            try { return skip(folder); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException || ex is SecurityException)
            {
                return false;
            }
        }

        // ---- playlist file helpers ----

        private static string ReadPlaylistText(string path)
        {
            byte[] b;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxPlaylistBytes) return null;
                b = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(false).GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (string.Equals(ExtensionOf(path), ".m3u8", StringComparison.OrdinalIgnoreCase)) return new UTF8Encoding(false).GetString(b);
            try
            {
                return new UTF8Encoding(false, true).GetString(b); // plain ASCII is valid UTF-8 too
            }
            catch (DecoderFallbackException)
            {
                return LegacyAnsi().GetString(b);
            }
        }

        // The system locale's legacy ANSI code page (Windows-1252 if unknown). Not Encoding.Default: the app
        // manifest makes the process code page UTF-8, which is exactly what such a file is not.
        private static Encoding LegacyAnsi()
        {
            int cp = 0;
            try
            {
                var sb = new StringBuilder(16);
                if (NativeMethods.GetLocaleInfoEx(NativeMethods.SystemDefaultLocale, NativeMethods.LocaleIDefaultAnsiCodePage, sb, sb.Capacity) > 0)
                    int.TryParse(sb.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out cp);
            }
            catch (EntryPointNotFoundException)
            {
            }
            if (cp <= 0 || cp == 65001) cp = 1252;
            try { return Encoding.GetEncoding(cp); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { return Encoding.GetEncoding(1252); }
        }

        // One playlist line -> full path or URL (null when it cannot be a path).
        private static string ResolveEntry(string raw, string dir)
        {
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2).Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) s = FileUriToPath(s);
            else if (IsUrl(s)) return s;
            if (s.Length == 0) return null;
            // "\dir\clip.mp4" means the list's own drive, not the current one.
            if ((s[0] == '\\' || s[0] == '/') && !(s.Length > 1 && (s[1] == '\\' || s[1] == '/')))
            {
                string root = SafeRoot(dir);
                if (root.Length > 0) s = root.TrimEnd(Separators) + s;
            }
            try
            {
                if (!Path.IsPathRooted(s)) s = Path.Combine(dir, s);
            }
            catch (ArgumentException)
            {
                return null;
            }
            return FullPath(s);
        }

        // Path of 'item' relative to 'dir' when both share a root (drive or UNC share), else 'item' as is.
        private static string RelativeEntry(string item, string dir)
        {
            string full = FullPath(item);
            if (full == null) return item;
            string root = SafeRoot(full), dirRoot = SafeRoot(dir);
            if (root.Length == 0 || !string.Equals(root.TrimEnd(Separators), dirRoot.TrimEnd(Separators), StringComparison.OrdinalIgnoreCase))
                return full;
            string[] from = dir.Substring(dirRoot.Length).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            string[] to = full.Substring(root.Length).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (to.Length == 0) return full;
            int common = 0;
            while (common < from.Length && common < to.Length - 1 &&
                   string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase)) common++;
            var sb = new StringBuilder();
            for (int i = common; i < from.Length; i++) sb.Append(@"..\");
            for (int i = common; i < to.Length; i++)
            {
                if (i > common) sb.Append('\\');
                sb.Append(to[i]);
            }
            string rel = sb.ToString();
            // A leading '#' would read back as a comment, leading blanks would be trimmed away.
            if (rel[0] == '#' || char.IsWhiteSpace(rel[0])) rel = @".\" + rel;
            return rel;
        }

        private static string SafeRoot(string path)
        {
            try { return Path.GetPathRoot(path) ?? ""; }
            catch (ArgumentException) { return ""; }
        }

        // ---- sorting ----

        private List<string> SortItems(IEnumerable<string> items)
        {
            bool stat = _sort == PlaylistSort.DateModified || _sort == PlaylistSort.Size;
            return Sorted(items.Select(i => Entry.FromItem(i, stat)).ToList());
        }

        private List<string> Sorted(List<Entry> entries)
        {
            // OrderBy is stable, and every key is read once up front (no file I/O inside comparisons).
            return entries.OrderBy(e => e, new EntryComparer(_sort, _descending)).Select(e => e.Path).ToList();
        }

        private static int IndexIn(List<string> items, string path)
        {
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i], path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string ExtensionOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int dot = path.LastIndexOf('.');
            if (dot < 0 || dot <= path.LastIndexOfAny(Separators)) return "";
            return path.Substring(dot);
        }

        private static bool IsAsciiLetter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

        private static string Patterns(IEnumerable<string> exts)
        {
            var sb = new StringBuilder();
            foreach (var e in exts)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append('*').Append(e);
            }
            return sb.ToString();
        }

        private sealed class Entry
        {
            public string Path;
            public string Name;
            public int DirLength;   // length of the folder part of Path (0 for URLs)
            public string Ext;
            public DateTime Time;
            public long Size;
            public bool Remote;

            public static Entry FromInfo(FileInfo f)
            {
                string path = f.FullName;
                return new Entry
                {
                    Path = path, Name = f.Name, DirLength = Math.Max(0, path.LastIndexOfAny(Separators)),
                    Ext = ExtensionOf(f.Name).ToLowerInvariant(), Time = f.LastWriteTimeUtc, Size = f.Length,
                };
            }

            // 'stat' reads time and size from disk; name and type sorts do not need them.
            public static Entry FromItem(string item, bool stat)
            {
                if (IsUrl(item)) return new Entry { Path = item, Name = item, Ext = "", Remote = true, Size = -1 };
                int sep = item.LastIndexOfAny(Separators);
                var e = new Entry
                {
                    Path = item, Name = item.Substring(sep + 1), DirLength = Math.Max(0, sep),
                    Ext = ExtensionOf(item).ToLowerInvariant(), Size = -1,
                };
                if (!stat) return e;
                try
                {
                    var f = new FileInfo(item);
                    if (f.Exists)
                    {
                        e.Time = f.LastWriteTimeUtc;
                        e.Size = f.Length;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException || ex is NotSupportedException)
                {
                }
                return e;
            }
        }

        private sealed class EntryComparer : IComparer<Entry>
        {
            private readonly PlaylistSort _sort;
            private readonly bool _descending;

            public EntryComparer(PlaylistSort sort, bool descending)
            {
                _sort = sort;
                _descending = descending;
            }

            public int Compare(Entry a, Entry b)
            {
                if (a.Remote != b.Remote) return a.Remote ? 1 : -1;
                int c;
                switch (_sort)
                {
                    case PlaylistSort.DateModified: c = a.Time.CompareTo(b.Time); break;
                    case PlaylistSort.Size: c = a.Size.CompareTo(b.Size); break;
                    case PlaylistSort.Type: c = string.CompareOrdinal(a.Ext, b.Ext); break;
                    default: c = ByName(a, b); break;
                }
                if (_descending) c = -c;
                if (c == 0) c = ByName(a, b);
                if (c == 0) c = NaturalCompare(a.Path, b.Path);
                if (c == 0) c = string.CompareOrdinal(a.Path, b.Path);
                return c;
            }

            // Folder first (segment by segment, so each folder's files stay together and come before
            // its subfolders), then the file name; all in natural order.
            private static int ByName(Entry a, Entry b)
            {
                int c = CompareFolders(a.Path, a.DirLength, b.Path, b.DirLength);
                return c != 0 ? c : NaturalCompare(a.Name, b.Name);
            }

            private static int CompareFolders(string a, int aLen, string b, int bLen)
            {
                int i = 0;
                while (true)
                {
                    bool aEnd = i >= aLen, bEnd = i >= bLen;
                    if (aEnd || bEnd) return aEnd == bEnd ? 0 : aEnd ? -1 : 1; // a folder's own files first
                    int ae = SegmentEnd(a, i, aLen), be = SegmentEnd(b, i, bLen);
                    // Identical leading segments are skipped without allocating; both positions stay aligned.
                    if (ae != be || string.Compare(a, i, b, i, ae - i, StringComparison.OrdinalIgnoreCase) != 0)
                    {
                        string sa = a.Substring(i, ae - i), sb = b.Substring(i, be - i);
                        int c = NaturalCompare(sa, sb);
                        if (c == 0) c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                        if (c == 0) c = string.CompareOrdinal(sa, sb);
                        return c;
                    }
                    i = ae + 1;
                }
            }

            private static int SegmentEnd(string s, int start, int limit)
            {
                int end = s.IndexOfAny(Separators, start, limit - start);
                return end < 0 ? limit : end;
            }
        }
    }
}
