using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace VidFlow.Media
{
    // What the container says about the video track's frame timing.
    public sealed class FrameRateInfo
    {
        public bool Known;          // the video track's frame timestamps were read
        public bool Variable;       // the frames do not sit on one constant-rate grid
        public double Fps;          // constant rate (snapped to n or n·1000/1001 when that close), else the average
        public long FrameCount;     // frames read (the whole track unless Partial)
        public double MinFps, MaxFps; // from the longest / shortest frame interval
        public bool Partial;        // the scan stopped early (time or size cap): judged from the first part
        public string Container = "";
        public string Reason = "";  // why Known is false, or what made it Variable

        public string Summary
        {
            get
            {
                if (!Known) return Container + " unknown (" + Reason + ")";
                var inv = CultureInfo.InvariantCulture;
                return Container + (Variable ? " VFR" : " CFR") + " " + Fps.ToString("0.#####", inv) + " fps, " +
                       FrameCount.ToString(inv) + " frames, " + MinFps.ToString("0.###", inv) + "-" +
                       MaxFps.ToString("0.###", inv) + " fps" + (Partial ? ", partial" : "") +
                       (Reason.Length > 0 ? " (" + Reason + ")" : "");
            }
        }
    }

    // Reads every video frame's presentation timestamp from the container index - MP4/MOV sample
    // tables (also fragmented MP4) or Matroska/WebM block headers - without decoding anything, and
    // decides whether the frames sit on a constant-rate grid. Pure file I/O: call it off the UI
    // thread. Never throws for unreadable or unsupported files (Known = false instead).
    public static class FrameRateProbe
    {
        // A scan that takes longer, or collects more frames, stops and judges from what it read.
        public const int TimeBudgetMs = 1500;
        public const int MaxFrames = 2000000;

        // cancelled (optional, polled from the worker): stop early; the answer is then Partial and unknown.
        public static FrameRateInfo Probe(string path, Func<bool> cancelled = null)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                {
                    var head = new byte[12];
                    int n = ReadFully(fs, head, 0, head.Length);
                    fs.Position = 0;
                    if (n >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
                        return new MatroskaScanner(fs, cancelled).Run();
                    if (n >= 8 && IsIsoBoxType(head, 4))
                        return new Mp4Scanner(fs, cancelled).Run();
                    return Unknown("", "unsupported container");
                }
            }
            // Damaged files must never escape as an exception: this runs on a worker thread.
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                       ex is NotSupportedException || ex is System.Security.SecurityException || ex is OverflowException ||
                                       ex is IndexOutOfRangeException || ex is OutOfMemoryException)
            {
                return Unknown("", ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsIsoBoxType(byte[] b, int o)
        {
            string t = Encoding.ASCII.GetString(b, o, 4);
            return t == "ftyp" || t == "moov" || t == "mdat" || t == "free" || t == "wide" || t == "skip" || t == "pnot" || t == "styp";
        }

        private static FrameRateInfo Unknown(string container, string reason)
        {
            return new FrameRateInfo { Container = container, Reason = reason };
        }

        private static FrameRateInfo Cancelled(string container)
        {
            return new FrameRateInfo { Container = container, Reason = "cancelled", Partial = true };
        }

        private static int ReadFully(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, offset + total, count - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        // ---- judgement ----------------------------------------------------------------------

        // pts: presentation times in ticks of 1/ticksPerSecond s (any order). tickExact: the container
        // stores exact times (MP4); false when they are rounded to whole ticks (Matroska, typically ms).
        // fpsHint: the rate the container declares (0 = none), preferred when it fits the timestamps.
        internal static FrameRateInfo Analyze(List<long> pts, double ticksPerSecond, bool tickExact, double fpsHint, string container, bool partial)
        {
            if (pts.Count < 2 || !(ticksPerSecond > 0)) return Unknown(container, pts.Count < 2 ? "fewer than 2 frames" : "no time base");
            pts.Sort();
            int n = pts.Count;
            double span = pts[n - 1] - pts[0];
            if (!(span > 0)) return Unknown(container, "all frames share one timestamp");
            double d = span / (n - 1); // the grid step a constant rate would have
            long minDelta = long.MaxValue, maxDelta = 0;
            double maxResidual = 0;
            for (int i = 1; i < n; i++)
            {
                long delta = pts[i] - pts[i - 1];
                if (delta < minDelta) minDelta = delta;
                if (delta > maxDelta) maxDelta = delta;
                double residual = Math.Abs(pts[i] - pts[0] - i * d);
                if (residual > maxResidual) maxResidual = residual;
            }
            var info = new FrameRateInfo
            {
                Known = true,
                Container = container,
                FrameCount = n,
                Partial = partial,
                MinFps = maxDelta > 0 ? ticksPerSecond / maxDelta : 0,
                MaxFps = minDelta > 0 ? ticksPerSecond / minDelta : double.PositiveInfinity,
            };
            // Rounded timestamps (ms in Matroska) wobble by up to a tick around the grid; exact ones only by
            // rounding to the time base. A tenth of a frame of drift, or one interval a tenth longer or shorter
            // than the rest, is a real rate change (or a dropped / duplicated frame), not rounding.
            double rounding = tickExact ? 1.01 : 1.51;
            double residualTol = Math.Max(rounding, 0.1 * d);
            double deltaTol = Math.Max(tickExact ? 1.01 : 2.01, 0.1 * d);
            if (maxResidual > residualTol)
            {
                info.Variable = true;
                info.Reason = "drift " + (maxResidual / ticksPerSecond * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " ms";
            }
            else if (minDelta == 0 || maxDelta - minDelta > deltaTol)
            {
                info.Variable = true;
                info.Reason = minDelta == 0 ? "duplicate timestamps" : "interval spread";
            }
            double measured = ticksPerSecond / d;
            if (info.Variable)
            {
                info.Fps = measured;
                return info;
            }
            // How far the measured rate may be from the true one: a grid fitted over the whole span is
            // off by at most the rounding at both ends - or by the interval spread, when the timestamps
            // were rounded more coarsely than their time base (an MKV remuxed to MP4 keeps whole ms).
            double relErr = Math.Max(1e-6, Math.Max(2 * rounding, maxDelta - minDelta) / span);
            double snapped = Snap(measured, relErr);
            if (fpsHint > 0 && Math.Abs(fpsHint - measured) / measured <= Math.Max(relErr, 1e-6))
                snapped = Snap(fpsHint, Math.Max(relErr, 2e-6));
            info.Fps = snapped;
            return info;
        }

        // The nearest common rate (n or n·1000/1001) when it is within the measurement error.
        private static double Snap(double fps, double relErr)
        {
            double best = fps, bestErr = double.MaxValue;
            int r = (int)Math.Round(fps);
            for (int k = Math.Max(1, r - 1); k <= r + 1; k++)
            {
                foreach (double c in new[] { (double)k, k * 1000.0 / 1001.0 })
                {
                    double e = Math.Abs(fps - c) / c;
                    if (e <= relErr && e < bestErr) { best = c; bestErr = e; }
                }
            }
            return best;
        }

        // ---- ISO base media (MP4 / MOV / fragmented MP4) ------------------------------------------

        private sealed class Mp4Track
        {
            public uint Id;
            public bool Video;
            public uint Timescale;
            public List<long> Pts = new List<long>();
            public uint TrexDuration;       // fragment default sample duration (mvex/trex)
            public long NextFragmentDts;    // decode time where the next fragment starts without a tfdt
        }

        private sealed class Mp4Scanner
        {
            private readonly FileStream _fs;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly Dictionary<uint, Mp4Track> _tracks = new Dictionary<uint, Mp4Track>();
            private bool _partial;
            private const long MaxMoovBytes = 256L << 20, MaxMoofBytes = 64L << 20;

            private readonly Func<bool> _cancelled;

            public Mp4Scanner(FileStream fs, Func<bool> cancelled) { _fs = fs; _cancelled = cancelled; }

            public FrameRateInfo Run()
            {
                long len = _fs.Length, pos = 0;
                var hdr = new byte[16];
                while (pos + 8 <= len)
                {
                    if (_cancelled != null && _cancelled()) return Cancelled("mp4");
                    if (_clock.ElapsedMilliseconds > TimeBudgetMs || TotalVideoFrames() > MaxFrames) { _partial = true; break; }
                    _fs.Position = pos;
                    if (ReadFully(_fs, hdr, 0, 8) < 8) break;
                    long size = BE32(hdr, 0);
                    string type = Encoding.ASCII.GetString(hdr, 4, 4);
                    int headLen = 8;
                    if (size == 1)
                    {
                        if (ReadFully(_fs, hdr, 8, 8) < 8) break;
                        size = (long)BE64(hdr, 8);
                        headLen = 16;
                    }
                    else if (size == 0) size = len - pos;
                    if (size < headLen || size > len - pos) break; // corrupt or truncated: judge from what was read
                    long body = size - headLen;
                    if (type == "moov" || type == "moof")
                    {
                        if (body > (type == "moov" ? MaxMoovBytes : MaxMoofBytes)) { _partial = true; break; }
                        var buf = new byte[body];
                        if (ReadFully(_fs, buf, 0, buf.Length) < buf.Length) break;
                        if (type == "moov") ParseMoov(buf, 0, buf.Length);
                        else ParseMoof(buf, 0, buf.Length);
                    }
                    pos += size;
                }
                Mp4Track video = null;
                foreach (var t in _tracks.Values)
                    if (t.Video && t.Timescale > 0 && (video == null || t.Pts.Count > video.Pts.Count)) video = t;
                if (video == null) return Unknown("mp4", "no video track");
                return Analyze(video.Pts, video.Timescale, true, 0, "mp4", _partial);
            }

            private int TotalVideoFrames()
            {
                int n = 0;
                foreach (var t in _tracks.Values) if (t.Video) n += t.Pts.Count;
                return n;
            }

            // Calls visit(type, bodyStart, bodyEnd) for each child box in [start, end).
            private static void Children(byte[] b, int start, int end, Action<string, int, int> visit)
            {
                int p = start;
                while (p + 8 <= end)
                {
                    long size = BE32(b, p);
                    string type = Encoding.ASCII.GetString(b, p + 4, 4);
                    int head = 8;
                    if (size == 1)
                    {
                        if (p + 16 > end) return;
                        size = (long)BE64(b, p + 8);
                        head = 16;
                    }
                    else if (size == 0) size = end - p;
                    if (size < head || p + size > end) return;
                    visit(type, p + head, (int)(p + size));
                    p += (int)size;
                }
            }

            private void ParseMoov(byte[] b, int start, int end)
            {
                Children(b, start, end, (type, s, e) =>
                {
                    if (type == "trak") ParseTrak(b, s, e);
                    else if (type == "mvex")
                        Children(b, s, e, (t2, s2, e2) =>
                        {
                            if (t2 != "trex" || e2 - s2 < 24) return;
                            uint id = BE32(b, s2 + 4);
                            Track(id).TrexDuration = BE32(b, s2 + 12);
                        });
                });
            }

            private Mp4Track Track(uint id)
            {
                Mp4Track t;
                if (!_tracks.TryGetValue(id, out t)) _tracks[id] = t = new Mp4Track { Id = id };
                return t;
            }

            private void ParseTrak(byte[] b, int start, int end)
            {
                uint id = 0, timescale = 0;
                bool video = false;
                List<KeyValuePair<uint, uint>> stts = null;   // (count, delta)
                List<KeyValuePair<uint, int>> ctts = null;    // (count, offset)
                Children(b, start, end, (type, s, e) =>
                {
                    if (type == "tkhd" && e - s >= 24)
                        id = b[s] == 1 ? BE32(b, s + 20) : BE32(b, s + 12);
                    else if (type == "mdia")
                        Children(b, s, e, (t2, s2, e2) =>
                        {
                            if (t2 == "mdhd" && e2 - s2 >= 24) timescale = b[s2] == 1 ? BE32(b, s2 + 20) : BE32(b, s2 + 12);
                            else if (t2 == "hdlr" && e2 - s2 >= 12) video = Encoding.ASCII.GetString(b, s2 + 8, 4) == "vide";
                            else if (t2 == "minf")
                                Children(b, s2, e2, (t3, s3, e3) =>
                                {
                                    if (t3 != "stbl") return;
                                    Children(b, s3, e3, (t4, s4, e4) =>
                                    {
                                        if (t4 == "stts") stts = ReadPairs(b, s4, e4, (c, v) => new KeyValuePair<uint, uint>(c, v));
                                        else if (t4 == "ctts") ctts = ReadPairs(b, s4, e4, (c, v) => new KeyValuePair<uint, int>(c, unchecked((int)v)));
                                    });
                                });
                        });
                });
                var track = Track(id);
                track.Video = video;
                track.Timescale = timescale;
                if (!video || stts == null) return;
                long dts = 0;
                int cIndex = 0;
                uint cLeft = ctts != null && ctts.Count > 0 ? ctts[0].Key : 0;
                foreach (var entry in stts)
                {
                    for (uint i = 0; i < entry.Key; i++)
                    {
                        if (track.Pts.Count >= MaxFrames) { _partial = true; return; }
                        long offset = 0;
                        if (ctts != null && cIndex < ctts.Count)
                        {
                            offset = ctts[cIndex].Value;
                            if (--cLeft == 0 && ++cIndex < ctts.Count) cLeft = ctts[cIndex].Key;
                        }
                        track.Pts.Add(dts + offset);
                        dts += entry.Value;
                    }
                }
                track.NextFragmentDts = dts;
            }

            private static List<KeyValuePair<uint, T>> ReadPairs<T>(byte[] b, int s, int e, Func<uint, uint, KeyValuePair<uint, T>> make)
            {
                var list = new List<KeyValuePair<uint, T>>();
                if (e - s < 8) return list;
                long count = BE32(b, s + 4);
                int p = s + 8;
                for (long i = 0; i < count && p + 8 <= e; i++, p += 8) list.Add(make(BE32(b, p), BE32(b, p + 4)));
                return list;
            }

            private void ParseMoof(byte[] b, int start, int end)
            {
                Children(b, start, end, (type, s, e) =>
                {
                    if (type != "traf") return;
                    Mp4Track track = null;
                    uint defaultDuration = 0;
                    long? baseDts = null;
                    Children(b, s, e, (t2, s2, e2) =>
                    {
                        if (t2 == "tfhd" && e2 - s2 >= 8)
                        {
                            uint flags = BE32(b, s2) & 0xFFFFFF;
                            _tracks.TryGetValue(BE32(b, s2 + 4), out track);
                            if (track == null) return;
                            defaultDuration = track.TrexDuration;
                            int p = s2 + 8;
                            if ((flags & 0x01) != 0) p += 8;
                            if ((flags & 0x02) != 0) p += 4;
                            if ((flags & 0x08) != 0 && p + 4 <= e2) defaultDuration = BE32(b, p);
                        }
                        else if (t2 == "tfdt" && e2 - s2 >= 8)
                            baseDts = b[s2] == 1 && e2 - s2 >= 12 ? (long)BE64(b, s2 + 4) : BE32(b, s2 + 4);
                        else if (t2 == "trun" && track != null && track.Video && e2 - s2 >= 8)
                        {
                            uint flags = BE32(b, s2) & 0xFFFFFF;
                            long count = BE32(b, s2 + 4);
                            int p = s2 + 8;
                            if ((flags & 0x01) != 0) p += 4;
                            if ((flags & 0x04) != 0) p += 4;
                            long dts = baseDts ?? track.NextFragmentDts;
                            baseDts = null; // a second trun in this traf continues where this one ends
                            for (long i = 0; i < count; i++)
                            {
                                if (track.Pts.Count >= MaxFrames) { _partial = true; break; }
                                uint duration = defaultDuration;
                                long offset = 0;
                                if ((flags & 0x100) != 0) { if (p + 4 > e2) break; duration = BE32(b, p); p += 4; }
                                if ((flags & 0x200) != 0) p += 4;
                                if ((flags & 0x400) != 0) p += 4;
                                if ((flags & 0x800) != 0) { if (p + 4 > e2) break; offset = unchecked((int)BE32(b, p)); p += 4; }
                                if (p > e2) break;
                                track.Pts.Add(dts + offset);
                                dts += duration;
                            }
                            track.NextFragmentDts = dts;
                        }
                    });
                });
            }
        }

        // ---- Matroska / WebM ------------------------------------------------------------------------

        private sealed class MatroskaScanner
        {
            private const uint IdSegment = 0x18538067, IdInfo = 0x1549A966, IdTimecodeScale = 0x2AD7B1,
                IdTracks = 0x1654AE6B, IdTrackEntry = 0xAE, IdTrackNumber = 0xD7, IdTrackType = 0x83, IdDefaultDuration = 0x23E383,
                IdCluster = 0x1F43B675, IdTimecode = 0xE7, IdSimpleBlock = 0xA3, IdBlockGroup = 0xA0, IdBlock = 0xA1;
            private const ulong UnknownSize = ulong.MaxValue;

            private readonly FileStream _fs;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly Dictionary<long, List<long>> _blocks = new Dictionary<long, List<long>>(); // track -> timecodes
            private readonly Dictionary<long, long> _trackTypes = new Dictionary<long, long>();
            private readonly Dictionary<long, long> _defaultDurations = new Dictionary<long, long>();
            private long _entryNumber = -1, _entryType = -1, _entryDuration;
            private long _clusterTimecode;
            private ulong _timecodeScale = 1000000;
            private bool _partial, _laced;
            private long _frames;

            private readonly Func<bool> _cancelled;

            public MatroskaScanner(FileStream fs, Func<bool> cancelled) { _fs = fs; _cancelled = cancelled; }

            public FrameRateInfo Run()
            {
                long len = _fs.Length;
                var scratch = new byte[8];
                while (_fs.Position < len)
                {
                    if (_cancelled != null && _cancelled()) return Cancelled("mkv");
                    if (_clock.ElapsedMilliseconds > TimeBudgetMs || _frames > MaxFrames) { _partial = true; break; }
                    uint id;
                    ulong size;
                    if (!ReadId(out id) || !ReadSize(out size)) break;
                    long bodyStart = _fs.Position;
                    // Master elements on the way to the blocks are entered rather than skipped; their children
                    // follow inline, so unknown sizes (live recordings) need no special handling.
                    if (id == IdSegment || id == IdCluster || id == IdBlockGroup || id == IdTracks || id == IdInfo) continue;
                    if (id == IdTrackEntry)
                    {
                        EndTrackEntry();
                        _entryNumber = _entryType = -1;
                        _entryDuration = 0;
                        continue;
                    }
                    if (size == UnknownSize) break; // an unknown-size element we cannot skip
                    if (bodyStart + (long)Math.Min(size, (ulong)long.MaxValue / 2) > len) break; // truncated file: judge from what was read
                    switch (id)
                    {
                        case IdTimecodeScale: _timecodeScale = Math.Max(1, ReadUInt(size)); break;
                        case IdTrackNumber: _entryNumber = (long)ReadUInt(size); break;
                        case IdTrackType: _entryType = (long)ReadUInt(size); break;
                        case IdDefaultDuration: _entryDuration = (long)ReadUInt(size); break;
                        case IdTimecode: _clusterTimecode = (long)ReadUInt(size); break;
                        case IdSimpleBlock:
                        case IdBlock:
                            ReadBlockHeader(size, scratch);
                            break;
                    }
                    _fs.Position = bodyStart + (long)size;
                }
                EndTrackEntry();
                long videoTrack = -1;
                foreach (var kv in _trackTypes)
                {
                    if (kv.Value != 1) continue;
                    List<long> list;
                    int count = _blocks.TryGetValue(kv.Key, out list) ? list.Count : 0;
                    int best = videoTrack >= 0 && _blocks.ContainsKey(videoTrack) ? _blocks[videoTrack].Count : -1;
                    if (count > best) videoTrack = kv.Key;
                }
                if (videoTrack < 0) return Unknown("mkv", "no video track");
                if (_laced) return Unknown("mkv", "laced video blocks");
                List<long> pts;
                if (!_blocks.TryGetValue(videoTrack, out pts)) pts = new List<long>();
                long dd;
                double hint = _defaultDurations.TryGetValue(videoTrack, out dd) && dd > 0 ? 1e9 / dd : 0;
                return Analyze(pts, 1e9 / _timecodeScale, false, hint, "mkv", _partial);
            }

            private void EndTrackEntry()
            {
                if (_entryNumber < 0) return;
                _trackTypes[_entryNumber] = _entryType;
                if (_entryDuration > 0) _defaultDurations[_entryNumber] = _entryDuration;
                _entryNumber = -1;
            }

            private void ReadBlockHeader(ulong size, byte[] scratch)
            {
                long track;
                int trackLen;
                if (!ReadVint(out track, out trackLen, false) || (ulong)(trackLen + 3) > size) return;
                if (ReadFully(_fs, scratch, 0, 3) < 3) return;
                short rel = (short)((scratch[0] << 8) | scratch[1]);
                bool video = !_trackTypes.ContainsKey(track) || _trackTypes[track] == 1;
                if (!video) return; // audio/subtitle blocks are not needed once the track types are known
                if ((scratch[2] & 0x06) != 0) _laced = true;
                List<long> list;
                if (!_blocks.TryGetValue(track, out list)) _blocks[track] = list = new List<long>();
                list.Add(_clusterTimecode + rel);
                _frames++;
            }

            private bool ReadId(out uint id)
            {
                id = 0;
                int first = _fs.ReadByte();
                if (first <= 0) return false;
                int len = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
                if (len == 0) return false;
                id = (uint)first;
                for (int i = 1; i < len; i++)
                {
                    int b = _fs.ReadByte();
                    if (b < 0) return false;
                    id = (id << 8) | (uint)b;
                }
                return true;
            }

            private bool ReadSize(out ulong size)
            {
                long v;
                int len;
                size = 0;
                if (!ReadVint(out v, out len, true)) return false;
                size = v < 0 ? UnknownSize : (ulong)v;
                return true;
            }

            // EBML variable-length integer. With allOnesUnknown, the reserved all-ones value returns -1.
            private bool ReadVint(out long value, out int len, bool allOnesUnknown)
            {
                value = 0;
                len = 0;
                int first = _fs.ReadByte();
                if (first <= 0) return false;
                int mask = 0x80;
                len = 1;
                while ((first & mask) == 0) { mask >>= 1; len++; }
                long v = first & (mask - 1);
                bool allOnes = v == mask - 1;
                for (int i = 1; i < len; i++)
                {
                    int b = _fs.ReadByte();
                    if (b < 0) return false;
                    v = (v << 8) | (uint)b;
                    allOnes &= b == 0xFF;
                }
                value = allOnesUnknown && allOnes ? -1 : v;
                return true;
            }

            private ulong ReadUInt(ulong size)
            {
                if (size > 8) return 0;
                ulong v = 0;
                for (ulong i = 0; i < size; i++)
                {
                    int b = _fs.ReadByte();
                    if (b < 0) return v;
                    v = (v << 8) | (uint)b;
                }
                return v;
            }
        }

        private static uint BE32(byte[] b, int o)
        {
            return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        }

        private static ulong BE64(byte[] b, int o)
        {
            return ((ulong)BE32(b, o) << 32) | BE32(b, o + 4);
        }
    }
}
