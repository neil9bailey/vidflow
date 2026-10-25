using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;

namespace VidFlow.Ui
{
    // mpv initialisation, observed state and the per-file lifecycle.
    public sealed partial class MainForm
    {
        private sealed class TrackInfo
        {
            public long Id;
            public string Type, Title, Lang, Codec;
            public bool Selected, External, AlbumArt;
        }

        private sealed class ChapterInfo
        {
            public string Title;
            public double Time;
        }

        // Last values reported by mpv (UI-thread only). Unknown numbers stay at their defaults.
        private sealed class MpvState
        {
            public bool Paused = true, Muted, EofReached, Idle = true, SubVisible = true;
            public double Position, Duration, Speed = 1, Volume = 100;
            public double Zoom, PanX, PanY, Panscan, AudioDelay, SubDelay, CacheEnd;
            public double? AbA, AbB, ContainerFps, VfFps;
            public long? Frame, FrameCount;
            public long Chapter = -1;
            public int VideoW, VideoH, ParamsRotate, Rotate;   // VideoW/H: display size before rotation (dw/dh)
            public string Path, MediaTitle, LoopFile, Hwdec, HwdecCurrent, Aspect = "no", Sid, Aid, Deinterlace;
            public List<TrackInfo> Tracks = new List<TrackInfo>();
            public List<ChapterInfo> Chapters = new List<ChapterInfo>();
        }

        private readonly MpvState _st = new MpvState();
        private IntPtr _surfaceHandleAtStart;
        // mpv playlist entries we loaded (loadfile's playlist_entry_id -> item) that have not ended
        // yet. Pairs START_FILE/END_FILE with our items without asking mpv which file is current.
        private readonly Dictionary<long, string> _liveEntries = new Dictionary<long, string>();
        private long _currentEntry, _lastStartedEntry; // entry of the current playback session (StartFile..EndFile), 0 = none
        private string _currentFile;          // its item
        private int _loadGeneration;          // bumped on every StartFile; menu entries built earlier carry an older one
        private bool _fileLoaded, _eofHandled, _autoFitPending, _posKnown, _engineStopped;
        private int _lastFitW, _lastFitH;
        private string _lastError, _lastOsd;
        private int _frameOsdRequestedAt;     // TickCount of the last frame step (0 = none pending)
        private string _mpvVersion = "", _ffmpegVersion = "";

        // time-pos arrives once per video frame; the bar and seek bar repaint on every push, so
        // pushes are limited to ~30 Hz while playing (always immediate when paused, stepping or jumping).
        private const int PositionPushIntervalMs = 33;
        private int _positionPushedAt;
        private bool _positionPushPending;
        private double _pushedPosition = double.NaN;

        private bool HasMedia
        {
            get { return _mpv != null && _mpv.IsAlive && _mpv.IsInitialized && !_st.Idle && _st.Path != null; }
        }

        private bool EngineAlive
        {
            get { return _mpv != null && _mpv.IsAlive && _mpv.IsInitialized && !_engineStopped; }
        }

        private bool HasVideo
        {
            get { return HasMedia && _st.VideoW > 0 && _st.VideoH > 0; }
        }

        // A file is opening whose video size is not known yet (and that may well have video).
        private bool VideoPending
        {
            get
            {
                if (!EngineAlive || HasVideo) return false;
                if (FileStartPending) return true;
                // Until this file's track list arrives (it can trail FILE_LOADED) assume video may come.
                return _currentEntry != 0 && _autoFitPending && (!_fileLoaded || !_tracksSeen || HasAnyVideoTrack());
            }
        }

        // A loadfile was sent whose file has not started yet (no reply, or no START_FILE yet).
        private bool FileStartPending
        {
            get
            {
                if (_pendingLoads.Count > 0) return true;
                foreach (var id in _liveEntries.Keys) if (id > _lastStartedEntry) return true;
                return false;
            }
        }

        // The exact rate of a verified constant-frame-rate file (e.g. 30000/1001), else what mpv reports.
        private double Fps
        {
            get
            {
                if (FrameExact) return _frameInfo.Fps;
                if (_st.ContainerFps.HasValue && _st.ContainerFps.Value > 0.5) return _st.ContainerFps.Value;
                if (_st.VfFps.HasValue && _st.VfFps.Value > 0.5) return _st.VfFps.Value;
                return 0;
            }
        }

