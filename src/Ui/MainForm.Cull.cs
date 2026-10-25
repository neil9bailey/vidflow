using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;
using VidFlow.Ui.Dialogs;

namespace VidFlow.Ui
{
    // Cull workflow: pick / reject / rate clip by clip (a key layer while Cull mode is on), a decisions
    // index that survives restarts, bins on hotkeys, F2 rename, the "Apply Decisions" batch and a
    // multi-level undo for all of it. Decisions never touch the files; only bins, rename and Apply move
    // them, always after mpv and the preview have let go of them.
    public sealed partial class MainForm
    {
        private const int CullSaveDelayMs = 800, CullReleaseTimeoutMs = 6000, CullUndoLimit = 500;
        private const string CullPartialSuffix = ".vidflow-partial";
        private const string CullOverlayId = "4711";   // mpv osd-overlay id of the fullscreen decision indicator
        private const string CullRepeatTestPrefix = "CullAsRepeat"; // automation only: "CullAsRepeat:<action>" runs it as a key auto-repeat
        private static readonly string[] CullFilterIds = { "all", "picked", "rejected", "undecided", "rated" };
        // Files that belong to a clip of the same base name and travel with it (subtitles, Lightroom XMP,
        // GoPro low-res proxy and thumbnail).
        private static readonly string[] CullSidecarExtensions = { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".xmp", ".lrf", ".thm" };

        private abstract class CullOp
        {
            public string Label;      // "✓ Pick · clip03.webm", "Move to Bin 2 · clip03.webm", ...
        }

        private sealed class CullDecisionOp : CullOp
        {
            public string Path;
            public CullMark Before, After;
        }

        private enum CullJobKind { Move, Copy, Recycle, Delete }

        // What a finished job does to the playlist.
        private enum CullListEffect { None, Remove, Replace, Insert }

        private sealed class CullJob
        {
            public CullJobKind Kind;
            public string From, To;
            public long Size;
            public CullListEffect Effect;
            public int ListIndex = -1;    // playlist position the item had (Remove) or gets back (Insert)
            public bool KeepName;         // a rename: fail instead of picking "name (2)" when the name is taken
            public bool Done, Recycled;   // Recycled: the Recycle Bin really took it (its item count went up)
            public string Error;
            // Sidecar files (subtitles, XMP, GoPro LRF/THM). Before the job runs: null = find them next to
            // From, otherwise exactly these (from, to; to = null for a recycle). After it ran: the ones it
            // actually moved, copied or recycled.
            public List<KeyValuePair<string, string>> Sidecars;
            public List<string> CreatedDirs;        // folders this job created (deepest first)
            public List<string> RemoveDirsIfEmpty;  // an undo: folders to remove afterwards when they are empty

            public CullJob Clone()
            {
                return new CullJob { Kind = Kind, From = From, To = To, Size = Size, Effect = Effect, ListIndex = ListIndex, KeepName = KeepName };
            }

            // The job that puts things back; null when that is impossible (a delete).
            public CullJob Reverse()
            {
                switch (Kind)
                {
                    case CullJobKind.Move:
                        var effect = Effect == CullListEffect.Remove ? CullListEffect.Insert
                                   : Effect == CullListEffect.Insert ? CullListEffect.Remove : Effect;
                        return new CullJob
                        {
                            Kind = CullJobKind.Move, From = To, To = From, Size = Size, Effect = effect, ListIndex = ListIndex, KeepName = true,
                            Sidecars = Swapped(Sidecars), RemoveDirsIfEmpty = CreatedDirs,
                        };
                    case CullJobKind.Copy:
                    {
                        var copies = new List<KeyValuePair<string, string>>();
                        if (Sidecars != null)
                            foreach (var p in Sidecars) copies.Add(new KeyValuePair<string, string>(p.Value, null));
                        return new CullJob { Kind = CullJobKind.Recycle, From = To, Size = Size, Sidecars = copies, RemoveDirsIfEmpty = CreatedDirs };
                    }
                    default:
                        return null;
                }
            }

            private static List<KeyValuePair<string, string>> Swapped(List<KeyValuePair<string, string>> pairs)
            {
                if (pairs == null) return null;
                var back = new List<KeyValuePair<string, string>>();
                foreach (var p in pairs) back.Add(new KeyValuePair<string, string>(p.Value, p.Key));
                return back;
            }
        }

        private sealed class CullFileOp : CullOp
        {
            public List<CullJob> Jobs = new List<CullJob>();
            public string DoneText;       // OSD after it ran (null = counted summary, for Apply)
            public string FocusPath;      // undo brings this clip back on screen (it was playing)
            public bool AdvanceFirst;     // the playing clip leaves the list before the move (bins)
            public int Generation = -1;   // Playlist.Generation when it was made: list positions only mean something in that list
        }

        private enum CullOpMode { Do, Undo, Redo }

        // Position to restore once a released file is reloaded (rename / undo of the playing file).
        private sealed class CullResume
        {
            public string Path;
            public double Position;
            public bool Paused;
            public bool Armed;     // the reload was sent
            public bool Started;   // ...and its START_FILE arrived: only its FILE_LOADED may use this
        }

        private sealed class CullPlan
        {
            public readonly List<CullJob> Jobs = new List<CullJob>();
            public readonly CullCommitSummary Summary = new CullCommitSummary();
        }

        private CullStore _cullStore;
        private string _cullFilter = "all";
        private readonly List<CullOp> _cullUndo = new List<CullOp>(), _cullRedo = new List<CullOp>();
        private Timer _cullSaveTimer;
        private bool _cullJobRunning, _cullPushPosted;
        private volatile bool _cullStopJobs;            // set at exit: background moves stop (a copy is abandoned cleanly)
        private System.Threading.Thread _cullJobThread;
        private CullResume _cullResume;
        private string _cullLastResult = "";
        private CullCommitSummary _cullLastSummary;   // what the last Apply planned (automation dump)
        private ContextMenuStrip _cullBarMenu;
        private Func<BarButton, string> _cullTooltip;
        private bool _cullPanelOffered;               // entering cull mode opened the playlist once already
        private string _cullIndicator;                // ASS text of the fullscreen decision indicator now shown (null = none)

        private CullStore CullIndex
        {
            get { return _cullStore ?? (_cullStore = new CullStore(_settings.Directory ?? Settings.DefaultDirectory)); }
        }

        // The clip on screen (a local file), or null.
        private string CullCurrentPath
        {
            get
            {
                if (!HasMedia) return null;
                string p = _currentFile ?? _st.Path;
                return string.IsNullOrEmpty(p) || Playlist.IsUrl(p) ? null : p;
            }
        }

        private CullMark CullMarkOf(string path)
        {
            return string.IsNullOrEmpty(path) || Playlist.IsUrl(path) ? CullMark.None : CullIndex.Get(path);
        }

        private CullMark CullCurrentMark()
        {
            string p = CullCurrentPath;
            return p == null ? CullMark.None : CullMarkOf(p);
        }

        // ---- hooks ------------------------------------------------------------------------

        partial void CullMenus()
        {
            _playlistPanel.MarkProvider = CullMarkOf;
            _playlistPanel.CullFilterRequested += f => Run(ActionIds.CullFilter + ":" + f);
            _bar.ButtonClicked += OnCullBarButton;
            _menus.CullBinLabelProvider = CullBinMenuLabel;
            CullLabelUndoItems(_contextMenu);
            CullSyncSkipFolder();
        }

        partial void CullInit()
        {
            _cullSaveTimer = new Timer { Interval = CullSaveDelayMs };
            _cullSaveTimer.Tick += (s, e) =>
            {
                _cullSaveTimer.Stop();
                if (_cullStore != null) _cullStore.Save();
            };
            _actions.Failed += CullActionFailed;
            // Entering/leaving fullscreen resizes the window and hides/shows the bar: the indicator follows.
            SizeChanged += (s, e) => CullUpdateIndicator();
            _bar.VisibleChanged += (s, e) => CullUpdateIndicator();
            CullSyncSkipFolder();
            CullWrapTooltip();
            PushCullState();
        }

