// CONTRACT STUB — see src/Ui/PlaylistPanel.cs for the real implementation.
#pragma warning disable 67
using System;
using System.Windows.Forms;
using VidFlow.Media;

namespace VidFlow.Ui
{
    // Side panel listing the playlist (dark, owner-drawn). Docked right by MainForm.
    // Only its filter text box may take keyboard focus; the list itself is mouse-driven.
    public sealed class PlaylistPanel : Control
    {
        public PlaylistPanel() { }

        public Playlist Playlist { get; set; }        // panel subscribes to Playlist.Changed

        public event Action<int> ItemActivated;       // double-click (or Enter in the filter box on the top match)
        public event Action<int[]> RemoveRequested;   // remove from list (context menu)
        public event Action<int[]> DeleteRequested;   // delete file(s) to Recycle Bin (context menu)
        public event Action<int> ShowInFolderRequested;
        public event Action<string[]> AppendRequested; // files dropped onto the panel
        public event Action CloseRequested;           // header close button
        public event Action SortChanged;              // user picked a sort/order in the panel (Playlist already updated)
        public event Action SaveRequested;            // "Save List As…" in the panel's menu

        // True while the filter box has focus (MainForm then lets typing through instead of shortcuts).
        public bool IsEditingFilter { get { return false; } }

        // Gives keyboard focus back the way Esc in the filter does (no-op when not editing).
        public void EndFilterEditing() { }
    }
}
