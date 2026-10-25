// CONTRACT STUB — see src/Ui/ThumbnailPreview.cs for the real implementation.
#pragma warning disable 67
using System;
using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui
{
    // Seek-bar hover popup: a small frame preview rendered by a second, silent libmpv
    // instance, plus a time label underneath. With ImagesEnabled=false (or when the source
    // has no video / fails to load) it shows the time label alone.
    // Owned top-level window: never activates, never takes focus, stays above the owner
    // (works over fullscreen too).
    public sealed class ThumbnailPreview : IDisposable
    {
        public ThumbnailPreview(Form owner) { }

        public bool ImagesEnabled { get; set; } = true;

        // File currently playing in the main player (null = none). Cheap: the preview player
        // (re)loads lazily on the next Show with images enabled.
        public void SetSource(string path) { }

        // seconds: time to preview; label: text under the image (e.g. "1:23.456  F 2001");
        // cursorScreenX: horizontal centre; barScreenBounds: popup sits just above this rect,
        // clamped to the owner's monitor working area.
        public void Show(double seconds, string label, int cursorScreenX, Rectangle barScreenBounds) { }

        public void Hide() { }

        public bool Visible { get { return false; } }

        public void Dispose() { }
    }
}
