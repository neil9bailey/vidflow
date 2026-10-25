using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace VidFlow.Core
{
    // Local named-pipe channel. Used for single-instance hand-off ("open" files in the
    // running window) and, when started with --automation, for scripted testing.
    //
    // Wire format (UTF-8, one request per connection):
    //   request : verb\n arg1\n arg2\n ... <<END>>\n
    //   response: line\n line\n ... <<END>>\n
    //
    // Server: one process owns the pipe name. Its first instance is created with
    // FILE_FLAG_FIRST_PIPE_INSTANCE and the next instance always exists before the served one
    // closes, so the name never lapses while the owner runs. A window that cannot get the name
    // retries every 500 ms, so a surviving window takes over when the owner exits. Each request
    // has a deadline and a size cap, so an idle or runaway client cannot block hand-offs. Once
    // listening, the server sets the named event ReadyEventName(pipe), which a starting
    // instance waits on before handing its files over (reset when the server stops).
    // Client: talks only to a pipe served by a process of the current user.
    public sealed class ControlChannel : IDisposable
    {
        public const string Terminator = "<<END>>";

        private const int RetryMs = 500;               // server creation retry while another process owns the name
        private const int RequestTimeoutMs = 3000;     // the whole request must arrive within this
        private const int ResponseTimeoutMs = 3000;    // writing the response + the client reading it
        private const int HandlerTimeoutMs = 15000;    // the UI-thread handler
        private const int MaxRequestBytes = 1 << 20;
        private const int MaxRequestLines = 10000;
        private const long MaxResponseBytes = 64L << 20;
        private const uint MaxInstances = 2;           // the one being served + the next one listening
        private const uint BufferSize = 4096;

        private readonly string _name;
        private readonly Control _sync;
        private readonly Func<string, string[], string> _handler;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly object _gate = new object();
        private EventWaitHandle _ready;
        private bool _disposed;
        private Thread _thread;

        public static string DefaultPipeName
        {
            get { return "VidFlow.ctl." + Process.GetCurrentProcess().SessionId + "." + Environment.UserName; }
        }

        // Named kernel objects of the single-instance protocol (session-local, no backslashes).
        public static string ReadyEventName(string pipeName) { return "VidFlow.ready." + pipeName; }
        public static string InstanceMutexName(string pipeName) { return "VidFlow.instance." + pipeName; }

        // handler(verb, args) runs on the UI thread and returns the response text.
        public ControlChannel(string pipeName, Control sync, Func<string, string[], string> handler)
        {
            _name = pipeName;
            _sync = sync;
            _handler = handler;
        }

        public void Start()
        {
            _thread = new Thread(ServerLoop) { IsBackground = true, Name = "VidFlow control pipe" };
            _thread.Start();
        }

        private static PipeSecurity CreateSecurity()
        {
            var sec = new PipeSecurity();
            using (var me = WindowsIdentity.GetCurrent())
                sec.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return sec;
        }

        // CreateNamedPipe directly: .NET Framework cannot pass FILE_FLAG_FIRST_PIPE_INSTANCE, which
        // makes the first instance fail while any other process (another window, or another user
        // squatting the name) holds it, instead of silently joining that process's pipe.
        private NamedPipeServerStream CreateInstance(bool first)
        {
            uint openMode = Win32.PIPE_ACCESS_DUPLEX | Win32.FILE_FLAG_OVERLAPPED | (first ? Win32.FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
            byte[] descriptor = CreateSecurity().GetSecurityDescriptorBinaryForm();
            var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            IntPtr h;
            try
            {
                var sa = new Win32.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(Win32.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = pin.AddrOfPinnedObject(),
                };
                h = Win32.CreateNamedPipe(@"\\.\pipe\" + _name, openMode, Win32.PIPE_REJECT_REMOTE_CLIENTS,
                    MaxInstances, BufferSize, BufferSize, 0, ref sa);
            }
            finally
            {
                pin.Free();
            }
            if (h == Win32.INVALID_HANDLE_VALUE) throw new Win32Exception(Marshal.GetLastWin32Error());
            var handle = new SafePipeHandle(h, true);
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private void ServerLoop()
        {
            NamedPipeServerStream listening = null;
            bool waiting = false; // logged once that another process owns the name
            try
            {
                while (!_stop.WaitOne(0))
                {
                    try
                    {
                        if (listening == null)
                        {
                            try { listening = CreateInstance(true); }
                            catch (Exception ex)
                            {
                                // Another VidFlow window owns the pipe (single instance off, --new) or is
                                // still closing. Keep trying, so this window takes over when it exits.
                                if (!waiting) Log.Info("control pipe busy, retrying every " + RetryMs + " ms: " + ex.Message);
                                waiting = true;
                                if (_stop.WaitOne(RetryMs)) break;
                                continue;
                            }
                            Log.Info("control pipe listening: " + _name);
                            waiting = false;
                            SignalReady();
                        }
                        var ar = listening.BeginWaitForConnection(null, null);
                        if (WaitHandle.WaitAny(new[] { ar.AsyncWaitHandle, _stop }) == 1) break;
                        var connected = listening;
                        listening = null;
                        // The next instance exists before this one closes: the name stays ours, and the
                        // next client can connect while this one is served.
                        try { listening = CreateInstance(false); }
                        catch (Exception ex) { Log.Warn("control pipe: next instance failed: " + ex.Message); }
                        using (connected)
                        {
                            try
                            {
                                connected.EndWaitForConnection(ar);
                                Serve(connected);
                            }
                            catch (Exception ex)
                            {
                                Log.Warn("control pipe: " + ex.Message); // that client's problem; keep serving
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Unexpected (the listening instance broke): start over with a fresh one.
                        Log.Warn("control pipe: " + ex.Message);
                        if (listening != null) listening.Dispose();
                        listening = null;
                        if (_stop.WaitOne(RetryMs)) break;
                    }
                }
            }
            finally
            {
                ClearReady(); // before the name is released, so a window taking it over signals afresh
                if (listening != null) listening.Dispose();
            }
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            bool terminated;
            string failure;
            var lines = ReadLines(pipe, RequestTimeoutMs, MaxRequestBytes, MaxRequestLines, _stop, out terminated, out failure);
            if (lines == null)
            {
                Log.Warn("control pipe: client dropped: " + failure);
                return;
            }
            // A sender that died mid-request (no terminator) must not run half a request.
            if (!terminated)
            {
                if (lines.Count > 0) Log.Warn("control pipe: client closed before the request ended, dropped");
                return;
            }
            if (lines.Count == 0) return;
            string verb = lines[0].Trim();
            string[] args = lines.GetRange(1, lines.Count - 1).ToArray();

            string response = "error: not handled";
            if (_sync.IsHandleCreated && !_sync.IsDisposed)
            {
                try
                {
                    var call = _sync.BeginInvoke(_handler, verb, args);
                    int done = WaitHandle.WaitAny(new[] { call.AsyncWaitHandle, _stop }, HandlerTimeoutMs);
                    if (done == 0) response = (string)_sync.EndInvoke(call) ?? "";
                    else response = done == 1 ? "error: closing" : "error: timeout";
                }
                catch (Exception ex)
                {
                    response = "error: " + ex.Message;
                }
            }

            byte[] data = new UTF8Encoding(false).GetBytes(response + "\n" + Terminator + "\n");
            var clock = Stopwatch.StartNew();
            var write = pipe.BeginWrite(data, 0, data.Length, null, null);
            if (!Await(write, ResponseTimeoutMs, null))
            {
                Log.Warn("control pipe: client did not read the response, dropped");
                return;
            }
            pipe.EndWrite(write);
            // A bounded WaitForPipeDrain: the client closes its end once it has read the terminator.
            var sink = new byte[256];
            try
            {
                while (true)
                {
                    var read = pipe.BeginRead(sink, 0, sink.Length, null, null);
                    if (!Await(read, ResponseTimeoutMs - (int)clock.ElapsedMilliseconds, _stop)) break;
                    if (pipe.EndRead(read) == 0) break;
                }
            }
            catch (IOException) { } // the client went away: done
        }

        // Reads lines up to the terminator line (or the end of the stream). null when the deadline
        // passed, a cap was exceeded or `cancel` was set (the reason in `failure`).
        private static List<string> ReadLines(PipeStream pipe, int timeoutMs, long maxBytes, int maxLines, WaitHandle cancel,
            out bool terminated, out string failure)
        {
            terminated = false;
            failure = null;
            var utf8 = new UTF8Encoding(false);
            var clock = Stopwatch.StartNew();
            var buffer = new byte[4096];
            var line = new MemoryStream();
            var lines = new List<string>();
            long total = 0;
            while (true)
            {
                var ar = pipe.BeginRead(buffer, 0, buffer.Length, null, null);
                if (!Await(ar, timeoutMs - (int)clock.ElapsedMilliseconds, cancel))
                {
                    failure = "no complete message within " + timeoutMs + " ms";
                    return null;
                }
                int n = pipe.EndRead(ar);
                if (n == 0) break; // the other end closed: what arrived is the message
                total += n;
                if (total > maxBytes)
                {
                    failure = "message larger than " + maxBytes + " bytes";
                    return null;
                }
                int start = 0;
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    string text = Decode(utf8, line);
                    line.SetLength(0);
                    if (text == Terminator)
                    {
                        terminated = true;
                        return lines;
                    }
                    lines.Add(text);
                    if (lines.Count > maxLines)
                    {
                        failure = "more than " + maxLines + " lines";
                        return null;
                    }
                }
                line.Write(buffer, start, n - start);
            }
            if (line.Length > 0) lines.Add(Decode(utf8, line));
            return lines;
        }

        private static string Decode(Encoding utf8, MemoryStream line)
        {
            int length = (int)line.Length;
            byte[] bytes = line.GetBuffer();
            if (length > 0 && bytes[length - 1] == (byte)'\r') length--; // "\r\n" senders
            return utf8.GetString(bytes, 0, length);
        }

        // Waits for an async pipe operation; false when it did not finish within ms or `cancel` was
        // set. An abandoned operation is cancelled when the stream is disposed.
        private static bool Await(IAsyncResult ar, int ms, WaitHandle cancel)
        {
            if (ar.IsCompleted) return true;
            if (ms <= 0) return false;
            return cancel != null ? WaitHandle.WaitAny(new[] { ar.AsyncWaitHandle, cancel }, ms) == 0 : ar.AsyncWaitHandle.WaitOne(ms);
        }

        private void SignalReady()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    if (_ready == null) _ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(_name));
                    _ready.Set();
                }
                catch (Exception ex)
                {
                    Log.Warn("control pipe: ready signal failed: " + ex.Message);
                }
            }
        }

        private void ClearReady()
        {
            lock (_gate)
            {
                if (_ready == null) return;
                try { _ready.Reset(); }
                catch (Exception) { }
                _ready.Dispose();
                _ready = null;
            }
        }

        // Client side. Returns false when no server answered within the timeouts (connect:
        // timeoutMs; request + complete response: responseTimeoutMs), or when the pipe is served
        // by a process that does not run as the current user.
        public static bool TrySend(string pipeName, string verb, IEnumerable<string> args, out string response,
            int timeoutMs = 2000, int responseTimeoutMs = 20000)
        {
            response = null;
            try
            {
                // Identification: the server may learn who we are, but cannot act as us.
                using (var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification))
                {
                    c.Connect(Math.Max(1, timeoutMs));
                    string refused = CheckServerUser(c);
                    if (refused != null)
                    {
                        Log.Warn("control pipe " + pipeName + " refused: " + refused);
                        return false;
                    }
                    var sb = new StringBuilder();
                    sb.Append(verb).Append('\n');
                    if (args != null) foreach (var a in args) sb.Append(a).Append('\n');
                    sb.Append(Terminator).Append('\n');
                    byte[] data = new UTF8Encoding(false).GetBytes(sb.ToString());
                    var clock = Stopwatch.StartNew();
                    var write = c.BeginWrite(data, 0, data.Length, null, null);
                    if (!Await(write, responseTimeoutMs, null))
                    {
                        Log.Info("control pipe send failed: the server did not read the request");
                        return false;
                    }
                    c.EndWrite(write);
                    bool terminated;
                    string failure;
                    var lines = ReadLines(c, responseTimeoutMs - (int)clock.ElapsedMilliseconds, MaxResponseBytes, int.MaxValue, null,
                        out terminated, out failure);
                    if (lines == null || !terminated)
                    {
                        Log.Info("control pipe send failed: " + (failure ?? "connection closed before the response ended"));
                        return false;
                    }
                    response = string.Join("\n", lines);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info("control pipe send failed: " + ex.Message);
                return false;
            }
        }

        // The pipe name is predictable and pipe names are machine-wide, so before saying anything
        // make sure the serving process runs as the current user. null = OK, else the reason.
        private static string CheckServerUser(NamedPipeClientStream c)
        {
            uint pid;
            if (!Win32.GetNamedPipeServerProcessId(c.SafePipeHandle, out pid))
                return "server process unknown (error " + Marshal.GetLastWin32Error() + ")";
            SecurityIdentifier user = ProcessUser(pid);
            if (user == null) return "cannot read the user of server process " + pid;
            using (var me = WindowsIdentity.GetCurrent())
                if (!user.Equals(me.User)) return "server process " + pid + " runs as " + user.Value;
            return null;
        }

        private static SecurityIdentifier ProcessUser(uint pid)
        {
            IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;
            IntPtr token = IntPtr.Zero, info = IntPtr.Zero;
            try
            {
                if (!Win32.OpenProcessToken(process, Win32.TOKEN_QUERY, out token)) return null;
                int size;
                Win32.GetTokenInformation(token, Win32.TokenUser, IntPtr.Zero, 0, out size);
                if (size <= 0) return null;
                info = Marshal.AllocHGlobal(size);
                if (!Win32.GetTokenInformation(token, Win32.TokenUser, info, size, out size)) return null;
                return new SecurityIdentifier(Marshal.ReadIntPtr(info)); // TOKEN_USER.User.Sid
            }
            finally
            {
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                if (token != IntPtr.Zero) Win32.CloseHandle(token);
                Win32.CloseHandle(process);
            }
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            ClearReady();
            _stop.Set();
        }

        private static class Win32
        {
            public const uint PIPE_ACCESS_DUPLEX = 0x00000003;
            public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
            public const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
            public const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008; // byte type/read mode, blocking
            public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            public const uint TOKEN_QUERY = 0x0008;
            public const int TokenUser = 1;
            public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

            [StructLayout(LayoutKind.Sequential)]
            public struct SECURITY_ATTRIBUTES
            {
                public int nLength;
                public IntPtr lpSecurityDescriptor;
                public int bInheritHandle;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
                uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SECURITY_ATTRIBUTES securityAttributes);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
