using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace VidFlow.Ui
{
    // Extension points called from the core MainForm partials and implemented in
    // MainForm.Features.cs (markers, persistent OSD, borderless, taskbar, reverse play, ...).
    // An unimplemented partial method compiles to nothing.
    public sealed partial class MainForm
    {
        partial void FeatureMpvOptions(Action<string, string> option);   // InitPlayer, before ExtraMpvOptions
        partial void FeatureInit();                                      // InitPlayer, after observers are registered
        partial void FeatureRegisterActions();                           // end of RegisterActions
        partial void FeatureMenus();                                     // after the menus are built
        partial void FeatureFileStarted();                               // a new file starts loading
        partial void FeatureFileLoaded();                                // the file is loaded
        partial void FeatureEof(ref bool handled);                       // end of file reached; set handled to skip the default
        partial void FeaturePosition(double position);                   // every time-pos update
        partial void FeaturePauseChanged(bool paused);
        partial void FeatureFullscreenChanged();                         // after entering/leaving fullscreen
        partial void FeatureWndProc(ref Message m, ref bool handled);    // first look at every window message
        partial void FeatureApplySettings();                             // after the Settings dialog applied values
        partial void FeatureDump(Dictionary<string, object> d);          // automation dump
        partial void FeatureClosing();                                   // window closing, before mpv is destroyed
        partial void FeatureFileMoved(string from, string to);           // VidFlow moved/renamed a file (to == null: deleted/recycled)
        partial void FeatureFrameTimingChanged();                        // FrameRateVariable / frame accuracy verdict changed

        // Cull workflow (MainForm.Cull.cs): decisions, bins, apply step, rename.
        partial void CullInit();
        partial void CullRegisterActions();
        partial void CullMenus();
        partial void CullFileStarted();
        partial void CullFileLoaded();
        partial void CullPlaylistChanged();
        partial void CullApplySettings();
        partial void CullDump(Dictionary<string, object> d);
        partial void CullClosing();

        // Extras (MainForm.Extras.cs): marker export, update check, quick start.
        partial void ExtrasRegisterActions();
        partial void ExtrasInit();
        partial void ExtrasDump(Dictionary<string, object> d);

        // Frame accuracy: true when the current file has a variable frame rate, so frame numbers
        // are estimates ("≈F"). Set by the frame code in MainForm.Player.cs, read by the OSD,
        // markers and exports.
        private bool FrameRateVariable { get; set; }
    }
}