        partial void CullRegisterActions()
        {
            Action<string, Action, Func<bool>, Func<bool>> r = (id, run, chk, en) => _actions.Register(id, run, chk, en);
            Func<bool> local = () => CullCurrentPath != null;
            Func<bool> files = () => CullCurrentPath != null && !_cullJobRunning;
            // A held key repeats ~30 times a second: a decision, bin move, rename or Apply takes one
            // deliberate press each (holding X must not reject a run of clips).
            Func<Action, Action> once = a => () => { if (!_keyIsAutoRepeat) a(); };
            Func<Action<string>, Action<string>> onceArg = a => arg => { if (!_keyIsAutoRepeat) a(arg); };

            r(ActionIds.ToggleCullMode, once(ToggleCullMode), () => _settings.CullMode, null);
            r(ActionIds.CullPick, once(() => CullSetFlag(1)), () => CullCurrentMark().IsPick, local);
            r(ActionIds.CullReject, once(() => CullSetFlag(-1)), () => CullCurrentMark().IsReject, local);
            r(ActionIds.CullUnflag, once(() => CullSetFlag(0)), null, local);
            _actions.RegisterParam(ActionIds.CullRate, onceArg(arg => CullSetRating(ParseInt(arg))),
                arg => { int n = ParseInt(arg); return n > 0 && n == CullCurrentMark().Rating; },
                arg => { int n = ParseInt(arg); return n >= 0 && n <= 5 && local(); });
            r(ActionIds.CullNextUndecided, once(CullNextUndecided), null, () => _playlist.Count > 0);
            r(ActionIds.CullUndo, () => CullUndoRedo(true), null, () => _cullUndo.Count > 0 && !_cullJobRunning);
            r(ActionIds.CullRedo, () => CullUndoRedo(false), null, () => _cullRedo.Count > 0 && !_cullJobRunning);
            r(ActionIds.CullCommit, once(CullCommitAction), null, () => _playlist.Count > 0 && !_cullJobRunning);
            // Automation only: the same batch without the dialog (rejects on drives without a Recycle Bin are skipped).
            _actions.RegisterParam(ActionIds.CullCommit, onceArg(arg => CullCommitWithoutDialog()), null,
                arg => _options.Automation && arg == "apply" && _playlist.Count > 0 && !_cullJobRunning);
            _actions.RegisterParam(ActionIds.CullToBin, onceArg(arg => CullMoveToBin(ParseInt(arg))), null,
                arg => { int n = ParseInt(arg); return n >= 1 && n <= 5 && files(); });
            r(ActionIds.CullAutoAdvance, () =>
            {
                _settings.CullAutoAdvance = !_settings.CullAutoAdvance;
                _settings.Save();
                ShowOsd(_settings.CullAutoAdvance ? "Advance after each decision: on" : "Advance after each decision: off");
            }, () => _settings.CullAutoAdvance, null);
            // Decision filters belong to cull mode (leaving it clears the filter).
            _actions.RegisterParam(ActionIds.CullFilter, CullSetFilter,
                arg => CullNormalizeFilter(arg) == _cullFilter,
                arg => { string f = CullNormalizeFilter(arg); return f != null && (_settings.CullMode || f == "all"); });
            r(ActionIds.RenameFile, once(RenameFileAction), null, files);
            // "RenameFile:<new name>" renames without asking (automation, custom bindings).
            _actions.RegisterParam(ActionIds.RenameFile, onceArg(CullRenameCurrentTo), null, arg => files() && !string.IsNullOrWhiteSpace(arg));

            // While a decision filter is on, file navigation stays inside it; otherwise the usual behaviour.
            r(ActionIds.NextFile, () => CullNavigate(true), null, () => CullCanNavigate(true));
            r(ActionIds.PrevFile, () => CullNavigate(false), null, () => CullCanNavigate(false));
            r(ActionIds.FirstFile, () => CullNavigateEnd(true), null, () => _playlist.Count > 0);
            r(ActionIds.LastFile, () => CullNavigateEnd(false), null, () => _playlist.Count > 0);

            // Automation only: runs an action as if its key were auto-repeating (the pipe cannot send the
            // repeat flag), so tests can check the guard above.
            _actions.RegisterParam(CullRepeatTestPrefix, arg =>
            {
                bool was = _keyIsAutoRepeat;
                _keyIsAutoRepeat = true;
                try { _actions.Execute(arg); }
                finally { _keyIsAutoRepeat = was; }
            }, null, arg => _options.Automation && _actions.IsKnown(arg) && !arg.StartsWith(CullRepeatTestPrefix + ":", StringComparison.OrdinalIgnoreCase));
        }

        partial void CullFileStarted()
        {
            // A file that was already on its way when the release began (its events still arriving)
            // must not take the position meant for the reload.
            var r = _cullResume;
            if (r != null && r.Armed)
            {
                if (SamePath(r.Path, _currentFile ?? _playlist.Current)) r.Started = true;
                else _cullResume = null;
            }
            PushCullState();
        }

        partial void CullFileLoaded()
        {
            var r = _cullResume;
            if (r != null && r.Started)
            {
                _cullResume = null;
                if (SamePath(r.Path, _currentFile ?? _st.Path))
                {
                    if (r.Position > 0.0005) _mpv.CommandAsync("no-osd", "seek", Num(r.Position), "absolute+exact");
                    SetPause(r.Paused);
                }
            }
            if (_cullIndicator != null) _cullIndicator = "\0"; // send it again (an engine restart drops overlays)
            PushCullState();
        }

        partial void CullPlaylistChanged()
        {
            // Coalesced: a batch (Apply, a big append) raises many changes in one go.
            if (_cullPushPosted || !IsHandleCreated || _closing) return;
            _cullPushPosted = true;
            BeginInvoke((MethodInvoker)PushCullState);
        }

        partial void CullApplySettings()
        {
            CullSyncSkipFolder();
            CullWrapTooltip();
            PushCullState();
        }

        partial void CullDump(Dictionary<string, object> d)
        {
            int picked, rejected, rated, total;
            CullCount(out picked, out rejected, out rated, out total);
            var m = CullCurrentMark();
            var marks = new List<object>();
            foreach (var item in _playlist.Items)
            {
                var im = CullMarkOf(item);
                marks.Add(new[] { im.Flag, im.Rating });
            }
            d["cull"] = new Dictionary<string, object>
            {
                { "mode", _settings.CullMode },
                { "filter", _cullFilter },
                { "autoAdvance", _settings.CullAutoAdvance },
                { "flag", m.Flag },
                { "rating", m.Rating },
                { "picked", picked },
                { "rejected", rejected },
                { "undecided", total - picked - rejected },
                { "rated", rated },
                { "total", total },
                { "progressText", CullProgressText() },
                { "indicator", _cullIndicator },
                { "lastPlan", _cullLastSummary == null ? null : new Dictionary<string, object>
                    {
                        { "picks", _cullLastSummary.PickCount }, { "rejects", _cullLastSummary.RejectCount },
                        { "alreadyKept", _cullLastSummary.AlreadyKept }, { "alreadyRejected", _cullLastSummary.AlreadyRejected },
                        { "permanentDeletes", _cullLastSummary.PermanentDeletes },
                    }
                },
                { "marks", marks },
                { "undo", _cullUndo.Count },
                { "redo", _cullRedo.Count },
                { "undoLabel", _cullUndo.Count > 0 ? _cullUndo[_cullUndo.Count - 1].Label : null },
                { "busy", _cullJobRunning },
                { "lastResult", _cullLastResult },
                { "storePath", CullIndex.FilePath },
                { "storeDirty", CullIndex.IsDirty },
                { "panelVisible", _playlistPanel.VisibleCount },
                { "panelSummary", _playlistPanel.SummaryText },
                { "panelFilter", _playlistPanel.CullFilter },
                { "bar", new Dictionary<string, object>
                    {
                        { "mode", _bar.CullMode }, { "flag", _bar.CullFlag }, { "rating", _bar.CullRating }, { "progress", _bar.CullProgressText },
                    }
                },
            };
        }

        partial void CullClosing()
        {
            _cullStopJobs = true;
            var t = _cullJobThread;
            if (t != null && t.IsAlive) t.Join(4000);
            if (_cullSaveTimer != null)
            {
                _cullSaveTimer.Stop();
                _cullSaveTimer.Dispose();
            }
            if (_cullStore != null) _cullStore.Save();
            if (_cullBarMenu != null) _cullBarMenu.Dispose();
        }

        // ---- state push -------------------------------------------------------------------

        private void PushCullState()
        {
            _cullPushPosted = false;
            if (_closing) return;
            bool mode = _settings.CullMode;
            var m = CullCurrentMark();
            _bar.CullMode = mode;
            _bar.CullFlag = m.Flag;
            _bar.CullRating = m.Rating;
            _bar.CullProgressText = mode ? CullShortProgressText() : "";
            _playlistPanel.CullMode = mode;
            _playlistPanel.CullFilter = _cullFilter;
            _playlistPanel.RefreshMarks();
            CullUpdateIndicator();
        }

        // Fullscreen shows no bar: a small lasting corner note says what the clip on screen is
        // ("✓ Pick ★★★", "✕ Reject", "– undecided"), drawn by mpv's OSD so it is part of the video.
        private void CullUpdateIndicator()
        {
            if (_closing) return;
            string text = null;
            if (_settings.CullMode && _fullscreen && CullCurrentPath != null) text = CullIndicatorAss(CullCurrentMark());
            if (text == _cullIndicator) return;
            if (_mpv == null || !_mpv.IsAlive || !_mpv.IsInitialized) return;
            _cullIndicator = text;
            // osd-overlay id format data res_x res_y z hidden compute_bounds (res_x 0 = from the aspect).
            if (text == null) _mpv.CommandAsync("osd-overlay", CullOverlayId, "none", "", "0", "720", "0", "no", "no");
            else _mpv.CommandAsync("osd-overlay", CullOverlayId, "ass-events", text, "0", "720", "0", "no", "no");
        }

        // Top-right corner, small, in the chip's colours (ASS colours are &HBBGGRR&).
        private static string CullIndicatorAss(CullMark m)
        {
            string flag = m.IsPick ? "{\\1c&H78C858&}✓ Pick" : m.IsReject ? "{\\1c&H5C5CEE&}✕ Reject" : "{\\1c&HC8C8C8&}– undecided";
            string stars = m.Rating > 0 ? "{\\1c&H40C4FF&} " + new string('★', Math.Max(0, Math.Min(5, m.Rating))) : "";
            return "{\\an9\\fs22\\bord1.5\\shad0\\3c&H000000&}" + flag + stars;
        }

        private void CullCount(out int picked, out int rejected, out int rated, out int total)
        {
            picked = rejected = rated = total = 0;
            foreach (var item in _playlist.Items)
            {
                if (Playlist.IsUrl(item)) continue;
                total++;
                var m = CullMarkOf(item);
                if (m.IsPick) picked++;
                else if (m.IsReject) rejected++;
                if (m.Rating > 0) rated++;
            }
        }

        private string CullProgressText()
        {
            int picked, rejected, rated, total;
            CullCount(out picked, out rejected, out rated, out total);
            return total == 0 ? "" : (picked + rejected) + " / " + total + " decided";
        }

        // The bar chip has little room: "12/40".
        private string CullShortProgressText()
        {
            int picked, rejected, rated, total;
            CullCount(out picked, out rejected, out rated, out total);
            return total == 0 ? "" : (picked + rejected).ToString(Inv) + "/" + total.ToString(Inv);
        }

        private int CullUndecidedCount()
        {
            int picked, rejected, rated, total;
            CullCount(out picked, out rejected, out rated, out total);
            return total - picked - rejected;
        }

        // A decision or file change happened: save soon, refresh the bar and the panel.
        private void CullChanged()
        {
            if (_cullSaveTimer != null)
            {
                _cullSaveTimer.Stop();
                _cullSaveTimer.Start();
            }
            else CullIndex.Save();
            PushCullState();
        }

        private void CullWrapTooltip()
        {
            var current = _bar.TooltipProvider;
            if (current != null && ReferenceEquals(current, _cullTooltip)) return;
            var inner = current;
            _cullTooltip = b => b == BarButton.Cull ? CullTooltipText() : (inner != null ? inner(b) : null);
            _bar.TooltipProvider = _cullTooltip;
        }

