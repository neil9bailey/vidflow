// CONTRACT STUB — real implementations live in src/Ui/Dialogs/*.cs (one class per file).
#pragma warning disable 67
using System;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui.Dialogs
{
    // Base for all dialogs: dark theme, Segoe UI 9pt, DPI-aware layout, Esc = cancel,
    // centred on owner, no taskbar button.
    public class DarkDialog : Form
    {
        public DarkDialog() { }
    }

    public static class GoToDialog
    {
        // Lets the user type a time ("1:23.456", "83.4", "-2" = 2 s before end) or a frame number
        // (frame mode / "f123"). Returns the target time in seconds, or null if cancelled.
        public static double? Show(IWin32Window owner, double position, double duration, double fps) { return null; }
    }

    public static class TextInputDialog
    {
        // Single-line prompt (used for "Open URL or Path"). Returns null if cancelled.
        public static string Show(IWin32Window owner, string title, string prompt, string initialText) { return null; }

        // Same, with an initial selection (e.g. the name part of "clip.mp4" for a rename).
        public static string Show(IWin32Window owner, string title, string prompt, string initialText, int selectionStart, int selectionLength) { return null; }
    }

    public static class MediaInfoDialog
    {
        // Read-only, copyable report built from the player's properties
        // (file, container, size, duration, video/audio/subtitle tracks, metadata, chapters).
        public static void Show(IWin32Window owner, MpvPlayer player, string path) { }
    }

    public static class ShortcutsDialog
    {
        // Searchable list of every catalog action with its current keys, grouped by category,
        // plus a fixed "Mouse" section describing the mouse gestures for the given settings.
        public static void Show(IWin32Window owner, ActionRegistry registry, Settings settings) { }
    }

    public static class SettingsDialog
    {
        // Edits a copy of `settings`; on OK copies every value back into the same instance
        // (including KeyOverrides, which only stores entries that differ from the defaults)
        // and returns true. Cancel leaves `settings` untouched and returns false.
        public static bool Show(IWin32Window owner, Settings settings, ActionRegistry registry) { return false; }
    }

    public static class AboutDialog
    {
        public static void Show(IWin32Window owner, string mpvVersion, string ffmpegVersion, string enginePath, string settingsPath) { }
    }

    public static class FolderPicker
    {
        // Modern Vista+ folder picker (IFileOpenDialog with FOS_PICKFOLDERS). Null if cancelled.
        public static string Show(IWin32Window owner, string title, string initialFolder) { return null; }
    }

    public static class ConfirmDialog
    {
        // Dark Yes/No confirmation. Returns true for Yes. If `dontAskAgain` is non-null a
        // "Don't ask again" checkbox is shown and its final state written back.
        public static bool Show(IWin32Window owner, string title, string message, string yesText, string noText, ref bool? dontAskAgain) { return false; }
    }
}
