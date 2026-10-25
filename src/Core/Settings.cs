using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace VidFlow.Core
{
    public enum RepeatMode { Off, One, All }
    public enum WheelAction { Volume, Seek, FrameStep, Zoom }
    public enum ClickAction { PlayPause, None }
    public enum OsdStatusMode { Off, FullscreenOnly, Always }

    // User preferences, persisted as a flat "key=value" INI file.
    //
    // Location: VidFlow.ini next to VidFlow.exe if that file exists (portable mode),
    // otherwise %APPDATA%\VidFlow\VidFlow.ini. Every public read/write property of type
    // bool/int/double/string/enum is saved automatically; RecentFiles and KeyOverrides
    // are stored as "recent.N=" and "key.<ActionId>=" lines.
    //
    // Several VidFlow windows may share the file. Save() re-reads it and writes only what this
    // process changed since it loaded or last saved (values compared with a snapshot, so a
    // Clone() edited by the Settings dialog and copied back is detected too); everything else
    // keeps the file's value. The file is replaced atomically, the previous one kept as
    // VidFlow.ini.bak. If an existing file cannot be read at startup the instance is read-only
    // for the session, so the user's settings are never overwritten with defaults.
    public sealed class Settings
    {
        // ---- playback ----
        public double Volume { get; set; } = 100;
        public bool Muted { get; set; }
        public RepeatMode Repeat { get; set; } = RepeatMode.One;
        public bool Shuffle { get; set; }
        public bool AutoLoadFolder { get; set; } = true;     // opening one file queues its folder's media
        public bool AutoAdvance { get; set; }                // Repeat Off: go to next file at end instead of pausing
        public bool ResumePlayback { get; set; }             // remember position per file (mpv watch-later)
        public bool PitchCorrection { get; set; } = true;
        public bool KeepSpeedBetweenFiles { get; set; } = true;
        public string HwDec { get; set; } = "auto-safe";     // mpv --hwdec value; "no" disables
        public double SeekStep { get; set; } = 5;            // Left/Right
        public double SeekStepShort { get; set; } = 1;       // Shift+Left/Right
        public double SeekStepLong { get; set; } = 30;       // Alt+Left/Right
        public string ScreenshotFolder { get; set; } = "";   // "" = Pictures\VidFlow
        public string ScreenshotFormat { get; set; } = "png"; // png | jpg | webp
        public string PreferredAudioLang { get; set; } = ""; // mpv --alang, e.g. "en,eng"
        public string PreferredSubLang { get; set; } = "";   // mpv --slang
        public bool SubtitlesVisible { get; set; } = true;
        public double SubScale { get; set; } = 1.0;
        public string AudioDevice { get; set; } = "auto";    // mpv --audio-device name
        public bool AudioNormalize { get; set; }             // dynaudnorm audio filter
        public string AudioChannels { get; set; } = "auto";  // auto | stereo | mono

        // ---- cull (review decisions; stored in a local index, never beside the originals) ----
        public bool CullMode { get; set; }                   // P/X/U/0-5/N act as cull keys
        public bool CullAutoAdvance { get; set; } = true;    // go to the next clip after pick/reject
        public string CullPickAction { get; set; } = "Move"; // Move | Copy | None  (what Apply does with picks)
        public string CullKeepFolder { get; set; } = "";     // "" = a "Keep" folder beside each clip
        public string CullRejectAction { get; set; } = "RecycleBin"; // RecycleBin | Folder | None
        public string CullRejectFolder { get; set; } = "";   // "" = a "Rejected" folder beside each clip
        public string CullBin1 { get; set; } = "";           // F5..F9 move targets ("" = unset, asks once)
        public string CullBin2 { get; set; } = "";
        public string CullBin3 { get; set; } = "";
        public string CullBin4 { get; set; } = "";
        public string CullBin5 { get; set; } = "";

        // ---- first run / updates ----
        public bool QuickStartShown { get; set; }            // the first-run card was dismissed
        public string UpdateRepo { get; set; } = AppInfo.Repo; // "owner/repo" on GitHub; "" hides Check for Updates

        // ---- playlist ----
        public bool IncludeSubfolders { get; set; } = true;  // folders the user opens/drops are walked recursively
        public string PlaylistSortBy { get; set; } = "Name"; // Name | DateModified | Size | Type
        public bool PlaylistSortDescending { get; set; }

        // ---- window / view ----
        public int WindowX { get; set; } = int.MinValue;     // int.MinValue = let Windows/auto-fit decide
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; } = 960;
        public int WindowHeight { get; set; } = 600;
        public bool WindowMaximized { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool ShowControls { get; set; } = true;       // control bar in windowed mode
        public bool ShowPlaylist { get; set; }
        public int PlaylistWidth { get; set; } = 280;
        public bool AutoFitWindow { get; set; } = true;      // resize window to the video on open
        public int AutoFitMaxPercent { get; set; } = 75;     // of the monitor work area
        public bool FullscreenControlsOnHover { get; set; }  // false = truly clean fullscreen
        public bool OsdMessages { get; set; } = true;
        public bool ThumbnailPreview { get; set; } = true;   // seek-bar hover preview
        public bool HideCursorWhenIdle { get; set; } = true;
        public bool ShowFrameNumber { get; set; } = true;
        public bool ShowRemainingTime { get; set; }
        public OsdStatusMode OsdStatus { get; set; } = OsdStatusMode.Off; // persistent time/frame line on the video
        public bool Borderless { get; set; }                 // windowed mode without a title bar
        public bool KeepAspectOnResize { get; set; }         // window edges snap to the video's aspect ratio
        public bool TaskbarProgress { get; set; } = true;    // progress + thumbnail buttons on the taskbar button

        // ---- mouse ----
        public WheelAction Wheel { get; set; } = WheelAction.Volume;
        public ClickAction LeftClick { get; set; } = ClickAction.PlayPause;
        public bool DoubleClickFullscreen { get; set; } = true;
        public bool DragMovesWindow { get; set; } = true;

        // ---- behaviour ----
        public bool SingleInstance { get; set; } = true;
        public bool ConfirmDelete { get; set; } = true;
        public string LastFolder { get; set; } = "";
        public int MaxRecentFiles { get; set; } = 15;
        public string ExtraMpvOptions { get; set; } = "";    // one "name=value" per line, applied before init

        public List<string> RecentFiles { get; private set; } = new List<string>();
        // Action id -> shortcut list ("Ctrl+Right, ."); an empty value unbinds the action.
        public Dictionary<string, string> KeyOverrides { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string FilePath { get; private set; }

        // True when the file exists but could not be read at startup (locked by a sync or backup
        // tool, another process, no access). Save() then does nothing for the rest of the session
        // instead of replacing the user's settings with the defaults this instance holds.
        public bool IsReadOnly { get; private set; }
        private bool _readOnlyLogged;

        // What this process last loaded or saved (formatted values, recent list, overrides).
        // Save() compares against it to find what changed here.
        private Dictionary<string, string> _savedValues;
        private List<string> _savedRecent;
        private Dictionary<string, string> _savedKeys;

        private const string Header = "; VidFlow settings. Edit while VidFlow is closed, or use Settings in the app.";
        private const int IoAttempts = 5, IoRetryMs = 100; // brief retries while another process holds the file
        private const int SaveLockMs = 5000;

        private static PropertyInfo[] _persisted;

        public static string DefaultDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VidFlow"); }
        }

        public static string ResolvePath()
        {
            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VidFlow.ini");
            return File.Exists(portable) ? portable : Path.Combine(DefaultDirectory, "VidFlow.ini");
        }

        // One-time move from the beta name "Glance": when there is no VidFlow.ini yet, copy the old
        // settings, cull decisions, mpv config and watch-later data across (originals are left in
        // place, so an old beta keeps working), and drop the old unpacked-engine cache.
        // Best effort: a failure just means starting with defaults. Call before Load() when the
        // default settings location is used.
        public static void MigrateFromGlance()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string portableOld = Path.Combine(exeDir, "Glance.ini"), portableNew = Path.Combine(exeDir, "VidFlow.ini");
                if (File.Exists(portableOld) && !File.Exists(portableNew))
                {
                    CopyLegacyFiles(exeDir, exeDir);
                    Log.Info("migrated portable Glance settings next to the exe");
                }
                string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");
                if (!File.Exists(portableNew) && File.Exists(Path.Combine(oldDir, "Glance.ini"))
                    && !File.Exists(Path.Combine(DefaultDirectory, "VidFlow.ini")))
                {
                    System.IO.Directory.CreateDirectory(DefaultDirectory);
                    CopyLegacyFiles(oldDir, DefaultDirectory);
                    CopyTree(Path.Combine(oldDir, "mpv"), Path.Combine(DefaultDirectory, "mpv"));
                    CopyTree(Path.Combine(oldDir, "watch_later"), Path.Combine(DefaultDirectory, "watch_later"));
                    Log.Info("migrated Glance settings from " + oldDir);
                }
                string oldEngine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance", "engine");
                if (System.IO.Directory.Exists(oldEngine))
                {
                    try { System.IO.Directory.Delete(oldEngine, true); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { } // an old beta is running
                }
            }
            catch (Exception ex)
            {
                Log.Warn("settings migration from Glance skipped: " + ex.Message);
            }
        }

        private static void CopyLegacyFiles(string fromDir, string toDir)
        {
            File.Copy(Path.Combine(fromDir, "Glance.ini"), Path.Combine(toDir, "VidFlow.ini"), false);
            string decisions = Path.Combine(fromDir, "cull-decisions.tsv");
            string decisionsTo = Path.Combine(toDir, "cull-decisions.tsv");
            if (File.Exists(decisions) && !File.Exists(decisionsTo)) File.Copy(decisions, decisionsTo, false);
        }

        private static void CopyTree(string from, string to)
        {
            if (!System.IO.Directory.Exists(from) || System.IO.Directory.Exists(to)) return;
            foreach (var dir in System.IO.Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                System.IO.Directory.CreateDirectory(to + dir.Substring(from.Length));
            System.IO.Directory.CreateDirectory(to);
            foreach (var file in System.IO.Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, to + file.Substring(from.Length), false);
        }

        public string Directory
        {
            get { return Path.GetDirectoryName(FilePath); }
        }

        public string EffectiveScreenshotFolder
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ScreenshotFolder)) return ScreenshotFolder;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VidFlow");
            }
        }

        public void AddRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, path);
            int max = Math.Max(0, MaxRecentFiles);
            if (RecentFiles.Count > max) RecentFiles.RemoveRange(max, RecentFiles.Count - max);
        }

        private static PropertyInfo[] Persisted()
        {
            if (_persisted != null) return _persisted;
            var list = new List<PropertyInfo>();
            foreach (var p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetSetMethod() == null) continue;
                var t = p.PropertyType;
                if (t == typeof(bool) || t == typeof(int) || t == typeof(double) || t == typeof(string) || t.IsEnum)
                    list.Add(p);
            }
            return _persisted = list.ToArray();
        }

        public static Settings Load(string path = null)
        {
            string file = path ?? ResolvePath();
            try { file = Path.GetFullPath(file); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
            var s = new Settings { FilePath = file };
            Exception error;
            string[] lines = ReadFile(file, out error);
            if (lines != null)
            {
                var ini = Parse(lines);
                var props = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Persisted()) props[p.Name] = p;
                foreach (var kv in ini.Values) TryAssign(s, props[kv.Key], kv.Value);
                s.RecentFiles.AddRange(ini.Recent);
                foreach (var kv in ini.Keys) s.KeyOverrides[kv.Key] = kv.Value;
            }
            else if (error != null)
            {
                s.IsReadOnly = true;
                Log.Error("settings: " + file + " could not be read; using defaults and not saving this session", error);
            }
            s.TakeSnapshot();
            return s;
        }

        // The file's lines, retrying briefly while another process (a VidFlow window replacing it,
        // a sync or backup tool) holds it. null with error == null: there is no file.
        private static string[] ReadFile(string file, out Exception error)
        {
            error = null;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(file)) return null;
                    var lines = new List<string>();
                    // Share everything, so a reader never makes another window's File.Replace fail.
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) lines.Add(line);
                    }
                    return lines.ToArray();
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    return null; // deleted between the check and the open
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt >= IoAttempts) { error = ex; return null; }
                    Thread.Sleep(IoRetryMs);
                }
                catch (Exception ex)
                {
                    error = ex;
                    return null;
                }
            }
        }

        // A settings file split into its parts. Values are unescaped; unknown keys (from a newer
        // version, or typed by hand) are kept so a save does not drop them.
        private sealed class IniContent
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // property name -> text
            public readonly List<string> Recent = new List<string>();
            public readonly Dictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<KeyValuePair<string, string>> Unknown = new List<KeyValuePair<string, string>>();
        }

        private static IniContent Parse(string[] lines)
        {
            var props = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Persisted()) props[p.Name] = p;
            var ini = new IniContent();
            var recent = new SortedDictionary<int, string>();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = Unescape(line.Substring(eq + 1).Trim());
                if (key.StartsWith("key.", StringComparison.OrdinalIgnoreCase))
                {
                    ini.Keys[key.Substring(4)] = value;
                    continue;
                }
                if (key.StartsWith("recent.", StringComparison.OrdinalIgnoreCase))
                {
                    int n;
                    if (int.TryParse(key.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) recent[n] = value;
                    continue;
                }
                PropertyInfo prop;
                if (props.TryGetValue(key, out prop)) ini.Values[prop.Name] = value;
                else
                {
                    ini.Unknown.RemoveAll(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
                    ini.Unknown.Add(new KeyValuePair<string, string>(key, value));
                }
            }
            foreach (var kv in recent) if (kv.Value.Length > 0) ini.Recent.Add(kv.Value);
            ini.Recent.RemoveAll(new DuplicateFilter().IsDuplicate);
            return ini;
        }

        // Predicate for List.RemoveAll that drops repeated paths (case-insensitive), keeping the first.
        private sealed class DuplicateFilter
        {
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool IsDuplicate(string path) { return path == null || !_seen.Add(path); }
        }

        private static void TryAssign(Settings s, PropertyInfo p, string value)
        {
            try
            {
                var t = p.PropertyType;
                object v;
                if (t == typeof(string)) v = value;
                else if (t == typeof(bool)) v = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                else if (t == typeof(int)) v = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                else if (t == typeof(double)) v = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                else if (t.IsEnum) v = Enum.Parse(t, value, true);
                else return;
                p.SetValue(s, v, null);
            }
            catch (Exception)
            {
                Log.Warn("settings: ignoring bad value for " + p.Name + ": " + value);
            }
        }

        private static string Format(object v)
        {
            return v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture)
                 : v is bool ? ((bool)v ? "true" : "false")
                 : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }

        private void TakeSnapshot()
        {
            _savedValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Persisted()) _savedValues[p.Name] = Format(p.GetValue(this, null));
            _savedRecent = new List<string>(RecentFiles);
            _savedKeys = new Dictionary<string, string>(KeyOverrides, StringComparer.OrdinalIgnoreCase);
        }

        public void Save()
        {
            if (IsReadOnly)
            {
                // Save runs on every file load: say it once.
                if (!_readOnlyLogged) Log.Warn("settings not saved: " + FilePath + " could not be read at startup (read-only this session)");
                _readOnlyLogged = true;
                return;
            }
            if (string.IsNullOrEmpty(FilePath))
            {
                Log.Warn("settings not saved: no settings file");
                return;
            }
            Mutex gate = AcquireSaveLock();
            try
            {
                Exception error;
                string[] lines = ReadFile(FilePath, out error);
                if (error != null)
                {
                    // Never replace a file we cannot read; the changes stay pending for the next Save.
                    Log.Error("settings not saved: " + FilePath + " could not be re-read", error);
                    return;
                }
                List<string> recent;
                string text = Compose(lines != null ? Parse(lines) : null, out recent);
                if (lines == null || !SameText(lines, text)) WriteFile(text);

                // This process is now in sync with the file: later saves only carry later changes.
                // The merged recent list (with other windows' files) is adopted; other values keep
                // this process's own state.
                _savedValues = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in Persisted()) _savedValues[p.Name] = Format(p.GetValue(this, null));
                if (!SameList(RecentFiles, recent))
                {
                    RecentFiles.Clear();
                    RecentFiles.AddRange(recent);
                }
                _savedRecent = new List<string>(recent);
                _savedKeys = new Dictionary<string, string>(KeyOverrides, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error("settings save failed", ex);
            }
            finally
            {
                if (gate != null)
                {
                    try { gate.ReleaseMutex(); } catch (Exception) { }
                    gate.Dispose();
                }
            }
        }

        // The file text: for each value, this process's value if it changed here since the last
        // load/save, otherwise the file's (`disk` is null when there is no file).
        private string Compose(IniContent disk, out List<string> recent)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            string maxRecentText = null;
            foreach (var p in Persisted())
            {
                string mine = Format(p.GetValue(this, null)), saved, theirs;
                bool changedHere = _savedValues == null || !_savedValues.TryGetValue(p.Name, out saved) || saved != mine;
                string text = !changedHere && disk != null && disk.Values.TryGetValue(p.Name, out theirs) ? theirs : mine;
                if (p.Name == nameof(MaxRecentFiles)) maxRecentText = text;
                sb.Append(p.Name).Append('=').AppendLine(Escape(text));
            }
            int max;
            if (!int.TryParse(maxRecentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out max)) max = MaxRecentFiles;
            recent = MergeRecent(disk, Math.Max(0, max));
            for (int i = 0; i < recent.Count; i++)
                sb.Append("recent.").Append(i.ToString(CultureInfo.InvariantCulture)).Append('=').AppendLine(Escape(recent[i]));
            foreach (var kv in MergeKeys(disk))
                sb.Append("key.").Append(kv.Key).Append('=').AppendLine(Escape(kv.Value ?? ""));
            if (disk != null)
                foreach (var kv in disk.Unknown)
                    sb.Append(kv.Key).Append('=').AppendLine(Escape(kv.Value));
            return sb.ToString();
        }

        // Recent files: the entries this process opened or re-opened since the last load/save come
        // first, then the file's list in its order (it holds what other windows opened), minus the
        // entries this process removed. Entries untouched here follow the file, so a removal or a
        // newer open in another window is not undone.
        private List<string> MergeRecent(IniContent disk, int max)
        {
            var local = new List<string>(RecentFiles);
            local.RemoveAll(new DuplicateFilter().IsDuplicate);
            List<string> result;
            if (disk == null || _savedRecent == null) result = local;
            else
            {
                var kept = new HashSet<string>(local, StringComparer.OrdinalIgnoreCase);
                var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var baseline = new List<string>();
                foreach (var p in _savedRecent)
                {
                    if (p == null) continue;
                    if (kept.Contains(p)) baseline.Add(p);
                    else removed.Add(p);
                }
                // AddRecent inserts at the front, so the untouched entries are the longest tail of
                // the local list that still runs in the baseline's order; what precedes it is new here.
                int fresh = local.Count, limit = baseline.Count - 1;
                while (fresh > 0)
                {
                    int at = LastIndexOf(baseline, local[fresh - 1], limit);
                    if (at < 0) break;
                    limit = at - 1;
                    fresh--;
                }
                result = local.GetRange(0, fresh);
                var seen = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
                foreach (var p in disk.Recent)
                    if (!removed.Contains(p) && seen.Add(p)) result.Add(p);
            }
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }

        private static int LastIndexOf(List<string> list, string item, int last)
        {
            for (int i = Math.Min(last, list.Count - 1); i >= 0; i--)
                if (string.Equals(list[i], item, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // Shortcut overrides: actions this process bound, rebound or reset since the last load/save
        // take its value; every other action keeps the file's.
        private Dictionary<string, string> MergeKeys(IniContent disk)
        {
            if (disk == null || _savedKeys == null) return new Dictionary<string, string>(KeyOverrides, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, string>(disk.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in KeyOverrides)
            {
                string before;
                if (!_savedKeys.TryGetValue(kv.Key, out before) || !string.Equals(before ?? "", kv.Value ?? "", StringComparison.Ordinal))
                    result[kv.Key] = kv.Value ?? "";
            }
            foreach (var kv in _savedKeys)
                if (!KeyOverrides.ContainsKey(kv.Key)) result.Remove(kv.Key);
            return result;
        }

        private static bool SameText(string[] lines, string text)
        {
            var mine = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            int n = mine.Length > 0 && mine[mine.Length - 1].Length == 0 ? mine.Length - 1 : mine.Length;
            if (n != lines.Length) return false;
            for (int i = 0; i < n; i++)
                if (!string.Equals(mine[i], lines[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // Writes a temp file and swaps it in atomically, keeping the previous file as VidFlow.ini.bak.
        private void WriteFile(string text)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", true);
                        else File.Move(tmp, FilePath);
                        return;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < IoAttempts)
                    {
                        Thread.Sleep(IoRetryMs); // a reader or another window's save holds the file
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { }
            }
        }

        // Serializes Save()'s read-merge-write between VidFlow processes sharing the file. null when
        // the lock is unavailable; the save then goes ahead without it.
        private Mutex AcquireSaveLock()
        {
            Mutex m = null;
            try
            {
                // Mutex names may not contain '\', so the path is hashed (FNV-1a, stable across processes).
                ulong h = 14695981039346656037UL;
                foreach (char c in FilePath.ToUpperInvariant()) { h ^= c; h *= 1099511628211UL; }
                m = new Mutex(false, "VidFlow.settings." + h.ToString("x16", CultureInfo.InvariantCulture));
                bool owned;
                try { owned = m.WaitOne(SaveLockMs); }
                catch (AbandonedMutexException) { owned = true; } // its holder died; the lock is ours
                if (owned) return m;
                Log.Warn("settings: another VidFlow window is still saving; saving anyway");
            }
            catch (Exception ex)
            {
                Log.Warn("settings: save lock unavailable: " + ex.Message);
            }
            if (m != null) m.Dispose();
            return null;
        }

        // Values are stored raw, so hand-typed Windows paths ("F:\clips\new") need no escaping.
        // Only multi-line values are escaped, behind an explicit "esc:" marker.
        private const string EscMarker = "esc:";

        private static string Escape(string s)
        {
            s = s.Replace("\r", "");
            if (s.IndexOf('\n') < 0 && !s.StartsWith(EscMarker, StringComparison.Ordinal)) return s;
            return EscMarker + s.Replace("\\", "\\\\").Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (!s.StartsWith(EscMarker, StringComparison.Ordinal)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = EscMarker.Length; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' ? '\n' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public Settings Clone()
        {
            var c = (Settings)MemberwiseClone();
            c.RecentFiles = new List<string>(RecentFiles);
            c.KeyOverrides = new Dictionary<string, string>(KeyOverrides, StringComparer.OrdinalIgnoreCase);
            if (_savedValues != null) c._savedValues = new Dictionary<string, string>(_savedValues, StringComparer.Ordinal);
            if (_savedRecent != null) c._savedRecent = new List<string>(_savedRecent);
            if (_savedKeys != null) c._savedKeys = new Dictionary<string, string>(_savedKeys, StringComparer.OrdinalIgnoreCase);
            return c;
        }
    }
}
