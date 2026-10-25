using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using VidFlow.Core;

static class CoreTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Out("FAIL " + what); } }
    static readonly List<string> lines = new List<string>();
    static void Out(string s) { lines.Add(s); }

    [STAThread]
    static void Main(string[] args)
    {
        var reg = new ActionRegistry();
        var problems = reg.BuildKeyMap(null);
        Out("keymap problems: " + problems.Count);
        foreach (var p in problems) Out("  " + p);
        Check(problems.Count == 0, "no keymap problems");

        int keys = 0;
        foreach (var def in ActionCatalog.All)
            foreach (var name in def.DefaultKeys)
            {
                Keys k;
                Check(KeyNames.TryParse(name, out k), "parse " + name);
                string f = KeyNames.Format(k);
                Keys k2;
                Check(KeyNames.TryParse(f, out k2) && k2 == k, "roundtrip " + name + " -> " + f);
                string layer = ActionRegistry.LayerOf(def.Id);
                Check(reg.FindActionForKey(k, layer) == def.Id, "lookup " + name + " [" + layer + "] -> " + def.Id + " got " + reg.FindActionForKey(k, layer));
                keys++;
            }
        Out("default keys checked: " + keys + ", actions: " + ActionCatalog.All.Count);
        Out("menu text samples: " + reg.ShortcutText(ActionIds.FrameStep) + " | " + reg.ShortcutText(ActionIds.SpeedHalve) + " | " + reg.ShortcutText(ActionIds.AudioDelayDown) + " | " + reg.ShortcutText(ActionIds.Settings) + " | " + reg.ShortcutText(ActionIds.Shortcuts));

        // list parsing incl. the comma key
        var errs = new List<string>();
        var list = KeyNames.ParseList("Ctrl+Right, Comma, Ctrl+,, .", errs);
        Check(list.Count == 4 && errs.Count == 0, "ParseList count=" + list.Count + " errs=" + string.Join("|", errs));
        Out("FormatList: " + KeyNames.FormatList(list));
        var back = KeyNames.ParseList(KeyNames.FormatList(list));
        Check(back.SequenceEqual(list), "FormatList roundtrip");

        // layers: cull keys don't steal global ones
        Check(reg.FindActionForKey(Keys.X) == ActionIds.SubDelayUp && reg.FindActionForKey(Keys.X, ActionCatalog.LayerCull) == ActionIds.CullReject, "X global vs cull layer");
        Check(reg.FindActionForKey(Keys.D3) == "SeekPercent:30" && reg.FindActionForKey(Keys.D3, ActionCatalog.LayerCull) == "CullRate:3", "3 global vs cull layer");
        Check(reg.FindActionForKey(Keys.Space, ActionCatalog.LayerCull) == null, "space not in cull layer (falls through to global)");

        // every default key list must round-trip through FormatList/ParseList (review finding: "Num+, Num9")
        foreach (var def in ActionCatalog.All)
        {
            var ks = reg.GetKeys(def.Id).ToList();
            if (ks.Count == 0) continue;
            var e2 = new List<string>();
            var rt = KeyNames.ParseList(KeyNames.FormatList(ks), e2);
            Check(rt.SequenceEqual(ks) && e2.Count == 0, "list roundtrip " + def.Id + ": " + KeyNames.FormatList(ks) + " errs=" + string.Join("|", e2));
        }
        var e3 = new List<string>();
        var l3 = KeyNames.ParseList("Ctrl+=, Num+, Num9, Z", e3);
        Check(l3.Count == 4 && e3.Count == 0, "Num+ list -> " + l3.Count + " errs=" + string.Join("|", e3));
        var l4 = KeyNames.ParseList("+, Z", e3);
        Check(l4.Count == 2, "'+, Z' -> " + l4.Count);
        double nan;
        Check(!Fmt.TryParseTime("NaN", out nan) && !Fmt.TryParseTime("Infinity", out nan), "reject NaN/Infinity");
        var lo = LaunchOptions.Parse(new[] { "--start=1:02.5", "--speed", "0.5x", "--repeat=all", "--paused", "a.mp4", "--pipe", "x\\y" });
        Check(lo.Start == "1:02.5" && lo.Speed == 0.5 && lo.Repeat == RepeatMode.All && lo.Paused && lo.Paths.Count == 1 && lo.PipeName == "x_y", "launch options");
        Check(LaunchOptions.Parse(lo.PlaybackSwitches()).Speed == 0.5, "switch roundtrip");

        // overrides: rebinding a key moves it
        var reg2 = new ActionRegistry();
        var ov = new Dictionary<string, string> { { ActionIds.Screenshot, "F5" }, { ActionIds.ToggleMute, "S" } };
        var pr2 = reg2.BuildKeyMap(ov);
        Check(reg2.FindActionForKey(Keys.S) == ActionIds.ToggleMute, "override S -> mute");
        Check(reg2.FindActionForKey(Keys.M) == null, "M unbound after override");
        Check(reg2.FindActionForKey(Keys.F5) == ActionIds.Screenshot, "F5 -> screenshot");
        Out("override problems: " + string.Join(" | ", pr2));

        // registry param routing
        string got = null;
        reg.RegisterParam("SetSpeed", a => got = a, a => a == "1");
        Check(reg.Execute("SetSpeed:0.5") && got == "0.5", "param exec");
        Check(reg.IsChecked("SetSpeed:1") && !reg.IsChecked("SetSpeed:2"), "param checked");
        Check(!reg.Execute("Nope"), "unknown action false");

        // time format/parse
        Check(Fmt.Time(75.5) == "1:15", "Time 75.5 -> " + Fmt.Time(75.5));
        Check(Fmt.Time(2.0333333, true) == "0:02.033", "Time ms -> " + Fmt.Time(2.0333333, true));
        Check(Fmt.Time(3725) == "1:02:05", "Time h -> " + Fmt.Time(3725));
        double t;
        Check(Fmt.TryParseTime("1:02:03.5", out t) && Math.Abs(t - 3723.5) < 1e-9, "parse h:m:s.f");
        Check(Fmt.TryParseTime("-2", out t) && t == -2, "parse negative");
        Check(!Fmt.TryParseTime("1:75", out t), "reject 1:75");
        Check(Fmt.Speed(1) == "1×" && Fmt.Speed(0.5) == "0.5×" && Fmt.Speed(1.25) == "1.25×", "speed fmt");

        // settings round trip
        string ini = Path.Combine(Path.GetTempPath(), "vidflow-coretest.ini");
        File.Delete(ini);
        var s = Settings.Load(ini);
        s.Volume = 42.5; s.Repeat = RepeatMode.All; s.ExtraMpvOptions = "a=1\nb=c:\\x"; s.AddRecent(@"C:\v\a.mp4"); s.AddRecent(@"C:\v\b.mp4"); s.AddRecent(@"c:\V\A.mp4");
        s.KeyOverrides["Screenshot"] = "F5, Comma";
        s.Save();
        var s2 = Settings.Load(ini);
        Check(s2.Volume == 42.5 && s2.Repeat == RepeatMode.All, "settings scalars");
        Check(s2.ExtraMpvOptions == "a=1\nb=c:\\x", "settings multiline escape: " + s2.ExtraMpvOptions);
        Check(s2.RecentFiles.Count == 2 && s2.RecentFiles[0] == @"c:\V\A.mp4", "recent dedupe/order");
        Check(s2.KeyOverrides["Screenshot"] == "F5, Comma", "key override persisted");
        File.Delete(ini);

        Out(fails == 0 ? "ALL OK" : ("FAILURES: " + fails));
        File.WriteAllLines(args[0], lines);
    }
}
