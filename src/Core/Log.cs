using System;
using System.IO;
using System.Text;

namespace VidFlow.Core
{
    // Minimal append-only log. Disabled unless Log.Open() was called (--log / --automation).
    public static class Log
    {
        private static readonly object Gate = new object();
        private static StreamWriter _w;

        public static string FilePath { get; private set; }
        public static bool Enabled { get { return _w != null; } }

        public static void Open(string path)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    _w.AutoFlush = true;
                    FilePath = path;
                    Write("INFO", "---- log opened " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ----");
                }
                catch { _w = null; }
            }
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }

        public static void Error(string msg, Exception ex = null)
        {
            Write("ERROR", ex == null ? msg : msg + ": " + ex);
        }

        public static void Write(string level, string msg)
        {
            if (_w == null) return;
            lock (Gate)
            {
                try { if (_w != null) _w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + msg); }
                catch { }
            }
        }
    }
}
