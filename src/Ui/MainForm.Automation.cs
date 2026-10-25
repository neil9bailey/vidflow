using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;

namespace VidFlow.Ui
{
    // Control channel (single-instance hand-off + --automation verbs) and the menu data source.
    public sealed partial class MainForm
    {
        private void StartControlChannel()
        {
            _channel = new ControlChannel(_pipeName, this, HandleControl);
            _channel.Start();
        }

        // Runs on the UI thread (ControlChannel marshals it).
        private string HandleControl(string verb, string[] args)
        {
            if (_closing) return "error: closing";
            args = args ?? new string[0];
            string v = (verb ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (v)
                {
                    case "open":
                    case "append":
                        return HandleOpenVerb(v == "append", NonBlank(args));
                    case "activate":
                        ActivateWindow();
                        return "ok";
                }
                if (!_options.Automation) return "error: automation disabled";
                switch (v)
                {
                    case "action": return AutomationActions(NonBlank(args));
                    case "key": return AutomationKeys(NonBlank(args));
                    case "mouse": return AutomationMouse(args);
                    case "dump": return Json.Write(Dump());
                    case "get":
                        if (args.Length < 1) return "error";
                        return (_mpv != null ? _mpv.GetString(args[0].Trim()) : null) ?? "error";
                    case "mpv":
                    {
                        if (args.Length == 0 || _mpv == null) return "error: no command";
                        int rc = _mpv.Command(args);
                        return rc >= 0 ? "ok" : "error " + rc.ToString(CultureInfo.InvariantCulture);
                    }
                    case "menu": return AutomationMenu(args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "");
                    case "screenshot":
                    {
                        if (args.Length < 1 || _mpv == null) return "error: no path";
                        int rc = _mpv.Command("screenshot-to-file", args[0].Trim(), "window");
                        return rc >= 0 ? "ok" : "error " + rc.ToString(CultureInfo.InvariantCulture);
                    }
                    case "resize":
                    {
                        int w, h;
                        if (args.Length < 2 || !TryInt(args[0], out w) || !TryInt(args[1], out h) || w <= 0 || h <= 0) return "error: usage resize w h";
                        if (_fullscreen) SetFullscreen(false);
                        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                        ClientSize = new Size(w, h);
                        return "ok";
                    }
                    case "quit":
                        BeginInvoke((MethodInvoker)Close);
                        return "ok";
                    default:
                        return "error: unknown verb " + verb;
                }
            }
            catch (Exception ex)
            {
                Log.Error("control verb " + verb + " failed", ex);
                return "error: " + ex.Message;
            }
        }

        // ---- single-instance hand-off & launch options ------------------------------------

        // Per-playback launch switches waiting for the file they apply to (see ApplyLaunchOptions).
        private sealed class LaunchPlayback
        {
            public string Start;      // --start text, resolved once the file is loaded
            public double? Speed;     // re-applied when the file starts (KeepSpeedBetweenFiles=false resets speed)
            public bool Paused;
            public long Entry;        // mpv playlist entry it was armed for
        }

        // Launches whose processes started within this window of each other are one multi-select.
        private const long OpenBurstTicks = 1500 * TimeSpan.TicksPerMillisecond;
        private long _lastOpenLaunchTicks;     // process start time (UTC ticks) behind the last open with files
        private LaunchPlayback _launchPending; // set around OpenPaths; PlayCurrent takes it for its loadfile
        private LaunchPlayback _launchArmed;   // bound to the entry that loadfile created