        private string CullTooltipText()
        {
            var m = CullCurrentMark();
            string state = m.IsPick ? "✓ Pick" : m.IsReject ? "✕ Reject" : "Undecided";
            if (m.Rating > 0) state += " " + CullStars(m.Rating);
            string progress = CullProgressText();
            return "Cull: " + state + (progress.Length > 0 ? " · " + progress : "") + " — click for cull actions, right-click to clear the flag";
        }

        // ---- decisions --------------------------------------------------------------------

        private void ToggleCullMode()
        {
            _settings.CullMode = !_settings.CullMode;
            _settings.Save();
            CullSyncSkipFolder();
            if (_settings.CullMode)
            {
                ShowOsd("Cull mode: " + CullKey(ActionIds.CullPick, "P") + " pick · " + CullKey(ActionIds.CullReject, "X") + " reject · " +
                        CullKey(ActionIds.CullRate + ":1", "1") + "–" + CullKey(ActionIds.CullRate + ":5", "5") + " stars · " +
                        CullKey(ActionIds.CullNextUndecided, "N") + " next undecided", false, 3000);
                // The panel carries the decision badges and the "12 ✓ · 5 ✕ · 23 left" summary: open it the
                // first time, when the window has room for it next to the video.
                if (!_cullPanelOffered)
                {
                    _cullPanelOffered = true;
                    if (!_settings.ShowPlaylist && !_fullscreen && WindowState != FormWindowState.Minimized && ClientSize.Width >= Px(720))
                        SetPlaylistVisible(true);
                }
            }
            else
            {
                _cullFilter = "all";
                ShowOsd("Cull mode off");
            }
            PushCullState();
        }

        // In cull mode a folder that is opened recursively leaves out the Keep / Rejected / bin folders,
        // so clips already sorted do not come back into the cull list.
        private void CullSyncSkipFolder()
        {
            _playlist.SkipFolder = _settings.CullMode ? (Func<string, bool>)CullSkipFolder : null;
        }

