using System;
using System.Globalization;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;
using VidFlow.Ui.Dialogs;

namespace VidFlow.Ui
{
    // Binds every ActionCatalog id (and the parameterised prefixes) to a handler.
    public sealed partial class MainForm
    {
        private bool _statsVisible;

        private bool Run(string id)
        {
            return _actions.Execute(id);
        }

        private void BuildKeyMap()
        {
            _actions.BuildKeyMap(_settings.KeyOverrides);
        }

        private void RegisterActions()
        {
            Func<bool> media = () => HasMedia;
            Func<bool> video = () => HasVideo;
            Func<bool> localFile = () => HasMedia && !Playlist.IsUrl(_st.Path);
            Action<string, Action, Func<bool>, Func<bool>> r = (id, run, chk, en) => _actions.Register(id, run, chk, en);

            // ---- file ----
            r(ActionIds.OpenFile, OpenFileDialogAction, null, null);
            r(ActionIds.OpenFolder, OpenFolderAction, null, null);
            r(ActionIds.OpenUrl, OpenUrlAction, null, null);
            r(ActionIds.LoadSubtitle, LoadSubtitleAction, null, media);
            r(ActionIds.CloseFile, StopPlayback, null, media);
            r(ActionIds.ShowInFolder, () => ShowInExplorer(_st.Path), null, localFile);
            r(ActionIds.CopyPath, CopyPathAction, null, media);
            r(ActionIds.DeleteFile, () => DeleteCurrentFile(_settings.ConfirmDelete), null, localFile);
            r(ActionIds.DeleteFileNoConfirm, () => DeleteCurrentFile(false), null, localFile);
            r(ActionIds.MediaInfo, () => RunModal(() => MediaInfoDialog.Show(this, _mpv, _st.Path)), null, media);
            r(ActionIds.Exit, Close, null, null);
            _actions.RegisterParam(ActionIds.OpenRecent, arg => OpenRecent(ParseInt(arg)),
                null, arg => { int i = ParseInt(arg); return i >= 0 && i < _settings.RecentFiles.Count; });
            // What the Recent Files menu uses: the path itself survives the list reordering.
            _actions.RegisterParam(ActionIds.OpenRecentPath, OpenRecentPath, null, arg => !string.IsNullOrWhiteSpace(arg));

            // ---- playback ----
            r(ActionIds.PlayPause, TogglePause, null, () => HasMedia || _playlist.Count > 0);
            r(ActionIds.Stop, StopAction, null, media);
            r(ActionIds.FrameStep, () => FrameStep(true), null, media);
            r(ActionIds.FrameBackStep, () => FrameStep(false), null, media);
            r(ActionIds.SeekForward, () => SeekRelative(_settings.SeekStep), null, media);
            r(ActionIds.SeekBackward, () => SeekRelative(-_settings.SeekStep), null, media);
            r(ActionIds.SeekForwardShort, () => SeekRelative(_settings.SeekStepShort), null, media);
            r(ActionIds.SeekBackwardShort, () => SeekRelative(-_settings.SeekStepShort), null, media);
            r(ActionIds.SeekForwardLong, () => SeekRelative(_settings.SeekStepLong), null, media);
            r(ActionIds.SeekBackwardLong, () => SeekRelative(-_settings.SeekStepLong), null, media);
            r(ActionIds.GoToStart, () => SeekAbsolute(0, true), null, media);
            r(ActionIds.GoToEnd, GoToEnd, null, () => HasMedia && _st.Duration > 0);
            _actions.RegisterParam(ActionIds.SeekPercent, arg => SeekPercent(ParseDouble(arg, 0)), null, _ => HasMedia);
            r(ActionIds.GoTo, GoToAction, null, () => HasMedia && _st.Duration > 0);
            r(ActionIds.NextFile, () => NextFile(true), null, () => _playlist.HasNext(_settings.Repeat == RepeatMode.All));
            r(ActionIds.PrevFile, () => NextFile(false), null, () => _playlist.HasPrevious(_settings.Repeat == RepeatMode.All));
            r(ActionIds.FirstFile, () => { if (_playlist.MoveFirst() || _playlist.Count > 0) PlayCurrent(false); }, null, () => _playlist.Count > 0);
            r(ActionIds.LastFile, () => { if (_playlist.MoveLast() || _playlist.Count > 0) PlayCurrent(false); }, null, () => _playlist.Count > 0);
            r(ActionIds.NextChapter, () => StepChapter(1), null, () => HasMedia && _st.Chapters.Count > 0);
            r(ActionIds.PrevChapter, () => StepChapter(-1), null, () => HasMedia && _st.Chapters.Count > 0);
            // Chapter and track ids from menus carry "@<load generation>" (see MenuData): an entry
            // built for an earlier file is disabled instead of being applied to the new one.
            _actions.RegisterParam(ActionIds.Chapter, arg => { if (FreshMenuArg(ref arg)) GoToChapter(ParseInt(arg)); },
                arg => FreshMenuArg(ref arg) && ParseInt(arg) == _st.Chapter,
                arg => { bool fresh = FreshMenuArg(ref arg); int i = ParseInt(arg); return fresh && HasMedia && i >= 0 && i < _st.Chapters.Count; });
            _actions.RegisterParam(ActionIds.PlayIndex, arg => PlayIndex(ParseInt(arg), true),
                arg => ParseInt(arg) == _playlist.Index,
                arg => { int i = ParseInt(arg); return i >= 0 && i < _playlist.Count; });

            // ---- speed ----
            r(ActionIds.SpeedUp, () => SpeedPreset(true), null, null);
            r(ActionIds.SpeedDown, () => SpeedPreset(false), null, null);
            r(ActionIds.SpeedFineUp, () => SetSpeed(Math.Round(_st.Speed + Presets.FineSpeedStep, 2)), null, null);
            r(ActionIds.SpeedFineDown, () => SetSpeed(Math.Round(_st.Speed - Presets.FineSpeedStep, 2)), null, null);
            r(ActionIds.SpeedDouble, () => SetSpeed(_st.Speed * 2), null, null);
            r(ActionIds.SpeedHalve, () => SetSpeed(_st.Speed / 2), null, null);
            r(ActionIds.SpeedReset, () => SetSpeed(1.0), () => Math.Abs(_st.Speed - 1) < 0.001, null);
            _actions.RegisterParam(ActionIds.SetSpeed, arg => SetSpeed(ParseDouble(arg, 1)),
                arg => Math.Abs(_st.Speed - ParseDouble(arg, double.NaN)) < 0.001, null);
            r(ActionIds.TogglePitchCorrection, TogglePitchCorrection, () => _settings.PitchCorrection, null);

            // ---- repeat & loop ----
            r(ActionIds.CycleRepeat, () => SetRepeat((RepeatMode)(((int)_settings.Repeat + 1) % 3)), null, null);
            _actions.RegisterParam(ActionIds.SetRepeat, arg => SetRepeat(ParseRepeat(arg)),
                arg => { RepeatMode m; return TryParseRepeat(arg, out m) && m == _settings.Repeat; },
                arg => { RepeatMode m; return TryParseRepeat(arg, out m); });
            r(ActionIds.AbLoop, CycleAbLoop, () => _st.AbA.HasValue, media);
            r(ActionIds.AbLoopClear, ClearAbLoop, null, () => _st.AbA.HasValue || _st.AbB.HasValue);
            r(ActionIds.ToggleShuffle, ToggleShuffle, () => _playlist.Shuffle, null);

            // ---- zoom & pan ----
            r(ActionIds.ZoomIn, () => ZoomBy(Presets.ZoomStepLog2, null), null, video);
            r(ActionIds.ZoomOut, () => ZoomBy(-Presets.ZoomStepLog2, null), null, video);
            r(ActionIds.ZoomReset, ResetZoom, () => IsZoomReset, video);
            _actions.RegisterParam(ActionIds.SetZoom, arg => SetPixelZoom(ParseDouble(arg, 100)),
                arg => IsPixelZoom(ParseDouble(arg, double.NaN)), _ => HasVideo);
            r(ActionIds.PanLeft, () => PanStep(1, 0), null, video);
            r(ActionIds.PanRight, () => PanStep(-1, 0), null, video);
            r(ActionIds.PanUp, () => PanStep(0, 1), null, video);
            r(ActionIds.PanDown, () => PanStep(0, -1), null, video);
            _actions.RegisterParam(ActionIds.WindowSize, SetWindowSize, IsWindowSize, arg => MayGetVideo && IsWindowSizeArg(arg));

            // ---- video ----
            r(ActionIds.CycleAspect, CycleAspect, null, video);
            _actions.RegisterParam(ActionIds.SetAspect, SetAspect, IsAspect, _ => HasVideo);
            r(ActionIds.ToggleFill, ToggleFill, () => _st.Panscan > 0.001, video);
            r(ActionIds.RotateCW, () => SetRotate(_st.Rotate + 90), null, video);
            r(ActionIds.RotateCCW, () => SetRotate(_st.Rotate + 270), null, video);
            _actions.RegisterParam(ActionIds.SetRotate, arg => SetRotate(ParseInt(arg)),
                arg => ParseInt(arg) == NormalizeRotation(_st.Rotate), _ => HasVideo);
            r(ActionIds.FlipH, () => SetFlip(!_flipH, _flipV), () => _flipH, video);
            r(ActionIds.FlipV, () => SetFlip(_flipH, !_flipV), () => _flipV, video);
            r(ActionIds.ToggleDeinterlace, ToggleDeinterlace, () => _st.Deinterlace == "yes", video);
            r(ActionIds.ToggleHwDec, ToggleHwDec, () => EffectiveHwdec() != "no", null);
            r(ActionIds.BrightnessUp, () => AdjustPicture("brightness", "Brightness", 1), null, video);
            r(ActionIds.BrightnessDown, () => AdjustPicture("brightness", "Brightness", -1), null, video);
            r(ActionIds.ContrastUp, () => AdjustPicture("contrast", "Contrast", 1), null, video);
            r(ActionIds.ContrastDown, () => AdjustPicture("contrast", "Contrast", -1), null, video);
            r(ActionIds.SaturationUp, () => AdjustPicture("saturation", "Saturation", 1), null, video);
            r(ActionIds.SaturationDown, () => AdjustPicture("saturation", "Saturation", -1), null, video);
            r(ActionIds.GammaUp, () => AdjustPicture("gamma", "Gamma", 1), null, video);
            r(ActionIds.GammaDown, () => AdjustPicture("gamma", "Gamma", -1), null, video);
            r(ActionIds.HueUp, () => AdjustPicture("hue", "Hue", 1), null, video);
            r(ActionIds.HueDown, () => AdjustPicture("hue", "Hue", -1), null, video);
            r(ActionIds.ResetVideoAdjust, ResetPicture, null, video);
            _actions.RegisterParam(ActionIds.VideoTrack, arg => { if (FreshMenuArg(ref arg)) SelectTrack("vid", arg); },
                arg => FreshMenuArg(ref arg) && IsTrackSelected("video", arg), arg => FreshMenuArg(ref arg) && HasMedia);

            // ---- audio ----
            r(ActionIds.VolumeUp, () => SetVolume(_st.Volume + Presets.VolumeStep, true), null, null);
            r(ActionIds.VolumeDown, () => SetVolume(_st.Volume - Presets.VolumeStep, true), null, null);
            r(ActionIds.ToggleMute, ToggleMute, () => _st.Muted, null);
            r(ActionIds.CycleAudioTrack, CycleAudioTrack, null, () => HasMedia && CountTracks("audio") > 0);
            _actions.RegisterParam(ActionIds.AudioTrack, arg => { if (FreshMenuArg(ref arg)) SelectTrack("aid", arg); },
                arg => FreshMenuArg(ref arg) && IsTrackSelected("audio", arg), arg => FreshMenuArg(ref arg) && HasMedia);
            r(ActionIds.AudioDelayUp, () => AdjustDelay("audio-delay", "Audio delay", Presets.DelayStep), null, media);
            r(ActionIds.AudioDelayDown, () => AdjustDelay("audio-delay", "Audio delay", -Presets.DelayStep), null, media);
            r(ActionIds.AudioDelayReset, () => AdjustDelay("audio-delay", "Audio delay", null), null, media);

            // ---- subtitles ----
            r(ActionIds.CycleSubtitle, () => CycleSubtitle(1), null, () => HasMedia && CountTracks("sub") > 0);
            r(ActionIds.CycleSubtitleBack, () => CycleSubtitle(-1), null, () => HasMedia && CountTracks("sub") > 0);
            r(ActionIds.ToggleSubtitles, ToggleSubtitles, () => _settings.SubtitlesVisible, null);
            _actions.RegisterParam(ActionIds.SubTrack, arg => { if (FreshMenuArg(ref arg)) SelectTrack("sid", arg); },
                arg => FreshMenuArg(ref arg) && IsTrackSelected("sub", arg), arg => FreshMenuArg(ref arg) && HasMedia);
            r(ActionIds.SubDelayUp, () => AdjustDelay("sub-delay", "Subtitle delay", Presets.DelayStep), null, media);
            r(ActionIds.SubDelayDown, () => AdjustDelay("sub-delay", "Subtitle delay", -Presets.DelayStep), null, media);
            r(ActionIds.SubDelayReset, () => AdjustDelay("sub-delay", "Subtitle delay", null), null, media);
            r(ActionIds.SubScaleUp, () => SetSubScale(_settings.SubScale + Presets.SubScaleStep), null, null);
            r(ActionIds.SubScaleDown, () => SetSubScale(_settings.SubScale - Presets.SubScaleStep), null, null);

            // ---- screenshots ----
            r(ActionIds.Screenshot, () => SaveScreenshot(false), null, video);
            r(ActionIds.ScreenshotDisplayed, () => SaveScreenshot(true), null, video);
            r(ActionIds.CopyFrame, CopyFrameToClipboard, null, video);
            r(ActionIds.OpenScreenshotFolder, OpenScreenshotFolder, null, null);

            // ---- view ----
            r(ActionIds.ToggleFullscreen, () => SetFullscreen(!_fullscreen), () => _fullscreen, null);
            r(ActionIds.ExitFullscreen, () => SetFullscreen(false), null, () => _fullscreen);
            r(ActionIds.ToggleOnTop, ToggleOnTop, () => TopMost, null);
            r(ActionIds.ToggleControls, () => SetControlsVisible(!_settings.ShowControls), () => _settings.ShowControls, null);
            r(ActionIds.TogglePlaylist, () => SetPlaylistVisible(!_settings.ShowPlaylist), () => _settings.ShowPlaylist, null);
            r(ActionIds.ToggleStats, ToggleStats, () => _statsVisible, media);
            r(ActionIds.ShowInfoOsd, ShowInfoOsd, null, media);
            r(ActionIds.ToggleOsd, ToggleOsdMessages, () => _settings.OsdMessages, null);

            // ---- application ----
            r(ActionIds.Settings, ShowSettingsDialog, null, null);
            r(ActionIds.Shortcuts, () => RunModal(() => ShortcutsDialog.Show(this, _actions, _settings)), null, null);
            r(ActionIds.About, () => RunModal(() => AboutDialog.Show(this, _mpvVersion, _ffmpegVersion,
                NativeLoader.LoadedFrom ?? "", _settings.FilePath ?? "")), null, null);
            r(ActionIds.ContextMenu, () => ShowContextMenuAtCursor(true), null, null);

            FeatureRegisterActions();
            CullRegisterActions();
            ExtrasRegisterActions();
            foreach (var def in ActionCatalog.All)
                if (!_actions.IsKnown(def.Id)) Log.Error("action not registered: " + def.Id);
        }

