using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;
using VidFlow.Ui.Dialogs;

namespace VidFlow.Ui
{
    // Implementations of the extension hooks declared in MainForm.Hooks.cs: persistent time/frame
    // OSD, A-B loop editing, review markers, audio device/normalize/downmix, borderless window,
    // aspect-locked resizing, reverse playback, playlist save/sort and "Open With" registration.
    // The taskbar button (progress + thumbnail toolbar) lives in MainForm.Taskbar.cs.
    public sealed partial class MainForm
    {
        // mpv expands this itself on every OSD redraw (osd-level 3). {0} = "F", or "≈F" while the
        // frame numbers are estimates (FrameApproximate: variable frame rate, or a container the
        // frame probe cannot read).
        private const string OsdStatusTemplate =
            "${{time-pos/full}} / ${{duration/full}}   {0} ${{estimated-frame-number}} / ${{estimated-frame-count}}   ${{speed}}×";
        private const string NormalizeFilter = "@vidflow-norm:lavfi=[dynaudnorm=f=250:g=31]";
        private const double ReverseWarnSeconds = 300; // demuxer-max-back-bytes covers short clips only

        private bool _reverse;                 // mpv play-direction=backward
        private bool _reverseTuned;            // demuxer/decoder set up for backward play (until the next file)
        private bool _reverseHwdecSwitched;    // ... including hardware decoding turned off
        private int _turnGeneration;           // invalidates a pending first turn (see SetPlayDirection)
        private int _stepsAfterTurn;           // frame steps waiting for the switch back to forward (+ next, - previous)
        private int _turnIssuedAt;             // TickCount of that switch
        private const int TurnTimeoutMs = 2000;
        private bool _normalizeApplied;        // what mpv's af chain holds (the setting may differ until applied)
        private bool _borderlessShown;         // the saved borderless style was applied on first show
        private string _audioDevice;           // observed audio-device
        private List<KeyValuePair<string, string>> _audioDevices = new List<KeyValuePair<string, string>>(); // name, description

        // Review markers per file (session only), sorted ascending.
        private readonly Dictionary<string, List<double>> _markers = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        private string _markersShownFor;       // path whose markers the seek bar shows (reference compare)
        // Frame rate of each file with markers, captured while it played (exports need it later).
        private readonly Dictionary<string, MarkerRate> _markerRates = new Dictionary<string, MarkerRate>(StringComparer.OrdinalIgnoreCase);
        private bool _osdStatusApprox;         // FrameApproximate as last sent in osd-status-msg

        // ---- hooks ------------------------------------------------------------------------

        partial void FeatureMpvOptions(Action<string, string> option)
        {
            _osdStatusApprox = FrameApproximate;
            option("osd-status-msg", OsdStatusFormat);
            if (_settings.OsdStatus == OsdStatusMode.Always) option("osd-level", "3");
            string device = (_settings.AudioDevice ?? "").Trim();
            if (device.Length > 0 && !device.Equals("auto", StringComparison.OrdinalIgnoreCase)) option("audio-device", device);
            option("audio-channels", MpvAudioChannels(_settings.AudioChannels) ?? "auto-safe");
            if (_settings.AudioNormalize) option("af", NormalizeFilter);
            _normalizeApplied = _settings.AudioNormalize;
        }

        partial void FeatureInit()
        {
            _mpv.ObserveNode("audio-device-list", OnAudioDeviceList);
            _mpv.ObserveString("audio-device", v => _audioDevice = v);
            _mpv.ObserveString("play-direction", v => _reverse = v == "backward" || v == "-");
            _mpv.ObserveDouble("duration", v => TaskbarUpdate(true)); // progress needs a duration
            _mpv.PlaybackRestart += OnPlaybackRestartAfterTurn;
            ApplyOsdStatus();
            TaskbarInit();
        }

        partial void FeatureRegisterActions()
        {
            // Startup values the playlist model does not read from Settings itself.
            PlaylistSort sort;
            if (TryParseSort(_settings.PlaylistSortBy, out sort)) _playlist.Sort = sort;
            _playlist.Descending = _settings.PlaylistSortDescending;
            _playlist.IncludeSubfolders = _settings.IncludeSubfolders;

            Func<bool> media = () => HasMedia;
            Func<bool> hasMarkers = () => HasMedia && CurrentMarkers().Count > 0;
            Action<string, Action, Func<bool>, Func<bool>> r = (id, run, chk, en) => _actions.Register(id, run, chk, en);

            // ---- view ----
            r(ActionIds.ToggleOsdStatus, CycleOsdStatus, () => _settings.OsdStatus != OsdStatusMode.Off, null);
            r(ActionIds.ToggleBorderless, ToggleBorderless, () => _settings.Borderless, null);
            r(ActionIds.ToggleKeepAspect, ToggleKeepAspect, () => _settings.KeepAspectOnResize, null);

            // ---- A-B loop ----
            r(ActionIds.AbSetA, () => SetAbPoint(false, _st.Position), () => _st.AbA.HasValue, media);
            r(ActionIds.AbSetB, () => SetAbPoint(true, _st.Position), () => _st.AbB.HasValue, media);
            _actions.RegisterParam(ActionIds.AbNudge, NudgeAbPoint, null, arg =>
            {
                bool isB;
                int frames;
                return HasMedia && TryParseNudge(arg, out isB, out frames) && (isB ? _st.AbB.HasValue : _st.AbA.HasValue);
            });

            // ---- markers ----
            r(ActionIds.AddMarker, ToggleMarkerHere, () => HasMedia && MarkerIndexNear(_st.Position) >= 0, media);
            r(ActionIds.NextMarker, () => StepMarker(1), null, hasMarkers);
            r(ActionIds.PrevMarker, () => StepMarker(-1), null, hasMarkers);
            r(ActionIds.ClearMarkers, ClearMarkers, null, hasMarkers);
            r(ActionIds.CopyMarkers, CopyMarkers, null, () => TotalMarkerCount() > 0);
            _actions.RegisterParam(ActionIds.GoToMarker, arg => GoToMarker(ParseInt(arg)),
                arg =>
                {
                    var list = CurrentMarkers();
                    int i = ParseInt(arg);
                    return HasMedia && i >= 0 && i < list.Count && Math.Abs(list[i] - _st.Position) <= HalfFrame;
                },
                arg => { int i = ParseInt(arg); return HasMedia && i >= 0 && i < CurrentMarkers().Count; });

            // ---- playback ----
            r(ActionIds.ToggleReverse, ToggleReverse, () => _reverse, media);
            // mpv steps in the play direction; Next/Previous Frame stay absolute (see StepFrameAbsolute).
            r(ActionIds.FrameStep, () => StepFrameAbsolute(true), null, media);
            r(ActionIds.FrameBackStep, () => StepFrameAbsolute(false), null, media);

            // ---- audio ----
            _actions.RegisterParam(ActionIds.AudioDevice, SetAudioDevice, IsAudioDevice, arg => !string.IsNullOrWhiteSpace(arg));
            r(ActionIds.ToggleAudioNormalize, ToggleAudioNormalize, () => _settings.AudioNormalize, null);
            _actions.RegisterParam(ActionIds.SetAudioChannels, SetAudioChannels,
                arg => string.Equals(NormalizeChannels(_settings.AudioChannels), NormalizeChannels(arg), StringComparison.Ordinal),
                arg => MpvAudioChannels(arg) != null);

            // ---- playlist ----
            r(ActionIds.SavePlaylist, SavePlaylistAction, null, () => _playlist.Count > 0);
            _actions.RegisterParam(ActionIds.SortPlaylist, SortPlaylistAction,
                arg => { PlaylistSort s; return TryParseSort(arg, out s) && s == _playlist.Sort; },
                arg => { PlaylistSort s; return TryParseSort(arg, out s); });
            r(ActionIds.TogglePlaylistSortOrder, TogglePlaylistSortOrder, () => _playlist.Descending, null);

            // ---- file types (the Store edition's file types are declared in its package manifest) ----
            r(ActionIds.RegisterFileTypes, RegisterFileTypesAction, null, () => !AppInfo.IsPackaged);
            r(ActionIds.UnregisterFileTypes, UnregisterFileTypesAction, null, () => !AppInfo.IsPackaged && FileAssociations.IsRegistered(Application.ExecutablePath));
        }