        private bool CullSkipFolder(string folder)
        {
            if (!_settings.CullMode || string.IsNullOrEmpty(folder)) return false;
            foreach (var t in CullTargetSettings())
            {
                string rel = CullRelativeSetting(t.Key, t.Value);
                if (rel != null)
                {
                    if (CullEndsWithFolder(folder, rel)) return true;
                    continue;
                }
                string s = Environment.ExpandEnvironmentVariables((t.Key ?? "").Trim().Trim('"'));
                try
                {
                    if (s.Length > 0 && CullSameFolder(Path.GetFullPath(s), folder)) return true;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
                {
                }
            }
            return false;
        }

        // (setting, fallback name) of every folder cull mode sorts clips into: Keep, Rejected, bins 1-5.
        private List<KeyValuePair<string, string>> CullTargetSettings()
        {
            var list = new List<KeyValuePair<string, string>>();
            if (CullChoice(_settings.CullPickAction, "Move", "Copy", "None") != "None")
                list.Add(new KeyValuePair<string, string>(_settings.CullKeepFolder ?? "", "Keep"));
            if (CullChoice(_settings.CullRejectAction, "RecycleBin", "Folder", "None") == "Folder")
                list.Add(new KeyValuePair<string, string>(_settings.CullRejectFolder ?? "", "Rejected"));
            for (int n = 1; n <= 5; n++)
            {
                string b = CullBinFolder(n);
                if (!string.IsNullOrWhiteSpace(b)) list.Add(new KeyValuePair<string, string>(b, null));
            }
            return list;
        }

        private string CullKey(string id, string fallback)
        {
            string k = _actions.ShortcutText(id, 1);
            return k.Length > 0 ? k : fallback;
        }

        private static string CullStars(int n)
        {
            n = Math.Max(0, Math.Min(5, n));
            return new string('★', n) + new string('☆', 5 - n);
        }

        private static string CullMarkText(CullMark m)
        {
            string flag = m.IsPick ? "✓ Pick" : m.IsReject ? "✕ Reject" : "Unflagged";
            return m.Rating > 0 ? flag + " " + CullStars(m.Rating) : flag;
        }

        // P / X: set the flag, or take it back when the clip already has it; U clears it.
        private void CullSetFlag(int flag)
        {
            string path = CullCurrentPath;
            if (path == null) return;
            var before = CullMarkOf(path);
            if (flag == 0 && before.Flag == 0)
            {
                ShowOsd("Not flagged");
                return;
            }
            int newFlag = flag != 0 && before.Flag == flag ? 0 : flag;
            if (!CullRecord(path, before, before.WithFlag(newFlag))) return;
            string text = newFlag > 0 ? "✓ Pick" : newFlag < 0 ? "✕ Reject" : flag == 0 ? "Flag cleared" : flag > 0 ? "Pick removed" : "Reject removed";
            if (newFlag != 0 && _settings.CullAutoAdvance) CullAdvance(text);
            else ShowOsd(text);
        }

        private void CullSetRating(int stars)
        {
            string path = CullCurrentPath;
            if (path == null || stars < 0 || stars > 5) return;
            var before = CullMarkOf(path);
            if (!CullRecord(path, before, before.WithRating(stars))) return;
            ShowOsd(stars == 0 ? "Rating cleared" : CullStars(stars));
        }

        private bool CullRecord(string path, CullMark before, CullMark after)
        {
            if (before == after) return true;
            if (!CullIndex.Set(path, after))
            {
                ShowOsd("Error: cannot read " + FileNameOf(path) + " to remember the decision", true);
                return false;
            }
            CullPushUndo(new CullDecisionOp { Label = CullMarkText(after) + " · " + FileNameOf(path), Path = path, Before = before, After = after });
            CullChanged();
            return true;
        }

        private void CullPushUndo(CullOp op)
        {
            _cullUndo.Add(op);
            if (_cullUndo.Count > CullUndoLimit) _cullUndo.RemoveAt(0);
            _cullRedo.Clear();
        }

        // After a pick or reject: the next clip the decision filter shows, in play order (shuffle
        // included), never wrapping. At the end the clip stays and the OSD says what is left.
        private void CullAdvance(string decided)
        {
            if (CullMoveToNextMatching())
            {
                ShowOsd(decided);
                PlayCurrent(false);
                return;
            }
            int left = CullUndecidedCount();
            ShowOsd(decided + " · " + (left == 0 ? "All clips decided"
                : "Last clip · " + left + " undecided (" + CullKey(ActionIds.CullNextUndecided, "N") + " to jump)"), false, 2500);
        }

        private bool CullPasses(string path)
        {
            if (_cullFilter == "all") return true;
            var m = CullMarkOf(path);
            switch (_cullFilter)
            {
                case "picked": return m.IsPick;
                case "rejected": return m.IsReject;
                case "undecided": return m.Flag == 0;
                case "rated": return m.Rating > 0;
                default: return true;
            }
        }

        private bool CullMoveToNextMatching()
        {
            int n = _playlist.Count;
            if (n == 0) return false;
            if (!_playlist.Shuffle)
            {
                for (int i = _playlist.Index + 1; i < n; i++)
                    if (CullPasses(_playlist.Items[i])) return _playlist.MoveTo(i);
                return false;
            }
            // Shuffle: walk the random order (it cannot be read ahead); go back if nothing matches.
            int start = _playlist.Index;
            for (int k = 0; k < n && _playlist.MoveNext(false); k++)
                if (CullPasses(_playlist.Current)) return true;
            if (_playlist.Index != start) _playlist.MoveTo(start);
            return false;
        }

        // N: the next clip without a pick/reject, in list order, wrapping around.
        private void CullNextUndecided()
        {
            int n = _playlist.Count, cur = _playlist.Index;
            for (int k = 1; k <= n; k++)
            {
                int i = ((cur < 0 ? -1 : cur) + k) % n;
                if (i == cur) break;
                string item = _playlist.Items[i];
                if (Playlist.IsUrl(item) || CullMarkOf(item).Flag != 0) continue;
                PlayIndex(i, false);
                return;
            }
            bool currentOpen = cur >= 0 && CullMarkOf(_playlist.Items[cur]).Flag == 0 && !Playlist.IsUrl(_playlist.Items[cur]);
            ShowOsd(currentOpen ? "This is the last undecided clip" : "All clips decided");
        }

        private static string CullNormalizeFilter(string arg)
        {
            string f = (arg ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(CullFilterIds, f) >= 0 ? f : null;
        }

        private void CullSetFilter(string arg)
        {
            string f = CullNormalizeFilter(arg) ?? "all";
            _cullFilter = f;
            PushCullState();
            int shown = _playlistPanel.VisibleCount;
            switch (f)
            {
                case "picked": ShowOsd("Showing picks (" + shown + ")"); break;
                case "rejected": ShowOsd("Showing rejects (" + shown + ")"); break;
                case "undecided": ShowOsd("Showing undecided clips (" + shown + ")"); break;
                case "rated": ShowOsd("Showing rated clips (" + shown + ")"); break;
                default: ShowOsd("Showing all clips"); break;
            }
        }

        private bool CullFilterActive
        {
            get { return _settings.CullMode && _cullFilter != "all"; }
        }

        // Next/Previous File: inside the decision filter while one is on (wrapping with Repeat All, like
        // the plain behaviour); otherwise exactly the usual NextFile.
        private void CullNavigate(bool forward)
        {
            if (!CullFilterActive)
            {
                NextFile(forward);
                return;
            }
            bool wrap = _settings.Repeat == RepeatMode.All;
            if (_playlist.Shuffle)
            {
                // The random order cannot be read ahead: walk it, and go back if nothing matches.
                int start = _playlist.Index, n = _playlist.Count;
                for (int k = 0; k < n && (forward ? _playlist.MoveNext(wrap) : _playlist.MovePrevious(wrap)); k++)
                {
                    if (_playlist.Index == start) break;
                    if (!CullPasses(_playlist.Current)) continue;
                    PlayCurrent(false);
                    return;
                }
                if (start >= 0 && _playlist.Index != start) _playlist.MoveTo(start);
            }
            else
            {
                int i = CullFindFiltered(forward, wrap);
                if (i >= 0)
                {
                    PlayIndex(i, false);
                    return;
                }
            }
            ShowOsd((forward ? "Last clip in this view (" : "First clip in this view (") + CullFilterName() + ")");
        }

        private bool CullCanNavigate(bool forward)
        {
            bool wrap = _settings.Repeat == RepeatMode.All;
            if (!CullFilterActive || _playlist.Shuffle) return forward ? _playlist.HasNext(wrap) : _playlist.HasPrevious(wrap);
            return CullFindFiltered(forward, wrap) >= 0;
        }

        // The next/previous list index the filter shows (list order, wrapping when asked), or -1.
        private int CullFindFiltered(bool forward, bool wrap)
        {
            int n = _playlist.Count, cur = _playlist.Index;
            if (n == 0) return -1;
            int from = cur >= 0 ? cur : (forward ? -1 : n);
            for (int k = 1; k <= n; k++)
            {
                int i = forward ? from + k : from - k;
                if (i < 0 || i >= n)
                {
                    if (!wrap) return -1;
                    i = ((i % n) + n) % n;
                }
                if (i == cur) return -1;
                if (CullPasses(_playlist.Items[i])) return i;
            }
            return -1;
        }

        // First/Last File: the first/last clip the filter shows.
        private void CullNavigateEnd(bool first)
        {
            if (!CullFilterActive)
            {
                if ((first ? _playlist.MoveFirst() : _playlist.MoveLast()) || _playlist.Count > 0) PlayCurrent(false);
                return;
            }
            int n = _playlist.Count;
            for (int k = 0; k < n; k++)
            {
                int i = first ? k : n - 1 - k;
                if (!CullPasses(_playlist.Items[i])) continue;
                PlayIndex(i, false);
                return;
            }
            ShowOsd("No clips in this view (" + CullFilterName() + ")");
        }

        private string CullFilterName()
        {
            switch (_cullFilter)
            {
                case "picked": return "picks";
                case "rejected": return "rejects";
                case "undecided": return "undecided";
                case "rated": return "rated";
                default: return "all clips";
            }
        }

        // ---- undo / redo ------------------------------------------------------------------

        private void CullUndoRedo(bool undo)
        {
            var from = undo ? _cullUndo : _cullRedo;
            if (from.Count == 0 || _cullJobRunning) return;
            var op = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);

            var d = op as CullDecisionOp;
            if (d != null)
            {
                if (!CullIndex.Set(d.Path, undo ? d.Before : d.After))
                {
                    ShowOsd("Error: " + FileNameOf(d.Path) + " is no longer there", true);
                    CullChanged();
                    return;
                }
                (undo ? _cullRedo : _cullUndo).Add(op);
                CullChanged();
                CullFocus(d.Path);
                ShowOsd((undo ? "Undo: " : "Redo: ") + d.Label);
                return;
            }

            var f = (CullFileOp)op;
            if (undo)
            {
                var back = new List<CullJob>();
                for (int i = f.Jobs.Count - 1; i >= 0; i--)
                {
                    var rev = f.Jobs[i].Reverse();
                    if (rev != null) back.Add(rev);
                }
                if (back.Count == 0)
                {
                    int inBin, gone;
                    CullCountLost(f.Jobs, out inBin, out gone);
                    string why = gone == 0 ? "files sent to the Recycle Bin can only be restored from the Recycle Bin"
                               : inBin == 0 ? "the files were deleted permanently"
                               : "files sent to the Recycle Bin can only be restored from there, and the others were deleted permanently";
                    ShowOsd("Cannot undo “" + f.Label + "”: " + why, true, 3500);
                    return;
                }
                CullStartJobs(f, back, CullOpMode.Undo, true);
                return;
            }
            var again = new List<CullJob>();
            foreach (var j in f.Jobs)
                if (j.Kind == CullJobKind.Move || j.Kind == CullJobKind.Copy) again.Add(j.Clone()); // deletes were never undone
            if (again.Count == 0) return;
            if (f.AdvanceFirst) CullAdvanceAway(again);
            CullStartJobs(f, again, CullOpMode.Redo, !f.AdvanceFirst);
        }

        // Brings a clip of the list on screen (undo returns to the clip it changed).
        private void CullFocus(string path)
        {
            if (path == null || SamePath(path, CullCurrentPath)) return;
            int i = _playlist.IndexOf(path);
            if (i >= 0) PlayIndex(i, false);
        }

        // ---- bins (F5-F9) -----------------------------------------------------------------

        private string CullBinFolder(int n)
        {
            switch (n)
            {
                case 1: return _settings.CullBin1;
                case 2: return _settings.CullBin2;
                case 3: return _settings.CullBin3;
                case 4: return _settings.CullBin4;
                case 5: return _settings.CullBin5;
                default: return null;
            }
        }

        private void CullSetBinFolder(int n, string folder)
        {
            switch (n)
            {
                case 1: _settings.CullBin1 = folder; break;
                case 2: _settings.CullBin2 = folder; break;
                case 3: _settings.CullBin3 = folder; break;
                case 4: _settings.CullBin4 = folder; break;
                case 5: _settings.CullBin5 = folder; break;
            }
        }

        // Moves the clip on screen into bin n right away (asks for the folder the first time), then
        // moves on to the next clip.
        private void CullMoveToBin(int n)
        {
            string path = CullCurrentPath;
            if (path == null || n < 1 || n > 5) return;
            string setting = (CullBinFolder(n) ?? "").Trim();
            if (setting.Length == 0)
            {
                setting = CullAskBinFolder(n, path);
                if (setting == null) return;
                path = CullCurrentPath;
                if (path == null) return;
            }
            if (CullAlreadyIn(setting, path, null))
            {
                ShowOsd(FileNameOf(path) + " is already in Bin " + n);
                return;
            }
            string folder = CullResolveFolder(setting, path);
            string target = CullUniqueTarget(folder, FileNameOf(path), null);
            var op = new CullFileOp
            {
                Label = "Bin " + n + " · " + FileNameOf(path),
                DoneText = "Moved to Bin " + n + " (" + FileNameOf(folder) + ") · " + FileNameOf(path),
                FocusPath = path,
                AdvanceFirst = true,
                Generation = _playlist.Generation,
            };
            op.Jobs.Add(new CullJob { Kind = CullJobKind.Move, From = path, To = target, Effect = CullListEffect.Remove });
            CullAdvanceAway(op.Jobs);
            CullStartJobs(op, op.Jobs, CullOpMode.Do, false);
        }

        // First use of an unset bin: say what a bin is, then ask for its folder (never the clip's own folder).
        // Returns the folder (saved in the settings), or null when the user backed out.
        private string CullAskBinFolder(int n, string clip)
        {
            string key = CullKey(ActionIds.CullToBin + ":" + n, "F" + (n + 4));
            string message = "A bin is a folder you sort clips into while you review. " + key + " moves the clip on screen into Bin " + n +
                             " at once and goes on to the next clip; " + CullKey(ActionIds.CullUndo, "Ctrl+Z") + " moves it back.\n\n" +
                             "Choose a folder for Bin " + n + ", other than the one the clips are in. You can change it later in Settings.";
            bool go = false;
            bool? noCheckbox = null;
            RunModal(() => go = ConfirmDialog.Show(this, "Bin " + n + " (" + key + ")", message, "Choose Folder…", "Cancel", ref noCheckbox));
            if (!go) return null;
            string own = SafeDirectoryName(clip);
            string title = "Folder for Bin " + n + " (" + key + ")", start = _settings.LastFolder;
            while (true)
            {
                string picked = null;
                string t = title, from = start;
                RunModal(() => picked = FolderPicker.Show(this, t, from));
                if (string.IsNullOrEmpty(picked)) return null;
                if (!CullSameFolder(CullResolveFolder(picked, clip), own))
                {
                    CullSetBinFolder(n, picked);
                    _settings.Save();
                    CullSyncSkipFolder();
                    return picked;
                }
                ShowOsd("Bin " + n + " can't be the folder the clip is already in: choose another one", true, 3000);
                title = "Folder for Bin " + n + ": not “" + FileNameOf(own) + "”, the folder the clip is in";
                start = picked;
            }
        }

        // The jobs' files leave the playlist now; when the playing clip is one of them, the next one
        // (that the decision filter shows) starts, which also makes mpv let go of it.
        private void CullAdvanceAway(List<CullJob> jobs)
        {
            string current = CullCurrentPath;
            bool currentGoes = false;
            var idx = new List<int>();
            foreach (var j in jobs)
            {
                if (j.Effect != CullListEffect.Remove) continue;
                int i = _playlist.IndexOf(j.From);
                j.ListIndex = i;
                if (i >= 0) idx.Add(i);
                if (SamePath(j.From, current)) currentGoes = true;
            }
            if (idx.Count > 0) _playlist.RemoveRange(idx);
            if (!currentGoes) return;
            _preview.SetSource(null);
            if (_playlist.Current == null)
            {
                StopPlayback();
                return;
            }
            if (!CullPasses(_playlist.Current)) CullMoveToNextMatching();
            PlayCurrent(false);
        }

        // ---- rename (F2) ------------------------------------------------------------------

        private void RenameFileAction()
        {
            string path = CullCurrentPath;
            if (path == null) return;
            string name = FileNameOf(path);
            string text = name, prompt = "New name for “" + name + "”:";
            while (true)
            {
                string entered = null;
                // Like Explorer: only the name part is selected, not the extension.
                int stemLength = text.Length - (Path.GetExtension(text) ?? "").Length;
                if (stemLength <= 0) stemLength = text.Length;
                string t = text, pr = prompt;
                RunModal(() => entered = TextInputDialog.Show(this, "Rename File", pr, t, 0, stemLength));
                if (entered == null) return;
                string error = CullRenameProblem(path, entered);
                if (error == null)
                {
                    CullRename(path, entered.Trim());
                    return;
                }
                prompt = error + "\n\nNew name for “" + name + "”:";
                text = entered;
            }
        }

        private void CullRenameCurrentTo(string newName)
        {
            string path = CullCurrentPath;
            if (path == null) return;
            string error = CullRenameProblem(path, newName);
            if (error != null)
            {
                _cullLastResult = "Rename refused: " + error;
                throw new CullRefusedException(error);
            }
            CullRename(path, newName.Trim());
        }

        // A refused automation call ("RenameFile:<name>") whose reason the caller should get back.
        private sealed class CullRefusedException : Exception
        {
            public CullRefusedException(string message) : base(message) { }
        }

        // The registry reports a failed action only as "failed" to the automation verb; with --automation a
        // refusal is passed on (after the OSD showed it), so the pipe answers "error: <id> failed: <reason>".
        private void CullActionFailed(string id, Exception ex)
        {
            if (_options.Automation && ex is CullRefusedException)
                throw new InvalidOperationException(id + " failed: " + ex.Message, ex);
        }

        // Null when `path` can be renamed to `newName`; otherwise what is wrong with the name.
        private static string CullRenameProblem(string path, string newName)
        {
            string error = CullValidateName(newName);
            if (error != null) return error;
            string name = newName.Trim();
            string target = Path.Combine(SafeDirectoryName(path) ?? "", name);
            if (!SamePath(target, path) && (File.Exists(target) || Directory.Exists(target)))
                return "A file named “" + name + "” already exists in this folder.";
            // Long paths are enabled at startup (Program.Main), so only the NTFS limits apply.
            if (name.Length > 255) return "That name is too long (255 characters at most).";
            if (target.Length >= 32000) return "That name makes the full path too long.";
            return null;
        }

        private void CullRename(string path, string newName)
        {
            string dir = SafeDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            string target = Path.Combine(dir, newName);
            if (string.Equals(target, path, StringComparison.Ordinal)) return;
            var op = new CullFileOp { Label = "Rename " + FileNameOf(path) + " → " + newName, DoneText = "Renamed to " + newName, Generation = _playlist.Generation };
            op.Jobs.Add(new CullJob { Kind = CullJobKind.Move, From = path, To = target, Effect = CullListEffect.Replace, KeepName = true });
            CullStartJobs(op, op.Jobs, CullOpMode.Do, true);
        }

        // Null when the name is usable as a Windows file name; otherwise why not.
        private static string CullValidateName(string raw)
        {
            string name = (raw ?? "").Trim();
            if (name.Length == 0) return "Enter a file name.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A file name can't contain any of these characters: \\ / : * ? \" < > |";
            if (name.EndsWith(".", StringComparison.Ordinal)) return "A file name can't end with a dot.";
            if (name == "." || name == "..") return "Enter a file name.";
            if (name.Length > 255) return "That name is too long (255 characters at most).";
            string stem = name;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            stem = stem.TrimEnd().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                return "“" + stem + "” is a name Windows reserves for devices.";
            return null;
        }

        // ---- apply (Ctrl+Enter) -----------------------------------------------------------

        private void CullCommitAction()
        {
            var plan = CullBuildPlan(true);
            if (plan.Jobs.Count == 0)
            {
                string why = plan.Summary.AlreadyKept + plan.Summary.AlreadyRejected > 0 ? "the decided clips are already in their folders"
                           : plan.Summary.Notes.Count > 0 ? plan.Summary.Notes[0]
                           : "pick (" + CullKey(ActionIds.CullPick, "P") + ") or reject (" + CullKey(ActionIds.CullReject, "X") + ") clips first";
                ShowOsd("Nothing to apply: " + why, true, 3000);
                return;
            }
            var op = new CullFileOp { Label = "Apply decisions", Generation = _playlist.Generation };
            op.Jobs.AddRange(plan.Jobs);
            string held = null;
            IntPtr owner = Handle;
            bool ran = false;
            RunModal(() => ran = CullCommitDialog.Show(this, plan.Summary,
                () =>
                {
                    held = CullReleaseFor(plan.Jobs, true);
                    _cullJobRunning = true;
                },
                (progress, stop) =>
                {
                    CullExecuteJobs(plan.Jobs, held, owner, progress, stop, () => _cullStopJobs);
                    return CullReport(plan.Jobs);
                }));
            if (ran) CullFinishJobs(op, plan.Jobs, CullOpMode.Do);
        }

        private void CullCommitWithoutDialog()
        {
            var plan = CullBuildPlan(false);
            if (plan.Jobs.Count == 0)
            {
                _cullLastResult = "Nothing to apply";
                ShowOsd(_cullLastResult);
                return;
            }
            var op = new CullFileOp { Label = "Apply decisions", Generation = _playlist.Generation };
            op.Jobs.AddRange(plan.Jobs);
            CullStartJobs(op, plan.Jobs, CullOpMode.Do, true);
        }

        // Settings value -> one of the given choices (the first is the default).
        private static string CullChoice(string value, params string[] choices)
        {
            string v = (value ?? "").Trim();
            foreach (var c in choices)
                if (string.Equals(v, c, StringComparison.OrdinalIgnoreCase)) return c;
            return choices[0];
        }

        // A folder setting: empty = "<clip folder>\<fallback>", relative = inside the clip's folder.
        private static string CullResolveFolder(string setting, string clip, string fallback = null)
        {
            string s = Environment.ExpandEnvironmentVariables((setting ?? "").Trim().Trim('"'));
            string dir = SafeDirectoryName(clip) ?? "";
            if (s.Length == 0) return fallback == null ? dir : Path.Combine(dir, fallback);
            try
            {
                return Path.GetFullPath(Path.IsPathRooted(s) ? s : Path.Combine(dir, s));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return Path.Combine(dir, fallback ?? "");
            }
        }

        private static bool CullSameFolder(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        // True when the clip already sits in the folder a setting sends it to: the resolved folder is the
        // clip's own, or (for an empty or relative setting, which resolves against the clip's folder) the
        // clip's folder already ends with that relative path. Without the second test a clip in "...\Keep"
        // would be sent on to "...\Keep\Keep" by a second Apply.
        private static bool CullAlreadyIn(string setting, string clip, string fallback)
        {
            string dir = SafeDirectoryName(clip);
            if (string.IsNullOrEmpty(dir)) return false;
            if (CullSameFolder(CullResolveFolder(setting, clip, fallback), dir)) return true;
            string rel = CullRelativeSetting(setting, fallback);
            return rel != null && CullEndsWithFolder(dir, rel);
        }

        // An empty or relative folder setting as the relative path it means ("Keep", "Bins\Maybe"); null for
        // an absolute (or unusable) one.
        private static string CullRelativeSetting(string setting, string fallback)
        {
            string s = Environment.ExpandEnvironmentVariables((setting ?? "").Trim().Trim('"'));
            if (s.Length == 0) s = fallback ?? "";
            else
            {
                try
                {
                    if (Path.IsPathRooted(s)) return null;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
            s = s.Replace('/', '\\').Trim('\\');
            if (s.Length == 0) return null;
            foreach (var part in s.Split('\\'))
                if (part.Length == 0 || part == "." || part == "..") return null;
            return s;
        }

        private static bool CullEndsWithFolder(string folder, string rel)
        {
            string f = (folder ?? "").TrimEnd('\\', '/');
            return f.EndsWith("\\" + rel, StringComparison.OrdinalIgnoreCase);
        }

        private static string CullFolderText(string setting, string fallback)
        {
            return string.IsNullOrWhiteSpace(setting) ? "into a “" + fallback + "” folder next to each clip" : "to " + setting.Trim();
        }

        // Every pick and reject in the list -> the jobs Apply runs (in list order) and their summary.
        private CullPlan CullBuildPlan(bool allowPermanent)
        {
            var plan = new CullPlan();
            var s = plan.Summary;
            string pickMode = CullChoice(_settings.CullPickAction, "Move", "Copy", "None");
            string rejectMode = CullChoice(_settings.CullRejectAction, "RecycleBin", "Folder", "None");
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int conflicts = 0, picksLeft = 0, rejectsLeft = 0, noBin = 0;
            foreach (var item in _playlist.Items)
            {
                if (Playlist.IsUrl(item)) continue;
                var m = CullMarkOf(item);
                if (m.Flag == 0) continue;
                long size = CullFileSize(item);
                string name = FileNameOf(item);
                if (m.IsPick)
                {
                    if (pickMode == "None") { picksLeft++; continue; }
                    if (CullAlreadyIn(_settings.CullKeepFolder, item, "Keep")) { s.AlreadyKept++; continue; }
                    string folder = CullResolveFolder(_settings.CullKeepFolder, item, "Keep");
                    string target = CullUniqueTarget(folder, name, reserved);
                    if (!string.Equals(FileNameOf(target), name, StringComparison.OrdinalIgnoreCase)) conflicts++;
                    reserved.Add(target);
                    bool copy = pickMode == "Copy";
                    plan.Jobs.Add(new CullJob
                    {
                        Kind = copy ? CullJobKind.Copy : CullJobKind.Move, From = item, To = target, Size = size,
                        Effect = copy ? CullListEffect.None : CullListEffect.Replace,
                    });
                    s.PickCount++;
                    s.PickBytes += size;
                    continue;
                }
                if (rejectMode == "None") { rejectsLeft++; continue; }
                if (rejectMode == "Folder")
                {
                    if (CullAlreadyIn(_settings.CullRejectFolder, item, "Rejected")) { s.AlreadyRejected++; continue; }
                    string folder = CullResolveFolder(_settings.CullRejectFolder, item, "Rejected");
                    string target = CullUniqueTarget(folder, name, reserved);
                    if (!string.Equals(FileNameOf(target), name, StringComparison.OrdinalIgnoreCase)) conflicts++;
                    reserved.Add(target);
                    plan.Jobs.Add(new CullJob { Kind = CullJobKind.Move, From = item, To = target, Size = size, Effect = CullListEffect.Remove });
                }
                else
                {
                    bool bin = CanRecycle(item);
                    if (!bin && !allowPermanent) { noBin++; continue; }
                    if (!bin) s.PermanentDeletes++;
                    plan.Jobs.Add(new CullJob { Kind = bin ? CullJobKind.Recycle : CullJobKind.Delete, From = item, Size = size, Effect = CullListEffect.Remove });
                }
                s.RejectCount++;
                s.RejectBytes += size;
            }
            s.PickAction = (pickMode == "Copy" ? "Copy " : "Move ") + CullFolderText(_settings.CullKeepFolder, "Keep");
            s.RejectAction = rejectMode == "Folder" ? "Move " + CullFolderText(_settings.CullRejectFolder, "Rejected")
                : s.PermanentDeletes == s.RejectCount ? "Delete permanently" : "Send to the Recycle Bin";
            if (conflicts > 0)
                s.Notes.Add(conflicts == 1 ? "1 name is already taken at the destination: that file gets “ (2)” added."
                                           : conflicts + " names are already taken at the destination: those files get “ (2)” added.");
            if (picksLeft > 0) s.Notes.Add((picksLeft == 1 ? "1 pick stays" : picksLeft + " picks stay") + " where it is (Apply is set to leave picks in place).");
            if (rejectsLeft > 0) s.Notes.Add((rejectsLeft == 1 ? "1 reject stays" : rejectsLeft + " rejects stay") + " where it is.");
            if (noBin > 0) s.Notes.Add(noBin + " rejects on drives without a Recycle Bin were skipped.");
            _cullLastSummary = s;
            return plan;
        }

        private static long CullFileSize(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return 0;
            }
        }

        // "<folder>\name.ext", or "name (2).ext", "name (3).ext"... when that is taken (on disk or by
        // another job of the same batch).
        private static string CullUniqueTarget(string folder, string name, ICollection<string> reserved)
        {
            Func<string, bool> taken = p => File.Exists(p) || Directory.Exists(p) || File.Exists(p + CullPartialSuffix) ||
                                            (reserved != null && reserved.Contains(p));
            string target = Path.Combine(folder, name);
            if (!taken(target)) return target;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int n = 2; n < 100000; n++)
            {
                target = Path.Combine(folder, stem + " (" + n + ")" + ext);
                if (!taken(target)) return target;
            }
            return Path.Combine(folder, stem + " (" + Guid.NewGuid().ToString("N").Substring(0, 8) + ")" + ext);
        }

        // Text the Apply dialog keeps on screen after the batch: null when everything went through.
        private static string CullReport(List<CullJob> jobs)
        {
            int done = 0, failed = 0, skipped = 0;
            var lines = new StringBuilder();
            foreach (var j in jobs)
            {
                if (j.Done) done++;
                else if (j.Error != null)
                {
                    failed++;
                    if (failed <= 6) lines.Append("\n• ").Append(FileNameOf(j.From)).Append(": ").Append(j.Error);
                }
                else skipped++;
                if (j.Done && j.Error != null)
                {
                    failed++;
                    if (failed <= 6) lines.Append("\n• ").Append(FileNameOf(j.From)).Append(": ").Append(j.Error);
                }
            }
            if (failed == 0 && skipped == 0) return null;
            var sb = new StringBuilder();
            if (skipped > 0) sb.Append("Stopped: ").Append(done).Append(" of ").Append(jobs.Count).Append(" files were done.");
            if (failed > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(failed == 1 ? "1 file had a problem:" : failed + " files had problems:").Append(lines);
                if (failed > 6) sb.Append("\n…and ").Append(failed - 6).Append(" more (see the log)");
            }
            return sb.ToString();
        }

        // ---- running file jobs ------------------------------------------------------------

        // Runs the jobs on a background thread once mpv and the preview have let go of the files, then
        // finishes on the UI thread. stopIfHeld: the file playing now is stopped and reloaded afterwards.
        private void CullStartJobs(CullFileOp op, List<CullJob> jobs, CullOpMode mode, bool stopIfHeld)
        {
            string held = CullReleaseFor(jobs, stopIfHeld);
            _cullJobRunning = true;
            IntPtr owner = Handle;
            var t = new System.Threading.Thread(() =>
            {
                CullExecuteJobs(jobs, held, owner, null, () => _cullStopJobs, () => _cullStopJobs);
                try { BeginInvoke((MethodInvoker)(() => CullFinishJobs(op, jobs, mode))); }
                catch (InvalidOperationException) { } // closing: the window is gone
            }) { IsBackground = true, Name = "VidFlow cull files" };
            t.SetApartmentState(System.Threading.ApartmentState.STA); // the shell's Recycle Bin delete wants STA
            _cullJobThread = t;
            t.Start();
        }

        // Makes the preview (always) and mpv (when stop is set and it plays one of the files) let go;
        // returns the file mpv still holds or is about to release, so the worker waits for it.
        private string CullReleaseFor(List<CullJob> jobs, bool stop)
        {
            string held = null;
            foreach (var j in jobs)
                if (IsOpenInPlayer(j.From)) { held = j.From; break; }
            _preview.SetSource(null);
            if (held != null && stop)
            {
                string current = CullCurrentPath;
                _cullResume = current != null && SamePath(current, held)
                    ? new CullResume { Path = held, Position = _st.Position, Paused = _st.Paused }
                    : null;
                StopPlayback();
            }
            return held;
        }

        // Background thread: file system only (no UI, no player, no index). Each job records Done/Error.
        // stop is checked between files, abort also inside a long copy.
        private static void CullExecuteJobs(List<CullJob> jobs, string held, IntPtr owner, Action<double, string> progress,
                                            Func<bool> stop, Func<bool> abort)
        {
            int n = jobs.Count;
            for (int i = 0; i < n; i++)
            {
                if (stop()) break;
                var j = jobs[i];
                int index = i;
                string text = CullJobText(j) + " (" + (i + 1) + " of " + n + ")";
                Action<double> part = f => { if (progress != null) progress((index + Math.Max(0, Math.Min(1, f))) / n, text); };
                part(0);
                if (held != null && SamePath(j.From, held)) CullWaitUntilFree(j.From, abort);
                try
                {
                    CullRunJob(j, owner, part, abort);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                           ex is NotSupportedException || ex is System.Security.SecurityException || ex is Win32Exception)
                {
                    j.Error = ex.Message;
                }
                if (j.Error != null) Log.Warn("cull: " + j.Kind + " " + j.From + (j.To != null ? " -> " + j.To : "") + " failed: " + j.Error);
                else if (j.Done) Log.Info("cull: " + j.Kind + " " + j.From + (j.To != null ? " -> " + j.To : ""));
            }
            CullRemoveEmptyFolders(jobs);
            if (progress != null) progress(1, "Done");
        }

        // An undo removes the folders the original batch created (Keep, a bin...) once they are empty; so
        // does a job that created its destination folder and then failed.
        private static void CullRemoveEmptyFolders(List<CullJob> jobs)
        {
            var dirs = new List<string>();
            foreach (var j in jobs)
            {
                var list = j.Done ? j.RemoveDirsIfEmpty : j.CreatedDirs;
                if (list == null) continue;
                foreach (var d in list)
                    if (!dirs.Exists(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) dirs.Add(d);
            }
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length)); // children before their parents
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    using (var e = Directory.EnumerateFileSystemEntries(d).GetEnumerator())
                        if (e.MoveNext()) continue;
                    Directory.Delete(d, false);
                    Log.Info("cull: removed empty folder " + d);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    Log.Warn("cull: could not remove folder " + d + ": " + ex.Message);
                }
            }
        }