        private static string Num(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        // ---- init -------------------------------------------------------------------------

        // Returns false (and closes the window) when the engine cannot start: nothing works without it.
        private bool InitPlayer()
        {
            _surfaceHandleAtStart = _host.Surface.Handle;
            try
            {
                _mpv = new MpvPlayer(this, "main");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                Log.Error("mpv_create failed", ex);
                FailEngine(ex.Message);
                return false;
            }

            string dir = _settings.Directory ?? Settings.DefaultDirectory;
            var opts = new List<KeyValuePair<string, string>>();
            Action<string, string> o = (k, v) => opts.Add(new KeyValuePair<string, string>(k, v));
            // Options the window and the file lifecycle depend on. Applied first and again after
            // the user's ExtraMpvOptions, so an extra line cannot break them.
            var required = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("wid", _surfaceHandleAtStart.ToInt64().ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("keep-open", "yes"),
                new KeyValuePair<string, string>("keep-open-pause", "yes"),
                new KeyValuePair<string, string>("idle", "yes"),
                new KeyValuePair<string, string>("force-window", "no"),
                new KeyValuePair<string, string>("input-default-bindings", "no"),
                new KeyValuePair<string, string>("input-vo-keyboard", "no"),
                new KeyValuePair<string, string>("input-cursor", "no"),
                new KeyValuePair<string, string>("input-media-keys", "no"),
                new KeyValuePair<string, string>("cursor-autohide", "no"),
                new KeyValuePair<string, string>("osc", "no"),
                new KeyValuePair<string, string>("terminal", "no"),
            };
            foreach (var kv in required) o(kv.Key, kv.Value);
            o("hwdec", EffectiveHwdec());
            o("ytdl", "no");
            o("config", "yes");
            o("config-dir", Path.Combine(dir, "mpv"));
            o("osd-level", "1");
            o("osd-duration", "1200");
            o("osd-font-size", "34");
            o("osd-border-size", "2");
            o("osd-bar-h", "2.5");
            o("hr-seek", "yes");
            o("hr-seek-framedrop", "no");
            o("cache", "yes");
            // 64 MiB each way holds a whole short review clip (a 10 s 4K HEVC clip at 41 Mbit/s is
            // 52 MB), so back steps and replays stay in the cache; the former 256 MiB each only grew
            // memory on long files (60 s 4K: 1335 -> 1165 MB private, back step 435 vs 532 ms median,
            // both inside the run-to-run spread). 32 MiB slowed back steps on the 4K clip (974 ms).
            o("demuxer-max-bytes", "64MiB");
            o("demuxer-max-back-bytes", "64MiB");
            o("volume", Num(Math.Max(0, Math.Min(Presets.VolumeMax, _settings.Volume))));
            o("volume-max", Num(Presets.VolumeMax));
            o("mute", YesNo(_settings.Muted));
            o("audio-pitch-correction", YesNo(_settings.PitchCorrection));
            o("loop-file", _settings.Repeat == RepeatMode.One ? "inf" : "no");
            o("sub-auto", "fuzzy");
            o("sub-visibility", YesNo(_settings.SubtitlesVisible));
            o("sub-scale", Num(SubScaleClamp(_settings.SubScale)));
            if (!string.IsNullOrWhiteSpace(_settings.PreferredAudioLang)) o("alang", _settings.PreferredAudioLang.Trim());
            if (!string.IsNullOrWhiteSpace(_settings.PreferredSubLang)) o("slang", _settings.PreferredSubLang.Trim());
            o("screenshot-format", ScreenshotFormat());
            o("screenshot-jpeg-quality", "95");
            o("save-position-on-quit", YesNo(_settings.ResumePlayback));
            // Without this mpv would still resume from old watch-later files after the user turned resuming off.
            o("resume-playback", YesNo(_settings.ResumePlayback));
            o("watch-later-directory", Path.Combine(dir, "watch_later"));
            // Only the position: mpv's default list also restores volume, filters, rotation, speed...
            // as global options, out of step with the app's own state (flip, hwdec, volume).
            o("watch-later-options", "start");
            FeatureMpvOptions(o);
            foreach (var kv in ParseExtraOptions(_settings.ExtraMpvOptions))
            {
                string reqValue = RequiredValue(required, kv.Key, kv.Value);
                if (reqValue != null)
                    Log.Warn("ExtraMpvOptions: " + kv.Key + "=" + kv.Value + " overridden; VidFlow needs " + kv.Key + "=" + reqValue);
                o(kv.Key, kv.Value);
            }
            foreach (var kv in required) o(kv.Key, kv.Value);
            foreach (var kv in opts) _mpv.SetOption(kv.Key, kv.Value);

            _mpv.LogMessage += (level, prefix, text) => Log.Write(level == "warn" ? "WARN" : "ERROR", "mpv[" + prefix + "] " + text);
            _mpv.RequestLogMessages("warn");
            try
            {
                _mpv.Initialize();
            }
            catch (MpvException ex)
            {
                Log.Error("mpv init failed", ex);
                _mpv.Dispose();
                FailEngine(ex.Message);
                return false;
            }
            _mpvVersion = _mpv.GetString("mpv-version") ?? "";
            _ffmpegVersion = _mpv.GetString("ffmpeg-version") ?? "";
            InitHwdecMemo(_mpv.GetString("hwdec-codecs"));
            Log.Info("mpv ready: " + _mpvVersion + " / " + _ffmpegVersion);
            // mpv.conf (config=yes) is read during init and could still have changed these.
            foreach (var name in new[] { "idle", "keep-open", "keep-open-pause" })
            {
                string want = RequiredValue(required, name, null), have = _mpv.GetString(name);
                if (want == null || have == want) continue;
                Log.Warn("mpv option " + name + "=" + have + " (from mpv.conf?) overridden with " + want);
                _mpv.SetProperty(name, want);
            }

            _mpv.StartFileEntry += OnStartFileEntry;
            _mpv.FileLoaded += OnFileLoaded;
            _mpv.EndFileEntry += OnEndFileEntry;
            _mpv.PlaybackRestart += OnPlaybackRestart;
            _mpv.PlaybackRestart += CheckHwdecOutcome;
            _mpv.Shutdown += OnMpvShutdown;
            ObserveProperties();
            FeatureInit();
            CullInit();
            ExtrasInit();
            return true;
        }

        // The value VidFlow requires for an option (null when it is not required, or when `value`
        // already matches it). "no-name" is mpv's spelling of "name=no".
        private static string RequiredValue(List<KeyValuePair<string, string>> required, string name, string value)
        {
            if (name.StartsWith("no-", StringComparison.OrdinalIgnoreCase) && (value == null || value == "yes"))
            {
                name = name.Substring(3);
                value = "no";
            }
            foreach (var kv in required)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(kv.Value, value, StringComparison.OrdinalIgnoreCase) ? null : kv.Value;
            return null;
        }

        // The core quit on its own (an "idle=no" line, the "quit" command, a fatal error): show
        // the error surface and stop accepting files instead of silently doing nothing.
        private void OnMpvShutdown()
        {
            if (_closing || _engineStopped) return;
            _engineStopped = true;
            Log.Error("mpv core shut down unexpectedly");
            _lastError = "Playback engine stopped";
            _lastErrorDetail = "Restart VidFlow to play files again";
            // Outside the event pump: Dispose drains mpv's teardown messages itself.
            BeginInvoke((MethodInvoker)(() =>
            {
                if (_closing) return;
                if (_mpv != null) _mpv.Dispose();
                _st.Idle = true;
                _st.Path = null;
                _fileLoaded = false;
                _preview.SetSource(null);
                _preview.Hide();
                // Nothing can hold a file open any more.
                _liveEntries.Clear();
                _pendingLoads.Clear(); // their replies died with the core
                _currentEntry = 0;
                _currentFile = null;
                ReleaseDeletes();
                _bar.HasMedia = false;
                UpdateTitle();
                UpdateSurface();
                if (!_options.Automation)
                    RunModal(() => MessageBox.Show(this, "VidFlow's playback engine stopped unexpectedly, so no more files can be played.\n\n" +
                        "Restart VidFlow to continue. If this keeps happening, check Settings > Advanced > Extra mpv options.",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Error));
            }));
        }

        private void FailEngine(string message)
        {
            _lastError = "Playback engine failed to start";
            _lastErrorDetail = message;
            UpdateSurface();
            MessageBox.Show(this, "VidFlow could not start its playback engine.\n\n" + message, AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            BeginInvoke((MethodInvoker)Close);
        }

        private static string YesNo(bool b) { return b ? "yes" : "no"; }

        private static double SubScaleClamp(double v)
        {
            return double.IsNaN(v) ? 1 : Math.Max(0.1, Math.Min(10, v));
        }

        // "name=value" per line; "--" prefixes, blank lines and #/; comments are tolerated.
        private static IEnumerable<KeyValuePair<string, string>> ParseExtraOptions(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line.StartsWith("--")) line = line.Substring(2);
                int eq = line.IndexOf('=');
                string name = (eq < 0 ? line : line.Substring(0, eq)).Trim();
                string value = eq < 0 ? "yes" : line.Substring(eq + 1).Trim();
                if (name.Length > 0) yield return new KeyValuePair<string, string>(name, value);
            }
        }

