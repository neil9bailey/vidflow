using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VidFlow.Core
{
    public enum MpvEndReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

    public sealed class MpvException : Exception
    {
        public int Code { get; private set; }
        public MpvException(string message, int code) : base(message + ": " + LibMpv.ErrorString(code)) { Code = code; }
    }

    // One libmpv instance. Several may exist (main player + seek-bar thumbnail player).
    //
    // Threading: every public member must be called on the UI thread that owns `sync`.
    // libmpv's wakeup callback posts a drain to that thread, so all events and
    // property-change callbacks are raised on the UI thread too. Event data is copied
    // into managed values before any callback runs, so handlers may re-enter the
    // message loop (modal dialogs) without corrupting the event stream.
    public sealed class MpvPlayer : IDisposable
    {
        private readonly Control _sync;
        private IntPtr _h;
        private readonly LibMpv.WakeupCallback _wakeup;
        private readonly MethodInvoker _drain;
        private int _drainQueued;
        private ulong _nextId = 1;
        private readonly Dictionary<ulong, Action<object>> _observers = new Dictionary<ulong, Action<object>>();
        private readonly Dictionary<ulong, Action<int, object>> _replies = new Dictionary<ulong, Action<int, object>>();
        private readonly Dictionary<ulong, Action<object>> _getReplies = new Dictionary<ulong, Action<object>>();

        public string Name { get; private set; }
        public bool IsInitialized { get; private set; }
        public bool IsAlive { get { return _h != IntPtr.Zero; } }

        public event Action StartFile;
        public event Action FileLoaded;
        public event Action<MpvEndReason, int> EndFile;          // reason, mpv error code (0 = none)
        // Same events with mpv's playlist_entry_id, which pairs a START_FILE with its END_FILE
        // without asking mpv (synchronously) which file is current.
        public event Action<long> StartFileEntry;
        public event Action<MpvEndReason, int, long> EndFileEntry; // reason, error, playlist_entry_id
        public event Action PlaybackRestart;                     // a seek or file start finished; a frame is shown
        public event Action Seek;
        public event Action VideoReconfig;
        public event Action AudioReconfig;
        public event Action Shutdown;
        public event Action<string[]> ClientMessage;
        public event Action<string, string, string> LogMessage;  // level, prefix, text

        public static ulong ClientApiVersion { get { return LibMpv.mpv_client_api_version(); } }

        public MpvPlayer(Control sync, string name = "mpv")
        {
            if (sync == null) throw new ArgumentNullException("sync");
            _sync = sync;
            Name = name;
            _h = LibMpv.mpv_create();
            if (_h == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed (out of memory?)");
            _wakeup = OnWakeup;
            _drain = Drain;
        }

        // Before Initialize(): sets an option. After: identical to SetProperty(string).
        public bool SetOption(string name, string value)
        {
            if (_h == IntPtr.Zero) return false;
            int err;
            using (var n = new Utf8.Str(name))
            using (var v = new Utf8.Str(value))
                err = LibMpv.mpv_set_option_string(_h, n.Pointer, v.Pointer);
            if (err < 0) Log.Warn(Name + ": option " + name + "=" + value + " failed: " + LibMpv.ErrorString(err));
            return err >= 0;
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            int err = LibMpv.mpv_initialize(_h);
            if (err < 0) throw new MpvException("mpv_initialize failed", err);
            IsInitialized = true;
            LibMpv.mpv_set_wakeup_callback(_h, _wakeup, IntPtr.Zero);
            PumpEvents();
        }

        // Levels: "no", "fatal", "error", "warn", "info", "v", "debug", "trace".
        public void RequestLogMessages(string minLevel)
        {
            if (_h == IntPtr.Zero) return;
            using (var l = new Utf8.Str(minLevel)) LibMpv.mpv_request_log_messages(_h, l.Pointer);
        }

        // ---- commands -------------------------------------------------------------

        // Synchronous command. Returns the mpv error code (>= 0 on success).
        public int Command(params string[] args)
        {
            if (_h == IntPtr.Zero) return -1;
            int err;
            using (var a = new Utf8.StringArray(args)) err = LibMpv.mpv_command(_h, a.Pointer);
            if (err < 0) Log.Info(Name + ": command [" + string.Join(" ", args) + "] -> " + LibMpv.ErrorString(err));
            return err;
        }

        // Synchronous command returning its result node as plain .NET values (or null).
        public object CommandRet(params string[] args)
        {
            if (_h == IntPtr.Zero) return null;
            var node = new LibMpv.Node();
            int err;
            using (var a = new Utf8.StringArray(args)) err = LibMpv.mpv_command_ret(_h, a.Pointer, ref node);
            if (err < 0)
            {
                Log.Info(Name + ": command_ret [" + string.Join(" ", args) + "] -> " + LibMpv.ErrorString(err));
                return null;
            }
            try { return LibMpv.NodeToObject(node); }
            finally { LibMpv.mpv_free_node_contents(ref node); }
        }

        // Fire-and-forget asynchronous command (use for seeks while scrubbing).
        public void CommandAsync(params string[] args)
        {
            CommandAsync(null, args);
        }

        // Asynchronous command; onReply(errorCode, result) runs on the UI thread.
        public void CommandAsync(Action<int, object> onReply, params string[] args)
        {
            if (_h == IntPtr.Zero) return;
            ulong id = 0;
            if (onReply != null) { id = _nextId++; _replies[id] = onReply; }
            int err;
            using (var a = new Utf8.StringArray(args)) err = LibMpv.mpv_command_async(_h, id, a.Pointer);
            if (err < 0)
            {
                if (id != 0) _replies.Remove(id);
                Log.Info(Name + ": command_async [" + string.Join(" ", args) + "] -> " + LibMpv.ErrorString(err));
                if (onReply != null) onReply(err, null);
            }
        }

        // ---- properties -----------------------------------------------------------

        public int SetProperty(string name, string value)
        {
            if (_h == IntPtr.Zero) return -1;
            using (var n = new Utf8.Str(name))
            using (var v = new Utf8.Str(value ?? ""))
                return Report(name, LibMpv.mpv_set_property_string(_h, n.Pointer, v.Pointer));
        }

        public int SetProperty(string name, double value)
        {
            if (_h == IntPtr.Zero) return -1;
            using (var n = new Utf8.Str(name))
                return Report(name, LibMpv.mpv_set_property(_h, n.Pointer, LibMpv.Format.Double, ref value));
        }

        public int SetProperty(string name, long value)
        {
            if (_h == IntPtr.Zero) return -1;
            using (var n = new Utf8.Str(name))
                return Report(name, LibMpv.mpv_set_property(_h, n.Pointer, LibMpv.Format.Int64, ref value));
        }

        public int SetProperty(string name, bool value)
        {
            if (_h == IntPtr.Zero) return -1;
            int flag = value ? 1 : 0;
            using (var n = new Utf8.Str(name))
                return Report(name, LibMpv.mpv_set_property(_h, n.Pointer, LibMpv.Format.Flag, ref flag));
        }

        private int Report(string name, int err)
        {
            if (err < 0) Log.Info(Name + ": set " + name + " -> " + LibMpv.ErrorString(err));
            return err;
        }

        public string GetString(string name)
        {
            if (_h == IntPtr.Zero) return null;
            IntPtr p;
            using (var n = new Utf8.Str(name)) p = LibMpv.mpv_get_property_string(_h, n.Pointer);
            if (p == IntPtr.Zero) return null;
            try { return Utf8.FromNative(p); }
            finally { LibMpv.mpv_free(p); }
        }

        public double? GetDouble(string name)
        {
            if (_h == IntPtr.Zero) return null;
            double v;
            using (var n = new Utf8.Str(name))
                return LibMpv.mpv_get_property(_h, n.Pointer, LibMpv.Format.Double, out v) >= 0 ? v : (double?)null;
        }

        public long? GetLong(string name)
        {
            if (_h == IntPtr.Zero) return null;
            long v;
            using (var n = new Utf8.Str(name))
                return LibMpv.mpv_get_property(_h, n.Pointer, LibMpv.Format.Int64, out v) >= 0 ? v : (long?)null;
        }

        public bool? GetFlag(string name)
        {
            if (_h == IntPtr.Zero) return null;
            int v;
            using (var n = new Utf8.Str(name))
                return LibMpv.mpv_get_property(_h, n.Pointer, LibMpv.Format.Flag, out v) >= 0 ? v != 0 : (bool?)null;
        }

        // Structured property (track-list, chapter-list, metadata, video-params, ...) as
        // Dictionary<string, object> / List<object> / scalars, or null when unavailable.
        public object GetNode(string name)
        {
            if (_h == IntPtr.Zero) return null;
            var node = new LibMpv.Node();
            int err;
            using (var n = new Utf8.Str(name)) err = LibMpv.mpv_get_property(_h, n.Pointer, LibMpv.Format.Node, ref node);
            if (err < 0) return null;
            try { return LibMpv.NodeToObject(node); }
            finally { LibMpv.mpv_free_node_contents(ref node); }
        }

        // Non-blocking read: cb(value) runs on the UI thread once mpv answers (null when the
        // property is unavailable). Synchronous Get* calls wait for the playback core, which can
        // take hundreds of ms while a file or decoder is starting; use this from event handlers.
        public void GetPropertyAsync(string name, Action<object> cb)
        {
            if (_h == IntPtr.Zero || cb == null) return;
            ulong id = _nextId++;
            _getReplies[id] = cb;
            int err;
            using (var n = new Utf8.Str(name)) err = LibMpv.mpv_get_property_async(_h, id, n.Pointer, LibMpv.Format.Node);
            if (err < 0)
            {
                _getReplies.Remove(id);
                cb(null);
            }
        }

        // ---- observation ----------------------------------------------------------
        // Callbacks run on the UI thread. A null value means "property unavailable"
        // (e.g. duration while nothing is loaded). mpv sends the current value once
        // right after observing.

        public void ObserveDouble(string name, Action<double?> cb)
        {
            Observe(name, LibMpv.Format.Double, v => cb(v is double ? (double)v : (double?)null));
        }

        public void ObserveLong(string name, Action<long?> cb)
        {
            Observe(name, LibMpv.Format.Int64, v => cb(v is long ? (long)v : (long?)null));
        }

        public void ObserveFlag(string name, Action<bool?> cb)
        {
            Observe(name, LibMpv.Format.Flag, v => cb(v is bool ? (bool)v : (bool?)null));
        }

        public void ObserveString(string name, Action<string> cb)
        {
            Observe(name, LibMpv.Format.String, v => cb(v as string));
        }

        public void ObserveNode(string name, Action<object> cb)
        {
            Observe(name, LibMpv.Format.Node, cb);
        }

        private void Observe(string name, LibMpv.Format format, Action<object> cb)
        {
            if (_h == IntPtr.Zero) return;
            ulong id = _nextId++;
            _observers[id] = cb;
            int err;
            using (var n = new Utf8.Str(name)) err = LibMpv.mpv_observe_property(_h, id, n.Pointer, format);
            if (err < 0)
            {
                _observers.Remove(id);
                Log.Warn(Name + ": observe " + name + " failed: " + LibMpv.ErrorString(err));
            }
        }

        // ---- event pump -----------------------------------------------------------

        private void OnWakeup(IntPtr d)
        {
            // Called on an arbitrary mpv thread: must not call into mpv here.
            if (Interlocked.Exchange(ref _drainQueued, 1) != 0) return;
            try
            {
                if (_sync.IsHandleCreated && !_sync.IsDisposed) _sync.BeginInvoke(_drain);
                else Volatile.Write(ref _drainQueued, 0);
            }
            catch (Exception) // handle destroyed between the check and BeginInvoke
            {
                Volatile.Write(ref _drainQueued, 0);
            }
        }

        // Drains pending events now. Normally driven by the wakeup callback; call once
        // after the sync control's handle exists if mpv was initialized before that.
        public void PumpEvents()
        {
            if (_sync.IsHandleCreated) Drain();
        }

        private void Drain()
        {
            Volatile.Write(ref _drainQueued, 0);
            for (int i = 0; i < 500; i++)
            {
                if (_h == IntPtr.Zero) return;
                IntPtr p = LibMpv.mpv_wait_event(_h, 0);
                var ev = Marshal.PtrToStructure<LibMpv.Event>(p);
                if (ev.event_id == LibMpv.EventId.None) return;
                try { Dispatch(ev); }
                catch (Exception ex) { Log.Error(Name + ": event handler for " + ev.event_id + " threw", ex); }
            }
            OnWakeup(IntPtr.Zero); // more queued; yield to the message loop and continue later
        }

        private void Dispatch(LibMpv.Event ev)
        {
            switch (ev.event_id)
            {
                case LibMpv.EventId.PropertyChange:
                {
                    var prop = Marshal.PtrToStructure<LibMpv.EventProperty>(ev.data);
                    object value = ReadValue(prop.format, prop.data);
                    Action<object> cb;
                    if (_observers.TryGetValue(ev.reply_userdata, out cb)) cb(value);
                    break;
                }
                case LibMpv.EventId.CommandReply:
                {
                    object result = null;
                    if (ev.data != IntPtr.Zero)
                        result = LibMpv.NodeToObject(Marshal.PtrToStructure<LibMpv.Node>(ev.data));
                    Action<int, object> cb;
                    if (_replies.TryGetValue(ev.reply_userdata, out cb))
                    {
                        _replies.Remove(ev.reply_userdata);
                        cb(ev.error, result);
                    }
                    break;
                }
                case LibMpv.EventId.LogMessage:
                {
                    var msg = Marshal.PtrToStructure<LibMpv.EventLogMessage>(ev.data);
                    string level = Utf8.FromNative(msg.level), prefix = Utf8.FromNative(msg.prefix);
                    string text = (Utf8.FromNative(msg.text) ?? "").TrimEnd('\n');
                    var h = LogMessage;
                    if (h != null) h(level, prefix, text);
                    break;
                }
                case LibMpv.EventId.GetPropertyReply:
                {
                    object value = null;
                    if (ev.error >= 0 && ev.data != IntPtr.Zero)
                    {
                        var prop = Marshal.PtrToStructure<LibMpv.EventProperty>(ev.data);
                        value = ReadValue(prop.format, prop.data);
                    }
                    Action<object> cb;
                    if (_getReplies.TryGetValue(ev.reply_userdata, out cb))
                    {
                        _getReplies.Remove(ev.reply_userdata);
                        cb(value);
                    }
                    break;
                }
                case LibMpv.EventId.EndFile:
                {
                    var ef = Marshal.PtrToStructure<LibMpv.EventEndFile>(ev.data);
                    var h = EndFile;
                    if (h != null) h((MpvEndReason)ef.reason, ef.error);
                    var he = EndFileEntry;
                    if (he != null) he((MpvEndReason)ef.reason, ef.error, ef.playlist_entry_id);
                    break;
                }
                case LibMpv.EventId.StartFile:
                {
                    long entry = ev.data != IntPtr.Zero ? Marshal.PtrToStructure<LibMpv.EventStartFile>(ev.data).playlist_entry_id : 0;
                    Raise(StartFile);
                    var hs = StartFileEntry;
                    if (hs != null) hs(entry);
                    break;
                }
                case LibMpv.EventId.ClientMessage:
                {
                    var cm = Marshal.PtrToStructure<LibMpv.EventClientMessage>(ev.data);
                    var args = new string[cm.num_args];
                    for (int i = 0; i < cm.num_args; i++)
                        args[i] = Utf8.FromNative(Marshal.ReadIntPtr(cm.args, i * IntPtr.Size));
                    var h = ClientMessage;
                    if (h != null) h(args);
                    break;
                }
                case LibMpv.EventId.FileLoaded: Raise(FileLoaded); break;
                case LibMpv.EventId.PlaybackRestart: Raise(PlaybackRestart); break;
                case LibMpv.EventId.Seek: Raise(Seek); break;
                case LibMpv.EventId.VideoReconfig: Raise(VideoReconfig); break;
                case LibMpv.EventId.AudioReconfig: Raise(AudioReconfig); break;
                case LibMpv.EventId.Shutdown: Raise(Shutdown); break;
            }
        }

        private static void Raise(Action a)
        {
            if (a != null) a();
        }

        private static object ReadValue(LibMpv.Format format, IntPtr data)
        {
            if (data == IntPtr.Zero) return null;
            switch (format)
            {
                case LibMpv.Format.Flag: return Marshal.ReadInt32(data) != 0;
                case LibMpv.Format.Int64: return Marshal.ReadInt64(data);
                case LibMpv.Format.Double: return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data));
                case LibMpv.Format.String:
                case LibMpv.Format.OsdString: return Utf8.FromNative(Marshal.ReadIntPtr(data));
                case LibMpv.Format.Node: return LibMpv.NodeToObject(Marshal.PtrToStructure<LibMpv.Node>(data));
                default: return null;
            }
        }

        // ---- shutdown -------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMsg { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out NativeMsg msg, IntPtr hWnd, uint min, uint max, uint remove);

        private const uint PM_NOREMOVE = 0, PM_QS_SENDMESSAGE = 0x0040 << 16;

        // Destroys the core. mpv's video window lives on mpv's own thread as a child of
        // one of ours, so tearing it down sends messages to this (UI) thread. Blocking
        // here in mpv_terminate_destroy would deadlock; instead destroy on a worker and
        // service only *sent* messages (no input, no re-entrant posted messages) while waiting.
        public void Dispose()
        {
            IntPtr h = _h;
            if (h == IntPtr.Zero) return;
            _h = IntPtr.Zero;
            LibMpv.mpv_set_wakeup_callback(h, null, IntPtr.Zero);
            _observers.Clear();
            _replies.Clear();
            _getReplies.Clear();
            var t = new Thread(() => LibMpv.mpv_terminate_destroy(h)) { IsBackground = true, Name = Name + " destroy" };
            t.Start();
            var sw = Stopwatch.StartNew();
            NativeMsg m;
            while (!t.Join(5))
            {
                PeekMessage(out m, IntPtr.Zero, 0, 0, PM_NOREMOVE | PM_QS_SENDMESSAGE);
                if (sw.ElapsedMilliseconds > 5000)
                {
                    Log.Warn(Name + ": mpv_terminate_destroy did not finish within 5 s; abandoning it");
                    break;
                }
            }
        }
    }
}