        private static string CullJobText(CullJob j)
        {
            string name = FileNameOf(j.From);
            switch (j.Kind)
            {
                case CullJobKind.Copy: return "Copying " + name;
                case CullJobKind.Recycle: return "Recycling " + name;
                case CullJobKind.Delete: return "Deleting " + name;
                default: return "Moving " + name;
            }
        }

        private static void CullRunJob(CullJob j, IntPtr owner, Action<double> part, Func<bool> abort)
        {
            if (!File.Exists(j.From))
            {
                j.Error = "the file is not there any more";
                return;
            }
            if (j.Kind == CullJobKind.Recycle || j.Kind == CullJobKind.Delete)
            {
                bool recycle = j.Kind == CullJobKind.Recycle;
                var sidecars = j.Sidecars;
                if (sidecars == null && recycle)
                {
                    sidecars = new List<KeyValuePair<string, string>>();
                    foreach (var f in CullFindSidecars(j.From)) sidecars.Add(new KeyValuePair<string, string>(f, null));
                }
                j.Sidecars = new List<KeyValuePair<string, string>>();
                // FOF_ALLOWUNDO only recycles "if possible" (the user may accept the shell's permanent-delete
                // warning, e.g. for a file too big for the bin): the bin's item count tells what happened.
                string root = recycle ? LocalDriveRoot(j.From) : null;
                long binBefore = root != null ? Native.RecycleBinItemCount(root) : -1;
                bool aborted;
                int rc = Native.ShellDelete(owner, j.From, recycle, out aborted);
                if (aborted) j.Error = "cancelled";
                else if (rc != 0) j.Error = "shell error 0x" + rc.ToString("X", Inv);
                else if (File.Exists(j.From)) j.Error = "the file is still there";
                else
                {
                    j.Done = true;
                    j.Recycled = recycle && binBefore >= 0 && Native.RecycleBinItemCount(root) > binBefore;
                    if (!j.Recycled) return; // deleted for good: its sidecars stay rather than going the same way
                    foreach (var sc in sidecars)
                    {
                        if (string.IsNullOrEmpty(sc.Key) || !File.Exists(sc.Key)) continue;
                        bool a;
                        int r = Native.ShellDelete(owner, sc.Key, true, out a);
                        if (r == 0 && !a && !File.Exists(sc.Key)) j.Sidecars.Add(new KeyValuePair<string, string>(sc.Key, null));
                        else Log.Warn("cull: sidecar " + sc.Key + " was not recycled");
                    }
                }
                return;
            }

            string dir = SafeDirectoryName(j.To);
            if (string.IsNullOrEmpty(dir)) throw new IOException("bad destination " + j.To);
            var created = CullMissingFolders(dir);
            Directory.CreateDirectory(dir);
            if (created.Count > 0) j.CreatedDirs = created;
            if (!SamePath(j.From, j.To) && (File.Exists(j.To) || Directory.Exists(j.To)))
            {
                if (j.KeepName) throw new IOException("“" + FileNameOf(j.To) + "” already exists");
                j.To = CullUniqueTarget(dir, FileNameOf(j.To), null);
            }
            if (j.Kind == CullJobKind.Move && CullSameVolume(j.From, j.To) && CullTryRename(j.From, j.To))
            {
                j.Done = true;
                CullCarrySidecars(j, abort);
                part(1);
                return;
            }
            if (SamePath(j.From, j.To)) throw new IOException("source and destination are the same file");
            CullCopyFile(j.From, j.To, part, abort);
            if (j.Kind == CullJobKind.Copy)
            {
                j.Done = true;
                CullCarrySidecars(j, abort);
                return;
            }
            // Cross-volume move: the copy is complete (same size, same timestamps); now the original goes.
            try
            {
                var attrs = File.GetAttributes(j.From);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(j.From, attrs & ~FileAttributes.ReadOnly);
                File.Delete(j.From);
                j.Done = true;
                CullCarrySidecars(j, abort);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Both exist now: treat it as a copy, so undo removes the copy and nothing is lost.
                j.Kind = CullJobKind.Copy;
                j.Done = true;
                j.Error = "copied, but the original could not be removed (" + ex.Message + ")";
            }
        }