        private void ObserveProperties()
        {
            var m = _mpv;
            // Never call synchronous Get*/SetProperty in these callbacks: they wait for the playback
            // core, which is busy for hundreds of ms while a file or decoder starts.
            m.ObserveFlag("pause", v =>
            {
                _st.Paused = v ?? true;
                _bar.Paused = _st.Paused;
                if (_st.Paused) PushPosition(true);
                FeaturePauseChanged(_st.Paused);
            });
            m.ObserveDouble("time-pos", v =>
            {
                _st.Position = v ?? 0;
                _posKnown = v.HasValue;
                FeaturePosition(_st.Position);
                UpdateFrameFromClock();
                PushPosition(false);
            });
            m.ObserveDouble("duration", v =>
            {
                _st.Duration = v.HasValue && v.Value > 0 ? v.Value : 0;
                _bar.Duration = _st.Duration;
                if (_seek != null) _seek.Duration = _st.Duration;
                UpdateFrameFromClock();
                PushPosition(true);
            });
            m.ObserveDouble("speed", v => { _st.Speed = v ?? 1; _bar.Speed = _st.Speed; });
            m.ObserveDouble("volume", v => { _st.Volume = v ?? _st.Volume; _bar.Volume = _st.Volume; });
            m.ObserveFlag("mute", v => { _st.Muted = v ?? false; _bar.Muted = _st.Muted; });
            m.ObserveFlag("eof-reached", OnEofReachedChanged);
            m.ObserveFlag("idle-active", v => OnIdleChanged(v ?? true));
            m.ObserveString("path", OnPathChanged);
            m.ObserveString("media-title", v => { _st.MediaTitle = v; UpdateTitle(); UpdateSurface(); });
            m.ObserveLong("estimated-frame-count", v =>
            {
                _mpvFrameCount = v;
                RefreshFrameCount();
                UpdateFrameFromClock();
                PushPosition(true);
            });
            m.ObserveDouble("container-fps", v =>
            {
                _st.ContainerFps = v;
                if (_frameInfo != null) ApplyFrameTiming(); // FrameExact cross-checks the container rate
            });
            m.ObserveDouble("estimated-vf-fps", v => _st.VfFps = v);
            m.ObserveNode("video-params", OnVideoParams);
            m.ObserveDouble("video-zoom", v => { _st.Zoom = v ?? 0; UpdateZoomText(); });
            m.ObserveDouble("video-pan-x", v => { _st.PanX = v ?? 0; UpdateZoomText(); });
            m.ObserveDouble("video-pan-y", v => { _st.PanY = v ?? 0; UpdateZoomText(); });
            m.ObserveString("ab-loop-a", v => { _st.AbA = ParseLoopPoint(v); PushAbLoop(); });
            m.ObserveString("ab-loop-b", v => { _st.AbB = ParseLoopPoint(v); PushAbLoop(); });
            m.ObserveString("loop-file", v => _st.LoopFile = v);
            m.ObserveNode("chapter-list", OnChapterList);
            m.ObserveLong("chapter", v => _st.Chapter = v ?? -1);
            m.ObserveNode("track-list", OnTrackList);
            m.ObserveNode("demuxer-cache-state", v =>
            {
                var d = v as Dictionary<string, object>;
                _st.CacheEnd = d != null ? ToDouble(Get(d, "cache-end")) : 0;
                if (_seek != null) _seek.BufferedEnd = _st.CacheEnd;
            });
            m.ObserveString("hwdec", v => _st.Hwdec = v);
            m.ObserveString("hwdec-current", v => _st.HwdecCurrent = v);
            m.ObserveLong("video-rotate", v => _st.Rotate = (int)(v ?? 0));
            m.ObserveDouble("panscan", v => { _st.Panscan = v ?? 0; UpdateZoomText(); });
            m.ObserveString("video-aspect-override", v => _st.Aspect = v ?? "no");
            m.ObserveFlag("sub-visibility", v => _st.SubVisible = v ?? true);
            m.ObserveString("sid", v => _st.Sid = v);
            m.ObserveString("aid", v => _st.Aid = v);
            m.ObserveDouble("audio-delay", v => _st.AudioDelay = v ?? 0);
            m.ObserveDouble("sub-delay", v => _st.SubDelay = v ?? 0);
            m.ObserveString("deinterlace", v => _st.Deinterlace = v);
            m.ObserveFlag("seeking", v => _mpvSeeking = v ?? false);
        }

