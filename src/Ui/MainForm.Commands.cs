using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VidFlow.Core;
using VidFlow.Ui.Dialogs;

namespace VidFlow.Ui
{
    // Bodies of the playback / speed / loop / video / audio / subtitle actions.
    public sealed partial class MainForm
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private bool _flipH, _flipV;
        private string _hwdecOverride;   // null = follow the setting; "no" or a mode chosen by the toggle

        // Keyboard seeks show mpv's OSD bar (useful in fullscreen) unless OSD messages are off.
        private string SeekOsdPrefix
        {
            get { return _settings.OsdMessages ? "osd-bar" : "no-osd"; }
        }

        // ---- playback ---------------------------------------------------------------------

        private void TogglePause()
        {
            if (!HasMedia)
            {
                if (_playlist.Current != null) PlayCurrent(true);
                return;
            }
            if (_st.Paused && _st.EofReached)
            {
                // keep-open-pause parked us on the last frame: play again from the start.
                _mpv.CommandAsync("no-osd", "seek", "0", "absolute+exact");
                SetPause(false);
                return;
            }
            SetPause(!_st.Paused);
        }

        // Updates our copy immediately so a second toggle in the same burst (double-click undo,
        // key repeat) sees the new state before mpv's property notification arrives.
        // Asynchronous, so it never waits for a core that is busy opening a file (it runs in order
        // after any command sent before it).
        private void SetPause(bool paused)
        {
            _mpv.CommandAsync("set", "pause", YesNo(paused));
            _st.Paused = paused;
            _bar.Paused = paused;
            UpdateCursorVisibility();
        }

        private void StopAction()
        {
            SetPause(true);
            _mpv.CommandAsync("no-osd", "seek", "0", "absolute+exact");
            ShowOsd("Stopped");
        }

        // Frame steps are sent to mpv one at a time. frame-back-step seeks relative to the frame
        // currently shown, so a second step issued before the first lands targets the same frame
        // (two quick presses = one step), and a back step's seek cancels a forward step still
        // pending inside mpv. Requests made while a step is in flight are netted into a signed
        // queue (+ forward, - back) and replayed as each step completes: a back step when mpv
        // reports PlaybackRestart, a forward step when the next frame number arrives.
        // Auto-repeat from a held key is dropped while a step is in flight, so holding "," steps
        // as fast as frames decode and stops the moment the key is released.
        // A step counts as lost after StepTimeoutMs - except a back step while mpv is still seeking:
        // on long-GOP video (one keyframe in 10 s of 4K HEVC) each back step decodes the whole GOP up
        // to the target and takes 2-4 s (measured). Re-issuing it then would cancel that decode and
        // target the same frame again, so a second press was lost.
        private const int MaxQueuedSteps = 8, StepTimeoutMs = 2000, MaxBackStepMs = 30000;
        private int _queuedSteps;
        private int _stepIssuedAt;        // TickCount | 1 while a step is in flight, else 0
        private bool _stepForward;        // direction of the step in flight
        private bool _keyIsAutoRepeat;    // set by ProcessCmdKey around the action it runs

        private void FrameStep(bool forward)
        {
            _frameOsdRequestedAt = Environment.TickCount | 1;
            _st.Paused = true; // both commands pause
            _bar.Paused = true;
            int dir = forward ? 1 : -1;
            if (StepInFlight)
            {
                if (_keyIsAutoRepeat) return;
                _queuedSteps = Math.Max(-MaxQueuedSteps, Math.Min(MaxQueuedSteps, _queuedSteps + dir));
                return;
            }
            _queuedSteps = 0;
            IssueStep(dir);
        }

        private bool StepInFlight
        {
            get
            {
                if (_stepIssuedAt == 0) return false;
                int age = unchecked(Environment.TickCount - _stepIssuedAt);
                return age < StepTimeoutMs || (!_stepForward && _mpvSeeking && age < MaxBackStepMs);
            }
        }