        // The folders of `dir` that do not exist yet, deepest first (what Directory.CreateDirectory adds).
        private static List<string> CullMissingFolders(string dir)
        {
            var list = new List<string>();
            try
            {
                string d = Path.GetFullPath(dir);
                while (!string.IsNullOrEmpty(d) && !Directory.Exists(d))
                {
                    list.Add(d.TrimEnd('\\'));
                    d = Path.GetDirectoryName(d);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
            }
            return list;
        }

        // ---- sidecars ---------------------------------------------------------------------

        // GoPro names: GH010123 / GX010123 (video and .THM), GL010123.LRF (the low-res proxy).
        private static bool CullIsGoProStem(string stem)
        {
            if (stem == null || stem.Length != 8) return false;
            char a = char.ToUpperInvariant(stem[0]), b = char.ToUpperInvariant(stem[1]);
            if (a != 'G' || (b != 'H' && b != 'X')) return false;
            for (int i = 2; i < 8; i++) if (stem[i] < '0' || stem[i] > '9') return false;
            return true;
        }

        // Files next to `clip` with its exact base name and a sidecar extension (case-insensitive), plus
        // a GoPro clip's GL….LRF. Never another clip's: when another media file has the same base name it
        // is unclear whose they are, so none are returned (and the GoPro proxy only when exactly one GH/GX
        // clip has its number).
        private static List<string> CullFindSidecars(string clip)
        {
            var found = new List<string>();
            string dir = SafeDirectoryName(clip);
            string stem = Path.GetFileNameWithoutExtension(clip ?? "");
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return found;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
                {
                    string name = Path.GetFileName(f);
                    if (SamePath(f, clip) || !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Playlist.IsMediaFile(f)) return new List<string>();
                    if (Array.IndexOf(CullSidecarExtensions, name.Substring(stem.Length).ToLowerInvariant()) >= 0) found.Add(f);
                }
                if (CullIsGoProStem(stem))
                {
                    string number = stem.Substring(2), lrf = null;
                    bool otherClip = false;
                    foreach (var f in Directory.EnumerateFiles(dir, "G?" + number + ".*"))
                    {
                        string s2 = Path.GetFileNameWithoutExtension(f);
                        if (s2.Length != 8 || !s2.EndsWith(number, StringComparison.Ordinal) || string.Equals(s2, stem, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(s2.Substring(0, 2), "GL", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetExtension(f), ".lrf", StringComparison.OrdinalIgnoreCase)) lrf = f;
                        else if (Playlist.IsMediaFile(f)) otherClip = true;
                    }
                    if (lrf != null && !otherClip) found.Add(lrf);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                Log.Warn("cull: could not look for sidecars of " + clip + ": " + ex.Message);
            }
            return found;
        }

        // Where each sidecar of `from` goes when the clip becomes `to` ("a.srt" -> "<to folder>\b.srt").
        private static List<KeyValuePair<string, string>> CullPlanSidecars(string from, string to)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            string toDir = SafeDirectoryName(to) ?? "";
            string fromStem = Path.GetFileNameWithoutExtension(from), toStem = Path.GetFileNameWithoutExtension(to);
            foreach (var f in CullFindSidecars(from))
            {
                string name = Path.GetFileName(f), ext = Path.GetExtension(f);
                string target = name.StartsWith(fromStem + ".", StringComparison.OrdinalIgnoreCase) ? toStem + name.Substring(fromStem.Length)
                              : CullIsGoProStem(toStem) ? "GL" + toStem.Substring(2) + ext
                              : toStem + ext;
                pairs.Add(new KeyValuePair<string, string>(f, Path.Combine(toDir, target)));
            }
            return pairs;
        }

        // After the clip itself moved or was copied: its sidecars follow. A sidecar whose target name is
        // taken stays where it is (never overwritten); problems are logged, they do not fail the clip.
        private static void CullCarrySidecars(CullJob j, Func<bool> abort)
        {
            var pairs = j.Sidecars ?? CullPlanSidecars(j.From, j.To);
            bool copy = j.Kind == CullJobKind.Copy;
            var done = new List<KeyValuePair<string, string>>();
            foreach (var p in pairs)
            {
                if (string.IsNullOrEmpty(p.Key) || string.IsNullOrEmpty(p.Value) || SamePath(p.Key, p.Value)) continue;
                try
                {
                    if (!File.Exists(p.Key)) continue;
                    if (File.Exists(p.Value) || Directory.Exists(p.Value))
                    {
                        Log.Warn("cull: " + p.Value + " already exists; sidecar " + p.Key + " left in place");
                        continue;
                    }
                    string d = SafeDirectoryName(p.Value);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    if (!copy && CullSameVolume(p.Key, p.Value) && CullTryRename(p.Key, p.Value))
                    {
                        done.Add(p);
                        continue;
                    }
                    CullCopyFile(p.Key, p.Value, f => { }, abort);
                    if (!copy)
                    {
                        try
                        {
                            var attrs = File.GetAttributes(p.Key);
                            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(p.Key, attrs & ~FileAttributes.ReadOnly);
                            File.Delete(p.Key);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // Keep one copy only: the original stays, the new one goes.
                            CullDeleteQuietly(p.Value);
                            Log.Warn("cull: sidecar " + p.Key + " could not be moved: " + ex.Message);
                            continue;
                        }
                    }
                    done.Add(p);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                           ex is NotSupportedException || ex is System.Security.SecurityException || ex is Win32Exception)
                {
                    Log.Warn("cull: sidecar " + p.Key + " -> " + p.Value + " failed: " + ex.Message);
                }
            }
            foreach (var p in done) Log.Info("cull: sidecar " + p.Key + " -> " + p.Value);
            j.Sidecars = done;
        }

        private static bool CullSameVolume(string a, string b)
        {
            try
            {
                string ra = Path.GetPathRoot(Path.GetFullPath(a)), rb = Path.GetPathRoot(Path.GetFullPath(b));
                return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        // A rename within one volume. False when Windows says it needs a copy after all (a folder that
        // is a mount point or junction to another volume).
        private static bool CullTryRename(string from, string to)
        {
            try
            {
                File.Move(from, to);
                return true;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 17) // ERROR_NOT_SAME_DEVICE
            {
                return false;
            }
        }

        // Copies to "<to>.vidflow-partial" first and renames when complete, so a cancelled or failed copy
        // never leaves a truncated file under the real name. CopyFileEx keeps timestamps and attributes.
        private static void CullCopyFile(string from, string to, Action<double> part, Func<bool> abort)
        {
            string tmp = to + CullPartialSuffix;
            CullDeleteQuietly(tmp);
            int cancel = 0;
            CullNative.CopyProgressRoutine cb = (total, done, streamSize, streamDone, stream, reason, src, dst, data) =>
            {
                if (total > 0) part((double)done / total);
                return abort() ? CullNative.PROGRESS_CANCEL : CullNative.PROGRESS_CONTINUE;
            };
            bool ok = CullNative.CopyFileEx(from, tmp, cb, IntPtr.Zero, ref cancel, CullNative.COPY_FILE_FAIL_IF_EXISTS);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            GC.KeepAlive(cb);
            if (!ok)
            {
                CullDeleteQuietly(tmp);
                throw new IOException(err == CullNative.ERROR_REQUEST_ABORTED ? "stopped" : new Win32Exception(err).Message);
            }
            try
            {
                File.Move(tmp, to);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                CullDeleteQuietly(tmp);
                throw;
            }
            part(1);
        }

        private static void CullDeleteQuietly(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn("cull: could not remove " + path + ": " + ex.Message);
            }
        }

        // Waits (bounded) until no process has the file open, e.g. mpv finishing with the clip it left.
        private static void CullWaitUntilFree(string path, Func<bool> abort)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < CullReleaseTimeoutMs && !abort() && CullInUse(path))
                System.Threading.Thread.Sleep(40);
        }