        // "open"/"append" from another VidFlow process: paths plus "--switch" arguments.
        private string HandleOpenVerb(bool append, string[] args)
        {
            var lo = LaunchOptions.Parse(args);
            if (!append) ActivateWindow();
            if (lo.Paths.Count > 0 && !EngineAlive) return "error: playback engine stopped";
            append |= lo.Enqueue;
            // Not under --automation: scripted tests send "open" back to back and expect each to replace.
            bool burstCandidate = !append && lo.Paths.Count > 0 && !_options.Automation;
            // Explorer opens a multi-selection as one process per file, all started within a few ms
            // of each other: queue the later ones instead of each replacing the list. The senders'
            // start times decide it (not arrival times), so a slow first window can't turn a
            // separate double-click seconds later into an append.
            if (burstCandidate && lo.LaunchedUtcTicks != 0 && _lastOpenLaunchTicks != 0
                && Math.Abs(lo.LaunchedUtcTicks - _lastOpenLaunchTicks) <= OpenBurstTicks)
            {
                append = true;
                // Already listed (e.g. the first file's folder was auto-loaded): nothing to add.
                lo.Paths.RemoveAll(p => _playlist.IndexOf(p) >= 0);
            }
            ApplyLaunchOptions(lo, append);
            if (burstCandidate && lo.LaunchedUtcTicks != 0) MarkOpenDone(lo.LaunchedUtcTicks);
            return "ok";
        }

        // Remembers which launch the current list came from (the next hand-off is compared to it).
        private void MarkOpenDone(long launchedUtcTicks)
        {
            _lastOpenLaunchTicks = launchedUtcTicks;
        }

        // Command-line switches, at startup and from hand-offs. --fullscreen, --repeat and --speed
        // apply at once; --paused and --start apply to the file this opens (--start once it has
        // loaded), or to the current file when no files are given.
        private void ApplyLaunchOptions(LaunchOptions o, bool append)
        {
            if (o.Fullscreen && !_fullscreen) SetFullscreen(true);
            if (o.Repeat.HasValue) SetRepeat(o.Repeat.Value);
            if (o.Speed.HasValue && EngineAlive) SetSpeed(o.Speed.Value);
            if (o.Paths.Count == 0)
            {
                if (!HasMedia) return;
                if (o.Paused) SetPause(true);
                if (o.Start != null) ApplyStartTime(o.Start);
                return;
            }
            if (o.Paused || o.Start != null || o.Speed.HasValue)
                _launchPending = new LaunchPlayback { Start = o.Start, Speed = o.Speed, Paused = o.Paused };
            try
            {
                OpenPaths(o.Paths, append);
            }
            finally
            {
                _launchPending = null; // not armed = no file started (e.g. appended to a playing list)
            }
        }

        // PlayCurrent: the launch switches for the file it is about to load (applied once).
        private LaunchPlayback TakeLaunchPlayback()
        {
            var p = _launchPending;
            _launchPending = null;
            return p;
        }

        // loadfile reply: binds --start/--speed to the playlist entry the file got.
        private void ArmLaunchPlayback(LaunchPlayback p, long entry)
        {
            if (p == null || entry <= 0 || (p.Start == null && !p.Speed.HasValue)) return;
            p.Entry = entry;
            _launchArmed = p;
        }

        // OnStartFileEntry: the armed speed for this entry, if any (older armed entries are dropped).
        private double? LaunchSpeedFor(long entry)
        {
            var a = _launchArmed;
            if (a == null) return null;
            if (a.Entry < entry) { _launchArmed = null; return null; }
            return a.Entry == entry ? a.Speed : null;
        }

        // OnFileLoaded: --start for the current entry.
        private void ApplyLaunchStart()
        {
            var a = _launchArmed;
            if (a == null || a.Entry != _currentEntry) return;
            _launchArmed = null;
            if (a.Start != null) ApplyStartTime(a.Start);
        }