        private bool _mpvSeeking; // mpv is busy with a seek (a frame back step is one)

        private static double? ParseLoopPoint(string v)
        {
            double d;
            if (string.IsNullOrEmpty(v) || v == "no") return null;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static double ToDouble(object v)
        {
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            if (v is bool) return (bool)v ? 1 : 0;
            return 0;
        }

        // ---- observers --------------------------------------------------------------------

        private void OnPathChanged(string path)
        {
            _st.Path = string.IsNullOrEmpty(path) ? null : path;
            _preview.SetSource(_st.Path);
            _bar.HasMedia = HasMedia;
            PushFrameFlags();
            UpdateTitle();
            UpdateSurface();
        }

        private void OnIdleChanged(bool idle)
        {
            _st.Idle = idle;
            if (idle)
            {
                _fileLoaded = false;
                _posKnown = false;
                _st.VideoW = _st.VideoH = 0;
                _st.Frame = _st.FrameCount = _mpvFrameCount = null;
                _bar.FrameNumber = null;
                _bar.FrameCount = null;
                if (_currentEntry == 0 && !FileStartPending) ResetFrameTiming(); // not when the next file already started
                _preview.Hide();
                // Every entry that started has ended; ones loaded since then are still on their way.
                EndEntriesThrough(_lastStartedEntry);
            }
            _bar.HasMedia = HasMedia;
            PushFrameFlags();
            UpdateTitle();
            UpdateSurface();
            UpdateZoomText();
        }

        // The frame number comes from the clock (time stays the authority): estimated-frame-number has
        // no change notifications, and reading it synchronously on every tick stalls the UI while the
        // core is busy. On a verified constant-frame-rate file frame n starts at n / fps, so the shown
        // frame is round(time-pos * fps) - the true index. Otherwise it is mpv's estimate,
        // clamp(lrint(time-pos / duration * frame count), 0, count - 1), shown as "≈F" (FrameApproximate).
        private void UpdateFrameFromClock()
        {
            long? frame = null;
            long count = _st.FrameCount ?? 0;
            if (_posKnown && FrameExact)
            {
                long f = (long)Math.Floor(Math.Max(0, _st.Position) * _frameInfo.Fps + 0.5);
                frame = count > 0 ? Math.Min(count - 1, f) : f;
            }
            else if (_posKnown && _st.Duration > 0 && count > 0)
            {
                double r = Math.Max(0, Math.Min(1, _st.Position / _st.Duration));
                frame = Math.Max(0, Math.Min(count - 1, (long)Math.Round(r * count, MidpointRounding.ToEven)));
            }
            if (frame != _st.Frame) OnFrameChanged(frame);
        }

        // Bar/seek-bar position and frame readout; see PositionPushIntervalMs.
        private void PushPosition(bool force)
        {
            int now = Environment.TickCount;
            bool jumped = double.IsNaN(_pushedPosition) || Math.Abs(_st.Position - _pushedPosition) > 0.5;
            if (!force && !jumped && !_st.Paused && _stepIssuedAt == 0 && unchecked(now - _positionPushedAt) < PositionPushIntervalMs)
            {
                _positionPushPending = true;
                return;
            }
            _positionPushedAt = now;
            _positionPushPending = false;
            _pushedPosition = _st.Position;
            _bar.Position = _st.Position;
            _bar.FrameNumber = _st.Frame;
            if (_seek != null) _seek.Position = _st.Position;
        }

        // Timer backstop: the last throttled update of a run must still reach the bar.
        private void FlushPositionPush()
        {
            if (_positionPushPending) PushPosition(true);
        }

        private void OnFrameChanged(long? frame)
        {
            bool changed = frame != _st.Frame;
            _st.Frame = frame;
            if (changed && frame.HasValue) OnStepFrameArrived();
            if (_frameOsdRequestedAt != 0 && frame.HasValue)
            {
                // Only answer a recent step; a stale request must not pop up on unrelated playback.
                if (unchecked(Environment.TickCount - _frameOsdRequestedAt) < 1500)
                    ShowOsd("Frame " + FrameText(frame.Value) + (_st.FrameCount.HasValue ? " / " + _st.FrameCount.Value : "") +
                            (FrameRateVariable ? "  (VFR: estimate)" : ""));
                _frameOsdRequestedAt = 0;
            }
        }

        private void OnVideoParams(object node)
        {
            var d = node as Dictionary<string, object>;
            int dw = (int)ToDouble(Get(d, "dw")), dh = (int)ToDouble(Get(d, "dh"));
            _st.VideoW = Math.Max(0, dw);
            _st.VideoH = Math.Max(0, dh);
            _st.ParamsRotate = ((int)ToDouble(Get(d, "rotate")) % 360 + 360) % 360;
            if (_st.VideoW > 0 && _st.VideoH > 0 && _autoFitPending)
            {
                _autoFitPending = false;
                MaybeAutoFit();
                ApplyDeferredWindowSize(false);
            }
            UpdateSurface();
            UpdateZoomText();
        }

        private void OnChapterList(object node)
        {
            var list = new List<ChapterInfo>();
            var arr = node as List<object>;
            if (arr != null)
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    list.Add(new ChapterInfo { Title = Get(d, "title") as string, Time = ToDouble(Get(d, "time")) });
                }
            _st.Chapters = list;
            if (_seek != null)
            {
                var times = new List<double>(list.Count);
                foreach (var c in list) times.Add(c.Time);
                _seek.Chapters = times;
            }
        }