        private static bool CullInUse(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // UI thread: the jobs ran. Apply their effects on the playlist, the decisions index, recent files
        // and the undo history; reload a released file at its position; report on the OSD.
        private void CullFinishJobs(CullFileOp op, List<CullJob> jobs, CullOpMode mode)
        {
            _cullJobRunning = false;
            _cullJobThread = null;
            if (_closing) return;
            int moved = 0, copied = 0, recycled = 0, deleted = 0, failed = 0;
            string firstError = null, resumeTo = null;
            bool resumeGone = false;
            // Positions only mean something in the list the operation came from: after another folder
            // was opened, a restored file is not slotted into the new, unrelated list.
            bool sameList = op.Generation < 0 || op.Generation == _playlist.Generation;
            string restoredElsewhere = null;
            var removals = new List<int>();
            var inserts = new List<CullJob>();
            foreach (var j in jobs)
            {
                if (j.Error != null)
                {
                    failed++;
                    if (firstError == null) firstError = FileNameOf(j.From) + ": " + j.Error;
                }
                if (!j.Done)
                {
                    // A bin move took the clip out of the list beforehand: put it back.
                    if (j.Effect == CullListEffect.Remove && j.ListIndex >= 0 && _playlist.IndexOf(j.From) < 0 && File.Exists(j.From))
                    {
                        if (sameList) inserts.Add(new CullJob { From = null, To = j.From, ListIndex = j.ListIndex });
                        else if (restoredElsewhere == null) restoredElsewhere = j.From;
                    }
                    continue;
                }
                switch (j.Kind)
                {
                    case CullJobKind.Move:
                        moved++;
                        CullIndex.Move(j.From, j.To);
                        CullRemapPaths(j.From, j.To);
                        CullRecentMoved(j.From, j.To);
                        FeatureFileMoved(j.From, j.To);
                        break;
                    case CullJobKind.Copy:
                        copied++;
                        break;
                    default:
                        if (j.Recycled) recycled++;
                        else deleted++;
                        CullIndex.Remove(j.From);
                        CullRecentMoved(j.From, null);
                        FeatureFileMoved(j.From, null);
                        break;
                }
                if (_cullResume != null && SamePath(_cullResume.Path, j.From))
                {
                    if (j.Kind == CullJobKind.Move) resumeTo = j.To;
                    else if (j.Kind != CullJobKind.Copy) resumeGone = true;
                }
                switch (j.Effect)
                {
                    case CullListEffect.Remove:
                        int i = _playlist.IndexOf(j.From);
                        if (i >= 0)
                        {
                            j.ListIndex = i;
                            removals.Add(i);
                        }
                        break;
                    case CullListEffect.Replace:
                        CullReplaceInList(j.From, j.To);
                        break;
                    case CullListEffect.Insert:
                        if (sameList)
                        {
                            inserts.Add(j);
                            break;
                        }
                        // Another list is open: the file goes back on disk only (an entry for the place it
                        // left, e.g. the bin folder now listed, goes away).
                        int stale = _playlist.IndexOf(j.From);
                        if (stale >= 0) removals.Add(stale);
                        if (restoredElsewhere == null) restoredElsewhere = j.To;
                        break;
                }
            }
            if (removals.Count > 0)
            {
                if (removals.Count == _playlist.Count) _playlist.Clear();
                else _playlist.RemoveRange(removals);
            }
            // Ascending, so every item lands where it was before the batch took it out.
            inserts.Sort((a, b) => a.ListIndex.CompareTo(b.ListIndex));
            foreach (var j in inserts) CullInsertIntoList(j.To, j.ListIndex);

            // History.
            var done = jobs.FindAll(j => j.Done);
            if (mode == CullOpMode.Undo)
            {
                if (done.Count > 0) _cullRedo.Add(op);
                else _cullUndo.Add(op); // nothing could be put back: keep it, the user may retry
            }
            else if (done.Count > 0)
            {
                op.Jobs = done;
                if (mode == CullOpMode.Do) CullPushUndo(op);
                else _cullUndo.Add(op);
            }
            else if (mode == CullOpMode.Redo) _cullRedo.Add(op);

            // The released file: reload it (where it is now) at the same position and pause state.
            var r = _cullResume;
            if (r != null)
            {
                string path = resumeTo ?? (resumeGone ? null : r.Path);
                int at = path != null ? _playlist.IndexOf(path) : -1;
                if (at >= 0)
                {
                    r.Path = _playlist.Items[at];
                    r.Armed = true;
                    PlayIndex(at, !r.Paused);
                }
                else
                {
                    _cullResume = null;
                    if (_playlist.Current != null) PlayCurrent(false);
                }
            }
            else if (mode == CullOpMode.Undo && op.FocusPath != null)
            {
                CullFocus(op.FocusPath);
            }
            if (_cullResume == null && HasMedia) _preview.SetSource(_st.Path);

            // Report.
            string text;
            if (mode == CullOpMode.Undo)
            {
                int inBin, gone;
                CullCountLost(op.Jobs, out inBin, out gone);
                text = "Undo: " + op.Label +
                       (inBin > 0 ? " · " + inBin + (inBin == 1 ? " file stays" : " files stay") + " in the Recycle Bin (restore from there)" : "") +
                       (gone > 0 ? " · " + gone + (gone == 1 ? " file was" : " files were") + " deleted permanently" : "");
            }
            else if (op.DoneText != null && done.Count > 0) text = (mode == CullOpMode.Redo ? "Redo: " : "") + op.DoneText;
            else
            {
                var parts = new List<string>();
                if (moved > 0) parts.Add(moved + " moved");
                if (copied > 0) parts.Add(copied + " copied");
                if (recycled > 0) parts.Add(recycled + " sent to the Recycle Bin");
                if (deleted > 0) parts.Add(deleted + " deleted permanently");
                text = (mode == CullOpMode.Redo ? "Redo: " : "Applied: ") + (parts.Count > 0 ? string.Join(" · ", parts) : "nothing changed");
            }
            if (restoredElsewhere != null)
                text += " · back in “" + FileNameOf(SafeDirectoryName(restoredElsewhere) ?? "") + "” (another list is open now)";
            if (failed > 0) text += " · " + (failed == 1 ? "problem: " + firstError : failed + " problems, first: " + firstError);
            _cullLastResult = text;
            ShowOsd(text, failed > 0, failed > 0 ? 4000 : 2000);
            CullIndex.Save();
            CullChanged();
        }

        // Files a batch could not bring back: in the Recycle Bin, or deleted for good.
        private static void CullCountLost(List<CullJob> jobs, out int inBin, out int gone)
        {
            inBin = gone = 0;
            if (jobs == null) return;
            foreach (var j in jobs)
            {
                if (j.Kind != CullJobKind.Recycle && j.Kind != CullJobKind.Delete) continue;
                if (j.Recycled) inBin++;
                else gone++;
            }
        }

        // Replaces one playlist item with its new path, keeping its position and current-ness.
        private void CullReplaceInList(string from, string to)
        {
            int i = _playlist.IndexOf(from);
            if (i >= 0) _playlist.Replace(i, to);
        }

        private void CullInsertIntoList(string path, int index)
        {
            if (string.IsNullOrEmpty(path) || _playlist.IndexOf(path) >= 0) return;
            int before = _playlist.Count;
            _playlist.Append(new[] { path });
            if (_playlist.Count > before && index >= 0) _playlist.Move(_playlist.Count - 1, Math.Min(index, _playlist.Count - 1));
        }

        // Earlier decisions follow a file VidFlow moved, so undoing them still finds it.
        private void CullRemapPaths(string from, string to)
        {
            foreach (var stack in new[] { _cullUndo, _cullRedo })
                foreach (var op in stack)
                {
                    var d = op as CullDecisionOp;
                    if (d != null && SamePath(d.Path, from)) d.Path = to;
                }
        }

        private void CullRecentMoved(string from, string to)
        {
            var list = _settings.RecentFiles;
            bool changed = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!SamePath(list[i], from)) continue;
                if (to == null) list.RemoveAt(i);
                else list[i] = to;
                changed = true;
            }
            if (!changed) return;
            _settings.Save();
            _host.Surface.SetRecentFiles(_settings.RecentFiles);
        }

        // ---- menus ------------------------------------------------------------------------

        // Move Clip to Bin ▸ "Bin 2: Selects" (the folder's name); null while unset ("Bin 2").
        private string CullBinMenuLabel(int n)
        {
            string folder = (CullBinFolder(n) ?? "").Trim().Trim('"').TrimEnd('\\', '/');
            return folder.Length > 0 ? "Bin " + n + ": " + FileNameOf(folder) : null;
        }

        // Undo/Redo say what they will undo ("Undo: Bin 1 · clip03.webm"); Ctrl+Z also reverts renames,
        // bin moves and whole Apply batches, not only decisions.
        private void CullLabelUndoItems(ToolStrip menu)
        {
            if (menu != null) CullLabelUndoItems(menu.Items);
        }

        private void CullLabelUndoItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                var am = it as ActionMenuItem;
                if (am != null && string.Equals(am.Tag as string, ActionIds.CullUndo, StringComparison.Ordinal))
                    am.LabelProvider = () => _cullUndo.Count > 0 ? "Undo: " + CullShort(_cullUndo[_cullUndo.Count - 1].Label) : null;
                else if (am != null && string.Equals(am.Tag as string, ActionIds.CullRedo, StringComparison.Ordinal))
                    am.LabelProvider = () => _cullRedo.Count > 0 ? "Redo: " + CullShort(_cullRedo[_cullRedo.Count - 1].Label) : null;
                var dd = it as ToolStripDropDownItem;
                if (dd != null && dd.HasDropDownItems) CullLabelUndoItems(dd.DropDownItems);
            }
        }

        private static string CullShort(string label)
        {
            label = label ?? "";
            return label.Length <= 60 ? label : label.Substring(0, 59) + "…";
        }

        // The bar chip: left click = the shared Cull menu, right click = clear the flag.
        private void OnCullBarButton(BarButton button, MouseButtons mouse, Rectangle screenRect)
        {
            if (button != BarButton.Cull) return;
            if (mouse == MouseButtons.Right)
            {
                Run(ActionIds.CullUnflag);
                return;
            }
            if (_cullBarMenu == null)
            {
                _cullBarMenu = _menus.BuildCullMenu();
                if (_cullBarMenu == null) return;
                CullLabelUndoItems(_cullBarMenu);
                _cullBarMenu.Closed += (s, e) => OnMenuClosed();
            }
            ShowDropDown(_cullBarMenu, screenRect, false);
        }

        // CopyFileEx (byte progress + cancel for cross-volume moves and copies).
        private static class CullNative
        {
            public const uint COPY_FILE_FAIL_IF_EXISTS = 0x1, PROGRESS_CONTINUE = 0, PROGRESS_CANCEL = 1;
            public const int ERROR_REQUEST_ABORTED = 1235;

            public delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize,
                long streamBytesTransferred, uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CopyFileEx(string existingFileName, string newFileName, CopyProgressRoutine progressRoutine,
                IntPtr data, ref int cancel, uint copyFlags);
        }
    }
}