        partial void FeatureMenus()
        {
            if (_menus != null)
            {
                _menus.AudioDevicesProvider = AudioDeviceEntries;
                _menus.MarkersProvider = MarkerEntries;
            }
            if (_seek != null) _seek.LoopPointDragged += OnLoopPointDragged;
            _playlistPanel.SortChanged += OnPanelSortChanged;
            _playlistPanel.SaveRequested += () => Run(ActionIds.SavePlaylist);
        }

        partial void FeatureFileStarted()
        {
            // Backward play is per file, like zoom: the next clip starts forward.
            _stepsAfterTurn = 0;
            if (_reverse || _reverseTuned) SetPlayDirection(false, true);
            if (_seek != null) _seek.Markers = null;
            _markersShownFor = null;
            TaskbarUpdate(true);
        }

        partial void FeatureFileLoaded()
        {
            PushMarkers();
            TaskbarUpdate(true);
        }

        partial void FeatureEof(ref bool handled)
        {
            if (!_reverse) return;
            // Playing backward, reaching the first frame is the end of the clip.
            handled = true;
            switch (_settings.Repeat)
            {
                case RepeatMode.One:
                    RestartBackward();
                    break;
                case RepeatMode.All:
                    if (_playlist.Count <= 1) RestartBackward();
                    else if (_playlist.MoveNext(true)) PlayCurrent(true);
                    break;
                default:
                    if (_settings.AutoAdvance && _playlist.MoveNext(false)) PlayCurrent(true);
                    else SetPause(true);
                    break;
            }
        }

        partial void FeaturePosition(double position)
        {
            // The path observer and the file events race; follow whichever file is current.
            if (!ReferenceEquals(_markersShownFor, _st.Path)) PushMarkers();
            TaskbarUpdate(false);
        }

        partial void FeaturePauseChanged(bool paused)
        {
            TaskbarUpdate(true);
        }

        partial void FeatureFullscreenChanged()
        {
            ApplyOsdStatus();
            if (!_fullscreen) ApplyBorderless(true); // a toggle made while fullscreen takes effect now
        }

        partial void FeatureWndProc(ref Message m, ref bool handled)
        {
            switch (m.Msg)
            {
                case FeatureNative.WM_SHOWWINDOW:
                    if (m.WParam != IntPtr.Zero && !_borderlessShown)
                    {
                        _borderlessShown = true;
                        ApplyBorderless(false); // saved bounds already describe the borderless window
                    }
                    break;
                case FeatureNative.WM_NCHITTEST:
                    if (BorderlessResizeActive)
                    {
                        base.WndProc(ref m);
                        m.Result = (IntPtr)BorderlessHitTest(m.LParam, (int)m.Result.ToInt64());
                        handled = true;
                    }
                    break;
                case FeatureNative.WM_SIZING:
                    if (KeepAspectSizing(m.WParam, m.LParam))
                    {
                        m.Result = (IntPtr)1;
                        handled = true;
                    }
                    break;
                default:
                    if (TaskbarWndProc(ref m)) handled = true;
                    break;
            }
        }

        partial void FeatureApplySettings()
        {
            ApplyOsdStatus();
            UpdateQuickStart(); // its key caps follow the (possibly rebound) shortcuts
            ApplyBorderless(true);
            PlaylistSort sort;
            if (TryParseSort(_settings.PlaylistSortBy, out sort) && sort != _playlist.Sort) _playlist.Sort = sort;
            if (_playlist.Descending != _settings.PlaylistSortDescending) _playlist.Descending = _settings.PlaylistSortDescending;
            _playlist.IncludeSubfolders = _settings.IncludeSubfolders;
            ApplyAudioSettings();
            TaskbarUpdate(true);
        }

        partial void FeatureDump(Dictionary<string, object> d)
        {
            var markers = new List<object>();
            if (HasMedia) foreach (double t in CurrentMarkers()) markers.Add(t);
            d["osdStatus"] = _settings.OsdStatus.ToString();
            d["borderless"] = _settings.Borderless;
            d["captionVisible"] = IsHandleCreated && (FeatureNative.GetWindowLong(Handle, FeatureNative.GWL_STYLE) & FeatureNative.WS_CAPTION) == FeatureNative.WS_CAPTION;
            d["keepAspect"] = _settings.KeepAspectOnResize;
            d["reverse"] = _reverse;
            d["markers"] = markers;
            var marked = new List<object>();
            foreach (var kv in _markers) if (kv.Value.Count > 0) marked.Add(kv.Key);
            d["markedFiles"] = marked;   // every file with markers this session (they follow renames and moves)
            d["audioNormalize"] = _settings.AudioNormalize;
            d["audioChannels"] = NormalizeChannels(_settings.AudioChannels);
            d["audioDevice"] = _audioDevice ?? _settings.AudioDevice;
            d["taskbarProgress"] = _settings.TaskbarProgress;
            d["taskbarState"] = TaskbarStateName;
            d["playlistSort"] = _playlist.Sort.ToString();
            d["playlistDescending"] = _playlist.Descending;
        }

        // Settings are already saved here; every feature setting is written when it changes.
        partial void FeatureClosing()
        {
            TaskbarDispose();
        }

        // The frame code's verdict changed (probe answered, new file, idle): the persistent OSD
        // marks estimated frame numbers, and markers keep the rate their frame numbers use.
        partial void FeatureFrameTimingChanged()
        {
            if (FrameApproximate != _osdStatusApprox) ApplyOsdStatusMessage();
            RememberMarkerRate();
        }

