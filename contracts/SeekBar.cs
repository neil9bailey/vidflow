// CONTRACT STUB — the public surface every other module codes against.
// The real implementation in src/Ui/SeekBar.cs must keep these members and semantics
// exactly (it may add private/internal members and extra public ones).
#pragma warning disable 67
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui
{
    // Custom-drawn timeline. Never takes keyboard focus.
    public sealed class SeekBar : Control
    {
        public SeekBar() { }

        public double Duration { get; set; }          // seconds; <= 0 means nothing seekable (draw empty, ignore input)
        public double Position { get; set; }          // seconds; display ignores it while the user is scrubbing
        public double BufferedEnd { get; set; }       // absolute time (s) the demuxer cache reaches (mpv demuxer-cache-time); <= Position hides the band
        public IList<double> Chapters { get; set; }   // chapter start times (ticks); null/empty = none
        public double? LoopA { get; set; }            // A-B loop markers; range shaded when both set
        public double? LoopB { get; set; }
        public IList<double> Markers { get; set; }    // review markers (times); drawn as small ticks above the track

        // The user dragged an A-B loop marker: (isB, seconds, final). final=false while dragging
        // (throttled), true on release. A press within a few px of a marker drags it instead of seeking.
        public event Action<bool, double, bool> LoopPointDragged;
        public double WheelStepSeconds { get; set; } = 5;
        public bool IsScrubbing { get { return false; } }

        // (seconds, final). final=false while dragging (throttled to <= ~30/s, caller does a fast
        // keyframe seek); final=true on click or mouse-up (caller does an exact seek).
        // Wheel over the bar raises (Position ± WheelStepSeconds, true).
        public event Action<double, bool> SeekRequested;
        public event Action ScrubStarted;
        public event Action ScrubEnded;

        // Mouse moved over the bar (also during scrubbing): (seconds under cursor, cursor X in
        // screen coords, bar bounds in screen coords). Used to place the hover preview.
        public event Action<double, int, Rectangle> HoverChanged;
        public event Action HoverEnded;

        public int PreferredBarHeight { get { return 0; } } // DPI-scaled height the bar wants (hit area)
    }
}