        private void IssueStep(int dir)
        {
            // At either end mpv does nothing (no PlaybackRestart, no new frame), or a forward step
            // trips EOF handling: don't send it and don't wait for an answer that never comes.
            bool atStart = _st.Frame.HasValue && _st.Frame.Value <= 0;
            bool atEnd = _st.Frame.HasValue && _st.FrameCount.HasValue && _st.Frame.Value >= _st.FrameCount.Value - 1;
            if ((dir < 0 && atStart) || (dir > 0 && atEnd))
            {
                ResetFrameStepQueue();
                return;
            }
            _stepIssuedAt = Environment.TickCount | 1;
            _stepForward = dir > 0;
            _mpv.CommandAsync(dir > 0 ? "frame-step" : "frame-back-step");
        }

        private void OnStepCompleted()
        {
            _stepIssuedAt = 0;
            if (_queuedSteps == 0) return;
            int dir = Math.Sign(_queuedSteps);
            _queuedSteps -= dir;
            IssueStep(dir);
        }

        private void OnPlaybackRestart()
        {
            PushPosition(true); // a seek landed: show it now, not at the next throttle slot
            if (_stepIssuedAt != 0 && !_stepForward) OnStepCompleted();
        }

        // Called from the frame-number observers.
        private void OnStepFrameArrived()
        {
            if (_stepIssuedAt != 0 && _stepForward) OnStepCompleted();
        }

        private void ResetFrameStepQueue()
        {
            _stepIssuedAt = 0;
            _queuedSteps = 0;
        }

        private void SeekRelative(double seconds)
        {
            if (seconds == 0 || double.IsNaN(seconds)) return;
            ResetFrameStepQueue(); // a deliberate seek supersedes queued frame steps
            _mpv.CommandAsync(SeekOsdPrefix, "seek", Num(seconds), "relative+exact");
        }

        // On a verified constant-frame-rate file this lands on the frame shown at time t (FrameAtTime).
        private void SeekAbsolute(double t, bool osd)
        {
            if (double.IsNaN(t)) return;
            ResetFrameStepQueue();
            if (FrameExact) t = FrameSeekTime(FrameAtTime(t));
            else if (_st.Duration > 0) t = Math.Min(t, LatestSeekTime());
            _mpv.CommandAsync(osd ? SeekOsdPrefix : "no-osd", "seek", Num(Math.Max(0, t)), "absolute+exact");
        }

        // mpv's precise seek shows the first frame that starts at or after (target - 5 ms). Above
        // 100 fps that window spans more than half a frame, so a target on a frame's own start shows
        // the frame before it (verified at 240 fps). Aiming 5 ms past the middle of the previous frame
        // shows frame n for rates up to 300 fps (beyond that the window spans more than 1.5 frames);
        // at 100 fps and below the target is the frame start itself, as before.
        private const double HrSeekWindow = 0.005;

        private double FrameSeekTime(long frame)
        {
            double d = 1.0 / _frameInfo.Fps;
            return Math.Max(0, frame) * d + Math.Max(0, HrSeekWindow - d / 2);
        }

        // The frame on screen at time t of a verified constant-frame-rate file. A quarter-frame margin
        // absorbs timestamps rounded to whole milliseconds (Matroska), which can sit just before the
        // frame's exact start.
        private long FrameAtTime(double t)
        {
            long n = (long)Math.Floor(Math.Max(0, t) * _frameInfo.Fps + 0.25);
            long count = _st.FrameCount ?? 0;
            return count > 0 ? Math.Min(count - 1, n) : n;
        }

        // The latest time an absolute seek may target without tripping EOF (which would pause,
        // loop or advance): a hair past the last frame's start (so timestamp rounding cannot land on
        // the frame before it), or 50 ms before the end when the rate is unknown.
        private double LatestSeekTime()
        {
            return LatestSeekTime(_st.Duration, Fps);
        }

        private static double LatestSeekTime(double duration, double fps)
        {
            double t = fps > 0 ? duration - 1.0 / fps + Math.Min(0.001, 0.25 / fps) : duration - 0.05;
            return Math.Max(0, t);
        }