        // ---- argument parsing -------------------------------------------------------------

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : int.MinValue;
        }

        private static double ParseDouble(string s, double fallback)
        {
            double v;
            string t = (s ?? "").Trim().TrimEnd('x', 'X', '×', '%');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : fallback;
        }

        // Strips a trailing "@<load generation>" from a menu-built argument; false when it was built
        // for an earlier file. Arguments without one (keys, automation) are always fresh.
        private bool FreshMenuArg(ref string arg)
        {
            int at = (arg ?? "").LastIndexOf('@');
            if (at < 0) return true;
            bool fresh = ParseInt(arg.Substring(at + 1)) == _loadGeneration;
            arg = arg.Substring(0, at);
            return fresh;
        }

        private string MenuArg(string arg)
        {
            return arg + "@" + _loadGeneration.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryParseRepeat(string s, out RepeatMode mode)
        {
            mode = RepeatMode.Off;
            if (string.IsNullOrWhiteSpace(s)) return false;
            try
            {
                mode = (RepeatMode)Enum.Parse(typeof(RepeatMode), s.Trim(), true);
                return Enum.IsDefined(typeof(RepeatMode), mode);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static RepeatMode ParseRepeat(string s)
        {
            RepeatMode m;
            if (!TryParseRepeat(s, out m)) throw new ArgumentException("unknown repeat mode '" + s + "'");
            return m;
        }

        // ---- modal helpers ----------------------------------------------------------------

        private int _modalDepth;

        // Dialogs need a visible cursor and must not be fought by the idle-hide timer.
        private void RunModal(Action show)
        {
            ShowCursorNow();
            _preview.Hide();
            _modalDepth++;
            try { show(); }
            finally { _modalDepth--; }
        }

        private void ShowSettingsDialog()
        {
            bool ok = false;
            SyncLiveSettings(); // the dialog edits (and ApplySettings re-pushes) current values, not startup ones
            string hwdecBefore = _settings.HwDec;
            RunModal(() => ok = SettingsDialog.Show(this, _settings, _actions));
            if (!ok) return;
            // A new decoding preference replaces a temporary Ctrl+H override.
            if (!string.Equals(hwdecBefore, _settings.HwDec, StringComparison.OrdinalIgnoreCase)) _hwdecOverride = null;
            ApplySettings();
            _settings.Save();
        }

        // Pushes every setting that can change at runtime into the player and the UI.
        private void ApplySettings()
        {
            BuildKeyMap();
            if (_settings.RecentFiles.Count > Math.Max(0, _settings.MaxRecentFiles))
                _settings.RecentFiles.RemoveRange(Math.Max(0, _settings.MaxRecentFiles), _settings.RecentFiles.Count - Math.Max(0, _settings.MaxRecentFiles));
            ApplyHwdec();
            ApplyPlaylistSettings();
            if (EngineAlive)
            {
                _mpv.SetProperty("audio-pitch-correction", _settings.PitchCorrection);
                _mpv.SetProperty("sub-visibility", _settings.SubtitlesVisible);
                _mpv.SetProperty("sub-scale", SubScaleClamp(_settings.SubScale));
                _mpv.SetProperty("alang", (_settings.PreferredAudioLang ?? "").Trim());
                _mpv.SetProperty("slang", (_settings.PreferredSubLang ?? "").Trim());
                _mpv.SetProperty("loop-file", _settings.Repeat == RepeatMode.One ? "inf" : "no");
                _mpv.SetProperty("screenshot-format", ScreenshotFormat());
                _mpv.SetProperty("save-position-on-quit", _settings.ResumePlayback);
                _mpv.SetProperty("resume-playback", _settings.ResumePlayback);
                _mpv.SetProperty("volume", Math.Max(0, Math.Min(Presets.VolumeMax, _settings.Volume)));
                _mpv.SetProperty("mute", _settings.Muted);
            }
            _playlist.Shuffle = _settings.Shuffle;
            TopMost = _settings.AlwaysOnTop;
            PushStaticBarState();
            ApplyLayout();
            FeatureApplySettings();
            CullApplySettings();
            UpdateCursorVisibility();
            UpdateSurface();
            UpdateTitle();
        }
    }
}
