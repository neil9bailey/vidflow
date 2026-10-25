namespace VidFlow.Core
{
    // Shared preset lists (menus, cycling actions, settings UI).
    public static class Presets
    {
        public static readonly double[] Speeds = { 0.1, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 8.0, 16.0 };
        public const double MinSpeed = 0.01, MaxSpeed = 100.0, FineSpeedStep = 0.05;

        // mpv video-aspect-override values; "no" = use the file's own aspect.
        public static readonly string[] Aspects = { "no", "16:9", "4:3", "21:9", "2.35:1", "1.85:1", "1:1", "9:16", "4:5" };
        public static string AspectLabel(string v) { return v == "no" ? "Default (from file)" : v; }

        // Percent of the video's own pixel size (100 = 1:1 pixels).
        public static readonly int[] PixelZooms = { 25, 50, 100, 200, 300, 400, 800 };

        // Window size presets: percent of video pixel size, plus "fit".
        public static readonly string[] WindowSizes = { "25", "50", "100", "150", "200", "fit" };

        public static readonly int[] Rotations = { 0, 90, 180, 270 };

        public const double VolumeMax = 150, VolumeStep = 5;
        public const double ZoomStepLog2 = 0.3219280948873623; // log2(1.25): each step is 25%
        public const double MinZoomLog2 = -3, MaxZoomLog2 = 6;  // 1/8× .. 64× of "fit"
        public const double DelayStep = 0.1;                    // audio/subtitle delay seconds
        public const double SubScaleStep = 0.1;
        public const int PictureAdjustStep = 5;                 // brightness/contrast/... (-100..100)
    }
}
