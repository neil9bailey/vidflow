using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VidFlow.Core
{
    // Command line:  VidFlow.exe [options] [file|folder|url ...]
    //   --new              always open a new window (ignore single-instance)
    //   --fullscreen       start in fullscreen
    //   --start <t>        start position: seconds, m:ss(.fff), -t (from the end) or f<frame>
    //   --speed <x>        playback speed, e.g. 0.5
    //   --repeat <mode>    off | one | all
    //   --paused           open paused on the first frame
    //   --enqueue          add to the running window's playlist instead of replacing it
    //   --settings <ini>   use this settings file instead of the default
    //   --log [file.log]   write a diagnostic log (default %LOCALAPPDATA%\VidFlow\vidflow.log)
    //   --automation       accept scripted test commands on the control pipe (implies --new)
    //   --pipe <name>      control pipe name (defaults to a per-user name)
    // "--name=value" works as well as "--name value".
    public sealed class LaunchOptions
    {
        public List<string> Paths { get; private set; } = new List<string>();
        public bool ForceNewInstance { get; set; }
        public bool Fullscreen { get; set; }
        public bool Automation { get; set; }
        public string SettingsPath { get; set; }
        public string LogPath { get; set; }
        public string PipeName { get; set; }
        public string Start { get; set; }          // raw text; MainForm resolves it once the file is loaded
        public double? Speed { get; set; }
        public RepeatMode? Repeat { get; set; }
        public bool Paused { get; set; }
        public bool Enqueue { get; set; }
        // Start time (UTC ticks) of the process that sent a single-instance hand-off ("--launched=",
        // added by Program). Launches started together (Explorer multi-select) are queued, not replaced.
        public long LaunchedUtcTicks { get; set; }

        public static string DefaultLogPath
        {
            get { return Path.Combine(AppInfo.LocalDataFolder, "vidflow.log"); }
        }

        // True when any per-playback option was given (forwarded on single-instance hand-off).
        public bool HasPlaybackOptions
        {
            get { return Fullscreen || Start != null || Speed.HasValue || Repeat.HasValue || Paused || Enqueue; }
        }

        public static LaunchOptions Parse(IList<string> args)
        {
            var o = new LaunchOptions();
            if (args == null) return o;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i] ?? "";
                string name = a, inline = null;
                if (a.StartsWith("--"))
                {
                    int eq = a.IndexOf('=');
                    if (eq > 2) { name = a.Substring(0, eq); inline = a.Substring(eq + 1); }
                }
                Func<string> value = () => inline ?? (i + 1 < args.Count ? args[++i] : null);
                switch (name.ToLowerInvariant())
                {
                    case "--new": o.ForceNewInstance = true; break;
                    case "--fullscreen": o.Fullscreen = true; break;
                    case "--paused": o.Paused = true; break;
                    case "--enqueue": o.Enqueue = true; break;
                    case "--automation": o.Automation = true; o.ForceNewInstance = true; break;
                    case "--settings": o.SettingsPath = value(); break;
                    case "--pipe": o.PipeName = SanitizePipeName(value()); break;
                    case "--start": o.Start = value(); break;
                    case "--launched":
                    {
                        long t;
                        if (long.TryParse(value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out t) && t > 0) o.LaunchedUtcTicks = t;
                        break;
                    }
                    case "--speed":
                    {
                        double s;
                        if (double.TryParse((value() ?? "").Trim().TrimEnd('x', 'X', '×'), NumberStyles.Float, CultureInfo.InvariantCulture, out s)
                            && s >= Presets.MinSpeed && s <= Presets.MaxSpeed)
                            o.Speed = s;
                        break;
                    }
                    case "--repeat":
                    {
                        string v = (value() ?? "").Trim();
                        RepeatMode m;
                        if (Enum.TryParse(v, true, out m) && Enum.IsDefined(typeof(RepeatMode), m)) o.Repeat = m;
                        break;
                    }
                    case "--log":
                        if (inline != null) { o.LogPath = inline; break; }
                        // Only swallow the next argument when it is clearly a log file, so
                        // "VidFlow --log clip.mp4" still opens the clip.
                        string peek = i + 1 < args.Count ? args[i + 1] : null;
                        bool isLogFile = peek != null && (peek.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                                                       || peek.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
                        o.LogPath = isLogFile ? args[++i] : DefaultLogPath;
                        break;
                    default:
                        if (a.StartsWith("--")) break; // ignore unknown switches
                        o.Paths.Add(a);
                        break;
                }
            }
            return o;
        }

        // This process's start time in UTC ticks (falls back to now).
        public static long ProcessStartUtcTicks()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                return DateTime.UtcNow.Ticks;
            }
        }

        // Pipe and mutex names may not contain backslashes; keep them readable but safe.
        public static string SanitizePipeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            string s = sb.ToString();
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        // The per-playback switches as "--name=value" arguments, for the single-instance hand-off
        // ("open" verb): the running window parses them with Parse() just like a command line.
        public List<string> PlaybackSwitches()
        {
            var l = new List<string>();
            if (Fullscreen) l.Add("--fullscreen");
            if (Paused) l.Add("--paused");
            if (Enqueue) l.Add("--enqueue");
            if (Start != null) l.Add("--start=" + Start);
            if (Speed.HasValue) l.Add("--speed=" + Speed.Value.ToString("R", CultureInfo.InvariantCulture));
            if (Repeat.HasValue) l.Add("--repeat=" + Repeat.Value);
            return l;
        }
    }
}
