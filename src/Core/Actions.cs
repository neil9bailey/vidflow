using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace VidFlow.Core
{
    // Every user-invokable command has a stable string id. Ids may carry one argument
    // after a colon ("SetSpeed:1.5", "SeekPercent:30", "AudioTrack:2"); those route to a
    // handler registered with ActionRegistry.RegisterParam(prefix).
    public static class ActionIds
    {
        // File
        public const string OpenFile = "OpenFile", OpenFolder = "OpenFolder", OpenUrl = "OpenUrl",
            LoadSubtitle = "LoadSubtitle", CloseFile = "CloseFile", ShowInFolder = "ShowInFolder",
            CopyPath = "CopyPath", DeleteFile = "DeleteFile", DeleteFileNoConfirm = "DeleteFileNoConfirm",
            MediaInfo = "MediaInfo", Exit = "Exit", OpenRecent = "OpenRecent", // OpenRecent:<index>
            OpenRecentPath = "OpenRecentPath", // OpenRecentPath:<full path or URL> (stable while the list reorders)
            SavePlaylist = "SavePlaylist", RegisterFileTypes = "RegisterFileTypes", UnregisterFileTypes = "UnregisterFileTypes";

        // Playlist order
        public const string SortPlaylist = "SortPlaylist", // SortPlaylist:Name|DateModified|Size|Type
            TogglePlaylistSortOrder = "TogglePlaylistSortOrder";

        // Markers (per-file review bookmarks, kept for the session)
        public const string AddMarker = "AddMarker", NextMarker = "NextMarker", PrevMarker = "PrevMarker",
            ClearMarkers = "ClearMarkers", CopyMarkers = "CopyMarkers", GoToMarker = "GoToMarker"; // GoToMarker:<index>

        // Playback / navigation
        public const string PlayPause = "PlayPause", Stop = "Stop",
            FrameStep = "FrameStep", FrameBackStep = "FrameBackStep",
            SeekForward = "SeekForward", SeekBackward = "SeekBackward",
            SeekForwardShort = "SeekForwardShort", SeekBackwardShort = "SeekBackwardShort",
            SeekForwardLong = "SeekForwardLong", SeekBackwardLong = "SeekBackwardLong",
            GoToStart = "GoToStart", GoToEnd = "GoToEnd", SeekPercent = "SeekPercent", // SeekPercent:<0-100>
            GoTo = "GoTo", NextFile = "NextFile", PrevFile = "PrevFile", FirstFile = "FirstFile", LastFile = "LastFile",
            NextChapter = "NextChapter", PrevChapter = "PrevChapter", Chapter = "Chapter", // Chapter:<index>
            PlayIndex = "PlayIndex", // PlayIndex:<playlist index>
            ToggleReverse = "ToggleReverse"; // continuous backward playback (mpv play-direction)

        // Speed
        public const string SpeedUp = "SpeedUp", SpeedDown = "SpeedDown", SpeedDouble = "SpeedDouble",
            SpeedHalve = "SpeedHalve", SpeedReset = "SpeedReset", SetSpeed = "SetSpeed", // SetSpeed:<factor>
            SpeedFineUp = "SpeedFineUp", SpeedFineDown = "SpeedFineDown", TogglePitchCorrection = "TogglePitchCorrection";

        // Loop
        public const string CycleRepeat = "CycleRepeat", SetRepeat = "SetRepeat", // SetRepeat:Off|One|All
            AbLoop = "AbLoop", AbLoopClear = "AbLoopClear", ToggleShuffle = "ToggleShuffle",
            AbSetA = "AbSetA", AbSetB = "AbSetB", // set one end at the current position (swap if inverted)
            AbNudge = "AbNudge"; // AbNudge:A-1|A+1|B-1|B+1 (move one end by a frame)

        // Video / zoom
        public const string ZoomIn = "ZoomIn", ZoomOut = "ZoomOut", ZoomReset = "ZoomReset",
            SetZoom = "SetZoom", // SetZoom:<percent of video pixels, e.g. 100 = 1:1>
            PanLeft = "PanLeft", PanRight = "PanRight", PanUp = "PanUp", PanDown = "PanDown",
            WindowSize = "WindowSize", // WindowSize:<percent> | WindowSize:fit
            CycleAspect = "CycleAspect", SetAspect = "SetAspect", // SetAspect:<Presets.Aspects value>, "no" = from file
            ToggleFill = "ToggleFill", RotateCW = "RotateCW", RotateCCW = "RotateCCW", SetRotate = "SetRotate",
            FlipH = "FlipH", FlipV = "FlipV", ToggleDeinterlace = "ToggleDeinterlace", ToggleHwDec = "ToggleHwDec",
            BrightnessUp = "BrightnessUp", BrightnessDown = "BrightnessDown",
            ContrastUp = "ContrastUp", ContrastDown = "ContrastDown",
            SaturationUp = "SaturationUp", SaturationDown = "SaturationDown",
            GammaUp = "GammaUp", GammaDown = "GammaDown", HueUp = "HueUp", HueDown = "HueDown",
            ResetVideoAdjust = "ResetVideoAdjust", VideoTrack = "VideoTrack"; // VideoTrack:<id>|no

        // Audio
        public const string VolumeUp = "VolumeUp", VolumeDown = "VolumeDown", ToggleMute = "ToggleMute",
            CycleAudioTrack = "CycleAudioTrack", AudioTrack = "AudioTrack", // AudioTrack:<id>|no
            AudioDelayUp = "AudioDelayUp", AudioDelayDown = "AudioDelayDown", AudioDelayReset = "AudioDelayReset",
            AudioDevice = "AudioDevice", // AudioDevice:<mpv device name> ("auto" = system default)
            ToggleAudioNormalize = "ToggleAudioNormalize",
            SetAudioChannels = "SetAudioChannels"; // SetAudioChannels:auto|stereo|mono

        // Subtitles
        public const string CycleSubtitle = "CycleSubtitle", CycleSubtitleBack = "CycleSubtitleBack",
            ToggleSubtitles = "ToggleSubtitles", SubTrack = "SubTrack", // SubTrack:<id>|no
            SubDelayUp = "SubDelayUp", SubDelayDown = "SubDelayDown", SubDelayReset = "SubDelayReset",
            SubScaleUp = "SubScaleUp", SubScaleDown = "SubScaleDown";

        // Screenshot
        public const string Screenshot = "Screenshot", ScreenshotDisplayed = "ScreenshotDisplayed",
            CopyFrame = "CopyFrame", OpenScreenshotFolder = "OpenScreenshotFolder";

        // View
        public const string ToggleFullscreen = "ToggleFullscreen", ExitFullscreen = "ExitFullscreen",
            ToggleOnTop = "ToggleOnTop", ToggleControls = "ToggleControls", TogglePlaylist = "TogglePlaylist",
            ToggleStats = "ToggleStats", ShowInfoOsd = "ShowInfoOsd", ToggleOsd = "ToggleOsd",
            ToggleOsdStatus = "ToggleOsdStatus", // persistent time/frame/speed line on the video
            ToggleBorderless = "ToggleBorderless", ToggleKeepAspect = "ToggleKeepAspect";

        // App
        public const string Settings = "Settings", Shortcuts = "Shortcuts", About = "About", ContextMenu = "ContextMenu",
            CheckForUpdates = "CheckForUpdates", ShowQuickStart = "ShowQuickStart";

        // Cull (review decisions). Pick/Reject/Unflag/Rate/NextUndecided live in the Cull key layer.
        public const string ToggleCullMode = "ToggleCullMode", CullPick = "CullPick", CullReject = "CullReject",
            CullUnflag = "CullUnflag", CullRate = "CullRate", // CullRate:0..5 (0 clears)
            CullUndo = "CullUndo", CullRedo = "CullRedo", CullCommit = "CullCommit",
            CullToBin = "CullToBin", // CullToBin:1..5 (move the current clip to that bin folder)
            CullFilter = "CullFilter", // CullFilter:all|picked|rejected|undecided|rated
            CullAutoAdvance = "CullAutoAdvance", CullNextUndecided = "CullNextUndecided";

        // File (more)
        public const string RenameFile = "RenameFile", ExportMarkersCsv = "ExportMarkersCsv", ExportMarkersEdl = "ExportMarkersEdl";
    }

    public sealed class ActionDef
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Category { get; private set; }
        public string[] DefaultKeys { get; private set; }
        // Key layer the shortcuts belong to: null = always active; ActionCatalog.LayerCull =
        // only while Cull mode is on (where they take precedence over the global keys).
        public string Layer { get; private set; }

        public ActionDef(string id, string title, string category, params string[] defaultKeys)
        {
            Id = id; Title = title; Category = category; DefaultKeys = defaultKeys ?? new string[0];
        }

        internal ActionDef InLayer(string layer)
        {
            Layer = layer;
            return this;
        }
    }

    // The canonical list of bindable actions shown in menus, the shortcuts dialog and
    // the key editor. Parameterised ids listed here (SeekPercent:30, SetZoom:100, ...)
    // are the ones worth a default key; menus build other arguments on the fly.
    public static class ActionCatalog
    {
        public const string CatFile = "File", CatPlayback = "Playback", CatNavigate = "Navigation",
            CatSpeed = "Speed", CatLoop = "Repeat & Loop", CatZoom = "Zoom & Pan", CatVideo = "Video",
            CatAudio = "Audio", CatSubs = "Subtitles", CatShot = "Screenshot", CatView = "View", CatApp = "Application",
            CatPlaylist = "Playlist", CatMarkers = "Markers", CatCull = "Cull";

        // Keys of actions in this layer only work while Cull mode is on, and win over global keys then.
        public const string LayerCull = "cull";

        public static readonly IReadOnlyList<ActionDef> All = Build();
        private static Dictionary<string, ActionDef> _byId;

        public static ActionDef Find(string id)
        {
            if (id == null) return null;
            if (_byId == null)
            {
                var d = new Dictionary<string, ActionDef>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in All) d[a.Id] = a;
                _byId = d;
            }
            ActionDef def;
            return _byId.TryGetValue(id, out def) ? def : null;
        }

        public static IEnumerable<string> Categories
        {
            get { return All.Select(a => a.Category).Distinct(); }
        }

        private static IReadOnlyList<ActionDef> Build()
        {
            var l = new List<ActionDef>();
            Action<string, string, string, string[]> add = (id, title, cat, keys) => l.Add(new ActionDef(id, title, cat, keys));
            Func<string[], string[]> k = x => x;

            // File
            add(ActionIds.OpenFile, "Open File…", CatFile, k(new[] { "Ctrl+O" }));
            add(ActionIds.OpenFolder, "Open Folder…", CatFile, k(new[] { "Ctrl+Shift+O" }));
            add(ActionIds.OpenUrl, "Open URL or Path…", CatFile, k(new[] { "Ctrl+U" }));
            add(ActionIds.LoadSubtitle, "Load Subtitle File…", CatFile, k(new string[0]));
            add(ActionIds.CloseFile, "Close File", CatFile, k(new[] { "Ctrl+W" }));
            add(ActionIds.ShowInFolder, "Show in Folder", CatFile, k(new[] { "Ctrl+E" }));
            add(ActionIds.CopyPath, "Copy File Path", CatFile, k(new[] { "Ctrl+Shift+C" }));
            add(ActionIds.DeleteFile, "Delete File to Recycle Bin…", CatFile, k(new[] { "Delete" }));
            add(ActionIds.DeleteFileNoConfirm, "Delete File to Recycle Bin (No Prompt)", CatFile, k(new[] { "Shift+Delete" }));
            add(ActionIds.MediaInfo, "Media Info…", CatFile, k(new[] { "Ctrl+I" }));
            add(ActionIds.SavePlaylist, "Save Playlist As…", CatFile, k(new[] { "Ctrl+S" }));
            add(ActionIds.RenameFile, "Rename File…", CatFile, k(new[] { "F2" }));
            add(ActionIds.RegisterFileTypes, "Add VidFlow to “Open With” for Video Files", CatFile, k(new string[0]));
            add(ActionIds.UnregisterFileTypes, "Remove VidFlow from “Open With”", CatFile, k(new string[0]));
            add(ActionIds.Exit, "Exit", CatFile, k(new[] { "Ctrl+Q" }));

            // Playback
            add(ActionIds.PlayPause, "Play / Pause", CatPlayback, k(new[] { "Space", "K", "MediaPlayPause" }));
            add(ActionIds.Stop, "Stop", CatPlayback, k(new[] { "MediaStop" }));
            add(ActionIds.FrameStep, "Next Frame", CatPlayback, k(new[] { ".", "Ctrl+Right" }));
            add(ActionIds.FrameBackStep, "Previous Frame", CatPlayback, k(new[] { ",", "Ctrl+Left" }));
            add(ActionIds.SeekForward, "Seek Forward", CatPlayback, k(new[] { "Right" }));
            add(ActionIds.SeekBackward, "Seek Backward", CatPlayback, k(new[] { "Left" }));
            add(ActionIds.SeekForwardShort, "Seek Forward (Short, Exact)", CatPlayback, k(new[] { "Shift+Right" }));
            add(ActionIds.SeekBackwardShort, "Seek Backward (Short, Exact)", CatPlayback, k(new[] { "Shift+Left" }));
            add(ActionIds.SeekForwardLong, "Seek Forward (Long)", CatPlayback, k(new[] { "Alt+Right" }));
            add(ActionIds.SeekBackwardLong, "Seek Backward (Long)", CatPlayback, k(new[] { "Alt+Left" }));
            add(ActionIds.GoToStart, "Go to Start", CatPlayback, k(new[] { "Home" }));
            add(ActionIds.GoToEnd, "Go to Last Frame", CatPlayback, k(new[] { "End" }));
            for (int p = 0; p <= 90; p += 10)
                add(ActionIds.SeekPercent + ":" + p, "Jump to " + p + "%", CatPlayback, k(new[] { (p / 10).ToString(CultureInfo.InvariantCulture) }));
            add(ActionIds.GoTo, "Go to Time or Frame…", CatPlayback, k(new[] { "Ctrl+G" }));
            add(ActionIds.ToggleReverse, "Play Backward", CatPlayback, k(new[] { "Shift+Space" }));

            // Navigation
            add(ActionIds.NextFile, "Next File", CatNavigate, k(new[] { "PageDown", ">", "MediaNextTrack" }));
            add(ActionIds.PrevFile, "Previous File", CatNavigate, k(new[] { "PageUp", "<", "MediaPreviousTrack" }));
            add(ActionIds.FirstFile, "First File", CatNavigate, k(new[] { "Ctrl+Home" }));
            add(ActionIds.LastFile, "Last File", CatNavigate, k(new[] { "Ctrl+End" }));
            add(ActionIds.NextChapter, "Next Chapter", CatNavigate, k(new[] { "Ctrl+PageDown" }));
            add(ActionIds.PrevChapter, "Previous Chapter", CatNavigate, k(new[] { "Ctrl+PageUp" }));

            // Playlist order
            add(ActionIds.SortPlaylist + ":Name", "Sort by Name", CatPlaylist, k(new string[0]));
            add(ActionIds.SortPlaylist + ":DateModified", "Sort by Date Modified", CatPlaylist, k(new string[0]));
            add(ActionIds.SortPlaylist + ":Size", "Sort by Size", CatPlaylist, k(new string[0]));
            add(ActionIds.SortPlaylist + ":Type", "Sort by Type", CatPlaylist, k(new string[0]));
            add(ActionIds.TogglePlaylistSortOrder, "Sort Descending", CatPlaylist, k(new string[0]));

            // Markers
            add(ActionIds.AddMarker, "Add Marker Here", CatMarkers, k(new[] { "B" }));
            add(ActionIds.NextMarker, "Next Marker", CatMarkers, k(new[] { "Ctrl+Shift+Right" }));
            add(ActionIds.PrevMarker, "Previous Marker", CatMarkers, k(new[] { "Ctrl+Shift+Left" }));
            add(ActionIds.ClearMarkers, "Clear Markers", CatMarkers, k(new string[0]));
            add(ActionIds.CopyMarkers, "Copy Markers to Clipboard", CatMarkers, k(new string[0]));
            add(ActionIds.ExportMarkersCsv, "Export Markers as CSV…", CatMarkers, k(new string[0]));
            add(ActionIds.ExportMarkersEdl, "Export Markers as EDL (DaVinci Resolve)…", CatMarkers, k(new string[0]));

            // Cull: decide clip by clip, then apply the decisions in one reviewable batch
            add(ActionIds.ToggleCullMode, "Cull Mode", CatCull, k(new[] { "C" }));
            Action<string, string, string[]> cull = (id, title, keys) => l.Add(new ActionDef(id, title, CatCull, keys).InLayer(LayerCull));
            cull(ActionIds.CullPick, "Pick (Keep)", new[] { "P" });
            cull(ActionIds.CullReject, "Reject", new[] { "X" });
            cull(ActionIds.CullUnflag, "Clear Pick / Reject", new[] { "U" });
            // Top-row and numpad digits both rate (photo-culling habit).
            for (int stars = 1; stars <= 5; stars++)
                cull(ActionIds.CullRate + ":" + stars, "Rate " + new string('★', stars), new[] { stars.ToString(CultureInfo.InvariantCulture), "Num" + stars });
            cull(ActionIds.CullRate + ":0", "Clear Rating", new[] { "0", "Num0" });
            cull(ActionIds.CullNextUndecided, "Next Undecided Clip", new[] { "N" });
            add(ActionIds.CullUndo, "Undo Decision", CatCull, k(new[] { "Ctrl+Z" }));
            add(ActionIds.CullRedo, "Redo Decision", CatCull, k(new[] { "Ctrl+Y" }));
            add(ActionIds.CullCommit, "Apply Decisions… (Keep Picks, Bin Rejects)", CatCull, k(new[] { "Ctrl+Enter" }));
            // In the Cull layer: a stray F5 ("refresh" habit) must never move a clip outside Cull mode.
            for (int bin = 1; bin <= 5; bin++)
                cull(ActionIds.CullToBin + ":" + bin, "Move Clip to Bin " + bin, new[] { "F" + (bin + 4) });
            add(ActionIds.CullAutoAdvance, "Advance After Each Decision", CatCull, k(new string[0]));
            add(ActionIds.CullFilter + ":all", "Show All Clips", CatCull, k(new string[0]));
            add(ActionIds.CullFilter + ":picked", "Show Picks Only", CatCull, k(new string[0]));
            add(ActionIds.CullFilter + ":rejected", "Show Rejects Only", CatCull, k(new string[0]));
            add(ActionIds.CullFilter + ":undecided", "Show Undecided Only", CatCull, k(new string[0]));
            add(ActionIds.CullFilter + ":rated", "Show Rated Only", CatCull, k(new string[0]));

            // Speed
            add(ActionIds.SpeedUp, "Faster (Next Preset)", CatSpeed, k(new[] { "]", "Ctrl+Up" }));
            add(ActionIds.SpeedDown, "Slower (Previous Preset)", CatSpeed, k(new[] { "[", "Ctrl+Down" }));
            add(ActionIds.SpeedFineUp, "Faster by 0.05×", CatSpeed, k(new[] { "Ctrl+]" }));
            add(ActionIds.SpeedFineDown, "Slower by 0.05×", CatSpeed, k(new[] { "Ctrl+[" }));
            add(ActionIds.SpeedDouble, "Double Speed", CatSpeed, k(new[] { "}" }));
            add(ActionIds.SpeedHalve, "Halve Speed", CatSpeed, k(new[] { "{" }));
            add(ActionIds.SpeedReset, "Normal Speed (1×)", CatSpeed, k(new[] { "Backspace", "=", "Ctrl+R" }));
            add(ActionIds.TogglePitchCorrection, "Keep Audio Pitch", CatSpeed, k(new string[0]));

            // Repeat & loop
            add(ActionIds.CycleRepeat, "Repeat: Off / One / All", CatLoop, k(new[] { "R" }));
            add(ActionIds.SetRepeat + ":Off", "Repeat Off", CatLoop, k(new string[0]));
            add(ActionIds.SetRepeat + ":One", "Repeat This File", CatLoop, k(new string[0]));
            add(ActionIds.SetRepeat + ":All", "Repeat All (Advance Through Folder)", CatLoop, k(new string[0]));
            add(ActionIds.AbLoop, "A-B Loop (Set A → Set B → Clear)", CatLoop, k(new[] { "L" }));
            add(ActionIds.AbLoopClear, "Clear A-B Loop", CatLoop, k(new[] { "Shift+L" }));
            add(ActionIds.AbSetA, "Set Loop Start (A) Here", CatLoop, k(new[] { "Alt+[" }));
            add(ActionIds.AbSetB, "Set Loop End (B) Here", CatLoop, k(new[] { "Alt+]" }));
            add(ActionIds.AbNudge + ":A-1", "Loop Start − 1 Frame", CatLoop, k(new string[0]));
            add(ActionIds.AbNudge + ":A+1", "Loop Start + 1 Frame", CatLoop, k(new string[0]));
            add(ActionIds.AbNudge + ":B-1", "Loop End − 1 Frame", CatLoop, k(new string[0]));
            add(ActionIds.AbNudge + ":B+1", "Loop End + 1 Frame", CatLoop, k(new string[0]));
            add(ActionIds.ToggleShuffle, "Shuffle", CatLoop, k(new string[0]));

            // Zoom & pan
            add(ActionIds.ZoomIn, "Zoom In", CatZoom, k(new[] { "Ctrl+=", "Num+", "Num9" }));
            add(ActionIds.ZoomOut, "Zoom Out", CatZoom, k(new[] { "Ctrl+-", "Num-", "Num1" }));
            add(ActionIds.ZoomReset, "Fit to Window (Reset Zoom & Pan)", CatZoom, k(new[] { "Ctrl+0", "Num5" }));
            add(ActionIds.SetZoom + ":100", "Actual Pixels (1:1)", CatZoom, k(new[] { "Ctrl+1" }));
            add(ActionIds.SetZoom + ":200", "Zoom 200% of Video Pixels", CatZoom, k(new[] { "Ctrl+2" }));
            add(ActionIds.PanLeft, "Pan Left", CatZoom, k(new[] { "Num4" }));
            add(ActionIds.PanRight, "Pan Right", CatZoom, k(new[] { "Num6" }));
            add(ActionIds.PanUp, "Pan Up", CatZoom, k(new[] { "Num8" }));
            add(ActionIds.PanDown, "Pan Down", CatZoom, k(new[] { "Num2" }));
            add(ActionIds.WindowSize + ":50", "Window 50%", CatZoom, k(new[] { "Alt+1" }));
            add(ActionIds.WindowSize + ":100", "Window 100%", CatZoom, k(new[] { "Alt+2" }));
            add(ActionIds.WindowSize + ":200", "Window 200%", CatZoom, k(new[] { "Alt+3" }));
            add(ActionIds.WindowSize + ":fit", "Window Fit to Screen", CatZoom, k(new[] { "Alt+4" }));

            // Video
            add(ActionIds.CycleAspect, "Cycle Aspect Ratio", CatVideo, k(new[] { "Shift+A" }));
            add(ActionIds.ToggleFill, "Crop to Fill Window", CatVideo, k(new[] { "W" }));
            add(ActionIds.RotateCW, "Rotate 90° Clockwise", CatVideo, k(new[] { "Alt+R" }));
            add(ActionIds.RotateCCW, "Rotate 90° Counter-Clockwise", CatVideo, k(new[] { "Alt+Shift+R" }));
            add(ActionIds.FlipH, "Flip Horizontally (Mirror)", CatVideo, k(new[] { "Alt+H" }));
            add(ActionIds.FlipV, "Flip Vertically", CatVideo, k(new[] { "Alt+V" }));
            add(ActionIds.ToggleDeinterlace, "Deinterlace", CatVideo, k(new[] { "D" }));
            add(ActionIds.ToggleHwDec, "Hardware Decoding", CatVideo, k(new[] { "Ctrl+H" }));
            add(ActionIds.BrightnessUp, "Brightness +", CatVideo, k(new string[0]));
            add(ActionIds.BrightnessDown, "Brightness −", CatVideo, k(new string[0]));
            add(ActionIds.ContrastUp, "Contrast +", CatVideo, k(new string[0]));
            add(ActionIds.ContrastDown, "Contrast −", CatVideo, k(new string[0]));
            add(ActionIds.SaturationUp, "Saturation +", CatVideo, k(new string[0]));
            add(ActionIds.SaturationDown, "Saturation −", CatVideo, k(new string[0]));
            add(ActionIds.GammaUp, "Gamma +", CatVideo, k(new string[0]));
            add(ActionIds.GammaDown, "Gamma −", CatVideo, k(new string[0]));
            add(ActionIds.HueUp, "Hue +", CatVideo, k(new string[0]));
            add(ActionIds.HueDown, "Hue −", CatVideo, k(new string[0]));
            add(ActionIds.ResetVideoAdjust, "Reset Picture Adjustments", CatVideo, k(new string[0]));

            // Audio
            add(ActionIds.VolumeUp, "Volume Up", CatAudio, k(new[] { "Up" }));
            add(ActionIds.VolumeDown, "Volume Down", CatAudio, k(new[] { "Down" }));
            add(ActionIds.ToggleMute, "Mute", CatAudio, k(new[] { "M" }));
            add(ActionIds.CycleAudioTrack, "Next Audio Track", CatAudio, k(new[] { "A" }));
            add(ActionIds.AudioDelayUp, "Audio Delay +100 ms", CatAudio, k(new[] { "Ctrl+Shift+=" }));
            add(ActionIds.AudioDelayDown, "Audio Delay −100 ms", CatAudio, k(new[] { "Ctrl+Shift+-" }));
            add(ActionIds.AudioDelayReset, "Reset Audio Delay", CatAudio, k(new string[0]));
            add(ActionIds.ToggleAudioNormalize, "Normalize Volume", CatAudio, k(new[] { "Shift+N" }));
            add(ActionIds.SetAudioChannels + ":auto", "Original Channels", CatAudio, k(new string[0]));
            add(ActionIds.SetAudioChannels + ":stereo", "Downmix to Stereo", CatAudio, k(new string[0]));
            add(ActionIds.SetAudioChannels + ":mono", "Downmix to Mono", CatAudio, k(new string[0]));

            // Subtitles
            add(ActionIds.CycleSubtitle, "Next Subtitle Track", CatSubs, k(new[] { "J" }));
            add(ActionIds.CycleSubtitleBack, "Previous Subtitle Track", CatSubs, k(new[] { "Shift+J" }));
            add(ActionIds.ToggleSubtitles, "Show Subtitles", CatSubs, k(new[] { "V" }));
            add(ActionIds.SubDelayUp, "Subtitle Delay +100 ms", CatSubs, k(new[] { "X" }));
            add(ActionIds.SubDelayDown, "Subtitle Delay −100 ms", CatSubs, k(new[] { "Z" }));
            add(ActionIds.SubDelayReset, "Reset Subtitle Delay", CatSubs, k(new string[0]));
            add(ActionIds.SubScaleUp, "Larger Subtitles", CatSubs, k(new[] { "Alt+Up" }));
            add(ActionIds.SubScaleDown, "Smaller Subtitles", CatSubs, k(new[] { "Alt+Down" }));

            // Screenshot
            add(ActionIds.Screenshot, "Save Frame (Video Resolution)", CatShot, k(new[] { "S" }));
            add(ActionIds.ScreenshotDisplayed, "Save Frame as Displayed", CatShot, k(new[] { "Shift+S" }));
            add(ActionIds.CopyFrame, "Copy Frame to Clipboard", CatShot, k(new[] { "Ctrl+C" }));
            add(ActionIds.OpenScreenshotFolder, "Open Screenshot Folder", CatShot, k(new string[0]));

            // View
            add(ActionIds.ToggleFullscreen, "Fullscreen", CatView, k(new[] { "F", "F11", "Alt+Enter" }));
            add(ActionIds.ExitFullscreen, "Exit Fullscreen", CatView, k(new[] { "Esc" }));
            add(ActionIds.ToggleOnTop, "Always on Top", CatView, k(new[] { "Ctrl+T" }));
            add(ActionIds.ToggleControls, "Show Controls", CatView, k(new[] { "H" }));
            add(ActionIds.TogglePlaylist, "Show Playlist", CatView, k(new[] { "Ctrl+L" }));
            add(ActionIds.ToggleStats, "Statistics Overlay", CatView, k(new[] { "I" }));
            add(ActionIds.ShowInfoOsd, "Show Position on Screen", CatView, k(new[] { "Tab" }));
            add(ActionIds.ToggleOsd, "On-Screen Messages", CatView, k(new string[0]));
            add(ActionIds.ToggleOsdStatus, "Persistent Time & Frame Display", CatView, k(new[] { "O" }));
            add(ActionIds.ToggleBorderless, "Hide Title Bar", CatView, k(new[] { "Ctrl+B" }));
            add(ActionIds.ToggleKeepAspect, "Keep Aspect Ratio When Resizing", CatView, k(new string[0]));

            // Application
            add(ActionIds.Settings, "Settings…", CatApp, k(new[] { "Ctrl+," }));
            add(ActionIds.Shortcuts, "Keyboard Shortcuts…", CatApp, k(new[] { "F1", "?" }));
            add(ActionIds.About, "About VidFlow Player", CatApp, k(new string[0]));
            add(ActionIds.CheckForUpdates, "Check for Updates…", CatApp, k(new string[0]));
            add(ActionIds.ShowQuickStart, "Quick Start Guide", CatApp, k(new string[0]));
            add(ActionIds.ContextMenu, "Show Menu", CatApp, k(new[] { "Apps", "Shift+F10" }));
            return l;
        }
    }

    // Friendly key names <-> System.Windows.Forms.Keys (including modifier bits).
    // Accepts "Ctrl+Shift+Right", ".", "[", "{", "Num+", "PgDn", "Space", "F11", "Alt+Enter".
    // Shifted punctuation ("{", "}", "<", ">", "?", "+", "_", ":", "\"", "|", "~")
    // means Shift + the US-layout base key.
    public static class KeyNames
    {
        private static readonly Dictionary<string, Keys> Named = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            { "Space", Keys.Space }, { "Enter", Keys.Enter }, { "Return", Keys.Enter }, { "Esc", Keys.Escape }, { "Escape", Keys.Escape },
            { "Tab", Keys.Tab }, { "Backspace", Keys.Back }, { "Back", Keys.Back }, { "Delete", Keys.Delete }, { "Del", Keys.Delete },
            { "Insert", Keys.Insert }, { "Ins", Keys.Insert }, { "Home", Keys.Home }, { "End", Keys.End },
            { "PageUp", Keys.PageUp }, { "PgUp", Keys.PageUp }, { "PageDown", Keys.PageDown }, { "PgDn", Keys.PageDown },
            { "Left", Keys.Left }, { "Right", Keys.Right }, { "Up", Keys.Up }, { "Down", Keys.Down },
            { "Apps", Keys.Apps }, { "Menu", Keys.Apps }, { "Pause", Keys.Pause }, { "PrintScreen", Keys.PrintScreen },
            { "Num+", Keys.Add }, { "Num-", Keys.Subtract }, { "Num*", Keys.Multiply }, { "Num/", Keys.Divide }, { "Num.", Keys.Decimal },
            { "MediaPlayPause", Keys.MediaPlayPause }, { "MediaStop", Keys.MediaStop },
            { "MediaNextTrack", Keys.MediaNextTrack }, { "MediaPreviousTrack", Keys.MediaPreviousTrack },
            // display names produced by Format()
            { "Media Play/Pause", Keys.MediaPlayPause }, { "Media Stop", Keys.MediaStop },
            { "Media Next", Keys.MediaNextTrack }, { "Media Previous", Keys.MediaPreviousTrack },
            { "VolumeUp", Keys.VolumeUp }, { "VolumeDown", Keys.VolumeDown }, { "VolumeMute", Keys.VolumeMute },
            { "BrowserBack", Keys.BrowserBack }, { "BrowserForward", Keys.BrowserForward },
        };

        private static readonly Dictionary<char, Keys> Plain = new Dictionary<char, Keys>
        {
            { '.', Keys.OemPeriod }, { ',', Keys.Oemcomma }, { '[', Keys.OemOpenBrackets }, { ']', Keys.OemCloseBrackets },
            { '=', Keys.Oemplus }, { '-', Keys.OemMinus }, { '/', Keys.OemQuestion }, { ';', Keys.OemSemicolon },
            { '\'', Keys.OemQuotes }, { '\\', Keys.OemPipe }, { '`', Keys.Oemtilde },
        };

        private static readonly Dictionary<char, Keys> Shifted = new Dictionary<char, Keys>
        {
            { '>', Keys.OemPeriod }, { '<', Keys.Oemcomma }, { '{', Keys.OemOpenBrackets }, { '}', Keys.OemCloseBrackets },
            { '+', Keys.Oemplus }, { '_', Keys.OemMinus }, { '?', Keys.OemQuestion }, { ':', Keys.OemSemicolon },
            { '"', Keys.OemQuotes }, { '|', Keys.OemPipe }, { '~', Keys.Oemtilde },
        };

        public static bool TryParse(string text, out Keys keys)
        {
            keys = Keys.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            Keys mods = Keys.None;
            // Split modifiers off the front; the final token may itself be "+" or "-".
            while (true)
            {
                int plus = s.IndexOf('+');
                if (plus <= 0 || plus == s.Length - 1) break;
                string head = s.Substring(0, plus).Trim();
                Keys m;
                if (head.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || head.Equals("Control", StringComparison.OrdinalIgnoreCase)) m = Keys.Control;
                else if (head.Equals("Shift", StringComparison.OrdinalIgnoreCase)) m = Keys.Shift;
                else if (head.Equals("Alt", StringComparison.OrdinalIgnoreCase)) m = Keys.Alt;
                else break;
                mods |= m;
                s = s.Substring(plus + 1).Trim();
            }

            Keys key;
            if (s.Length == 1)
            {
                char c = s[0];
                if (c >= 'a' && c <= 'z') c = char.ToUpperInvariant(c);
                if (c >= 'A' && c <= 'Z') key = (Keys)c;
                else if (c >= '0' && c <= '9') key = Keys.D0 + (c - '0');
                else if (Plain.TryGetValue(c, out key)) { }
                else if (Shifted.TryGetValue(c, out key)) mods |= Keys.Shift;
                else return false;
            }
            else if (Named.TryGetValue(s, out key)) { }
            else if (s.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && s.Length == 4 && char.IsDigit(s[3]))
                key = Keys.NumPad0 + (s[3] - '0');
            else if ((s[0] == 'F' || s[0] == 'f') && s.Length <= 3)
            {
                int n;
                if (!int.TryParse(s.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 1 || n > 24) return false;
                key = Keys.F1 + (n - 1);
            }
            else
            {
                try { key = (Keys)Enum.Parse(typeof(Keys), s, true); }
                catch { return false; }
                if ((key & Keys.Modifiers) != 0) return false;
            }
            keys = key | mods;
            return true;
        }

        public static string Format(Keys keys)
        {
            Keys code = keys & Keys.KeyCode;
            bool shift = (keys & Keys.Shift) != 0;
            string name = null;

            // Show "{" rather than "Shift+[", but keep "Ctrl+Shift+=" readable as-is.
            if (shift && (keys & (Keys.Control | Keys.Alt)) == 0)
            {
                foreach (var kv in Shifted)
                    if (kv.Value == code) { name = kv.Key.ToString(); shift = false; break; }
            }
            if (name == null)
            {
                foreach (var kv in Plain)
                    if (kv.Value == code) { name = kv.Key.ToString(); break; }
            }
            if (name == null)
            {
                if (code >= Keys.A && code <= Keys.Z) name = ((char)code).ToString();
                else if (code >= Keys.D0 && code <= Keys.D9) name = ((char)('0' + (code - Keys.D0))).ToString();
                else if (code >= Keys.NumPad0 && code <= Keys.NumPad9) name = "Num" + (code - Keys.NumPad0);
                else if (code >= Keys.F1 && code <= Keys.F24) name = "F" + (code - Keys.F1 + 1);
                else
                {
                    switch (code)
                    {
                        case Keys.Escape: name = "Esc"; break;
                        case Keys.Back: name = "Backspace"; break;
                        case Keys.Enter: name = "Enter"; break;
                        case Keys.PageUp: name = "PgUp"; break;
                        case Keys.PageDown: name = "PgDn"; break;
                        case Keys.Delete: name = "Del"; break;
                        case Keys.Insert: name = "Ins"; break;
                        case Keys.Add: name = "Num+"; break;
                        case Keys.Subtract: name = "Num-"; break;
                        case Keys.Multiply: name = "Num*"; break;
                        case Keys.Divide: name = "Num/"; break;
                        case Keys.Decimal: name = "Num."; break;
                        case Keys.MediaPlayPause: name = "Media Play/Pause"; break;
                        case Keys.MediaStop: name = "Media Stop"; break;
                        case Keys.MediaNextTrack: name = "Media Next"; break;
                        case Keys.MediaPreviousTrack: name = "Media Previous"; break;
                        default: name = code.ToString(); break;
                    }
                }
            }

            string prefix = "";
            if ((keys & Keys.Control) != 0) prefix += "Ctrl+";
            if ((keys & Keys.Alt) != 0) prefix += "Alt+";
            if (shift) prefix += "Shift+";
            return prefix + name;
        }

        // Parses a comma-separated list ("Ctrl+Right, ."). The lone "," key is written as
        // "Comma" in lists to avoid ambiguity, but a bare trailing "," is also accepted.
        public static List<Keys> ParseList(string text, List<string> errors = null)
        {
            var result = new List<Keys>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (string part in SplitList(text))
            {
                string p = part.Equals("Comma", StringComparison.OrdinalIgnoreCase) ? ","
                         : part.EndsWith("+Comma", StringComparison.OrdinalIgnoreCase) ? part.Substring(0, part.Length - 5) + "," : part;
                Keys k;
                if (TryParse(p, out k)) result.Add(k);
                else if (errors != null) errors.Add(part);
            }
            return result;
        }

        public static string FormatList(IEnumerable<Keys> keys)
        {
            return string.Join(", ", keys.Select(k => { string f = Format(k); return f.EndsWith(",") ? f.Substring(0, f.Length - 1) + "Comma" : f; }));
        }

        private static IEnumerable<string> SplitList(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ',') continue;
                // A comma directly after a modifier ("Ctrl+,") or a lone comma token is the key
                // itself. Other names ending in '+' ("Num+", "+") are complete keys.
                string before = text.Substring(start, i - start).Trim();
                if (before.Length == 0 || EndsWithModifier(before)) continue;
                yield return before;
                start = i + 1;
            }
            string last = text.Substring(start).Trim();
            if (last.Length > 0) yield return last;
        }

        private static bool EndsWithModifier(string s)
        {
            foreach (var m in new[] { "Ctrl+", "Control+", "Shift+", "Alt+" })
                if (s.EndsWith(m, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // Runtime binding of action ids to handlers plus the active key map.
    public sealed class ActionRegistry
    {
        private sealed class Handler
        {
            public Action<string> Run;
            public Func<string, bool> IsChecked;
            public Func<string, bool> IsEnabled;
        }

        private readonly Dictionary<string, Handler> _plain = new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Handler> _param = new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<Keys, string>> _layers = new Dictionary<string, Dictionary<Keys, string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Keys>> _actionToKeys = new Dictionary<string, List<Keys>>(StringComparer.OrdinalIgnoreCase);

        // Raised after an action runs (for logging / automation); arg is the full id.
        public event Action<string> Executed;
        // Raised when a handler throws; MainForm shows it on the OSD.
        public event Action<string, Exception> Failed;

        public void Register(string id, Action run, Func<bool> isChecked = null, Func<bool> isEnabled = null)
        {
            _plain[id] = new Handler
            {
                Run = _ => run(),
                IsChecked = isChecked == null ? (Func<string, bool>)null : _ => isChecked(),
                IsEnabled = isEnabled == null ? (Func<string, bool>)null : _ => isEnabled(),
            };
        }

        // Handles every id of the form "<prefix>:<arg>"; the handler receives arg.
        public void RegisterParam(string prefix, Action<string> run, Func<string, bool> isChecked = null, Func<string, bool> isEnabled = null)
        {
            _param[prefix] = new Handler { Run = run, IsChecked = isChecked, IsEnabled = isEnabled };
        }

        private Handler Resolve(string id, out string arg)
        {
            arg = null;
            if (string.IsNullOrEmpty(id)) return null;
            Handler h;
            if (_plain.TryGetValue(id, out h)) return h;
            int colon = id.IndexOf(':');
            if (colon > 0 && _param.TryGetValue(id.Substring(0, colon), out h))
            {
                arg = id.Substring(colon + 1);
                return h;
            }
            return null;
        }

        public bool IsKnown(string id)
        {
            string arg;
            return Resolve(id, out arg) != null;
        }

        public bool IsEnabled(string id)
        {
            string arg;
            var h = Resolve(id, out arg);
            if (h == null) return false;
            try { return h.IsEnabled == null || h.IsEnabled(arg); }
            catch { return false; }
        }

        public bool IsChecked(string id)
        {
            string arg;
            var h = Resolve(id, out arg);
            if (h == null || h.IsChecked == null) return false;
            try { return h.IsChecked(arg); }
            catch { return false; }
        }

        public bool HasCheckState(string id)
        {
            string arg;
            var h = Resolve(id, out arg);
            return h != null && h.IsChecked != null;
        }

        public bool Execute(string id)
        {
            string arg;
            var h = Resolve(id, out arg);
            if (h == null)
            {
                Log.Warn("unknown action " + id);
                return false;
            }
            if (h.IsEnabled != null && !SafeBool(h.IsEnabled, arg)) return false;
            try
            {
                h.Run(arg);
            }
            catch (Exception ex)
            {
                Log.Error("action " + id + " failed", ex);
                var f = Failed;
                if (f != null) f(id, ex);
                return false;
            }
            var e = Executed;
            if (e != null) e(id);
            return true;
        }

        private static bool SafeBool(Func<string, bool> f, string arg)
        {
            try { return f(arg); } catch { return false; }
        }

        public static string TitleOf(string id)
        {
            var def = ActionCatalog.Find(id);
            return def != null ? def.Title : id;
        }

        // Builds the key maps (one per layer) from catalog defaults plus user overrides
        // (id -> "key, key"; an empty override unbinds). Returns human-readable problems (bad
        // key names, keys claimed by two actions of the same layer — the later override wins).
        public List<string> BuildKeyMap(IDictionary<string, string> overrides)
        {
            var problems = new List<string>();
            _layers.Clear();
            _actionToKeys.Clear();
            var bindings = new List<KeyValuePair<string, List<Keys>>>();
            foreach (var def in ActionCatalog.All)
            {
                string ov;
                if (overrides != null && overrides.TryGetValue(def.Id, out ov)) continue; // applied below so overrides win
                var list = new List<Keys>();
                foreach (var name in def.DefaultKeys)
                {
                    Keys k;
                    if (KeyNames.TryParse(name, out k)) list.Add(k);
                    else problems.Add("Built-in shortcut '" + name + "' for " + def.Id + " is invalid");
                }
                bindings.Add(new KeyValuePair<string, List<Keys>>(def.Id, list));
            }
            if (overrides != null)
            {
                foreach (var kv in overrides)
                {
                    var errs = new List<string>();
                    var list = KeyNames.ParseList(kv.Value, errs);
                    foreach (var e in errs) problems.Add("Unknown key '" + e + "' for " + kv.Key);
                    bindings.Add(new KeyValuePair<string, List<Keys>>(kv.Key, list));
                }
            }
            foreach (var b in bindings)
            {
                var map = LayerMap(LayerOf(b.Key));
                var kept = new List<Keys>();
                foreach (var k in b.Value)
                {
                    string prev;
                    if (map.TryGetValue(k, out prev))
                    {
                        problems.Add(KeyNames.Format(k) + " moved from " + prev + " to " + b.Key);
                        List<Keys> prevKeys;
                        if (_actionToKeys.TryGetValue(prev, out prevKeys)) prevKeys.Remove(k);
                    }
                    map[k] = b.Key;
                    kept.Add(k);
                }
                _actionToKeys[b.Key] = kept;
            }
            foreach (var p in problems) Log.Warn("keymap: " + p);
            return problems;
        }

        // Layer an action's keys live in ("" = global).
        public static string LayerOf(string id)
        {
            var def = ActionCatalog.Find(id);
            return def != null && def.Layer != null ? def.Layer : "";
        }

        private Dictionary<Keys, string> LayerMap(string layer)
        {
            Dictionary<Keys, string> map;
            if (!_layers.TryGetValue(layer ?? "", out map))
            {
                map = new Dictionary<Keys, string>();
                _layers[layer ?? ""] = map;
            }
            return map;
        }

        // Global key lookup.
        public string FindActionForKey(Keys keyData)
        {
            return FindActionForKey(keyData, "");
        }

        // Lookup in one layer only ("" = global). MainForm asks the Cull layer first while Cull mode is on.
        public string FindActionForKey(Keys keyData, string layer)
        {
            Dictionary<Keys, string> map;
            string id;
            return _layers.TryGetValue(layer ?? "", out map) && map.TryGetValue(keyData, out id) ? id : null;
        }

        public IList<Keys> GetKeys(string id)
        {
            List<Keys> list;
            return _actionToKeys.TryGetValue(id, out list) ? (IList<Keys>)list.AsReadOnly() : new Keys[0];
        }

        // ". / Ctrl+Right" — empty when unbound. maxKeys limits menu clutter; " / " keeps
        // punctuation keys ("," ".") readable; media keys are omitted (they label themselves).
        public string ShortcutText(string id, int maxKeys = 2)
        {
            var keys = GetKeys(id).Where(k => !IsDeviceKey(k & Keys.KeyCode));
            return string.Join(" / ", keys.Take(maxKeys).Select(KeyNames.Format));
        }

        private static bool IsDeviceKey(Keys code)
        {
            return (code >= Keys.BrowserBack && code <= Keys.LaunchApplication2);
        }
    }
}
