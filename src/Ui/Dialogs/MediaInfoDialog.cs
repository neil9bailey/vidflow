using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui.Dialogs
{
    public static class MediaInfoDialog
    {
        // Read-only, copyable report built from the player's properties
        // (file, container, size, duration, video/audio/subtitle tracks, metadata, chapters).
        public static void Show(IWin32Window owner, MpvPlayer player, string path)
        {
            using (var w = new Window(BuildReport(player, path), path))
                w.ShowDialog(owner);
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- report ----------------------------------------------------------------

        // Plain-text report with aligned "key  value" columns; unavailable values are skipped.
        internal static string BuildReport(MpvPlayer player, string path)
        {
            var r = new Report();
            bool live = player != null && player.IsAlive;
            Func<string, string> str = n => live ? NonEmpty(player.GetString(n)) : null;
            Func<string, double?> dbl = n => live ? Finite(player.GetDouble(n)) : null;
            Func<string, long?> lng = n => live ? player.GetLong(n) : null;
            Func<string, object> node = n => live ? player.GetNode(n) : null;

            bool isUrl = !string.IsNullOrEmpty(path) && path.IndexOf("://", StringComparison.Ordinal) > 1;
            string fileName = SafeFileName(path) ?? str("filename");

            // General
            r.Section("General");
            r.Add("File", isUrl ? path : fileName);
            if (!isUrl) r.Add("Folder", SafeDirectory(path));
            long? size = null;
            if (!isUrl && !string.IsNullOrEmpty(path))
            {
                try { var fi = new FileInfo(path); if (fi.Exists) size = fi.Length; }
                catch (Exception) { }
            }
            if (!size.HasValue) size = lng("file-size");
            if (size.HasValue && size.Value >= 0)
                r.Add("Size", Fmt.Bytes(size.Value) + "  (" + size.Value.ToString("N0", Inv) + " bytes)");
            r.Add("Container", str("file-format"));
            r.Add("Demuxer", str("current-demuxer"));
            double? duration = dbl("duration");
            if (duration.HasValue && duration.Value > 0)
                r.Add("Duration", Fmt.Time(duration.Value, true) + "  (" + Fmt.Num(duration.Value, "0.000") + " s)");
            long? frames = lng("estimated-frame-count");
            if (frames.HasValue && frames.Value > 0) r.Add("Frames", frames.Value.ToString("N0", Inv));
            double? fps = dbl("container-fps");
            if (fps.HasValue && fps.Value > 0) r.Add("Frame rate", Fmt.Num(fps.Value) + " fps");
            if (size.HasValue && duration.HasValue && duration.Value > 0 && size.Value > 0)
                r.Add("Overall bitrate", Bitrate(size.Value * 8.0 / duration.Value));
            string title = str("media-title");
            if (title != null && !string.Equals(title, fileName, StringComparison.OrdinalIgnoreCase)) r.Add("Title", title);

            // Video
            var vp = node("video-params") as Dictionary<string, object>;
            string vcodec = str("video-codec");
            if (vp != null || vcodec != null)
            {
                r.Section("Video");
                r.Add("Codec", CodecText(str("video-format"), vcodec));
                long? w = lng("width"), h = lng("height");
                if (w.HasValue && h.HasValue && w.Value > 0 && h.Value > 0) r.Add("Resolution", w.Value + " x " + h.Value);
                long? dw = AsLong(Get(vp, "dw")), dh = AsLong(Get(vp, "dh"));
                if (dw.HasValue && dh.HasValue && dw.Value > 0 && dh.Value > 0 && (dw != w || dh != h)) r.Add("Display size", dw.Value + " x " + dh.Value);
                double? aspect = AsDouble(Get(vp, "aspect"));
                if (aspect.HasValue && aspect.Value > 0) r.Add("Display aspect", AspectText(aspect.Value, dw, dh));
                double? par = AsDouble(Get(vp, "par"));
                if (par.HasValue && par.Value > 0 && Math.Abs(par.Value - 1) > 0.001) r.Add("Pixel aspect", Fmt.Num(par.Value, "0.####"));
                r.Add("Pixel format", PixelFormatText(AsString(Get(vp, "pixelformat")), AsString(Get(vp, "hw-pixelformat"))));
                r.Add("Colour matrix", AsString(Get(vp, "colormatrix")));
                r.Add("Colour levels", AsString(Get(vp, "colorlevels")));
                r.Add("Primaries", AsString(Get(vp, "primaries")));
                r.Add("Transfer", AsString(Get(vp, "gamma")));
                double? vfFps = dbl("estimated-vf-fps");
                if (vfFps.HasValue && vfFps.Value > 0) r.Add("Frame rate (output)", Fmt.Num(vfFps.Value) + " fps");
                double? vbr = dbl("video-bitrate");
                if (vbr.HasValue && vbr.Value > 0) r.Add("Bitrate", Bitrate(vbr.Value));
                string hw = str("hwdec-current");
                if (hw != null) r.Add("Hardware decoding", hw == "no" ? "no (software)" : hw);
                long? rot = AsLong(Get(vp, "rotate"));
                if (rot.HasValue) r.Add("Rotation", rot.Value + "°");
            }

            // Audio
            var ap = node("audio-params") as Dictionary<string, object>;
            string acodec = str("audio-codec-name");
            if (ap != null || acodec != null)
            {
                r.Section("Audio");
                r.Add("Codec", CodecText(acodec, str("audio-codec")));
                long? rate = AsLong(Get(ap, "samplerate"));
                if (rate.HasValue && rate.Value > 0) r.Add("Sample rate", rate.Value.ToString("N0", Inv) + " Hz");
                long? ch = AsLong(Get(ap, "channel-count"));
                string hr = ChannelName(AsString(Get(ap, "hr-channels")));
                if (ch.HasValue && ch.Value > 0) r.Add("Channels", ch.Value + (hr != null ? "  (" + hr + ")" : ""));
                else r.Add("Channels", hr);
                r.Add("Sample format", AsString(Get(ap, "format")));
                double? abr = dbl("audio-bitrate");
                if (abr.HasValue && abr.Value > 0) r.Add("Bitrate", Bitrate(abr.Value));
            }

            // Tracks
            var tracks = node("track-list") as List<object>;
            if (tracks != null && tracks.Count > 0)
            {
                r.Section("Tracks");
                foreach (var t in tracks.OfType<Dictionary<string, object>>())
                {
                    string type = AsString(Get(t, "type")) ?? "track";
                    var head = new StringBuilder();
                    // mpv numbers tracks per type, so "Audio #2" is the second audio track.
                    head.Append(TrackTypeName(type)).Append(" #").Append(AsLong(Get(t, "id")) ?? 0);
                    string codec = AsString(Get(t, "codec"));
                    if (codec != null) head.Append("  ").Append(codec);
                    string lang = AsString(Get(t, "lang"));
                    if (lang != null) head.Append("  ").Append(lang);
                    string ttitle = AsString(Get(t, "title"));
                    if (ttitle != null) head.Append("  \"").Append(ttitle).Append('"');
                    var flags = new List<string>();
                    foreach (var f in new[] { "default", "forced", "external", "selected", "image", "hearing-impaired", "visual-impaired" })
                        if (Get(t, f) is bool && (bool)Get(t, f)) flags.Add(f);
                    if (flags.Count > 0) head.Append("  [").Append(string.Join(", ", flags)).Append(']');
                    r.Heading(head.ToString());

                    r.Add("Decoder", AsString(Get(t, "decoder-desc")), 1);
                    long? tw = AsLong(Get(t, "demux-w")), th = AsLong(Get(t, "demux-h"));
                    if (tw.HasValue && th.HasValue && tw.Value > 0 && th.Value > 0) r.Add("Size", tw.Value + " x " + th.Value, 1);
                    double? tfps = AsDouble(Get(t, "demux-fps"));
                    if (tfps.HasValue && tfps.Value > 0) r.Add("Frame rate", Fmt.Num(tfps.Value) + " fps", 1);
                    long? trate = AsLong(Get(t, "demux-samplerate"));
                    if (trate.HasValue && trate.Value > 0) r.Add("Sample rate", trate.Value.ToString("N0", Inv) + " Hz", 1);
                    long? tch = AsLong(Get(t, "demux-channel-count"));
                    string tchn = ChannelName(AsString(Get(t, "demux-channels")));
                    if (tch.HasValue && tch.Value > 0) r.Add("Channels", tch.Value + (tchn != null ? "  (" + tchn + ")" : ""), 1);
                    double? tbr = AsDouble(Get(t, "demux-bitrate"));
                    if (tbr.HasValue && tbr.Value > 0) r.Add("Bitrate", Bitrate(tbr.Value), 1);
                    r.Add("File", AsString(Get(t, "external-filename")), 1);
                }
            }

            // Chapters
            var chapters = node("chapter-list") as List<object>;
            if (chapters != null && chapters.Count > 0)
            {
                r.Section("Chapters");
                int i = 0;
                foreach (var c in chapters.OfType<Dictionary<string, object>>())
                {
                    i++;
                    double? at = AsDouble(Get(c, "time"));
                    string ct = AsString(Get(c, "title")) ?? ("Chapter " + i);
                    r.Add(i.ToString(Inv), (at.HasValue ? Fmt.Time(at.Value, true) + "  " : "") + ct);
                }
            }

            // Metadata
            var meta = node("metadata") as Dictionary<string, object>;
            if (meta != null && meta.Count > 0)
            {
                r.Section("Metadata");
                foreach (var kv in meta) r.Add(kv.Key, AsString(kv.Value));
            }

            if (!live) r.Note("Playback details are unavailable (no file is loaded in the player).");
            return r.Build();
        }

        private sealed class Report
        {
            private enum Kind { Section, Heading, Pair, Note }

            private sealed class Line
            {
                public Kind Kind;
                public string Key, Value;
                public int Indent;
            }

            private readonly List<Line> _lines = new List<Line>();

            public void Section(string title) { _lines.Add(new Line { Kind = Kind.Section, Value = title }); }
            public void Heading(string text) { _lines.Add(new Line { Kind = Kind.Heading, Value = text }); }
            public void Note(string text) { _lines.Add(new Line { Kind = Kind.Note, Value = text }); }

            public void Add(string key, string value, int indent = 0)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                value = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " / ").Trim();
                _lines.Add(new Line { Kind = Kind.Pair, Key = key, Value = value, Indent = indent });
            }

            public string Build()
            {
                // Every value starts in the same column, indented keys included.
                const int indentWidth = 4;
                int column = 0;
                foreach (var l in _lines)
                    if (l.Kind == Kind.Pair) column = Math.Max(column, l.Indent * indentWidth + l.Key.Length);
                column += 3;
                var sb = new StringBuilder();
                bool first = true;
                for (int i = 0; i < _lines.Count; i++)
                {
                    var l = _lines[i];
                    // Drop sections that ended up with no lines (all values unavailable).
                    if (l.Kind == Kind.Section && (i + 1 >= _lines.Count || _lines[i + 1].Kind == Kind.Section || _lines[i + 1].Kind == Kind.Note)) continue;
                    switch (l.Kind)
                    {
                        case Kind.Section:
                            if (!first) sb.AppendLine();
                            sb.AppendLine(l.Value.ToUpperInvariant());
                            sb.AppendLine(new string('─', column + 40));
                            break;
                        case Kind.Heading:
                            if (i > 0 && _lines[i - 1].Kind != Kind.Section) sb.AppendLine();
                            sb.AppendLine(l.Value);
                            break;
                        case Kind.Pair:
                            sb.Append(' ', l.Indent * indentWidth).Append(l.Key.PadRight(column - l.Indent * indentWidth)).AppendLine(l.Value);
                            break;
                        case Kind.Note:
                            if (!first) sb.AppendLine();
                            sb.AppendLine(l.Value);
                            break;
                    }
                    first = false;
                }
                return sb.ToString().TrimEnd() + Environment.NewLine;
            }
        }

        // ---- value helpers ---------------------------------------------------------

        private static object Get(Dictionary<string, object> map, string key)
        {
            object v;
            return map != null && map.TryGetValue(key, out v) ? v : null;
        }

        private static string AsString(object v)
        {
            if (v == null) return null;
            if (v is double) return Fmt.Num((double)v);
            if (v is bool) return (bool)v ? "yes" : "no";
            return NonEmpty(Convert.ToString(v, Inv));
        }

        private static long? AsLong(object v)
        {
            if (v is long) return (long)v;
            if (v is double && !double.IsNaN((double)v) && !double.IsInfinity((double)v)) return (long)Math.Round((double)v);
            return null;
        }

        private static double? AsDouble(object v)
        {
            if (v is double) return Finite((double)v);
            if (v is long) return (long)v;
            return null;
        }

        private static double? Finite(double? v)
        {
            return v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) ? v : null;
        }

        private static string NonEmpty(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        // "d3d11" + "nv12" -> "nv12 (d3d11 hardware frames)".
        private static string PixelFormatText(string format, string hwFormat)
        {
            if (hwFormat == null) return format;
            return format == null ? hwFormat : hwFormat + "  (" + format + " hardware frames)";
        }

        // mpv reports layouts it cannot name as "unknownN"; the count alone says more.
        private static string ChannelName(string layout)
        {
            return layout == null || layout.StartsWith("unknown", StringComparison.OrdinalIgnoreCase) ? null : layout;
        }

        private static string TrackTypeName(string type)
        {
            switch (type)
            {
                case "video": return "Video";
                case "audio": return "Audio";
                case "sub": return "Subtitle";
                default: return string.IsNullOrEmpty(type) ? "Track" : char.ToUpperInvariant(type[0]) + type.Substring(1);
            }
        }

        // "hevc" + "HEVC (High Efficiency Video Coding)" -> "hevc — HEVC (High Efficiency Video Coding)".
        private static string CodecText(string name, string description)
        {
            if (name == null) return description;
            if (description == null || string.Equals(name, description, StringComparison.OrdinalIgnoreCase)) return name;
            if (description.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) return description;
            return name + " — " + description;
        }

        private static string Bitrate(double bitsPerSecond)
        {
            if (bitsPerSecond >= 1000000) return (bitsPerSecond / 1000000).ToString("0.00", Inv) + " Mb/s";
            if (bitsPerSecond >= 1000) return (bitsPerSecond / 1000).ToString("0", Inv) + " kb/s";
            return bitsPerSecond.ToString("0", Inv) + " b/s";
        }

        private static readonly KeyValuePair<string, double>[] NamedAspects =
        {
            new KeyValuePair<string, double>("16:9", 16.0 / 9), new KeyValuePair<string, double>("4:3", 4.0 / 3),
            new KeyValuePair<string, double>("21:9", 21.0 / 9), new KeyValuePair<string, double>("2.39:1", 2.39),
            new KeyValuePair<string, double>("2.35:1", 2.35), new KeyValuePair<string, double>("1.85:1", 1.85),
            new KeyValuePair<string, double>("3:2", 1.5), new KeyValuePair<string, double>("5:4", 1.25),
            new KeyValuePair<string, double>("1:1", 1.0), new KeyValuePair<string, double>("9:16", 9.0 / 16),
            new KeyValuePair<string, double>("4:5", 0.8), new KeyValuePair<string, double>("2:1", 2.0),
        };

        private static string AspectText(double aspect, long? dw, long? dh)
        {
            string name = null;
            if (dw.HasValue && dh.HasValue && dw.Value > 0 && dh.Value > 0)
            {
                long g = Gcd(dw.Value, dh.Value);
                if (dw.Value / g <= 32 && dh.Value / g <= 32) name = (dw.Value / g) + ":" + (dh.Value / g);
            }
            if (name == null)
                foreach (var kv in NamedAspects)
                    if (Math.Abs(aspect - kv.Value) / kv.Value < 0.01) { name = kv.Key; break; }
            return Fmt.Num(aspect, "0.000") + (name != null ? "  (" + name + ")" : "");
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0) { long t = a % b; a = b; b = t; }
            return Math.Max(1, a);
        }

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return NonEmpty(Path.GetFileName(path)); }
            catch (ArgumentException) { return path; }
        }

        private static string SafeDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return NonEmpty(Path.GetDirectoryName(path)); }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        // ---- window ----------------------------------------------------------------

        internal sealed class Window : DarkDialog
        {
            private readonly string _report;
            private readonly DarkButton _copy;
            private readonly Timer _copiedTimer;

            public Window(string report, string path)
            {
                _report = report ?? "";
                string name = SafeFileName(path);
                Text = string.IsNullOrEmpty(name) ? "Media Info" : "Media Info — " + name;
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;

                var root = new DarkTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var text = new DarkTextBox { Multiline = true, ReadOnly = true, Monospace = true, Dock = DockStyle.Fill, Text = _report };
                var host = new DarkTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
                host.Controls.Add(text, 0, 0);
                OnDpi(() =>
                {
                    host.Padding = Pad(16, 16, 16, 16);
                    text.Margin = Padding.Empty;
                    ClientSize = new Size(S(680), S(600));
                    MinimumSize = SizeFromClientSize(new Size(S(420), S(300)));
                });

                _copy = CreateButton("Copy", false);
                _copy.Glyph = "";
                _copy.Click += (s, e) => CopyReport();
                var close = CreateButton("Close", true, DialogResult.Cancel);
                root.Controls.Add(host, 0, 0);
                root.Controls.Add(CreateFooter(_copy, close), 0, 1);
                Controls.Add(root);
                AcceptButton = close;
                CancelButton = close;
                ActiveControl = close;

                _copiedTimer = new Timer { Interval = 1500 };
                _copiedTimer.Tick += (s, e) =>
                {
                    _copiedTimer.Stop();
                    _copy.Text = "Copy";
                    _copy.Glyph = "";
                };
            }

            private void CopyReport()
            {
                if (_report.Length == 0) return;
                try
                {
                    Clipboard.SetText(_report);
                    _copy.Text = "Copied";
                    _copy.Glyph = Glyphs.Check;
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    _copy.Text = "Clipboard busy";
                }
                _copiedTimer.Stop();
                _copiedTimer.Start();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _copiedTimer.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