        private void OnTrackList(object node)
        {
            var list = new List<TrackInfo>();
            var arr = node as List<object>;
            if (arr != null)
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    list.Add(new TrackInfo
                    {
                        Id = (long)ToDouble(Get(d, "id")),
                        Type = Get(d, "type") as string ?? "",
                        Title = Get(d, "title") as string,
                        Lang = Get(d, "lang") as string,
                        Codec = Get(d, "codec") as string,
                        Selected = Get(d, "selected") as bool? ?? false,
                        External = Get(d, "external") as bool? ?? false,
                        AlbumArt = Get(d, "albumart") as bool? ?? false,
                    });
                }
            _st.Tracks = list;
            if (list.Count > 0) _tracksSeen = true; // mpv clears the list at file start; only a filled one counts
            UpdateSurface();
        }

        private bool _tracksSeen; // the current file's track list has arrived

        private void OnEofReachedChanged(bool? v)
        {
            bool eof = v ?? false;
            _st.EofReached = eof;
            if (!eof) { _eofHandled = false; return; }
            PushPosition(true);
            // Scrubbing to the very end must not advance the playlist under the user's cursor.
            if (_eofHandled || (_seek != null && _seek.IsScrubbing)) return;
            HandleEof();
        }

        private void HandleEof()
        {
            if (!HasMedia || _eofHandled) return;
            _eofHandled = true;
            bool handled = false;
            FeatureEof(ref handled);
            if (handled) return;
            switch (_settings.Repeat)
            {
                case RepeatMode.One:
                    break; // loop-file=inf never reports EOF; a race with switching modes just stays put
                case RepeatMode.All:
                    if (_playlist.Count <= 1)
                    {
                        _mpv.CommandAsync("no-osd", "seek", "0", "absolute+exact");
                        SetPause(false);
                    }
                    else if (_playlist.MoveNext(true)) PlayCurrent(true);
                    break;
                default:
                    if (_settings.AutoAdvance && _playlist.MoveNext(false)) PlayCurrent(true);
                    break; // otherwise keep-open-pause leaves us paused on the last frame
            }
        }

        // ---- file lifecycle ---------------------------------------------------------------

        private void OnStartFileEntry(long entry)
        {
            _loadGeneration++;
            // mpv reports END_FILE before the next START_FILE, so every older entry is over:
            // either it ended, or it was replaced before it ever started (and never will).
            EndEntriesThrough(entry - 1);
            _currentEntry = entry;
            _lastStartedEntry = Math.Max(_lastStartedEntry, entry);
            string item;
            _currentFile = _liveEntries.TryGetValue(entry, out item) ? item : null;
            _fileLoaded = false;
            _tracksSeen = false;
            _eofHandled = false;
            _autoFitPending = true;
            _frameOsdRequestedAt = 0;
            ResetFrameStepQueue();
            StartFrameProbe(_currentFile ?? _playlist.Current);
            ApplyDeferredWindowSize(true);
            _lastError = null;
            // These are global options in mpv; per-file semantics are ours. Asynchronous: they run in
            // order after the loadfile without waiting for the core, which is busy opening the file.
            _mpv.CommandAsync("set", "video-zoom", "0");
            _mpv.CommandAsync("set", "video-pan-x", "0");
            _mpv.CommandAsync("set", "video-pan-y", "0");
            _mpv.CommandAsync("set", "ab-loop-a", "no");
            _mpv.CommandAsync("set", "ab-loop-b", "no");
            _st.Zoom = _st.PanX = _st.PanY = 0;
            _st.AbA = _st.AbB = null;
            PushAbLoop();
            UpdateZoomText();
            double? launchSpeed = LaunchSpeedFor(entry); // a --speed wins over the per-file reset, once
            if (launchSpeed.HasValue) SetSpeedQuiet(launchSpeed.Value);
            else if (!_settings.KeepSpeedBetweenFiles) SetSpeedQuiet(1.0);
            FeatureFileStarted();
            CullFileStarted();
            UpdateSurface();
        }

        private void OnFileLoaded()
        {
            _fileLoaded = true;
            string path = _currentFile ?? _st.Path;
            if (!string.IsNullOrEmpty(path))
            {
                _settings.AddRecent(Playlist.IsUrl(path) ? path : SafeFullPath(path));
                _settings.Save();
                _host.Surface.SetRecentFiles(_settings.RecentFiles);
            }
            _bar.HasMedia = HasMedia;
            ApplyLaunchStart();
            FeatureFileLoaded();
            CullFileLoaded();
            UpdateTitle();
            UpdateSurface();
        }