        // VidFlow renamed or moved a file (rename, cull bins, Apply) or deleted it (to == null):
        // its markers follow it, or go with it.
        partial void FeatureFileMoved(string from, string to)
        {
            if (string.IsNullOrEmpty(from)) return;
            List<double> times;
            MarkerRate rate;
            bool hadMarkers = _markers.TryGetValue(from, out times);
            bool hadRate = _markerRates.TryGetValue(from, out rate);
            _markers.Remove(from);
            _markerRates.Remove(from);
            if (!string.IsNullOrEmpty(to) && !SamePath(from, to))
            {
                if (hadMarkers)
                {
                    List<double> existing;
                    if (_markers.TryGetValue(to, out existing))
                    {
                        // A file already marked under the new name (a move back): keep both sets.
                        foreach (double t in times) if (!existing.Exists(x => Math.Abs(x - t) < 0.0005)) existing.Add(t);
                        existing.Sort();
                    }
                    else _markers[to] = times;
                }
                if (hadRate && !_markerRates.ContainsKey(to)) _markerRates[to] = rate;
            }
            else if (!string.IsNullOrEmpty(to))
            {
                // Same path (only the case changed): put the entries back under the new spelling.
                if (hadMarkers) _markers[to] = times;
                if (hadRate) _markerRates[to] = rate;
            }
            if (!hadMarkers) return;
            _markersShownFor = null;
            PushMarkers();
        }

        // ---- helpers ----------------------------------------------------------------------

        private double FrameSeconds
        {
            get { return 1.0 / (Fps > 0 ? Fps : 30); }
        }

        private double HalfFrame
        {
            get { return FrameSeconds / 2; }
        }

        // "F 37" for the frame shown at time t of the playing file (the frame code's FrameNumberAt:
        // exact on a verified constant-frame-rate file), "≈F 37" when that number is an estimate,
        // null when the frame rate is unknown.
        private string FrameTag(double t)
        {
            bool approximate;
            long n = FrameNumberAt(t, out approximate);
            if (n < 0) return null;
            return (approximate ? "≈F " : "F ") + n.ToString(Inv);
        }

        // "0:01.233 (F 37)"
        private string TimeAndFrame(double t)
        {
            string tag = FrameTag(t);
            return Fmt.Time(t, true) + (tag != null ? " (" + tag + ")" : "");
        }

        // ---- persistent time / frame display ----------------------------------------------

        private void CycleOsdStatus()
        {
            var next = (OsdStatusMode)(((int)_settings.OsdStatus + 1) % 3);
            _settings.OsdStatus = next;
            ApplyOsdStatus();
            string text = next == OsdStatusMode.Off ? "off" : next == OsdStatusMode.FullscreenOnly ? "in fullscreen" : "always";
            ShowOsd("Time & frame display: " + text, true);
        }

        private string OsdStatusFormat
        {
            get { return string.Format(CultureInfo.InvariantCulture, OsdStatusTemplate, _osdStatusApprox ? "≈F" : "F"); }
        }

        // Asynchronous: called from the frame code's verdict (inside observer callbacks), where a
        // synchronous set would wait for the core.
        private void ApplyOsdStatusMessage()
        {
            _osdStatusApprox = FrameApproximate;
            if (_mpv == null || !_mpv.IsAlive || !_mpv.IsInitialized) return;
            _mpv.CommandAsync("set", "osd-status-msg", OsdStatusFormat);
        }

        private bool OsdStatusActive
        {
            get
            {
                var mode = _settings.OsdStatus;
                return mode == OsdStatusMode.Always || (mode == OsdStatusMode.FullscreenOnly && _fullscreen);
            }
        }

        private void ApplyOsdStatus()
        {
            if (_mpv == null || !_mpv.IsAlive || !_mpv.IsInitialized) return;
            if (OsdStatusActive)
            {
                _osdStatusApprox = FrameApproximate;
                _mpv.CommandAsync("set", "osd-status-msg", OsdStatusFormat);
                _mpv.CommandAsync("set", "osd-level", "3");
            }
            else _mpv.CommandAsync("set", "osd-level", "1");
        }

        // ---- A-B loop editing -------------------------------------------------------------

        // Sets one end; inverted ends are swapped, B without A loops from the start.
        private void SetAbPoint(bool isB, double t)
        {
            if (!HasMedia) return;
            t = ClampToDuration(t);
            double? a = _st.AbA, b = _st.AbB;
            if (isB)
            {
                b = t;
                if (!a.HasValue) a = 0;
            }
            else a = t;
            if (a.HasValue && b.HasValue)
            {
                if (Math.Abs(b.Value - a.Value) < 0.001)
                {
                    ShowOsd(isB ? "Loop B must differ from A" : "Loop A must differ from B");
                    return;
                }
                if (b.Value < a.Value) { double s = a.Value; a = b; b = s; }
            }
            ApplyAbLoop(a, b);
            if (a.HasValue && b.HasValue) ShowOsd("A-B loop: " + TimeAndFrame(a.Value) + " – " + TimeAndFrame(b.Value));
            else ShowOsd("Loop A " + TimeAndFrame(t));
        }

        private void ApplyAbLoop(double? a, double? b)
        {
            _mpv.CommandAsync("set", "ab-loop-a", a.HasValue ? Num(a.Value) : "no");
            _mpv.CommandAsync("set", "ab-loop-b", b.HasValue ? Num(b.Value) : "no");
            _st.AbA = a;
            _st.AbB = b;
            PushAbLoop();
        }

        private static bool TryParseNudge(string arg, out bool isB, out int frames)
        {
            isB = false;
            frames = 0;
            string s = (arg ?? "").Trim();
            if (s.Length < 2) return false;
            char end = char.ToUpperInvariant(s[0]);
            if (end != 'A' && end != 'B') return false;
            isB = end == 'B';
            return int.TryParse(s.Substring(1), NumberStyles.AllowLeadingSign, Inv, out frames) && frames != 0 && Math.Abs(frames) <= 1000;
        }

        // AbNudge:A-1 / A+1 / B-1 / B+1: one end moves by whole frames, never past the other end.
        private void NudgeAbPoint(string arg)
        {
            bool isB;
            int frames;
            if (!TryParseNudge(arg, out isB, out frames)) throw new ArgumentException("invalid loop nudge '" + arg + "'");
            double? a = _st.AbA, b = _st.AbB;
            double? cur = isB ? b : a;
            if (!cur.HasValue) { ShowOsd(isB ? "Loop B is not set" : "Loop A is not set"); return; }
            double step = FrameSeconds;
            double t = ClampToDuration(cur.Value + frames * step);
            if (isB && a.HasValue) t = Math.Max(t, a.Value + step);
            if (!isB && b.HasValue) t = Math.Min(t, b.Value - step);
            t = ClampToDuration(t);
            if (isB) b = t; else a = t;
            ApplyAbLoop(a, b);
            // Paused, show the frame the end now sits on.
            if (_st.Paused) SeekAbsolute(t, false);
            ShowOsd((isB ? "Loop B " : "Loop A ") + TimeAndFrame(t));
        }