        private void SeekPercent(double percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            ResetFrameStepQueue();
            if (FrameExact && _st.Duration > 0)
            {
                SeekAbsolute(_st.Duration * percent / 100, true); // lands on a whole frame (see FrameSeekTime)
                return;
            }
            if (_st.Duration > 0 && _st.Duration * percent / 100 > LatestSeekTime())
            {
                SeekAbsolute(_st.Duration, true); // clamped short of the end
                return;
            }
            _mpv.CommandAsync(SeekOsdPrefix, "seek", Num(percent), "absolute-percent+exact");
        }

        // Last frame without tripping EOF (which would pause, loop or advance).
        private void GoToEnd()
        {
            if (_st.Duration <= 0) return;
            SetPause(true);
            ResetFrameStepQueue();
            double t = FrameExact && _st.FrameCount > 0 ? FrameSeekTime(_st.FrameCount.Value - 1) : LatestSeekTime();
            _mpv.CommandAsync(SeekOsdPrefix, "seek", Num(t), "absolute+exact");
        }

        private void GoToAction()
        {
            double? target = null;
            RunModal(() => target = GoToDialog.Show(this, _st.Position, _st.Duration, Fps));
            if (target.HasValue && HasMedia) SeekAbsolute(target.Value, true);
        }

        private void NextFile(bool forward)
        {
            bool wrap = _settings.Repeat == RepeatMode.All;
            bool moved = forward ? _playlist.MoveNext(wrap) : _playlist.MovePrevious(wrap);
            if (moved) PlayCurrent(false);
            else ShowOsd(forward ? "Last file" : "First file");
        }

        private void StepChapter(int direction)
        {
            int count = _st.Chapters.Count;
            if (count == 0) return;
            int cur = (int)Math.Max(-1, Math.Min(count - 1, _st.Chapter));
            if (direction > 0)
            {
                if (cur + 1 < count) GoToChapter(cur + 1);
                else ShowOsd("Last chapter");
                return;
            }
            // Like most players: first go back to the start of the current chapter.
            if (cur >= 0 && _st.Position - _st.Chapters[cur].Time > 2) GoToChapter(cur);
            else GoToChapter(Math.Max(0, cur - 1));
        }

        private void GoToChapter(int index)
        {
            if (index < 0 || index >= _st.Chapters.Count) throw new ArgumentException("no chapter " + index);
            _mpv.SetProperty("chapter", (long)index);
            _st.Chapter = index;
            ShowOsd(ChapterLabel(index));
        }

        private string ChapterLabel(int index)
        {
            var c = _st.Chapters[index];
            string title = string.IsNullOrWhiteSpace(c.Title) ? "" : ": " + c.Title.Trim();
            return "Chapter " + (index + 1) + title + " (" + Fmt.Time(c.Time) + ")";
        }

        // ---- speed ------------------------------------------------------------------------

        private void SpeedPreset(bool up)
        {
            double cur = _st.Speed, target = double.NaN;
            if (up)
            {
                foreach (double p in Presets.Speeds)
                    if (p > cur + 1e-6) { target = p; break; }
            }
            else
            {
                foreach (double p in Presets.Speeds)
                    if (p < cur - 1e-6) target = p;
            }
            if (double.IsNaN(target)) ShowOsd("Speed " + SpeedText(cur) + (up ? " (fastest preset)" : " (slowest preset)"));
            else SetSpeed(target);
        }