        private void OnEndFileEntry(MpvEndReason reason, int error, long entry)
        {
            string file;
            if (!_liveEntries.TryGetValue(entry, out file) && entry == _currentEntry) file = _currentFile;
            _liveEntries.Remove(entry);
            if (entry == _currentEntry)
            {
                _currentEntry = 0;
                _currentFile = null;
            }
            if (reason == MpvEndReason.Error)
            {
                string name = FileNameOf(file ?? _playlist.Current ?? "");
                _lastError = "Cannot play " + name;
                _lastErrorDetail = MpvErrorText(error);
                ShowOsd("Error: " + _lastError + " (" + _lastErrorDetail + ")", true);
                Log.Warn("playback failed: " + file + ": " + _lastErrorDetail);
            }
            ReleaseDeletes();
            UpdateSurface();
        }

        // Forgets loaded entries up to and including `last` (they can no longer hold a file open).
        private void EndEntriesThrough(long last)
        {
            if (_liveEntries.Count == 0) return;
            var gone = new List<long>();
            foreach (var id in _liveEntries.Keys) if (id <= last) gone.Add(id);
            foreach (var id in gone) _liveEntries.Remove(id);
            if (gone.Count > 0) ReleaseDeletes();
        }

        // True while mpv has (or is about to open) this file: a loaded entry that has not ended, or
        // a loadfile still waiting for its reply.
        private bool IsOpenInPlayer(string path)
        {
            foreach (var item in _liveEntries.Values)
                if (SamePath(item, path)) return true;
            foreach (var load in _pendingLoads)
                if (SamePath(load.Item, path)) return true;
            return false;
        }

        private string _lastErrorDetail;

        private static string MpvErrorText(int code)
        {
            if (code == 0) return "the file could not be played";
            try { return LibMpv.ErrorString(code); }
            catch (Exception) { return "error " + code; }
        }

        private static string SafeFullPath(string p)
        {
            try { return Path.GetFullPath(p); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException) { return p; }
        }

        // Loads the playlist's current item (or stops when there is none).
        private void PlayCurrent(bool unpause)
        {
            if (!EngineAlive) return;
            string item = _playlist.Current;
            if (item == null)
            {
                StopPlayback();
                return;
            }
            SaveResumePosition(false);
            bool wasAtEnd = _st.EofReached;
            _lastError = null;
            var launch = TakeLaunchPlayback();
            // Asynchronous: even loadfile waits whenever the core is busy (a decoder starting, the
            // previous file being torn down), which froze the window on file switches. Its reply
            // carries the new playlist entry id and always arrives before that entry's START_FILE.
            // Missing files fail later, in END_FILE.
            var load = new PendingLoad { Item = item };
            _pendingLoads.Add(load);
            _mpv.CommandAsync((err, result) => OnLoadFileReply(load, launch, err, result), "loadfile", item, "replace");
            // Queued behind the loadfile, so these apply to the new file. Leaving a file that ended
            // paused (keep-open-pause) should not leave the next one paused.
            if (launch != null && launch.Paused) SetPause(true); // --paused
            else if (unpause || wasAtEnd) SetPause(false);
            UpdateTitle();
            UpdateSurface();
        }

        // A loadfile sent by PlayCurrent that mpv has not answered yet.
        private sealed class PendingLoad
        {
            public string Item;
        }

        private readonly List<PendingLoad> _pendingLoads = new List<PendingLoad>();

        private void OnLoadFileReply(PendingLoad load, LaunchPlayback launch, int err, object result)
        {
            _pendingLoads.Remove(load);
            if (_engineStopped) return;
            if (err < 0)
            {
                _lastError = "Cannot open " + FileNameOf(load.Item);
                _lastErrorDetail = MpvErrorText(err);
                ShowOsd("Error: " + _lastError, true);
                ReleaseDeletes();
                UpdateSurface();
                return;
            }
            long entry = (long)ToDouble(Get(result as Dictionary<string, object>, "playlist_entry_id"));
            if (entry <= 0) return; // no id from this mpv: deletes fall back to waiting for the file lock
            _liveEntries[entry] = load.Item;
            ArmLaunchPlayback(launch, entry);
        }

        private void StopPlayback()
        {
            if (!EngineAlive) return;
            SaveResumePosition(false);
            _mpv.CommandAsync("stop");
        }

        // wait: synchronous (at exit, right before the core is destroyed); otherwise queued in order
        // with the commands that follow (a loadfile replacing the file it saves).
        private void SaveResumePosition(bool wait)
        {
            if (!_settings.ResumePlayback || !HasMedia || _st.EofReached) return;
            if (wait) _mpv.Command("write-watch-later-config");
            else _mpv.CommandAsync("write-watch-later-config");
        }

        // ---- hardware decoding memo -------------------------------------------------------
        // With hwdec=auto-safe on a GPU that cannot decode a codec (e.g. AV1 on a Vega 8), mpv tries
        // every hardware API in turn (d3d11va, dxva2, cuda: 14 logged errors, about 1.2 s) for every
        // such file before it decodes in software. When a file of a codec starts in software although
        // hardware decoding was asked for, that codec leaves hwdec-codecs for the rest of the session,
        // so its later files go straight to software. A codec that decoded in hardware once this
        // session never leaves the list: some GPUs decode only some profiles (HEVC 8-bit, not 10-bit).
        // The verdict is read at the file's first playback restart, after the decoder has settled.

        private List<string> _hwdecCodecs;   // mpv's hwdec-codecs as a list; null = "all" or unknown (no memo)
        private readonly SortedSet<string> _hwdecSkipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _hwdecWorked = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        private long _hwdecCheckedEntry;     // playback entry whose outcome has been read

        private void InitHwdecMemo(string codecs)
        {
            _hwdecCodecs = null;
            string s = (codecs ?? "").Trim();
            if (s.Length == 0 || s.Equals("all", StringComparison.OrdinalIgnoreCase)) return;
            var list = new List<string>();
            foreach (var part in s.Split(','))
            {
                string c = part.Trim().ToLowerInvariant();
                if (c.Length > 0 && !list.Contains(c)) list.Add(c);
            }
            _hwdecCodecs = list;
        }

