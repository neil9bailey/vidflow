// CONTRACT STUB — see src/Ui/ControlBar.cs for the real implementation.
#pragma warning disable 67
using System;
using System.Drawing;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    public enum BarButton
    {
        PrevFile, FrameBack, PlayPause, FrameForward, NextFile,
        Repeat, AbLoop, Shuffle, Speed, Zoom, Mute, Playlist, Fullscreen, Menu,
        Cull // the cull chip (only present while CullMode)
    }

    public enum AbLoopState { None, ASet, Active }

    // Bottom control strip (custom drawn, dark). Hosts the SeekBar along its top edge.
    // Purely a view: MainForm pushes state in through the properties and reacts to events.
    // Never takes keyboard focus.
    public sealed class ControlBar : Control
    {
        public ControlBar() { }

        public SeekBar SeekBar { get { return null; } }

        // ---- state (setting a property repaints only if the value changed) ----
        public bool HasMedia { get; set; }
        public bool Paused { get; set; } = true;
        public double Position { get; set; }          // seconds, for the time readout (SeekBar is set separately)
        public double Duration { get; set; }
        public long? FrameNumber { get; set; }        // 0-based current frame; null = unknown
        public long? FrameCount { get; set; }
        public bool ShowFrameNumber { get; set; } = true;
        public bool ShowRemaining { get; set; }       // "-0:03" instead of elapsed
        public double Speed { get; set; } = 1.0;
        public double Volume { get; set; } = 100;     // 0..VolumeMax
        public double VolumeMax { get; set; } = 150;
        public bool Muted { get; set; }
        public RepeatMode Repeat { get; set; }
        public bool Shuffle { get; set; }
        public AbLoopState AbLoop { get; set; }
        public string ZoomText { get; set; } = "Fit"; // e.g. "Fit", "250%"
        public bool Fullscreen { get; set; }
        public bool PlaylistVisible { get; set; }
        public string PlaylistText { get; set; } = ""; // e.g. "3 / 12"; empty hides

        // Frame readout honesty: FrameApproximate draws "≈F 61"; VariableFrameRate adds a small "VFR"
        // badge (tooltip explains that frame numbers are estimates on variable-frame-rate video).
        public bool FrameApproximate { get; set; }
        public bool VariableFrameRate { get; set; }

        // Cull mode chip after the time readout: shown while CullMode; flag 1 = Pick (green ✓),
        // -1 = Reject (red ✕), 0 = undecided (dim "–"); Rating 0..5 drawn as stars. Clicking it
        // raises ButtonClicked(BarButton.Cull, ...).
        public bool CullMode { get; set; }
        public int CullFlag { get; set; }
        public int CullRating { get; set; }
        public string CullProgressText { get; set; } = ""; // e.g. "12 / 40 decided"; empty hides

        // Tooltip text per button (MainForm supplies names + shortcuts). Null = no tooltip.
        public Func<BarButton, string> TooltipProvider { get; set; }

        // ---- events ----
        // A button was clicked. MouseButtons tells left vs right click; the rectangle is the
        // button's bounds in screen coordinates (to anchor drop-down menus).
        public event Action<BarButton, MouseButtons, Rectangle> ButtonClicked;
        public event Action<double> VolumeChanged;    // user moved the volume slider (click, drag or wheel)
        public event Action TimeDisplayClicked;       // user clicked the time readout (toggle remaining)

        public int PreferredBarHeight { get { return 0; } } // DPI-scaled total height incl. the seek bar
    }
}
