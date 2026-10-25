using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Threading;
using System.Windows.Forms;
using VidFlow.Core;
using VidFlow.Media;
using VidFlow.Ui;

[assembly: System.Reflection.AssemblyTitle("VidFlow Player")]
[assembly: System.Reflection.AssemblyProduct("VidFlow Player")]
[assembly: System.Reflection.AssemblyDescription("Get through a folder of video clips fast: keep the good ones, bin the rest")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (C) 2026 VidFlow Player contributors. GNU GPL v3.")]

namespace VidFlow
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Paths over 260 characters. VidFlow.exe carries no TargetFrameworkAttribute, so .NET
            // Framework would use its legacy path handling (File.Exists false, PathTooLongException)
            // despite the longPathAware manifest. Must run before anything touches System.IO.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            // Crash logging before any startup work, so even a failure there leaves crash.log.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception, true);

            var opt = LaunchOptions.Parse(args);
            if (opt.LogPath != null) Log.Open(opt.LogPath);
            else if (opt.Automation) Log.Open(LaunchOptions.DefaultLogPath);

            if (opt.SettingsPath == null) Settings.MigrateFromGlance(); // beta testers keep their settings
            var settings = Settings.Load(opt.SettingsPath);
            string pipe = opt.PipeName ?? ControlChannel.DefaultPipeName;

            // Single instance: if a VidFlow window is already running (or still starting), hand it
            // our files. The gate is released when this process's window closes.
            using (var instance = new InstanceGate(pipe))
            {
                if (settings.SingleInstance && !opt.ForceNewInstance && instance.HandOff(opt)) return 0;

                try
                {
                    if (NativeLoader.NeedsExtraction()) RunWithSplash(NativeLoader.Load);
                    else NativeLoader.Load();
                }
                catch (Exception ex)
                {
                    Log.Error("engine load failed", ex);
                    MessageBox.Show("VidFlow could not start its playback engine.\n\n" + ex.Message,
                        "VidFlow Player", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }

                using (var form = new MainForm(settings, opt, pipe))
                    Application.Run(form);
                return 0;
            }
        }

        // Single-instance protocol. The first instance owns the named mutex for as long as it runs,
        // so a waiting instance learns when it exits (or dies: the mutex is then abandoned). The
        // window serving the control pipe sets the ready event (ControlChannel does, once it
        // listens), so a later instance waits for that - the first one may still be unpacking
        // the engine - instead of timing out and opening a second window.
        private sealed class InstanceGate : IDisposable
        {
            private const int ReadyWaitMs = 30000;    // bounded wait for a starting window
            private const int TakeOverWaitMs = 1000;  // a surviving window takes the pipe over within 500 ms
            private const int RetryMs = 500;

            private readonly string _pipe;
            private Mutex _mutex;
            private bool _owned;

            public InstanceGate(string pipe) { _pipe = pipe; }

            // True when a running window took the request, so this process should exit.
            public bool HandOff(LaunchOptions opt)
            {
                bool createdNew;
                try { _mutex = new Mutex(true, ControlChannel.InstanceMutexName(_pipe), out createdNew); }
                catch (Exception ex)
                {
                    Log.Warn("single instance unavailable, opening a new window: " + ex.Message);
                    return false;
                }
                _owned = createdNew;

                string verb;
                List<string> lines;
                BuildRequest(opt, out verb, out lines);
                EventWaitHandle ready = null;
                try { ready = new EventWaitHandle(false, EventResetMode.ManualReset, ControlChannel.ReadyEventName(_pipe)); }
                catch (Exception ex) { Log.Warn("single instance: ready event unavailable: " + ex.Message); }
                try
                {
                    // First instance - unless a window without the mutex (started with --new, or one
                    // that outlived the first window) already serves the pipe.
                    if (_owned) return ready != null && ready.WaitOne(0) && Send(verb, lines, 1000);
                    return WaitAndSend(ready, verb, lines);
                }
                finally
                {
                    if (ready != null) ready.Dispose();
                }
            }

            // Another instance holds the mutex: wait (bounded) until a window serves the pipe.
            private bool WaitAndSend(EventWaitHandle ready, string verb, List<string> lines)
            {
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    int left = ReadyWaitMs - (int)clock.ElapsedMilliseconds;
                    if (left <= 0) break;
                    int signaled;
                    try
                    {
                        signaled = ready != null ? WaitHandle.WaitAny(new WaitHandle[] { _mutex, ready }, left)
                                                 : (_mutex.WaitOne(left) ? 0 : WaitHandle.WaitTimeout);
                    }
                    catch (AbandonedMutexException) { signaled = 0; } // its owner died; the mutex is ours now
                    if (signaled == WaitHandle.WaitTimeout) break;
                    if (signaled == 0) return TookOver(ready, verb, lines);
                    if (Send(verb, lines, 2000)) return true;
                    // Ready, but nobody took the request: that window is closing, or another one is
                    // taking the pipe over. Wait a moment for either.
                    bool acquired;
                    try { acquired = _mutex.WaitOne(Math.Max(1, Math.Min(RetryMs, left))); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (acquired) return TookOver(ready, verb, lines);
                }
                Log.Info("single instance: no window became ready within " + ReadyWaitMs / 1000 + " s");
                return Send(verb, lines, 1000);
            }

            // The first instance has exited and this process owns the mutex now. A window that
            // outlived it takes the pipe over shortly; otherwise this process opens the window.
            private bool TookOver(EventWaitHandle ready, string verb, List<string> lines)
            {
                _owned = true;
                if (ready == null) return false;
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    int left = TakeOverWaitMs - (int)clock.ElapsedMilliseconds;
                    if (left <= 0 || !ready.WaitOne(left)) return false;
                    if (Send(verb, lines, Math.Min(left, RetryMs))) return true;
                    Thread.Sleep(50); // a stale signal from a window that died: look again shortly
                }
            }

            private bool Send(string verb, List<string> lines, int connectMs)
            {
                string response;
                if (!ControlChannel.TrySend(_pipe, verb, lines, out response, connectMs)) return false;
                // The window answered but could not act on it (it is closing): not delivered.
                if (response.StartsWith("error: closing", StringComparison.Ordinal) || response.StartsWith("error: not handled", StringComparison.Ordinal))
                {
                    Log.Info("single instance: running window answered \"" + response + "\"");
                    return false;
                }
                Log.Info("single instance: handed \"" + verb + "\" to the running window");
                return true;
            }

            // Released on the thread that took it (Main's), when this process's window has closed.
            public void Dispose()
            {
                if (_mutex == null) return;
                if (_owned)
                {
                    try { _mutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                    _owned = false;
                }
                _mutex.Dispose();
                _mutex = null;
            }
        }

        // The hand-off request. Paths are made absolute against OUR working directory (the running
        // window has its own); the playback switches follow as "--name=value" lines, which the
        // window reads back with LaunchOptions.Parse. --enqueue appends instead of replacing.
        // Nothing to open and no switch = just bring the window forward.
        private static void BuildRequest(LaunchOptions opt, out string verb, out List<string> lines)
        {
            lines = new List<string>();
            foreach (var raw in opt.Paths)
            {
                string p = (raw ?? "").Trim();
                if (p.Length >= 2 && p[0] == '"' && p[p.Length - 1] == '"') p = p.Substring(1, p.Length - 2).Trim();
                if (p.Length == 0) continue;
                if (!Playlist.IsUrl(p))
                {
                    try { p = Path.GetFullPath(p); }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException) { }
                }
                lines.Add(p);
            }
            int paths = lines.Count;
            var switches = opt.PlaybackSwitches();
            if (paths == 0 && (switches.Count == 0 || (switches.Count == 1 && opt.Enqueue)))
            {
                verb = "activate";
                lines.Clear();
                return;
            }
            verb = opt.Enqueue && paths > 0 ? "append" : "open";
            lines.AddRange(switches);
            // Our start time lets the window tell an Explorer multi-select (all launched together)
            // from separate double-clicks.
            if (paths > 0) lines.Add("--launched=" + LaunchOptions.ProcessStartUtcTicks().ToString(CultureInfo.InvariantCulture));
        }

        private static void ReportCrash(Exception ex, bool fatal)
        {
            Log.Error(fatal ? "fatal unhandled exception" : "unhandled UI exception", ex);
            try
            {
                string crashLog = Path.Combine(Path.GetDirectoryName(LaunchOptions.DefaultLogPath), "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(crashLog));
                File.AppendAllText(crashLog, DateTime.Now.ToString("s") + " " + ex + Environment.NewLine + Environment.NewLine);
                if (!fatal)
                    MessageBox.Show("Something went wrong, but VidFlow will keep running.\n\n" + (ex != null ? ex.Message : "") +
                        "\n\nDetails were written to " + crashLog, "VidFlow Player", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        // First run of the single-file build: unpack the engine behind a small splash.
        private static void RunWithSplash(Action work)
        {
            Exception error = null;
            var done = new ManualResetEvent(false);
            var worker = new Thread(() =>
            {
                try { work(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            }) { IsBackground = true, Name = "engine extract" };

            using (var splash = new Form())
            {
                splash.FormBorderStyle = FormBorderStyle.None;
                splash.StartPosition = FormStartPosition.CenterScreen;
                splash.BackColor = Theme.Panel;
                splash.ClientSize = new Size(Theme.Scale(null, 360), Theme.Scale(null, 90));
                splash.ShowInTaskbar = true;
                splash.Text = "VidFlow Player";
                var label = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Theme.Text,
                    Font = Theme.UiFont(10f),
                    Text = "Preparing VidFlow Player for first use…",
                };
                splash.Controls.Add(label);
                var timer = new System.Windows.Forms.Timer { Interval = 50 };
                timer.Tick += (s, e) => { if (done.WaitOne(0)) { timer.Stop(); splash.Close(); } };
                splash.Shown += (s, e) => { worker.Start(); timer.Start(); };
                Application.Run(splash);
                timer.Dispose();
            }
            done.WaitOne();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