        // PlaybackRestart: once per playback entry, asks mpv (asynchronously) which codec the video
        // has and whether it is decoded in hardware.
        private void CheckHwdecOutcome()
        {
            long entry = _currentEntry;
            if (_hwdecCodecs == null || !_fileLoaded || entry == 0 || entry == _hwdecCheckedEntry || !EngineAlive) return;
            if (string.IsNullOrEmpty(_st.Hwdec) || _st.Hwdec == "no") return;
            _hwdecCheckedEntry = entry;
            _mpv.GetPropertyAsync("current-tracks/video/codec", c =>
            {
                string codec = (c as string ?? "").Trim().ToLowerInvariant();
                if (codec.Length == 0 || entry != _currentEntry || !EngineAlive) return;
                _mpv.GetPropertyAsync("hwdec-current", h => OnHwdecOutcome(entry, codec, h as string));
            });
        }

        private void OnHwdecOutcome(long entry, string codec, string current)
        {
            if (entry != _currentEntry || !EngineAlive || _hwdecCodecs == null) return;
            if (!string.IsNullOrEmpty(current) && current != "no")
            {
                _hwdecWorked.Add(codec);
                return;
            }
            // Unknown, not attempted (codec not in the list, hardware decoding turned off since), or
            // known to work for other files of this codec: nothing to remember.
            if (current != "no" || !_hwdecCodecs.Contains(codec) || _hwdecWorked.Contains(codec)) return;
            if (string.IsNullOrEmpty(_st.Hwdec) || _st.Hwdec == "no") return;
            _hwdecCodecs.Remove(codec);
            _hwdecSkipped.Add(codec);
            // "none" matches no codec (an empty value would mean mpv's default list).
            string value = _hwdecCodecs.Count > 0 ? string.Join(",", _hwdecCodecs) : "none";
            _mpv.CommandAsync("set", "hwdec-codecs", value);
            Log.Info("hwdec: " + codec + " started in software decoding; hardware decoding is not tried for " + codec +
                     " again this session (hwdec-codecs=" + value + ")");
        }

        // Automation dump: "hwdecMemo" {skipped: codecs taken out of hwdec-codecs, hardware: codecs
        // seen decoding in hardware}.
        private void HwdecDump(Dictionary<string, object> d)
        {
            d["hwdecMemo"] = new Dictionary<string, object>
            {
                { "skipped", new List<object>(_hwdecSkipped) },
                { "hardware", new List<object>(_hwdecWorked) },
            };
        }

        // ---- honest frame numbers ---------------------------------------------------------
        // Each file's frame timestamps are read from its container index on a worker thread
        // (FrameRateProbe: MP4/MOV sample tables or Matroska/WebM block times, no decoding, a few ms).
        // A verified constant rate makes frame numbers exact (frame n starts at n / fps); a variable
        // rate, or a container the probe cannot read, makes them estimates: "≈F" in the bar, plus a
        // VFR badge when the rate is known to vary. Time stays the authority for markers and loops.

        private FrameRateInfo _frameInfo;    // timing of the current file; null while probing or idle
        private bool _frameProbePending;
        private int _frameProbeToken;        // bumped per probe; a reply for an older one is dropped
        private long? _mpvFrameCount;        // estimated-frame-count as mpv reports it
        private static readonly Dictionary<string, FrameRateInfo> FrameInfoCache = new Dictionary<string, FrameRateInfo>(StringComparer.OrdinalIgnoreCase);
        private const int FrameInfoCacheSize = 256;

        // A constant frame rate read from the container, which also agrees with the rate mpv's demuxer
        // reports (a second, independent reading of the file) when it reports one.
        private bool FrameExact
        {
            get
            {
                var fi = _frameInfo;
                if (fi == null || !fi.Known || fi.Variable || !(fi.Fps > 0)) return false;
                double c = _st.ContainerFps ?? 0;
                return !(c > 0.5) || Math.Abs(c - fi.Fps) / fi.Fps < 0.002;
            }
        }

        // Frame numbers on screen are estimates. False while the probe is still running (a few ms at
        // file start), so a constant-rate file never flashes "≈".
        private bool FrameApproximate
        {
            get { return HasMedia && !_frameProbePending && !FrameExact; }
        }

        // "61", or "≈61" when frame numbers are estimates.
        private string FrameText(long frame)
        {
            return (FrameApproximate ? "≈" : "") + frame.ToString(CultureInfo.InvariantCulture);
        }

        // The frame on screen at time t of the current file (for markers, loop points and exports):
        // exact on a verified constant-frame-rate file, else an estimate (approximate = true); -1 when
        // the rate is unknown.
        private long FrameNumberAt(double t, out bool approximate)
        {
            approximate = !FrameExact;
            if (FrameExact) return FrameAtTime(t);
            double fps = Fps;
            return fps > 0 ? (long)Math.Floor(Math.Max(0, t) * fps + 1e-6) : -1;
        }

