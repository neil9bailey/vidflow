// CONTRACT STUB — see src/Media/Playlist.cs for the real implementation.
#pragma warning disable 67
using System;
using System.Collections.Generic;

namespace VidFlow.Media
{
    public enum PlaylistSort { Name, DateModified, Size, Type }

    // Ordered list of media paths/URLs with a current index. UI-agnostic, not thread-safe
    // (UI thread only). Items are full paths (or URLs, kept verbatim).
    public sealed class Playlist
    {
        public static readonly string[] VideoExtensions = new string[0];   // lower-case, with dot
        public static readonly string[] AudioExtensions = new string[0];
        public static readonly string[] SubtitleExtensions = new string[0];

        public static readonly string[] PlaylistExtensions = new string[0]; // .m3u .m3u8 .pls
        public static bool IsPlaylistFile(string path) { return false; }

        // Entries of an .m3u/.m3u8/.pls file (relative entries resolved against its folder, URLs kept).
        public static List<string> ReadPlaylistFile(string path) { return null; }
        // Writes a UTF-8 .m3u8 (#EXTM3U); paths relative to the file's folder when on the same drive.
        public static void WritePlaylistFile(string path, IEnumerable<string> items) { }

        public static bool IsMediaFile(string path) { return false; }      // video or audio by extension
        public static bool IsSubtitleFile(string path) { return false; }
        public static bool IsUrl(string s) { return false; }               // has a "scheme://" prefix
        public static int NaturalCompare(string a, string b) { return 0; } // Explorer-style ("clip2" < "clip10")

        // Open-file dialog filter string covering all supported media.
        public static string MediaFileFilter { get { return ""; } }
        public static string SubtitleFileFilter { get { return ""; } }

        public IReadOnlyList<string> Items { get { return null; } }
        public int Count { get { return 0; } }
        public int Index { get { return -1; } }       // -1 when empty
        public string Current { get { return null; } } // null when empty

        // Shuffle: Next/Previous walk a random permutation that starts at the current item.
        // Turning it off returns to list order (current item unchanged).
        public bool Shuffle { get; set; }
        public PlaylistSort Sort { get; set; }        // re-sorts (natural order for Name); current item kept
        public bool Descending { get; set; }          // reverses the sort; ties still by natural name
        // Folders given to Load/Append are walked recursively (skipping hidden/system folders and
        // reparse points, depth <= 8, at most 5000 files). The auto-loaded sibling folder is never recursive.
        public bool IncludeSubfolders { get; set; }
        public bool LastLoadTruncated { get { return false; } } // the last Load/Append hit the file cap

        public event Action Changed;                  // any change to items, order or index

        // Replace the list. Each input may be a file, a folder (its media files, not recursive)
        // or a URL. If exactly one local media file is given and autoLoadFolder is true, the list
        // becomes every media file in that file's folder (sorted by Sort) with that file current.
        // Otherwise the first playable input is current. Returns false if nothing playable.
        public bool Load(IEnumerable<string> inputs, bool autoLoadFolder) { return false; }

        // Add to the end (files, folders, URLs). If the list was empty the first added item becomes current.
        public void Append(IEnumerable<string> inputs) { }

        public bool HasNext(bool wrap) { return false; }
        public bool HasPrevious(bool wrap) { return false; }
        public bool MoveNext(bool wrap) { return false; }      // false (index unchanged) at the end without wrap
        public bool MovePrevious(bool wrap) { return false; }
        public bool MoveTo(int index) { return false; }
        public bool MoveFirst() { return false; }
        public bool MoveLast() { return false; }

        // Removing the current item makes the following item current (or the new last item if it
        // was last; -1 when the list becomes empty).
        // Folders for which this returns true are not walked when expanding a folder recursively
        // (e.g. a cull session's Keep / Rejected / bin folders). Null = walk everything.
        public Func<string, bool> SkipFolder { get; set; }

        // Replaces one item in place (rename/move): position, current item and shuffle walk kept; one Changed.
        public void Replace(int index, string path) { }

        public void RemoveAt(int index) { }
        public void RemoveRange(IList<int> indices) { }        // batch remove, one Changed event; same current-item rule
        public bool Remove(string path) { return false; }
        public void Move(int from, int to) { }                 // reorder; current item follows
        public void MoveBlock(IList<int> indices, int target) { } // moves the rows (kept in order) to insert before
                                                               // the item now at `target` (Count = end); one Changed
        public void Clear() { }
        public int IndexOf(string path) { return -1; }         // case-insensitive for local paths
    }
}