        private void SetSpeed(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed)) throw new ArgumentException("invalid speed");
            speed = Math.Max(Presets.MinSpeed, Math.Min(Presets.MaxSpeed, speed));
            _mpv.SetProperty("speed", speed);
            _st.Speed = speed;
            _bar.Speed = speed;
            ShowOsd("Speed " + SpeedText(speed));
        }

        // No OSD and asynchronous: for the file-start handler, where the core is busy.
        private void SetSpeedQuiet(double speed)
        {
            _mpv.CommandAsync("set", "speed", Num(speed));
            _st.Speed = speed;
            _bar.Speed = speed;
        }

        private static string SpeedText(double s)
        {
            bool twoDecimals = Math.Abs(s * 100 - Math.Round(s * 100)) < 1e-6;
            return s.ToString(twoDecimals ? "0.00" : "0.###", Inv) + "×";
        }

        private void TogglePitchCorrection()
        {
            _settings.PitchCorrection = !_settings.PitchCorrection;
            _mpv.SetProperty("audio-pitch-correction", _settings.PitchCorrection);
            ShowOsd("Keep audio pitch: " + OnOff(_settings.PitchCorrection));
        }

        private static string OnOff(bool b) { return b ? "on" : "off"; }

        // ---- repeat & loop ----------------------------------------------------------------

        private void SetRepeat(RepeatMode mode)
        {
            _settings.Repeat = mode;
            _mpv.SetProperty("loop-file", mode == RepeatMode.One ? "inf" : "no");
            _bar.Repeat = mode;
            ShowOsd("Repeat: " + RepeatLabel(mode));
        }

        private static string RepeatLabel(RepeatMode mode)
        {
            switch (mode)
            {
                case RepeatMode.One: return "This File";
                case RepeatMode.All: return "All";
                default: return "Off";
            }
        }

        private void CycleAbLoop()
        {
            double pos = _st.Position;
            if (!_st.AbA.HasValue)
            {
                _mpv.SetProperty("ab-loop-a", pos);
                _st.AbA = pos;
                _st.AbB = null;
                ShowOsd("Loop A: " + Fmt.Time(pos, true));
            }
            else if (!_st.AbB.HasValue)
            {
                double a = _st.AbA.Value, b = pos;
                if (Math.Abs(b - a) < 0.001)
                {
                    ShowOsd("Loop B must differ from A");
                    return;
                }
                if (b < a) { double t = a; a = b; b = t; }
                _mpv.SetProperty("ab-loop-a", a);
                _mpv.SetProperty("ab-loop-b", b);
                _st.AbA = a;
                _st.AbB = b;
                ShowOsd("A-B loop: " + Fmt.Time(a, true) + " – " + Fmt.Time(b, true));
            }
            else
            {
                ClearAbLoop();
                return;
            }
            PushAbLoop();
        }

        private void ClearAbLoop()
        {
            _mpv.SetProperty("ab-loop-a", "no");
            _mpv.SetProperty("ab-loop-b", "no");
            _st.AbA = _st.AbB = null;
            PushAbLoop();
            ShowOsd("A-B loop cleared");
        }

        private void PushAbLoop()
        {
            bool a = _st.AbA.HasValue, b = _st.AbB.HasValue;
            _bar.AbLoop = a && b ? AbLoopState.Active : a ? AbLoopState.ASet : AbLoopState.None;
            if (_seek == null) return;
            _seek.LoopA = a ? _st.AbA : null;
            _seek.LoopB = a && b ? _st.AbB : null;
        }

        private void ToggleShuffle()
        {
            _playlist.Shuffle = !_playlist.Shuffle;
            _settings.Shuffle = _playlist.Shuffle;
            _bar.Shuffle = _playlist.Shuffle;
            ShowOsd("Shuffle: " + OnOff(_playlist.Shuffle));
        }

        // ---- video ------------------------------------------------------------------------

        // "16:9" / "2.35:1" / "1.333" -> ratio; "no"/"auto"/<=0 -> 0 (use the file's aspect).
        private static double AspectRatio(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return 0;
            v = v.Trim();
            if (v.Equals("no", StringComparison.OrdinalIgnoreCase) || v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return 0;
            int colon = v.IndexOf(':');
            double a, b;
            if (colon > 0)
            {
                if (double.TryParse(v.Substring(0, colon), NumberStyles.Float, Inv, out a) &&
                    double.TryParse(v.Substring(colon + 1), NumberStyles.Float, Inv, out b) && a > 0 && b > 0)
                    return a / b;
                return -1;
            }
            return double.TryParse(v, NumberStyles.Float, Inv, out a) ? Math.Max(0, a) : -1;
        }

        private bool IsAspect(string arg)
        {
            double want = AspectRatio(arg), cur = AspectRatio(_st.Aspect);
            if (want < 0) return false;
            if (want == 0) return cur <= 0;
            return cur > 0 && Math.Abs(want - cur) < 0.01;
        }

        private void CycleAspect()
        {
            var list = Presets.Aspects;
            int idx = -1;
            for (int i = 0; i < list.Length; i++) if (IsAspect(list[i])) { idx = i; break; }
            SetAspect(list[(idx + 1) % list.Length]);
        }

        private void SetAspect(string arg)
        {
            string v = (arg ?? "").Trim();
            double ratio = AspectRatio(v);
            if (ratio < 0) throw new ArgumentException("invalid aspect '" + arg + "'");
            if (ratio == 0) v = "no";
            if (_mpv.SetProperty("video-aspect-override", v) < 0 && ratio == 0) _mpv.SetProperty("video-aspect-override", "-1");
            _st.Aspect = ratio == 0 ? "no" : ratio.ToString("0.######", Inv);
            ShowOsd("Aspect ratio: " + Presets.AspectLabel(v));
        }

        private void ToggleFill()
        {
            double v = _st.Panscan > 0.001 ? 0 : 1;
            _mpv.SetProperty("panscan", v);
            _st.Panscan = v;
            UpdateZoomText();
            ShowOsd("Crop to fill: " + OnOff(v > 0));
        }

        private static int NormalizeRotation(int degrees)
        {
            int r = (int)Math.Round(degrees / 90.0) * 90 % 360;
            return r < 0 ? r + 360 : r;
        }

        private void SetRotate(int degrees)
        {
            if (degrees == int.MinValue) throw new ArgumentException("invalid rotation");
            int r = NormalizeRotation(degrees);
            _mpv.SetProperty("video-rotate", (long)r);
            _st.Rotate = r;
            ShowOsd("Rotation: " + r + "°");
        }

        private void SetFlip(bool h, bool v)
        {
            bool oldH = _flipH, oldV = _flipV;
            _flipH = h;
            _flipV = v;
            // Software filters need copy-back decoding: switch before adding, restore after removing.
            if (h || v) ApplyHwdec();
            if (h != oldH && !ApplyFilter("@vidflow-hflip", "hflip", h)) _flipH = oldH;
            if (v != oldV && !ApplyFilter("@vidflow-vflip", "vflip", v)) _flipV = oldV;
            ApplyHwdec();
            if (_flipH != oldH) ShowOsd("Flip horizontally: " + OnOff(_flipH));
            else if (_flipV != oldV) ShowOsd("Flip vertically: " + OnOff(_flipV));
        }

        private bool ApplyFilter(string label, string filter, bool on)
        {
            int rc = on ? _mpv.Command("vf", "add", label + ":" + filter) : _mpv.Command("vf", "remove", label);
            if (rc < 0) ShowOsd("Error: could not " + (on ? "apply " : "remove ") + filter, true);
            return rc >= 0;
        }

        private string EffectiveHwdec()
        {
            string setting = (_settings.HwDec ?? "").Trim();
            if (setting.Length == 0) setting = "auto-safe";
            string mode = _hwdecOverride ?? setting;
            if (mode == "no") return "no";
            return _flipH || _flipV ? "auto-copy-safe" : mode;
        }

        private void ApplyHwdec()
        {
            if (_mpv == null || !_mpv.IsInitialized) return;
            string want = EffectiveHwdec();
            if (_st.Hwdec != want && _mpv.SetProperty("hwdec", want) >= 0) _st.Hwdec = want;
        }

        private void ToggleHwDec()
        {
            string setting = (_settings.HwDec ?? "").Trim();
            if (EffectiveHwdec() != "no") _hwdecOverride = "no";
            else _hwdecOverride = setting.Length > 0 && setting != "no" ? null : "auto-safe";
            ApplyHwdec();
            ShowOsd("Hardware decoding: " + OnOff(EffectiveHwdec() != "no"));
        }

        private void ToggleDeinterlace()
        {
            string v = _st.Deinterlace == "yes" ? "no" : "yes";
            _mpv.SetProperty("deinterlace", v);
            _st.Deinterlace = v;
            ShowOsd("Deinterlace: " + OnOff(v == "yes"));
        }

        private void AdjustPicture(string property, string label, int direction)
        {
            double cur = _mpv.GetDouble(property) ?? 0;
            long v = (long)Math.Max(-100, Math.Min(100, Math.Round(cur) + direction * Presets.PictureAdjustStep));
            _mpv.SetProperty(property, v);
            ShowOsd(label + " " + (v > 0 ? "+" : "") + v);
        }

        private void ResetPicture()
        {
            foreach (var p in new[] { "brightness", "contrast", "saturation", "gamma", "hue" }) _mpv.SetProperty(p, 0L);
            ShowOsd("Picture adjustments reset");
        }

        // ---- tracks -----------------------------------------------------------------------

        private int CountTracks(string type)
        {
            int n = 0;
            foreach (var t in _st.Tracks) if (t.Type == type) n++;
            return n;
        }

        private TrackInfo SelectedTrack(string type)
        {
            foreach (var t in _st.Tracks) if (t.Type == type && t.Selected) return t;
            return null;
        }

        private bool IsTrackSelected(string type, string arg)
        {
            var sel = SelectedTrack(type);
            if (string.Equals(arg, "no", StringComparison.OrdinalIgnoreCase)) return sel == null;
            return sel != null && ParseInt(arg) == sel.Id;
        }

        // property: vid / aid / sid; arg: track id or "no".
        private void SelectTrack(string property, string arg)
        {
            string v = (arg ?? "").Trim().ToLowerInvariant();
            if (v != "no" && v != "auto" && ParseInt(v) == int.MinValue) throw new ArgumentException("invalid track '" + arg + "'");
            if (_mpv.SetProperty(property, v) < 0) throw new InvalidOperationException("track " + arg + " is not available");
            string type = property == "vid" ? "video" : property == "aid" ? "audio" : "sub";
            string kind = property == "vid" ? "Video" : property == "aid" ? "Audio" : "Subtitles";
            TrackInfo t = null;
            foreach (var x in _st.Tracks) if (x.Type == type && x.Id == ParseInt(v)) t = x;
            ShowOsd(kind + ": " + (t != null ? TrackLabel(t) : v == "no" ? "off" : v));
        }

        private void CycleAudioTrack()
        {
            var ids = new List<long>();
            foreach (var t in _st.Tracks) if (t.Type == "audio") ids.Add(t.Id);
            if (ids.Count == 0) return;
            var sel = SelectedTrack("audio");
            int idx = sel == null ? -1 : ids.IndexOf(sel.Id);
            SelectTrack("aid", ids[(idx + 1) % ids.Count].ToString(Inv));
        }

        private void CycleSubtitle(int direction)
        {
            var options = new List<string> { "no" };
            foreach (var t in _st.Tracks) if (t.Type == "sub") options.Add(t.Id.ToString(Inv));
            var sel = SelectedTrack("sub");
            int idx = sel == null ? 0 : Math.Max(0, options.IndexOf(sel.Id.ToString(Inv)));
            SelectTrack("sid", options[((idx + direction) % options.Count + options.Count) % options.Count]);
        }

        // "1: English 440Hz [eng] (aac)"
        private static string TrackLabel(TrackInfo t)
        {
            var sb = new StringBuilder();
            sb.Append(t.Id.ToString(Inv)).Append(": ");
            if (!string.IsNullOrWhiteSpace(t.Title)) sb.Append(t.Title.Trim());
            else sb.Append(t.Type == "sub" ? "Subtitle " : t.Type == "audio" ? "Audio " : "Video ").Append(t.Id.ToString(Inv));
            if (!string.IsNullOrWhiteSpace(t.Lang)) sb.Append(" [").Append(t.Lang.Trim()).Append(']');
            if (!string.IsNullOrWhiteSpace(t.Codec)) sb.Append(" (").Append(t.Codec.Trim()).Append(')');
            if (t.External) sb.Append(" · external");
            return sb.ToString();
        }

        // ---- audio & subtitles ------------------------------------------------------------

        private void SetVolume(double volume, bool osd)
        {
            if (double.IsNaN(volume)) return;
            volume = Math.Max(0, Math.Min(Presets.VolumeMax, volume));
            _mpv.SetProperty("volume", volume);
            _st.Volume = volume;
            _bar.Volume = volume;
            if (osd) ShowOsd("Volume " + volume.ToString("0", Inv) + "%" + (_st.Muted ? " (muted)" : ""));
        }

        private void ToggleMute()
        {
            bool m = !_st.Muted;
            _mpv.SetProperty("mute", m);
            _st.Muted = m;
            _bar.Muted = m;
            ShowOsd(m ? "Muted" : "Unmuted");
        }

        // delta null = reset to 0.
        private void AdjustDelay(string property, string label, double? delta)
        {
            double cur = _mpv.GetDouble(property) ?? 0;
            double v = delta.HasValue ? Math.Round(cur + delta.Value, 3) : 0;
            _mpv.SetProperty(property, v);
            int ms = (int)Math.Round(v * 1000);
            ShowOsd(label + ": " + (ms > 0 ? "+" : "") + ms.ToString(Inv) + " ms");
        }

        private void ToggleSubtitles()
        {
            _settings.SubtitlesVisible = !_settings.SubtitlesVisible;
            _mpv.SetProperty("sub-visibility", _settings.SubtitlesVisible);
            _st.SubVisible = _settings.SubtitlesVisible;
            ShowOsd("Subtitles: " + (_settings.SubtitlesVisible ? "shown" : "hidden"));
        }

        private void SetSubScale(double scale)
        {
            scale = Math.Round(SubScaleClamp(scale), 2);
            _settings.SubScale = scale;
            _mpv.SetProperty("sub-scale", scale);
            ShowOsd("Subtitle size: " + scale.ToString("0.00", Inv));
        }

        // ---- view toggles -----------------------------------------------------------------

        private void ToggleOnTop()
        {
            TopMost = !TopMost;
            _settings.AlwaysOnTop = TopMost;
            ShowOsd("Always on top: " + OnOff(TopMost));
        }

        private void ToggleStats()
        {
            _mpv.CommandAsync("script-binding", "stats/display-stats-toggle");
            _statsVisible = !_statsVisible;
        }

        private void ShowInfoOsd()
        {
            var sb = new StringBuilder();
            sb.Append(Fmt.Time(_st.Position, true)).Append(" / ").Append(Fmt.Time(_st.Duration, true));
            if (_st.Frame.HasValue)
                sb.Append("   Frame ").Append(FrameText(_st.Frame.Value))
                  .Append(_st.FrameCount.HasValue ? " / " + _st.FrameCount.Value.ToString(Inv) : "")
                  .Append(FrameRateVariable ? " (VFR)" : "");
            sb.Append("   ").Append(SpeedText(_st.Speed));
            if (_playlist.Count > 1) sb.Append("   [").Append(_playlist.Index + 1).Append('/').Append(_playlist.Count).Append(']');
            string text = sb.ToString();
            _lastOsd = CurrentDisplayName() + "\n" + text;
            // ${filename} is expanded by mpv; everything else is literal.
            _mpv.CommandAsync("show-text", "${filename}\n" + text.Replace("$", "$$"), "2500");
        }

        private void ToggleOsdMessages()
        {
            _settings.OsdMessages = !_settings.OsdMessages;
            ShowOsd("On-screen messages: " + OnOff(_settings.OsdMessages), true);
        }
    }
}