        // A loop marker dragged on the seek bar; ends never cross while dragging.
        private void OnLoopPointDragged(bool isB, double t, bool final)
        {
            if (!HasMedia) return;
            double? a = _st.AbA, b = _st.AbB;
            double step = FrameSeconds;
            t = ClampToDuration(t);
            if (isB && a.HasValue) t = Math.Max(t, a.Value + step);
            if (!isB && b.HasValue) t = Math.Min(t, b.Value - step);
            t = ClampToDuration(t);
            if (isB) b = t; else a = t;
            ApplyAbLoop(a, b);
            if (final)
            {
                SeekAbsolute(t, false);
                ShowOsd((isB ? "Loop B " : "Loop A ") + TimeAndFrame(t));
            }
        }

        private double ClampToDuration(double t)
        {
            if (double.IsNaN(t) || double.IsInfinity(t)) t = 0;
            t = Math.Max(0, t);
            return _st.Duration > 0 ? Math.Min(t, _st.Duration) : t;
        }

        // ---- markers ----------------------------------------------------------------------

        private static readonly List<double> NoMarkers = new List<double>();

        private List<double> CurrentMarkers()
        {
            List<double> list;
            string path = _st.Path;
            return path != null && _markers.TryGetValue(path, out list) ? list : NoMarkers;
        }

        private int TotalMarkerCount()
        {
            int n = 0;
            foreach (var kv in _markers) n += kv.Value.Count;
            return n;
        }

        private int MarkerIndexNear(double t)
        {
            var list = CurrentMarkers();
            double half = HalfFrame;
            for (int i = 0; i < list.Count; i++)
                if (Math.Abs(list[i] - t) <= half) return i;
            return -1;
        }

        // B: adds a marker at the shown frame, or removes the one already there.
        private void ToggleMarkerHere()
        {
            string path = _st.Path;
            if (path == null) return;
            double t = ClampToDuration(_st.Position);
            List<double> list;
            if (!_markers.TryGetValue(path, out list)) _markers[path] = list = new List<double>();
            int near = MarkerIndexNear(t);
            if (near >= 0)
            {
                list.RemoveAt(near);
                if (list.Count == 0) _markers.Remove(path);
                ShowOsd("Marker removed (" + list.Count + " left)");
            }
            else
            {
                int at = 0;
                while (at < list.Count && list[at] < t) at++;
                list.Insert(at, t);
                RememberMarkerRate();
                ShowOsd("Marker " + (at + 1) + " / " + list.Count + ": " + TimeAndFrame(t));
            }
            PushMarkers();
        }

