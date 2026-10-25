using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VidFlow.Core
{
    // Raw P/Invoke surface for libmpv's client API (client.h, API 2.x).
    // All strings crossing this boundary are UTF-8; use Utf8 helpers below.
    internal static class LibMpv
    {
        public const string Dll = "libmpv-2.dll";

        public enum Format
        {
            None = 0, String = 1, OsdString = 2, Flag = 3, Int64 = 4, Double = 5,
            Node = 6, NodeArray = 7, NodeMap = 8, ByteArray = 9
        }

        public enum EventId
        {
            None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4,
            CommandReply = 5, StartFile = 6, EndFile = 7, FileLoaded = 8, Idle = 11, Tick = 14,
            ClientMessage = 16, VideoReconfig = 17, AudioReconfig = 18, Seek = 20,
            PlaybackRestart = 21, PropertyChange = 22, QueueOverflow = 24, Hook = 25
        }

        public enum EndFileReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

        [StructLayout(LayoutKind.Sequential)]
        public struct Event
        {
            public EventId event_id;
            public int error;
            public ulong reply_userdata;
            public IntPtr data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventProperty
        {
            public IntPtr name;
            public Format format;
            public IntPtr data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventLogMessage
        {
            public IntPtr prefix;
            public IntPtr level;
            public IntPtr text;
            public int log_level;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventEndFile
        {
            public int reason;
            public int error;
            public long playlist_entry_id;
            public long playlist_insert_id;
            public int playlist_insert_num_entries;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventStartFile
        {
            public long playlist_entry_id;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventClientMessage
        {
            public int num_args;
            public IntPtr args;
        }

        // mpv_node: 8-byte union followed by the format tag.
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct Node
        {
            [FieldOffset(0)] public IntPtr ptr;      // string / list / byte array
            [FieldOffset(0)] public int flag;
            [FieldOffset(0)] public long int64;
            [FieldOffset(0)] public double dbl;
            [FieldOffset(8)] public Format format;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NodeList
        {
            public int num;
            public IntPtr values; // Node*
            public IntPtr keys;   // char** (maps only)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ByteArray
        {
            public IntPtr data;
            public UIntPtr size;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void WakeupCallback(IntPtr d);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong mpv_client_api_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_error_string(int error);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free(IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_create();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_initialize(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_terminate_destroy(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_option_string(IntPtr ctx, IntPtr name, IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command(IntPtr ctx, IntPtr args);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command_ret(IntPtr ctx, IntPtr args, ref Node result);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command_async(IntPtr ctx, ulong reply_userdata, IntPtr args);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(IntPtr ctx, IntPtr name, Format format, ref long data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(IntPtr ctx, IntPtr name, Format format, ref double data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(IntPtr ctx, IntPtr name, Format format, ref int data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property_string(IntPtr ctx, IntPtr name, IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr ctx, IntPtr name, Format format, out long data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr ctx, IntPtr name, Format format, out double data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr ctx, IntPtr name, Format format, out int data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr ctx, IntPtr name, Format format, ref Node data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_get_property_string(IntPtr ctx, IntPtr name);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property_async(IntPtr ctx, ulong reply_userdata, IntPtr name, Format format);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_observe_property(IntPtr ctx, ulong reply_userdata, IntPtr name, Format format);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_request_log_messages(IntPtr ctx, IntPtr min_level);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_set_wakeup_callback(IntPtr ctx, WakeupCallback cb, IntPtr d);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free_node_contents(ref Node node);

        public static string ErrorString(int error)
        {
            try { return Utf8.FromNative(mpv_error_string(error)) ?? ("error " + error); }
            catch { return "error " + error; }
        }

        // Converts an mpv_node into plain .NET values:
        // string, bool, long, double, List<object>, Dictionary<string, object>, byte[] or null.
        public static object NodeToObject(Node node)
        {
            switch (node.format)
            {
                case Format.String:
                case Format.OsdString:
                    return Utf8.FromNative(node.ptr);
                case Format.Flag:
                    return node.flag != 0;
                case Format.Int64:
                    return node.int64;
                case Format.Double:
                    return node.dbl;
                case Format.NodeArray:
                case Format.NodeMap:
                {
                    if (node.ptr == IntPtr.Zero) return null;
                    var list = Marshal.PtrToStructure<NodeList>(node.ptr);
                    int size = Marshal.SizeOf<Node>();
                    if (node.format == Format.NodeArray)
                    {
                        var arr = new List<object>(list.num);
                        for (int i = 0; i < list.num; i++)
                            arr.Add(NodeToObject(Marshal.PtrToStructure<Node>(list.values + i * size)));
                        return arr;
                    }
                    var map = new Dictionary<string, object>(list.num, StringComparer.Ordinal);
                    for (int i = 0; i < list.num; i++)
                    {
                        string key = Utf8.FromNative(Marshal.ReadIntPtr(list.keys, i * IntPtr.Size));
                        map[key ?? ""] = NodeToObject(Marshal.PtrToStructure<Node>(list.values + i * size));
                    }
                    return map;
                }
                case Format.ByteArray:
                {
                    if (node.ptr == IntPtr.Zero) return null;
                    var ba = Marshal.PtrToStructure<ByteArray>(node.ptr);
                    var bytes = new byte[(long)ba.size.ToUInt64()];
                    if (bytes.Length > 0) Marshal.Copy(ba.data, bytes, 0, bytes.Length);
                    return bytes;
                }
                default:
                    return null;
            }
        }
    }

    // UTF-8 marshalling helpers (the .NET Framework marshaller has no UTF-8 string arrays).
    internal static class Utf8
    {
        public static IntPtr ToNative(string s)
        {
            if (s == null) return IntPtr.Zero;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0);
            return p;
        }

        public static string FromNative(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] bytes = new byte[len];
            Marshal.Copy(p, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }

        // NULL-terminated char*[] for mpv_command and friends. Dispose frees everything.
        public sealed class StringArray : IDisposable
        {
            private readonly IntPtr[] _items;
            public IntPtr Pointer { get; private set; }

            public StringArray(string[] values)
            {
                _items = new IntPtr[values.Length];
                for (int i = 0; i < values.Length; i++) _items[i] = ToNative(values[i] ?? "");
                Pointer = Marshal.AllocHGlobal(IntPtr.Size * (values.Length + 1));
                for (int i = 0; i < values.Length; i++) Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, _items[i]);
                Marshal.WriteIntPtr(Pointer, values.Length * IntPtr.Size, IntPtr.Zero);
            }

            public void Dispose()
            {
                if (Pointer == IntPtr.Zero) return;
                foreach (var p in _items) Marshal.FreeHGlobal(p);
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
        }

        public sealed class Str : IDisposable
        {
            public IntPtr Pointer { get; private set; }
            public Str(string s) { Pointer = ToNative(s); }
            public void Dispose()
            {
                if (Pointer != IntPtr.Zero) Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }
}