        // Seeks the current file to a GoToDialog-style position. Duration and frame rate may not
        // have been reported yet right after loading: ask mpv for them asynchronously.
        private void ApplyStartTime(string text)
        {
            if (!EngineAlive) return;
            long entry = _currentEntry;
            _mpv.GetPropertyAsync("duration", d =>
            {
                if (!EngineAlive || entry != _currentEntry) return;
                _mpv.GetPropertyAsync("container-fps", f =>
                {
                    if (!EngineAlive || entry != _currentEntry) return;
                    double dur = ToDouble(d), fps = ToDouble(f);
                    if (dur <= 0) dur = _st.Duration;
                    if (!(fps > 0.5)) fps = Fps;
                    double? t = ResolveStartTime(text, dur, fps);
                    if (!t.HasValue)
                    {
                        ShowOsd("Error: invalid start position \"" + text + "\"", true);
                        return;
                    }
                    double target = dur > 0 ? Math.Min(t.Value, LatestSeekTime(dur, fps)) : t.Value;
                    ResetFrameStepQueue();
                    _mpv.CommandAsync("no-osd", "seek", Num(Math.Max(0, target)), "absolute+exact");
                });
            });
        }

        // Same rules as the Go To dialog: "83.4", "1:23.5", "-2" (from the end), "f123" / "#123"
        // (frame; "f-10" = 10 frames before the end). Null when invalid or not resolvable.
        private static double? ResolveStartTime(string text, double duration, double fps)
        {
            string s = (text ?? "").Trim();
            if (s.Length == 0) return null;
            if (s[0] == 'f' || s[0] == 'F' || s[0] == '#')
            {
                if (fps <= 0) return null;
                s = s.Substring(1).Trim();
                bool back = s.StartsWith("-");
                long n;
                if (!long.TryParse(back ? s.Substring(1).Trim() : s, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null;
                if (back)
                {
                    if (duration <= 0) return null;
                    n = Math.Max(1, (long)Math.Floor(duration * fps + 1e-6)) - n;
                }
                return Math.Max(0, n / fps);
            }
            double t;
            if (!Fmt.TryParseTime(s, out t)) return null;
            if (s.StartsWith("-"))
            {
                if (duration <= 0) return null;
                t = duration + t;
            }
            return Math.Max(0, t);
        }

        private static string[] NonBlank(string[] args)
        {
            var l = new List<string>();
            foreach (var a in args) if (!string.IsNullOrWhiteSpace(a)) l.Add(a.Trim());
            return l.ToArray();
        }

        private static bool TryInt(string s, out int v)
        {
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        private void ActivateWindow()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = _lastNonMinimizedState;
            if (!Visible) Show();
            // Another process asked us to come forward; a TopMost blip gets past the foreground lock.
            if (!TopMost)
            {
                TopMost = true;
                TopMost = false;
            }
            Activate();
        }

        private string AutomationActions(string[] ids)
        {
            if (ids.Length == 0) return "error: no action";
            foreach (var id in ids)
            {
                if (!_actions.IsKnown(id)) return "error: " + id + " unknown";
                if (!_actions.IsEnabled(id)) return "error: " + id + " disabled";
                if (!_actions.Execute(id)) return "error: " + id + " failed";
            }
            return "ok";
        }

        private string AutomationKeys(string[] names)
        {
            if (names.Length == 0) return "error: no key";
            var lines = new List<string>();
            foreach (var name in names)
            {
                Keys k;
                string id = KeyNames.TryParse(name, out k) ? HandleKey(k) : null;
                lines.Add(id != null ? "ok " + id : "unbound " + name);
            }
            return string.Join("\n", lines);
        }

        // Client coordinates of the VideoHost; drives the same handlers real input uses.
        private string AutomationMouse(string[] args)
        {
            var a = NonBlank(args);
            if (a.Length == 0) return "error: usage mouse <click|dblclick|wheel|drag|rclick> ...";
            var n = new List<int>();
            var words = new List<string>();
            for (int i = 1; i < a.Length; i++)
            {
                int v;
                if (TryInt(a[i], out v)) n.Add(v); else words.Add(a[i].ToLowerInvariant());
            }
            Func<int, Point> pt = i => new Point(n[i], n[i + 1]);
            _simulating = true;
            try
            {
                switch (a[0].ToLowerInvariant())
                {
                    case "click":
                    {
                        if (n.Count < 2) return "error: usage mouse click x y";
                        // VideoHost handles presses on the quick start card's buttons before MainForm's
                        // mouse handlers see them: a click on one presses it instead of reaching the video.
                        Point p = pt(0);
                        var card = _host.Surface.HitTestQuickStart(new Point(p.X - _host.Surface.Left, p.Y - _host.Surface.Top));
                        if (card != QuickStartButton.None)
                        {
                            OnQuickStartClicked(card);
                            return "ok " + card;
                        }
                        VideoMouseDown(MouseButtons.Left, p, 1, true);
                        VideoMouseUp(MouseButtons.Left, p, true);
                        return "ok";
                    }
                    case "dblclick":
                        if (n.Count < 2) return "error: usage mouse dblclick x y";
                        VideoMouseDown(MouseButtons.Left, pt(0), 1, true);
                        VideoMouseUp(MouseButtons.Left, pt(0), true);
                        VideoMouseDown(MouseButtons.Left, pt(0), 2, true);
                        VideoMouseUp(MouseButtons.Left, pt(0), true);
                        return "ok";
                    case "rclick":
                        if (n.Count < 2) return "error: usage mouse rclick x y";
                        VideoMouseDown(MouseButtons.Right, pt(0), 1, true);
                        VideoMouseUp(MouseButtons.Right, pt(0), true);
                        return "ok";
                    case "wheel":
                    {
                        if (n.Count < 3) return "error: usage mouse wheel x y delta [ctrl|shift|alt]";
                        Keys mods = Keys.None;
                        foreach (var w in words)
                        {
                            if (w == "ctrl" || w == "control") mods |= Keys.Control;
                            else if (w == "shift") mods |= Keys.Shift;
                            else if (w == "alt") mods |= Keys.Alt;
                        }
                        VideoWheel(pt(0), n[2], mods);
                        return "ok";
                    }
                    case "drag":
                    {
                        if (n.Count < 4) return "error: usage mouse drag x1 y1 x2 y2 [middle]";
                        var button = words.Contains("middle") ? MouseButtons.Middle : MouseButtons.Left;
                        Point p1 = pt(0), p2 = pt(2);
                        VideoMouseDown(button, p1, 1, true);
                        const int steps = 8;
                        for (int i = 1; i <= steps; i++)
                            VideoMouseMove(new Point(p1.X + (p2.X - p1.X) * i / steps, p1.Y + (p2.Y - p1.Y) * i / steps), true);
                        VideoMouseUp(button, p2, true);
                        return "ok";
                    }
                    default:
                        return "error: unknown mouse verb " + a[0];
                }
            }
            finally
            {
                _simulating = false;
            }
        }

        private Dictionary<string, object> Dump()
        {
            bool alive = _mpv != null && _mpv.IsAlive && _mpv.IsInitialized;
            bool media = HasMedia;
            var playlist = new List<object>();
            var items = _playlist.Items;
            if (items != null) foreach (var i in items) playlist.Add(i);
            Func<IntPtr, object> handle = h => h == IntPtr.Zero ? null : (object)h.ToInt64();
            bool controlsVisible = _bar.Visible && (_bar.Parent == this || OverlayVisible);

            var d = new Dictionary<string, object>();
            d["path"] = _st.Path;
            d["playlistIndex"] = _playlist.Index;
            d["playlistCount"] = _playlist.Count;
            d["playlist"] = playlist;
            d["paused"] = alive ? (object)_st.Paused : null;
            d["position"] = media ? (object)_st.Position : null;
            d["duration"] = media && _st.Duration > 0 ? (object)_st.Duration : null;
            d["frame"] = media ? (object)_st.Frame : null;
            d["frameCount"] = media ? (object)_st.FrameCount : null;
            d["fps"] = media && Fps > 0 ? (object)Fps : null;
            d["speed"] = alive ? (object)_st.Speed : null;
            d["volume"] = alive ? (object)_st.Volume : null;
            d["muted"] = alive ? (object)_st.Muted : null;
            d["repeat"] = _settings.Repeat.ToString();
            d["shuffle"] = _playlist.Shuffle;
            d["abA"] = _st.AbA;
            d["abB"] = _st.AbB;
            d["zoom"] = alive ? (object)_st.Zoom : null;
            d["panX"] = alive ? (object)_st.PanX : null;
            d["panY"] = alive ? (object)_st.PanY : null;
            d["zoomText"] = _bar.ZoomText;
            d["rotate"] = alive ? (object)_st.Rotate : null;
            d["aspect"] = alive ? _st.Aspect : null;
            d["panscan"] = alive ? (object)_st.Panscan : null;
            d["hwdec"] = alive ? _st.Hwdec : null; // observed: a synchronous read waits for a busy core
            d["hwdecCurrent"] = media ? _st.HwdecCurrent : null;
            d["fullscreen"] = _fullscreen;
            d["onTop"] = TopMost;
            d["controlsVisible"] = controlsVisible;
            d["playlistVisible"] = _playlistPanel.Visible;
            d["windowBounds"] = new[] { Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height };
            d["clientSize"] = new[] { ClientSize.Width, ClientSize.Height };
            d["videoHostSize"] = new[] { _host.ClientSize.Width, _host.ClientSize.Height };
            d["surfaceHandle"] = handle(_host.Surface.IsHandleCreated ? _host.Surface.Handle : IntPtr.Zero);
            d["surfaceHandleAtStart"] = handle(_surfaceHandleAtStart);
            d["title"] = Text;
            d["eofReached"] = alive ? (object)_st.EofReached : null;
            d["idle"] = alive ? (object)_st.Idle : null;
            d["subVisible"] = alive ? (object)_st.SubVisible : null;
            d["sid"] = media ? _st.Sid : null;
            d["aid"] = media ? _st.Aid : null;
            d["audioDelay"] = alive ? (object)_st.AudioDelay : null;
            d["subDelay"] = alive ? (object)_st.SubDelay : null;
            d["cursorHidden"] = _cursorHidden;
            d["lastOsd"] = _lastOsd;
            d["lastError"] = _lastError == null ? null : _lastError + (string.IsNullOrEmpty(_lastErrorDetail) ? "" : ": " + _lastErrorDetail);
            d["controlBar"] = new Dictionary<string, object>
            {
                { "paused", _bar.Paused },
                { "position", _bar.Position },
                { "duration", _bar.Duration },
                { "speed", _bar.Speed },
                { "repeat", _bar.Repeat.ToString() },
                { "abLoop", _bar.AbLoop.ToString() },
                { "zoomText", _bar.ZoomText },
                { "playlistText", _bar.PlaylistText },
                // What the bar renders right now: the frame readout text, the VFR badge, the cull chip.
                { "frameText", _bar.FrameText },
                { "vfrBadgeVisible", _bar.VfrBadgeVisible },
                { "cullChipVisible", _bar.CullChipVisible },
            };
            FramesDump(d);
            HwdecDump(d);
            FeatureDump(d);
            CullDump(d);
            ExtrasDump(d);
            return d;
        }

        // ---- menu dumps -------------------------------------------------------------------

        private string AutomationMenu(string which)
        {
            ContextMenuStrip strip;
            switch (which)
            {
                case "context": strip = _contextMenu; break;
                case "speed": strip = _speedMenu; break;
                case "repeat": strip = _repeatMenu; break;
                case "zoom": strip = _zoomMenu; break;
                default: return "error: usage menu context|speed|repeat|zoom";
            }
            if (strip == null) return "[]";
            // Menus refresh their items when opening; raise those events without showing anything.
            RaiseProtected(strip, "OnOpening", new CancelEventArgs());
            return Json.Write(MenuItems(strip.Items, 0));
        }

        private static List<object> MenuItems(ToolStripItemCollection items, int depth)
        {
            var list = new List<object>();
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripSeparator || !item.Available) continue;
                var mi = item as ToolStripMenuItem;
                var children = new List<object>();
                if (mi != null && depth < 4)
                {
                    RaiseProtected(mi, "OnDropDownShow", EventArgs.Empty); // raises DropDownOpening
                    if (mi.HasDropDownItems)
                    {
                        RaiseProtected(mi.DropDown, "OnOpening", new CancelEventArgs());
                        children = MenuItems(mi.DropDownItems, depth + 1);
                    }
                }
                list.Add(new Dictionary<string, object>
                {
                    { "text", StripMnemonic(item.Text) },
                    { "shortcut", mi != null ? mi.ShortcutKeyDisplayString ?? "" : "" },
                    { "checked", mi != null && mi.Checked },
                    { "enabled", item.Enabled },
                    { "children", children },
                });
            }
            return list;
        }

