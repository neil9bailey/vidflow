// CONTRACT STUB — see src/Ui/Menus.cs for the real implementation.
#pragma warning disable 67
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    // One dynamic menu entry (recent files, tracks, chapters, playlist...).
    public sealed class MenuEntry
    {
        public string Text { get; set; }
        public string ActionId { get; set; }          // executed via ActionRegistry when clicked
        public bool Checked { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsSeparator { get; set; }
        public static MenuEntry Separator() { return new MenuEntry { IsSeparator = true }; }
    }

    // Supplies the dynamic parts of menus. Implemented by MainForm; called each time a menu opens.
    public interface IMenuDataSource
    {
        IList<MenuEntry> RecentFiles();     // ActionId "OpenRecent:<index>"
        IList<MenuEntry> VideoTracks();     // "VideoTrack:<id>" (+ "VideoTrack:no")
        IList<MenuEntry> AudioTracks();     // "AudioTrack:<id>" (+ "AudioTrack:no")
        IList<MenuEntry> SubtitleTracks();  // "SubTrack:<id>" (+ "SubTrack:no")
        IList<MenuEntry> Chapters();        // "Chapter:<index>"
    }

    // Dark ToolStrip renderer used by every menu in the app.
    public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() { }
    }

    // Builds the app's menus from ActionRegistry ids. Item text = ActionRegistry.TitleOf(id)
    // (or a menu-specific label), shortcut text = registry.ShortcutText(id), checked/enabled
    // state queried from the registry every time a menu opens. Clicking runs registry.Execute(id).
    // Keyboard shortcuts are NOT registered on the items (MainForm dispatches keys itself).
    public sealed class MenuBuilder
    {
        public MenuBuilder(ActionRegistry registry, IMenuDataSource data) { }

        // Extra dynamic submenus (null = submenu hidden). Audio ▸ Audio Device uses
        // "AudioDevice:<name>" entries; Markers ▸ uses "GoToMarker:<index>" entries.
        public Func<IList<MenuEntry>> AudioDevicesProvider { get; set; }
        public Func<IList<MenuEntry>> MarkersProvider { get; set; }

        // Label for "Move Clip to Bin n" (e.g. "Bin 1: Selects"); null result = "Bin n".
        public Func<int, string> CullBinLabelProvider { get; set; }

        // The Cull menu on its own (used by the control bar's cull chip).
        public ContextMenuStrip BuildCullMenu() { return null; }

        // Shows a menu at a screen point, sized for the DPI of the monitor at that point
        // (not the cursor's monitor). Use this for every menu MainForm opens.
        public static void ShowAt(ContextMenuStrip menu, System.Drawing.Point screen) { }

        // The full right-click menu (also used by the control bar's "…" button).
        public ContextMenuStrip BuildContextMenu() { return null; }

        // Small drop-downs for control-bar buttons.
        public ContextMenuStrip BuildSpeedMenu() { return null; }
        public ContextMenuStrip BuildRepeatMenu() { return null; }
        public ContextMenuStrip BuildZoomMenu() { return null; }

        // Brings a built menu's checked/enabled/shortcut state and dynamic submenus up to date
        // without showing it (used by the automation "menu" verb).
        public void Refresh(ToolStrip menu) { }

        // Applies the dark theme (renderer, colours, font) to any ToolStrip/ContextMenuStrip.
        public static void ApplyTheme(ToolStrip strip) { }
    }
}