        private void StartFrameProbe(string path)
        {
            int token = ++_frameProbeToken;
            _frameInfo = null;
            _frameProbePending = false;
            if (string.IsNullOrEmpty(path) || Playlist.IsUrl(path))
            {
                _frameInfo = new FrameRateInfo { Container = "stream", Reason = "not a local file" };
                ApplyFrameTiming();
                return;
            }
            _frameProbePending = true;
            ApplyFrameTiming();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // A newer file (fast paging through a folder) abandons this scan.
                FrameRateInfo info = ProbeFrameRateCached(path, () => token != System.Threading.Volatile.Read(ref _frameProbeToken));
                double ms = sw.Elapsed.TotalMilliseconds;
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (token != _frameProbeToken || _closing) return;
                        _frameProbePending = false;
                        _frameInfo = info;
                        Log.Info("frames: " + FileNameOf(path) + ": " + info.Summary + " [" + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms]");
                        ApplyFrameTiming();
                    }));
                }
                catch (InvalidOperationException) { } // the window closed meanwhile
            });
        }

        // Runs on a worker thread. Keyed by path, size and write time: culling revisits files often.
        private static FrameRateInfo ProbeFrameRateCached(string path, Func<bool> cancelled)
        {
            string key = null;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) key = fi.FullName + "|" + fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException) { }
            FrameRateInfo info;
            if (key != null)
                lock (FrameInfoCache)
                    if (FrameInfoCache.TryGetValue(key, out info)) return info;
            info = FrameRateProbe.Probe(path, cancelled);
            // A partial scan (time cap, cancelled) may be due to a busy disk; only complete answers are kept.
            if (key != null && !info.Partial)
                lock (FrameInfoCache)
                {
                    if (FrameInfoCache.Count >= FrameInfoCacheSize) FrameInfoCache.Clear();
                    FrameInfoCache[key] = info;
                }
            return info;
        }

        private void ResetFrameTiming()
        {
            _frameProbeToken++;
            _frameProbePending = false;
            _frameInfo = null;
            ApplyFrameTiming();
        }

        // The probe's frame count is exact (the container's own count); mpv's is duration × fps.
        private void RefreshFrameCount()
        {
            var fi = _frameInfo;
            long? count = fi != null && fi.Known && !fi.Partial && fi.FrameCount > 0 ? fi.FrameCount : _mpvFrameCount;
            _st.FrameCount = count;
            _bar.FrameCount = count;
        }

        // Pushes the frame-timing verdict to everything that shows frame numbers.
        private void ApplyFrameTiming()
        {
            var fi = _frameInfo;
            FrameRateVariable = fi != null && fi.Known && fi.Variable;
            RefreshFrameCount();
            UpdateFrameFromClock();
            PushPosition(true);
            PushFrameFlags();
        }

        // FrameApproximate also depends on HasMedia, which can change after the probe answered.
        private void PushFrameFlags()
        {
            bool approx = FrameApproximate;
            _bar.FrameApproximate = approx;
            _bar.VariableFrameRate = FrameRateVariable && HasMedia;
            // Also visible to mpv scripts (and to tests through the "get" verb).
            var fi = _frameInfo;
            string vfr = fi == null || !fi.Known || !HasMedia ? "unknown" : YesNo(fi.Variable), ap = YesNo(approx);
            if (EngineAlive && (vfr != _publishedVfr || ap != _publishedApprox))
            {
                _publishedVfr = vfr;
                _publishedApprox = ap;
                _mpv.CommandAsync("set", "user-data/vidflow/vfr", vfr);
                _mpv.CommandAsync("set", "user-data/vidflow/frame-approximate", ap);
            }
            // The end of every frame-timing update (probe verdict, new file, idle): OSD, markers.
            FeatureFrameTimingChanged();
        }

        private string _publishedVfr, _publishedApprox;

        // Automation dump keys: "vfr" (null = unknown / still probing), "frameApproximate", "frameTiming".
        private void FramesDump(Dictionary<string, object> d)
        {
            var fi = _frameInfo;
            bool media = HasMedia;
            d["vfr"] = media && fi != null && fi.Known ? (object)fi.Variable : null;
            d["frameApproximate"] = media ? (object)FrameApproximate : null;
            d["frameTiming"] = media && fi != null ? fi.Summary : null;
        }

        // ---- surface & OSD ----------------------------------------------------------------

        private void UpdateSurface()
        {
            var s = _host.Surface;
            string openKey = _actions.ShortcutText(ActionIds.OpenFile, 1);
            s.Hint = "Drop videos here · " + (openKey.Length > 0 ? openKey + " to open" : "right-click for the menu");
            s.SetRecentFiles(_settings.RecentFiles);
            if (_mpv == null || !_mpv.IsAlive)
            {
                if (_lastError != null)
                {
                    s.Title = _lastError;
                    s.Detail = _lastErrorDetail ?? "";
                    s.Mode = SurfaceMode.Error;
                }
                return;
            }
            bool videoShown = HasVideo;
            if (_lastError != null && !videoShown)
            {
                s.Title = _lastError;
                s.Detail = _lastErrorDetail ?? "";
                s.Mode = SurfaceMode.Error;
            }
            else if (!HasMedia) s.Mode = SurfaceMode.Idle;
            else if (videoShown || !_fileLoaded || HasSelectedVideoTrack()) s.Mode = SurfaceMode.Video;
            else
            {
                s.Title = CurrentDisplayName() ?? "";
                s.Mode = SurfaceMode.AudioOnly;
            }
        }

        private bool HasSelectedVideoTrack()
        {
            foreach (var t in _st.Tracks)
                if (t.Type == "video" && t.Selected) return true;
            return false;
        }

        // Shows a message on mpv's OSD. Respects Settings.OsdMessages unless forced (errors, explicit requests).
        private void ShowOsd(string text, bool force = false, int durationMs = 1200)
        {
            if (string.IsNullOrEmpty(text)) return;
            _lastOsd = text;
            if (!force && !_settings.OsdMessages) return;
            if (_mpv == null || !_mpv.IsAlive || !_mpv.IsInitialized) return;
            // show-text expands ${...}; file names may contain '$'.
            _mpv.CommandAsync("show-text", text.Replace("$", "$$"), durationMs.ToString(CultureInfo.InvariantCulture));
        }
    }
}