        private static string StripMnemonic(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
        }

        private static void RaiseProtected(object target, string method, EventArgs args)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    null, new[] { args.GetType() }, null);
                if (m == null) continue;
                try { m.Invoke(target, new object[] { args }); }
                catch (TargetInvocationException ex) { Log.Warn("menu refresh " + method + " threw: " + ex.InnerException); }
                return;
            }
        }

        // ---- IMenuDataSource --------------------------------------------------------------

        // Entry texts are plain text: MenuBuilder escapes '&' and shortens long names itself.
        private sealed class MenuData : IMenuDataSource
        {
            private readonly MainForm _f;
            public MenuData(MainForm owner) { _f = owner; }

            private static IList<MenuEntry> OrNone(List<MenuEntry> list, string none)
            {
                if (list.Count == 0) list.Add(new MenuEntry { Text = none, Enabled = false });
                return list;
            }

            public IList<MenuEntry> RecentFiles()
            {
                var list = new List<MenuEntry>();
                var recent = _f._settings.RecentFiles;
                for (int i = 0; i < recent.Count; i++)
                    list.Add(new MenuEntry
                    {
                        Text = FileNameOf(recent[i]),
                        // The path, not the position: the list reorders when a file loads while the menu is open.
                        ActionId = ActionIds.OpenRecentPath + ":" + recent[i],
                        Checked = _f.HasMedia && SamePath(recent[i], _f._st.Path),
                    });
                return OrNone(list, "No recent files");
            }

            public IList<MenuEntry> VideoTracks() { return Tracks("video", ActionIds.VideoTrack, "No video"); }
            public IList<MenuEntry> AudioTracks() { return Tracks("audio", ActionIds.AudioTrack, "No audio"); }
            public IList<MenuEntry> SubtitleTracks() { return Tracks("sub", ActionIds.SubTrack, "No subtitles"); }

            private IList<MenuEntry> Tracks(string type, string prefix, string none)
            {
                var list = new List<MenuEntry>();
                if (!_f.HasMedia) return OrNone(list, none);
                bool any = false;
                foreach (var t in _f._st.Tracks)
                {
                    if (t.Type != type) continue;
                    if (!any)
                    {
                        list.Add(new MenuEntry { Text = "Off", ActionId = prefix + ":" + _f.MenuArg("no"), Checked = _f.SelectedTrack(type) == null });
                        list.Add(MenuEntry.Separator());
                        any = true;
                    }
                    list.Add(new MenuEntry
                    {
                        Text = TrackLabel(t),
                        ActionId = prefix + ":" + _f.MenuArg(t.Id.ToString(CultureInfo.InvariantCulture)),
                        Checked = t.Selected,
                    });
                }
                return OrNone(list, none);
            }

            public IList<MenuEntry> Chapters()
            {
                var list = new List<MenuEntry>();
                if (_f.HasMedia)
                    for (int i = 0; i < _f._st.Chapters.Count; i++)
                        list.Add(new MenuEntry
                        {
                            Text = _f.ChapterLabel(i),
                            ActionId = ActionIds.Chapter + ":" + _f.MenuArg(i.ToString(CultureInfo.InvariantCulture)),
                            Checked = i == _f._st.Chapter,
                        });
                return OrNone(list, "No chapters");
            }
        }
    }
}