        private void StepMarker(int direction)
        {
            var list = CurrentMarkers();
            if (list.Count == 0) return;
            double pos = _st.Position, half = HalfFrame;
            int target = -1;
            if (direction > 0)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] > pos + half) { target = i; break; }
            }
            else
            {
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] < pos - half) { target = i; break; }
            }
            if (target < 0) { ShowOsd(direction > 0 ? "No later marker" : "No earlier marker"); return; }
            GoToMarker(target);
        }

        private void GoToMarker(int index)
        {
            var list = CurrentMarkers();
            if (index < 0 || index >= list.Count) throw new ArgumentException("no marker " + index);
            double t = list[index];
            SeekAbsolute(t, false);
            ShowOsd("Marker " + (index + 1) + " / " + list.Count + ": " + TimeAndFrame(t));
        }

        private void ClearMarkers()
        {
            if (_st.Path == null) return;
            int n = CurrentMarkers().Count;
            _markers.Remove(_st.Path);
            PushMarkers();
            ShowOsd(n == 1 ? "1 marker cleared" : n + " markers cleared");
        }

        // One line per marker for review notes: "<file name>\t<m:ss.fff>\tF<n>", playlist order first.
        private void CopyMarkers()
        {
            var paths = new List<string>();
            var items = _playlist.Items;
            if (items != null)
                foreach (var p in items)
                    if (_markers.ContainsKey(p) && !paths.Exists(x => SamePath(x, p))) paths.Add(p);
            foreach (var kv in _markers)
                if (!paths.Exists(x => SamePath(x, kv.Key))) paths.Add(kv.Key);

            var sb = new StringBuilder();
            int count = 0;
            foreach (var p in paths)
            {
                bool current = SamePath(p, _st.Path);
                foreach (double t in _markers[p])
                {
                    sb.Append(FileNameOf(p)).Append('\t').Append(Fmt.Time(t, true));
                    // The frame rate is only known for the playing file.
                    string tag = current ? FrameTag(t) : null;
                    if (tag != null) sb.Append('\t').Append(tag.Replace(" ", ""));
                    sb.Append("\r\n");
                    count++;
                }
            }
            if (count == 0) return;
            string text = sb.ToString();
            if (SetClipboard(() => Clipboard.SetDataObject(text, true, 5, 100)))
                ShowOsd("Copied " + count + (count == 1 ? " marker" : " markers") +
                        (paths.Count > 1 ? " from " + paths.Count + " files" : ""));
        }

        private void PushMarkers()
        {
            _markersShownFor = _st.Path;
            if (_seek == null) return;
            var list = CurrentMarkers();
            _seek.Markers = list.Count == 0 ? null : new List<double>(list);
        }

        // ---- marker data for the exports (MainForm.Extras.cs) ------------------------------

        private struct MarkerRate
        {
            public double Fps;
            public bool Approximate;   // frame numbers are estimates (FrameNumberAt's verdict)
            public bool Exact;         // verified constant frame rate: frame n starts at n / Fps
        }

        // The frame rate is only known for the playing file: keep it with that file's markers.
        // Not while the probe is still running (its verdict calls this again via FeatureFrameTimingChanged).
        private void RememberMarkerRate()
        {
            // The frame verdict belongs to the entry that started (_currentFile); mpv's path can still
            // name the previous file for a moment after the next one started.
            string path = _st.Path;
            if (path == null || !SamePath(path, _currentFile ?? path)) return;
            if (Fps <= 0 || _frameProbePending || !_markers.ContainsKey(path)) return;
            bool approximate;
            FrameNumberAt(0, out approximate);
            _markerRates[path] = new MarkerRate { Fps = Fps, Approximate = approximate, Exact = FrameExact };
        }

        // The export writers number frames as floor(t * fps). On a verified constant-frame-rate
        // file the player's own rule (FrameAtTime) absorbs millisecond-rounded timestamps, so the
        // writers get each marker at the start of the frame the player shows for it: both agree.
        private static List<double> ExportTimes(IList<double> times, MarkerRate rate)
        {
            var list = new List<double>(times);
            if (!rate.Exact || !(rate.Fps > 0)) return list;
            for (int i = 0; i < list.Count; i++)
                list[i] = Math.Floor(Math.Max(0, list[i]) * rate.Fps + 0.25) / rate.Fps;
            return list;
        }

        // Every file with markers this session (playlist order first, then the others), each with
        // a copy of its marker times and the frame rate it played at (0 when never known).
        private List<MarkedClip> MarkedClips()
        {
            RememberMarkerRate();
            var paths = new List<string>();
            var items = _playlist.Items;
            if (items != null)
                foreach (var p in items)
                    if (_markers.ContainsKey(p) && !paths.Exists(x => SamePath(x, p))) paths.Add(p);
            foreach (var kv in _markers)
                if (!paths.Exists(x => SamePath(x, kv.Key))) paths.Add(kv.Key);
            var clips = new List<MarkedClip>();
            foreach (var p in paths)
            {
                List<double> times;
                if (!_markers.TryGetValue(p, out times) || times.Count == 0) continue;
                MarkerRate rate;
                _markerRates.TryGetValue(p, out rate);
                clips.Add(new MarkedClip(p, ExportTimes(times, rate), rate.Fps, rate.Approximate));
            }
            return clips;
        }

        // The playing file's markers for the EDL export (null without markers or frame rate).
        private MarkedClip CurrentMarkedClip()
        {
            RememberMarkerRate();
            string path = _st.Path;
            var list = CurrentMarkers();
            if (path == null || list.Count == 0 || Fps <= 0) return null;
            bool approximate;
            FrameNumberAt(0, out approximate);
            var rate = new MarkerRate { Fps = Fps, Approximate = approximate, Exact = FrameExact };
            return new MarkedClip(path, ExportTimes(list, rate), Fps, approximate);
        }

        private IList<MenuEntry> MarkerEntries()
        {
            var entries = new List<MenuEntry>();
            if (HasMedia)
            {
                var list = CurrentMarkers();
                double half = HalfFrame;
                for (int i = 0; i < list.Count; i++)
                {
                    string tag = FrameTag(list[i]);
                    string frame = tag != null ? "  (" + tag + ")" : "";
                    entries.Add(new MenuEntry
                    {
                        Text = (i + 1).ToString(Inv) + ":  " + Fmt.Time(list[i], true) + frame,
                        ActionId = ActionIds.GoToMarker + ":" + i.ToString(Inv),
                        Checked = Math.Abs(list[i] - _st.Position) <= half,
                    });
                }
            }
            if (entries.Count == 0) entries.Add(new MenuEntry { Text = "No markers", Enabled = false });
            return entries;
        }

        // ---- reverse playback -------------------------------------------------------------

        // Two mpv quirks make backward play stall (both verified with the test clips):
        //  * backward decoding queues a whole GOP; d3d11va's fixed surface pool runs out
        //    ("Static surface pool size exceeded"). Copy-back decoding avoids that but, switched
        //    on at a file's end, its decoder often produced no frame at all; software decoding
        //    started every time, and short review clips decode in software easily.
        //  * a backward seek served from the demuxer cache finds nothing past the last keyframe
        //    ("seeking stream 0 (video) to nothing"), so the last GOP - where "play backward from
        //    the end" and Repeat One's wrap land - never starts. Seeking through the file works.
        // Both stay until the next file starts: switching the decoder back together with the
        // direction change lost the position, and a new file has no decoder or cache yet.
        // The steps are spaced out: the cache option reaches the demuxer only after the player
        // loop has run, the decoder switch does its own seek, and a seek that follows the direction
        // change too closely merges with the turn's own seek and fails ("Seek failed"). Sent
        // back-to-back (or chained only on mpv's replies), backward play from the end stayed stuck
        // in most runs; with these pauses it started in every run (verified).
        // afterTurn runs once mpv has applied the direction (and, backward, after a short settle).
        private const int SetupSettleMs = 300, TurnSettleMs = 150;

        private void SetPlayDirection(bool backward, bool restore = false, Action afterTurn = null)
        {
            _reverse = backward;
            int gen = ++_turnGeneration;
            // A newer turn (toggled back, next file) supersedes a pending one.
            Func<bool> current = () => gen == _turnGeneration && !_closing && _mpv != null && _mpv.IsAlive;
            Action<int, object> applied = afterTurn == null ? null : (Action<int, object>)((rc, v) =>
            {
                if (!current()) return;
                if (backward) After(TurnSettleMs, () => { if (current()) afterTurn(); });
                else afterTurn();
            });
            Action turn = () => _mpv.CommandAsync(applied, "set", "play-direction", backward ? "backward" : "forward");
            if (backward && !_reverseTuned)
            {
                _reverseTuned = true;
                _reverseHwdecSwitched = EffectiveHwdec() != "no";
                Action<int, object> setupDone = (rc, v) => { if (current()) After(SetupSettleMs, () => { if (current()) turn(); }); };
                if (_reverseHwdecSwitched)
                {
                    _mpv.CommandAsync("set", "demuxer-seekable-cache", "no");
                    _mpv.CommandAsync(setupDone, "set", "hwdec", "no");
                }
                else _mpv.CommandAsync(setupDone, "set", "demuxer-seekable-cache", "no");
                return;
            }
            turn();
            if (!backward && restore && _reverseTuned)
            {
                _reverseTuned = false;
                _mpv.CommandAsync("set", "demuxer-seekable-cache", "auto");
                if (_reverseHwdecSwitched) _mpv.CommandAsync("set", "hwdec", EffectiveHwdec());
                _reverseHwdecSwitched = false;
            }
        }

        private void ToggleReverse()
        {
            _stepsAfterTurn = 0;
            bool backward = !_reverse;
            SetPlayDirection(backward, false, () =>
            {
                if (backward)
                {
                    // An explicit seek after the turn: the turn's own internal seek does not restart
                    // a file that sits at EOF after the decoder switch (verified). At the start, play from the end.
                    bool atStart = _st.Position <= HalfFrame && _st.Duration > 0;
                    double start = BackwardStart(atStart ? _st.Duration : _st.Position);
                    _mpv.CommandAsync("no-osd", "seek", Num(start), "absolute+exact");
                    // Safety net for a slower machine: a backward start that has not moved after
                    // 3.5 s (a healthy one from the end takes ~2 s; a stuck one never moves) is
                    // restarted by one more seek a little earlier, which recovered every stuck start.
                    int gen = _turnGeneration;
                    After(3500, () =>
                    {
                        if (gen != _turnGeneration || !_reverse || _st.Paused || !HasMedia) return;
                        if (Math.Abs(_st.Position - start) > HalfFrame) return;
                        Log.Info("backward start stalled at " + Num(start) + "; seeking again");
                        _mpv.CommandAsync("no-osd", "seek", Num(BackwardStart(start - 2 * FrameSeconds)), "absolute+exact");
                    });
                }
                UnpauseQueued();
            });
            string text = _reverse ? "Playing backward" : "Playing forward";
            if (_reverse && _st.Duration > ReverseWarnSeconds) text += " (long files may stutter)";
            ShowOsd(text);
        }

        // While playing backward mpv's frame-step goes backward too (and frame-back-step does
        // nothing), so a step first turns playback forward. The turn is an internal seek that
        // swallows a step sent before it lands: the step is replayed on its PlaybackRestart.
        private void StepFrameAbsolute(bool forward)
        {
            // A turn whose PlaybackRestart never came (nothing to seek) must not hold steps forever.
            if (_stepsAfterTurn != 0 && !_reverse && unchecked(Environment.TickCount - _turnIssuedAt) > TurnTimeoutMs)
                _stepsAfterTurn = 0;
            if (!_reverse && _stepsAfterTurn == 0)
            {
                FrameStep(forward);
                return;
            }
            if (_reverse)
            {
                _turnIssuedAt = Environment.TickCount;
                SetPause(true);
                SetPlayDirection(false);
            }
            _stepsAfterTurn = Math.Max(-8, Math.Min(8, _stepsAfterTurn + (forward ? 1 : -1)));
        }

        private void OnPlaybackRestartAfterTurn()
        {
            if (_stepsAfterTurn == 0 || _reverse) return;
            int n = _stepsAfterTurn;
            _stepsAfterTurn = 0;
            for (int i = 0; i < Math.Abs(n); i++) FrameStep(n > 0);
        }

        private void RestartBackward()
        {
            _mpv.CommandAsync("no-osd", "seek", Num(BackwardStart(_st.Duration)), "absolute+exact");
            UnpauseQueued();
        }

        // Runs action once on the UI thread after ms (unless the window is closing by then).
        private void After(int ms, Action action)
        {
            var timer = new Timer { Interval = Math.Max(1, ms) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!_closing && !IsDisposed) action();
            };
            timer.Start();
        }

        // Like SetPause(false), but queued behind the direction change and seek just sent: a
        // synchronous set overtakes them, and unpausing at EOF before the turn left backward
        // playback stuck on the last frame (verified).
        private void UnpauseQueued()
        {
            _mpv.CommandAsync("set", "pause", "no");
            _st.Paused = false;
            _bar.Paused = false;
            UpdateCursorVisibility();
        }

        // A backward start inside the last frame rather than at the exact end, which mpv treats as EOF.
        private double BackwardStart(double t)
        {
            return _st.Duration > 0 ? Math.Max(0, Math.Min(t, _st.Duration - HalfFrame)) : Math.Max(0, t);
        }

        // ---- audio ------------------------------------------------------------------------

        private void OnAudioDeviceList(object node)
        {
            var list = new List<KeyValuePair<string, string>>();
            var arr = node as List<object>;
            if (arr != null)
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    string name = Get(d, "name") as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add(new KeyValuePair<string, string>(name, Get(d, "description") as string ?? ""));
                }
            _audioDevices = list;
        }

        private string CurrentAudioDevice
        {
            get
            {
                string d = !string.IsNullOrEmpty(_audioDevice) ? _audioDevice : _settings.AudioDevice;
                return string.IsNullOrWhiteSpace(d) ? "auto" : d.Trim();
            }
        }

        private bool IsAudioDevice(string name)
        {
            return string.Equals(CurrentAudioDevice, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void SetAudioDevice(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("no audio device");
            _settings.AudioDevice = name;
            _audioDevice = name;
            _mpv.CommandAsync("set", "audio-device", name);
            ShowOsd("Audio device: " + AudioDeviceLabel(name, null));
        }

        private string AudioDeviceLabel(string name, string description)
        {
            if (string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase)) return "System Default";
            if (string.IsNullOrWhiteSpace(description))
                foreach (var kv in _audioDevices)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { description = kv.Value; break; }
            return string.IsNullOrWhiteSpace(description) ? name : description.Trim();
        }

        private IList<MenuEntry> AudioDeviceEntries()
        {
            var entries = new List<MenuEntry>();
            bool hasAuto = false;
            foreach (var kv in _audioDevices)
            {
                bool auto = string.Equals(kv.Key, "auto", StringComparison.OrdinalIgnoreCase);
                hasAuto |= auto;
                entries.Add(new MenuEntry
                {
                    Text = AudioDeviceLabel(kv.Key, kv.Value),
                    ActionId = ActionIds.AudioDevice + ":" + kv.Key,
                    Checked = IsAudioDevice(kv.Key),
                });
                if (auto && _audioDevices.Count > 1) entries.Add(MenuEntry.Separator());
            }
            if (!hasAuto)
                entries.Insert(0, new MenuEntry { Text = "System Default", ActionId = ActionIds.AudioDevice + ":auto", Checked = IsAudioDevice("auto") });
            return entries;
        }

        private void ToggleAudioNormalize()
        {
            bool on = !_normalizeApplied;
            if (_mpv.Command("af", "toggle", NormalizeFilter) < 0)
            {
                ShowOsd("Error: could not " + (on ? "enable" : "disable") + " volume normalization", true);
                return;
            }
            _normalizeApplied = on;
            _settings.AudioNormalize = on;
            ShowOsd("Normalize volume: " + OnOff(on));
        }

        // "auto" / "stereo" / "mono" (anything else unknown -> "auto").
        private static string NormalizeChannels(string v)
        {
            string s = (v ?? "").Trim().ToLowerInvariant();
            return s == "stereo" || s == "mono" ? s : "auto";
        }

        private static string MpvAudioChannels(string arg)
        {
            switch ((arg ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "auto": return "auto-safe";
                case "stereo": return "stereo";
                case "mono": return "mono";
                default: return null;
            }
        }

        private void SetAudioChannels(string arg)
        {
            string mpvValue = MpvAudioChannels(arg);
            if (mpvValue == null) throw new ArgumentException("unknown channel layout '" + arg + "'");
            string v = NormalizeChannels(arg);
            _settings.AudioChannels = v;
            _mpv.CommandAsync("set", "audio-channels", mpvValue);
            ShowOsd(v == "auto" ? "Audio channels: original" : v == "stereo" ? "Audio: downmix to stereo" : "Audio: downmix to mono");
        }

        // The Settings dialog may have changed any of the audio values.
        private void ApplyAudioSettings()
        {
            if (_mpv == null || !_mpv.IsAlive || !_mpv.IsInitialized) return;
            if (!IsAudioDevice(_settings.AudioDevice))
            {
                string name = string.IsNullOrWhiteSpace(_settings.AudioDevice) ? "auto" : _settings.AudioDevice.Trim();
                _audioDevice = name;
                _mpv.CommandAsync("set", "audio-device", name);
            }
            _mpv.CommandAsync("set", "audio-channels", MpvAudioChannels(_settings.AudioChannels) ?? "auto-safe");
            if (_settings.AudioNormalize != _normalizeApplied && _mpv.Command("af", "toggle", NormalizeFilter) >= 0)
                _normalizeApplied = _settings.AudioNormalize;
        }

        // ---- borderless window ------------------------------------------------------------

        private void ToggleBorderless()
        {
            _settings.Borderless = !_settings.Borderless;
            ApplyBorderless(true);
            ShowOsd(_settings.Borderless ? "Title bar hidden" : "Title bar shown");
        }

        // Pure Win32 style change (like fullscreen): no handle is recreated, so mpv's wid stays
        // valid. keepClient keeps the video where it is on screen and resizes the frame around it.
        private void ApplyBorderless(bool keepClient)
        {
            if (!IsHandleCreated || _fullscreen || _closing) return;
            IntPtr h = Handle;
            int style = FeatureNative.GetWindowLong(h, FeatureNative.GWL_STYLE);
            int want = _settings.Borderless ? style & ~FeatureNative.WS_CAPTION : style | FeatureNative.WS_CAPTION;
            want |= FeatureNative.WS_THICKFRAME;
            if (want == style) return;
            bool normal = WindowState == FormWindowState.Normal && Visible;
            Rectangle clientBefore = RectangleToScreen(ClientRectangle);
            FeatureNative.SetWindowLong(h, FeatureNative.GWL_STYLE, want);
            FeatureNative.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, FeatureNative.SWP_NOMOVE | FeatureNative.SWP_NOSIZE |
                FeatureNative.SWP_NOZORDER | FeatureNative.SWP_NOOWNERZORDER | FeatureNative.SWP_NOACTIVATE | FeatureNative.SWP_FRAMECHANGED);
            if (keepClient && normal)
            {
                Rectangle clientNow = RectangleToScreen(ClientRectangle);
                Rectangle b = Bounds;
                var nb = Rectangle.FromLTRB(b.Left + clientBefore.Left - clientNow.Left, b.Top + clientBefore.Top - clientNow.Top,
                                            b.Right + clientBefore.Right - clientNow.Right, b.Bottom + clientBefore.Bottom - clientNow.Bottom);
                // A returning title bar must stay reachable.
                Rectangle wa = Screen.FromHandle(h).WorkingArea;
                if (!_settings.Borderless && nb.Top < wa.Top) nb.Offset(0, wa.Top - nb.Top);
                if (nb.Width > 0 && nb.Height > 0 && nb != b)
                    FeatureNative.SetWindowPos(h, IntPtr.Zero, nb.X, nb.Y, nb.Width, nb.Height,
                        FeatureNative.SWP_NOZORDER | FeatureNative.SWP_NOOWNERZORDER | FeatureNative.SWP_NOACTIVATE);
            }
            ApplyDpiLayout(); // MinimumSize includes the frame
            if (_surfaceHandleAtStart != IntPtr.Zero && _host.Surface.Handle != _surfaceHandleAtStart)
                Log.Error("video surface handle changed after a title-bar toggle");
        }

        private bool BorderlessResizeActive
        {
            get { return _settings.Borderless && !_fullscreen && WindowState == FormWindowState.Normal && IsHandleCreated; }
        }

        // Without a caption the top edge has no sizing frame; give every edge a thin grab band.
        private int BorderlessHitTest(IntPtr lParam, int defaultResult)
        {
            if (defaultResult != FeatureNative.HTCLIENT && defaultResult != FeatureNative.HTNOWHERE) return defaultResult;
            long lp = lParam.ToInt64();
            var p = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            FeatureNative.RECT wr;
            if (!FeatureNative.GetWindowRect(Handle, out wr)) return defaultResult;
            int band = Math.Max(4, Px(6));
            bool left = p.X < wr.Left + band, right = p.X >= wr.Right - band;
            bool top = p.Y < wr.Top + band, bottom = p.Y >= wr.Bottom - band;
            if (top && left) return FeatureNative.HTTOPLEFT;
            if (top && right) return FeatureNative.HTTOPRIGHT;
            if (bottom && left) return FeatureNative.HTBOTTOMLEFT;
            if (bottom && right) return FeatureNative.HTBOTTOMRIGHT;
            if (top) return FeatureNative.HTTOP;
            if (bottom) return FeatureNative.HTBOTTOM;
            if (left) return FeatureNative.HTLEFT;
            if (right) return FeatureNative.HTRIGHT;
            return defaultResult;
        }

        // ---- keep aspect while resizing ---------------------------------------------------

        private void ToggleKeepAspect()
        {
            _settings.KeepAspectOnResize = !_settings.KeepAspectOnResize;
            if (_settings.KeepAspectOnResize) SnapWindowToAspect();
            ShowOsd("Keep aspect ratio when resizing: " + OnOff(_settings.KeepAspectOnResize));
        }

        private bool KeepAspectActive
        {
            get
            {
                return _settings.KeepAspectOnResize && HasVideo && !_fullscreen && WindowState == FormWindowState.Normal && IsHandleCreated;
            }
        }

        // Turning the option on: fit the height to the current width.
        private void SnapWindowToAspect()
        {
            if (!KeepAspectActive) return;
            Size v = VideoPixelSize();
            Size extra = NonVideoClientSize() + (Size - ClientSize);
            int w = Width - extra.Width;
            if (w <= 0) return;
            int h = (int)Math.Round(w * (double)v.Height / v.Width) + extra.Height;
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            if (h > wa.Height) return; // would not fit: leave it to the next manual resize
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - h));
            Bounds = new Rectangle(Left, y, Width, Math.Max(MinimumSize.Height, h));
        }

        // WM_SIZING: snap the dragged edge(s) so the video area keeps the video's aspect.
        private bool KeepAspectSizing(IntPtr wParam, IntPtr lParam)
        {
            if (!KeepAspectActive || lParam == IntPtr.Zero) return false;
            Size v = VideoPixelSize();
            if (v.Width <= 0 || v.Height <= 0) return false;
            double aspect = v.Width / (double)v.Height;
            var r = (FeatureNative.RECT)Marshal.PtrToStructure(lParam, typeof(FeatureNative.RECT));
            Size extra = NonVideoClientSize() + (Size - ClientSize);
            int vw = r.Right - r.Left - extra.Width, vh = r.Bottom - r.Top - extra.Height;
            if (vw <= 0 || vh <= 0) return false;

            int edge = (int)wParam.ToInt64();
            bool horizontal = edge == FeatureNative.WMSZ_LEFT || edge == FeatureNative.WMSZ_RIGHT;
            bool vertical = edge == FeatureNative.WMSZ_TOP || edge == FeatureNative.WMSZ_BOTTOM;
            if (horizontal) vh = (int)Math.Round(vw / aspect);
            else if (vertical) vw = (int)Math.Round(vh * aspect);
            else if (vw / (double)vh > aspect) vh = (int)Math.Round(vw / aspect); // corner: the larger side leads
            else vw = (int)Math.Round(vh * aspect);

            // Never below the minimum window size: grow both sides together instead.
            int minVw = MinimumSize.Width - extra.Width, minVh = MinimumSize.Height - extra.Height;
            if (vw < minVw) { vw = minVw; vh = (int)Math.Round(vw / aspect); }
            if (vh < minVh) { vh = minVh; vw = (int)Math.Round(vh * aspect); }
            int w = vw + extra.Width, h = vh + extra.Height;

            bool moveLeft = edge == FeatureNative.WMSZ_LEFT || edge == FeatureNative.WMSZ_TOPLEFT || edge == FeatureNative.WMSZ_BOTTOMLEFT;
            bool moveTop = edge == FeatureNative.WMSZ_TOP || edge == FeatureNative.WMSZ_TOPLEFT || edge == FeatureNative.WMSZ_TOPRIGHT;
            if (moveLeft) r.Left = r.Right - w; else r.Right = r.Left + w;
            if (moveTop) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
            Marshal.StructureToPtr(r, lParam, false);
            return true;
        }

        // ---- playlist save & sort ---------------------------------------------------------

        private static bool TryParseSort(string s, out PlaylistSort sort)
        {
            sort = PlaylistSort.Name;
            if (string.IsNullOrWhiteSpace(s)) return false;
            try
            {
                sort = (PlaylistSort)Enum.Parse(typeof(PlaylistSort), s.Trim(), true);
                return Enum.IsDefined(typeof(PlaylistSort), sort);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string SortLabel(PlaylistSort s)
        {
            switch (s)
            {
                case PlaylistSort.DateModified: return "date modified";
                case PlaylistSort.Size: return "size";
                case PlaylistSort.Type: return "type";
                default: return "name";
            }
        }

        private void SortPlaylistAction(string arg)
        {
            PlaylistSort s;
            if (!TryParseSort(arg, out s)) throw new ArgumentException("unknown sort '" + arg + "'");
            _playlist.Sort = s;
            _settings.PlaylistSortBy = s.ToString();
            ShowOsd("Playlist sorted by " + SortLabel(s) + (_playlist.Descending ? " (descending)" : ""));
        }

        private void TogglePlaylistSortOrder()
        {
            _playlist.Descending = !_playlist.Descending;
            _settings.PlaylistSortDescending = _playlist.Descending;
            ShowOsd("Playlist sorted by " + SortLabel(_playlist.Sort) + (_playlist.Descending ? " (descending)" : " (ascending)"));
        }

        // The panel already changed the playlist; remember the choice.
        private void OnPanelSortChanged()
        {
            _settings.PlaylistSortBy = _playlist.Sort.ToString();
            _settings.PlaylistSortDescending = _playlist.Descending;
        }

        private void SavePlaylistAction()
        {
            if (_playlist.Count == 0) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Save Playlist As";
                dlg.Filter = "M3U8 playlist (*.m3u8)|*.m3u8|M3U playlist (*.m3u)|*.m3u";
                dlg.DefaultExt = "m3u8";
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                dlg.RestoreDirectory = true;
                string folder = _settings.LastFolder;
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    dlg.InitialDirectory = folder;
                    string name = FileNameOf(folder);
                    dlg.FileName = (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Playlist" : name) + ".m3u8";
                }
                else dlg.FileName = "Playlist.m3u8";
                DialogResult result = DialogResult.Cancel;
                RunModal(() => result = dlg.ShowDialog(this));
                if (result != DialogResult.OK || string.IsNullOrEmpty(dlg.FileName)) return;
                try
                {
                    Playlist.WritePlaylistFile(dlg.FileName, _playlist.Items);
                    int n = _playlist.Count;
                    ShowOsd("Playlist saved: " + FileNameOf(dlg.FileName) + " (" + n + (n == 1 ? " item)" : " items)"));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                           ex is NotSupportedException || ex is SecurityException)
                {
                    ShowOsd("Error: could not save the playlist (" + ex.Message + ")", true);
                }
            }
        }

        // ---- "Open With" registration -----------------------------------------------------

        private void RegisterFileTypesAction()
        {
            bool yes = false;
            bool? noCheckbox = null;
            string msg = "Add VidFlow to the “Open with” menu for video and audio files?\n\n" +
                         "This writes per-user entries only (HKEY_CURRENT_USER) and does not change your default player. " +
                         "Explorer will also offer “Add to VidFlow playlist”.\n\nVidFlow location:\n" + Application.ExecutablePath;
            RunModal(() => yes = ConfirmDialog.Show(this, "Open With", msg, "Add", "Cancel", ref noCheckbox));
            if (!yes) return;
            int written;
            try
            {
                written = FileAssociations.Register(Application.ExecutablePath, FileAssociations.DefaultExtensions());
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException || ex is IOException)
            {
                ShowOsd("Error: could not register file types (" + ex.Message + ")", true);
                return;
            }
            FileAssociations.NotifyShell();
            Log.Info("file types registered: " + written + " extensions");
            ShowOsd("VidFlow added to “Open with” (" + written + " file types)", true);

            bool open = false;
            noCheckbox = null;
            RunModal(() => open = ConfirmDialog.Show(this, "Open With",
                "VidFlow is now listed under “Open with”.\n\nTo make it the default player, choose VidFlow in Windows Settings › Default apps. Open it now?",
                "Open Settings", "Not Now", ref noCheckbox));
            if (!open) return;
            try { Process.Start("ms-settings:defaultapps"); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                ShowOsd("Error: could not open Settings (" + ex.Message + ")", true);
            }
        }

        private void UnregisterFileTypesAction()
        {
            int removed;
            try
            {
                removed = FileAssociations.Unregister(Application.ExecutablePath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException || ex is IOException)
            {
                ShowOsd("Error: could not unregister file types (" + ex.Message + ")", true);
                return;
            }
            FileAssociations.NotifyShell();
            Log.Info("file types unregistered: " + removed + " entries");
            ShowOsd(removed > 0 ? "VidFlow removed from “Open with”" : "VidFlow was not registered", true);
        }

        // ---- Win32 ------------------------------------------------------------------------

        private static class FeatureNative
        {
            public const int GWL_STYLE = -16;
            public const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
            public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;
            public const int WM_SHOWWINDOW = 0x0018, WM_NCHITTEST = 0x0084, WM_SIZING = 0x0214;
            public const int HTNOWHERE = 0, HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
            public const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5,
                WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left, Top, Right, Bottom; }

            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
            public static extern int GetWindowLong(IntPtr hWnd, int index);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
            public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        }
    }
}
