using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using VidFlow.Core;

namespace VidFlow.Media
{
    // A clip's review decision: Flag 1 = pick, -1 = reject, 0 = undecided; Rating 0..5 stars.
    public struct CullMark : IEquatable<CullMark>
    {
        public static readonly CullMark None = new CullMark(0, 0);

        public CullMark(int flag, int rating)
        {
            Flag = Math.Sign(flag);
            Rating = Math.Max(0, Math.Min(5, rating));
        }

        public int Flag { get; }
        public int Rating { get; }

        public bool IsEmpty { get { return Flag == 0 && Rating == 0; } }
        public bool IsPick { get { return Flag > 0; } }
        public bool IsReject { get { return Flag < 0; } }

        public CullMark WithFlag(int flag) { return new CullMark(flag, Rating); }
        public CullMark WithRating(int rating) { return new CullMark(Flag, rating); }

        public bool Equals(CullMark other) { return Flag == other.Flag && Rating == other.Rating; }
        public override bool Equals(object obj) { return obj is CullMark && Equals((CullMark)obj); }
        public override int GetHashCode() { return Flag * 8 + Rating; }
        public static bool operator ==(CullMark a, CullMark b) { return a.Equals(b); }
        public static bool operator !=(CullMark a, CullMark b) { return !a.Equals(b); }
    }

    // The decisions index (cull mode). Decisions are keyed by full path and remembered together with
    // the file's size and last-write time, so an entry only applies to the exact file it was made for:
    // a file that has since changed is ignored (and forgotten), a file that is missing right now (an
    // unplugged drive) is ignored but kept. Nothing is ever written next to the originals: the index
    // is one small UTF-8 file in the settings folder, loaded on first use and replaced atomically.
    // Several VidFlow windows may share it: a save merges this session's changes into what is on disk.
    // UI thread only.
    public sealed class CullStore
    {
        public const string FileName = "cull-decisions.tsv";
        private const string Header = "# VidFlow cull decisions v1: flag<TAB>rating<TAB>size<TAB>mtime-utc-ticks<TAB>updated-utc-ticks<TAB>path";
        private const int MaxEntries = 100000;

        private sealed class Entry
        {
            public string Path;
            public long Size, WriteTicks, Updated;
            public int Flag, Rating;
            public byte State; // 0 = not checked yet, 1 = matches the file on disk, 2 = file missing right now
        }

        private readonly string _file;
        private Dictionary<string, Entry> _entries;   // null until first use
        // Paths changed this session (value null = removed); a save applies exactly these on top of the file.
        private readonly Dictionary<string, Entry> _touched = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public CullStore(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory");
            _file = Path.Combine(directory, FileName);
        }

        public string FilePath { get { return _file; } }

        // Unsaved changes.
        public bool IsDirty { get { return _touched.Count > 0; } }

        // Bumped on every change (lets views cache counts cheaply).
        public int Version { get; private set; }

        public CullMark Get(string path)
        {
            var e = Find(path);
            return e == null ? CullMark.None : new CullMark(e.Flag, e.Rating);
        }

        // Records a decision for a local file (an empty mark forgets it). False when the file cannot be
        // read (gone, or not a local path), so a decision is never stored for a file we cannot identify.
        public bool Set(string path, CullMark mark)
        {
            string key = Key(path);
            if (key == null) return false;
            EnsureLoaded();
            if (mark.IsEmpty)
            {
                Forget(key);
                return true;
            }
            long size, ticks;
            if (!Stat(key, out size, out ticks)) return false;
            var e = new Entry
            {
                Path = key, Size = size, WriteTicks = ticks, Updated = DateTime.UtcNow.Ticks,
                Flag = mark.Flag, Rating = mark.Rating, State = 1,
            };
            Entry old;
            if (_entries.TryGetValue(key, out old) && old.State == 1 && old.Flag == e.Flag && old.Rating == e.Rating && old.Size == size && old.WriteTicks == ticks)
                return true;
            _entries[key] = e;
            _touched[key] = e;
            Version++;
            return true;
        }

        // The file was moved or renamed by VidFlow: its decision follows it.
        public void Move(string from, string to)
        {
            string a = Key(from), b = Key(to);
            if (a == null || b == null) return;
            EnsureLoaded();
            Entry e;
            if (!_entries.TryGetValue(a, out e)) return;
            long size, ticks;
            bool known = Stat(b, out size, out ticks);
            Forget(a); // for a case-only rename this is the same key, re-added below with the new spelling
            var moved = new Entry
            {
                Path = b,
                Size = known ? size : e.Size,
                WriteTicks = known ? ticks : e.WriteTicks,
                Updated = DateTime.UtcNow.Ticks,
                Flag = e.Flag, Rating = e.Rating,
                State = (byte)(known ? 1 : 0),
            };
            _entries[b] = moved;
            _touched[b] = moved;
            Version++;
        }

        public void Remove(string path)
        {
            string key = Key(path);
            if (key == null) return;
            EnsureLoaded();
            Forget(key);
        }

        // Writes the index if anything changed (temporary file + replace, so a crash never leaves half
        // a file). Changes made by another window since our load are kept; ours win where both changed.
        public bool Save()
        {
            if (_touched.Count == 0) return true;
            try
            {
                string dir = Path.GetDirectoryName(_file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var merged = ReadFile();
                foreach (var kv in _touched)
                {
                    if (kv.Value == null) merged.Remove(kv.Key);
                    else merged[kv.Key] = kv.Value;
                }
                var list = new List<Entry>(merged.Values);
                if (list.Count > MaxEntries)
                {
                    list.Sort((x, y) => y.Updated.CompareTo(x.Updated));
                    list.RemoveRange(MaxEntries, list.Count - MaxEntries);
                }
                var sb = new StringBuilder(list.Count * 96 + Header.Length + 2);
                sb.Append(Header).Append('\n');
                foreach (var e in list)
                {
                    sb.Append(e.Flag.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.Rating.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.WriteTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.Updated.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.Path).Append('\n');
                }
                string tmp = _file + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(_file)) File.Replace(tmp, _file, null, true);
                else File.Move(tmp, _file);
                _touched.Clear();
                // Pick up what other windows saved, without re-checking files we already know.
                foreach (var kv in merged)
                    if (!_entries.ContainsKey(kv.Key)) _entries[kv.Key] = kv.Value;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException ||
                                       ex is NotSupportedException || ex is ArgumentException)
            {
                Log.Warn("cull decisions not saved to " + _file + ": " + ex.Message);
                return false;
            }
        }

        // ---- internals ----

        private Entry Find(string path)
        {
            string key = Key(path);
            if (key == null) return null;
            EnsureLoaded();
            Entry e;
            if (!_entries.TryGetValue(key, out e)) return null;
            if (e.State == 0)
            {
                long size, ticks;
                if (!Stat(key, out size, out ticks)) e.State = 2;
                else if (size != e.Size || ticks != e.WriteTicks)
                {
                    // Edited or replaced since the decision: it no longer applies.
                    Forget(key);
                    return null;
                }
                else e.State = 1;
            }
            return e.State == 1 ? e : null;
        }

        private void Forget(string key)
        {
            bool had = _entries.Remove(key);
            if (had || _touched.ContainsKey(key))
            {
                _touched[key] = null;
                Version++;
            }
        }

        private void EnsureLoaded()
        {
            if (_entries != null) return;
            _entries = ReadFile();
        }

        private Dictionary<string, Entry> ReadFile()
        {
            var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            string[] lines;
            try
            {
                if (!File.Exists(_file)) return map;
                lines = File.ReadAllLines(_file, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException ||
                                       ex is NotSupportedException || ex is ArgumentException)
            {
                Log.Warn("cull decisions not read from " + _file + ": " + ex.Message);
                return map;
            }
            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split(new[] { '\t' }, 6);
                if (f.Length < 6) continue;
                int flag, rating;
                long size, ticks, updated;
                if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out flag) ||
                    !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rating) ||
                    !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
                    !long.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                    !long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out updated)) continue;
                string path = f[5];
                var mark = new CullMark(flag, rating);
                if (mark.IsEmpty || Key(path) == null) continue;
                map[path] = new Entry { Path = path, Size = size, WriteTicks = ticks, Updated = updated, Flag = mark.Flag, Rating = mark.Rating };
            }
            return map;
        }

        // Absolute local or UNC file paths only (URLs and relative paths have no stable identity).
        private static string Key(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Playlist.IsUrl(path)) return null;
            if (path.IndexOf('\t') >= 0 || path.IndexOf('\n') >= 0 || path.IndexOf('\r') >= 0) return null;
            try
            {
                return Path.IsPathRooted(path) && Path.GetPathRoot(path).Length > 1 ? path : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool Stat(string path, out long size, out long writeTicks)
        {
            size = 0;
            writeTicks = 0;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                size = fi.Length;
                writeTicks = fi.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException ||
                                       ex is NotSupportedException || ex is ArgumentException)
            {
                return false;
            }
        }
    }
}
