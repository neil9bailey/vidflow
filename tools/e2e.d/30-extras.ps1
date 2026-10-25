# Extras: marker export (CSV + Resolve EDL), first-run quick start card, click-only update check.
# Dot-sourced by tools\e2e.ps1 (hidden desktop). Dialogs are driven with window messages posted to
# this test's own VidFlow windows (no real input); the update check talks to a loopback server.

if (-not ('VidFlowExtrasWin' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class VidFlowExtrasWin {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, string l, uint flags, uint ms, out IntPtr result);

    public static string Text(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public static IntPtr Pt(int x, int y) { return (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)); }
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    // Mouse message in the target's client pixels. Posted from a less DPI-aware thread, Windows
    // would rescale the coordinates for the per-monitor-aware VidFlow window.
    public static bool PostMouse(IntPtr h, uint msg, int buttons, int x, int y) {
        SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        return PostMessage(h, msg, (IntPtr)buttons, Pt(x, y));
    }

    // A visible top-level window of the process whose title contains the text.
    public static IntPtr FindTop(int pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h) && Text(h).IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // The file name box of a common Save dialog: a visible Edit inside a ComboBox.
    public static IntPtr FileNameEdit(IntPtr dlg) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(dlg, (h, l) => {
            if (Cls(h) == "Edit" && IsWindowVisible(h)) {
                string pc = Cls(GetParent(h));
                if (pc == "ComboBox" || pc == "ComboBoxEx32") { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool SetText(IntPtr h, string text) {
        IntPtr r;
        return SendMessageTimeout(h, 0x000C /*WM_SETTEXT*/, IntPtr.Zero, text, 0x0002, 3000, out r) != IntPtr.Zero;
    }
}

// Loopback-only HTTP responder standing in for api.github.com.
public sealed class VidFlowExtrasHttp : IDisposable {
    private readonly TcpListener _listener;
    private volatile bool _stop;
    private int _requests;
    public int Port { get; private set; }
    public int Requests { get { return Volatile.Read(ref _requests); } }
    public volatile int Status = 200;
    public volatile string Body = "{}";
    public volatile int DelayMs = 0;
    public volatile string LastPath, LastUserAgent, LastAccept;

    public VidFlowExtrasHttp() {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var t = new Thread(Loop); t.IsBackground = true; t.Start();
    }

    private void Loop() {
        while (!_stop) {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Serve(c));
        }
    }

    private void Serve(TcpClient c) {
        try {
            using (c) {
                var s = c.GetStream();
                s.ReadTimeout = 5000;
                var head = new StringBuilder();
                var buf = new byte[1];
                while (head.Length < 16384 && !head.ToString().EndsWith("\r\n\r\n")) {
                    if (s.Read(buf, 0, 1) != 1) break;
                    head.Append((char)buf[0]);
                }
                string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                LastPath = first.Length > 1 ? first[1] : "";
                foreach (var line in lines) {
                    if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) LastUserAgent = line.Substring(11).Trim();
                    if (line.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase)) LastAccept = line.Substring(7).Trim();
                }
                Interlocked.Increment(ref _requests);
                if (DelayMs > 0) Thread.Sleep(DelayMs);
                byte[] body = Encoding.UTF8.GetBytes(Body ?? "");
                string resp = "HTTP/1.1 " + Status + " X\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                byte[] h = Encoding.ASCII.GetBytes(resp);
                s.Write(h, 0, h.Length);
                s.Write(body, 0, body.Length);
            }
        } catch { /* the client gave up (timeout test) */ }
    }

    public void Dispose() { _stop = true; try { _listener.Stop(); } catch { } }
}
'@
}

# Runs one action on a pipe from a thread job: it blocks while the action's modal dialog is open.
function Start-ExtrasAction([string]$pipe, [string]$id) {
    Start-ThreadJob -ScriptBlock {
        param($pipe, $id)
        $enc = New-Object System.Text.UTF8Encoding($false)
        $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $c.Connect(5000)
            $w = New-Object System.IO.StreamWriter($c, $enc, 4096, $true)
            $w.Write("action`n$id`n<<END>>`n"); $w.Flush()
            $r = New-Object System.IO.StreamReader($c, $enc, $false, 4096, $true)
            $r.ReadLine()
        } finally { $c.Dispose() }
    } -ArgumentList $pipe, $id
}

function Wait-ExtrasWindow([int]$procId, [string]$title, [int]$ms = 8000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        $h = [VidFlowExtrasWin]::FindTop($procId, $title)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 100
    }
    return [IntPtr]::Zero
}

# Fills a Save dialog's file name and confirms it (WM_COMMAND IDOK), all by window messages.
function Complete-ExtrasSaveDialog([int]$procId, [string]$title, [string]$path) {
    $dlg = Wait-ExtrasWindow $procId $title
    if ($dlg -eq [IntPtr]::Zero) { return "dialog '$title' not found" }
    Start-Sleep -Milliseconds 400
    $edit = [IntPtr]::Zero
    for ($i = 0; $i -lt 30 -and $edit -eq [IntPtr]::Zero; $i++) { $edit = [VidFlowExtrasWin]::FileNameEdit($dlg); if ($edit -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 100 } }
    if ($edit -eq [IntPtr]::Zero) { [void][VidFlowExtrasWin]::PostMessage($dlg, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); return 'file name box not found' }
    [void][VidFlowExtrasWin]::SetText($edit, $path)
    [void][VidFlowExtrasWin]::PostMessage($dlg, 0x0111, [IntPtr]1, [IntPtr]::Zero)   # WM_COMMAND IDOK
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ([VidFlowExtrasWin]::IsWindow($dlg) -and $sw.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 100 }
    if ([VidFlowExtrasWin]::IsWindow($dlg)) { [void][VidFlowExtrasWin]::PostMessage($dlg, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); return 'dialog did not accept the name' }
    return 'ok'
}

function Close-ExtrasWindow([IntPtr]$h) {
    [void][VidFlowExtrasWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE = cancel
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ([VidFlowExtrasWin]::IsWindow($h) -and $sw.ElapsedMilliseconds -lt 4000) { Start-Sleep -Milliseconds 100 }
    return -not [VidFlowExtrasWin]::IsWindow($h)
}

Test 'extras-export' {
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' -and $d.fps -gt 0 } 8000 'clip01' | Out-Null
    if (-not (Dump).paused) { Act 'PlayPause' }
    Ctl 'action' @('ClearMarkers') | Out-Null
    foreach ($t in 1, 3) { Ctl 'mpv' @('seek', "$t", 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position $t 0.005 } 3000 "pos $t" | Out-Null; Act 'AddMarker' }
    Ctl 'open' @($clip4) | Out-Null
    WaitFor { param($d) $d.path -like '*clip04*' -and (Near $d.fps 24 0.01) } 8000 'clip04' | Out-Null
    if (-not (Dump).paused) { Act 'PlayPause' }
    Ctl 'action' @('ClearMarkers') | Out-Null
    Ctl 'mpv' @('seek', '2', 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position 2 0.005 } 3000 'pos 2' | Out-Null
    Act 'AddMarker'
    $d = Dump
    Check 'markers on two files' ($d.markerExport.files -eq 2 -and $d.markerExport.markers -eq 3) "files=$($d.markerExport.files) markers=$($d.markerExport.markers)"

    # CSV: every file with markers, through the real Save dialog.
    $csv = Join-Path $work 'extras-markers.csv'
    $job = Start-ExtrasAction $Pipe 'ExportMarkersCsv'
    $r = Complete-ExtrasSaveDialog $proc.Id 'Export Markers as CSV' $csv
    $resp = Receive-Job $job -Wait -AutoRemoveJob
    Check 'CSV save dialog driven' ($r -eq 'ok') "$r / action: $resp"
    Check 'CSV written' (Test-Path $csv) $csv
    if (Test-Path $csv) {
        $bytes = [IO.File]::ReadAllBytes($csv)
        Check 'CSV has a UTF-8 BOM' ($bytes.Length -gt 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $rows = @(Import-Csv $csv)
        $hdr = (Get-Content $csv -TotalCount 1).TrimStart([char]0xFEFF)
        Check 'CSV header' ($hdr -eq 'File,Folder,Marker,Seconds,Timecode,Frame,FPS,Approximate') $hdr
        Check 'CSV has 3 rows' ($rows.Count -eq 3) "$($rows.Count)"
        if ($rows.Count -eq 3) {
            $exp = @(
                @{ File = 'clip01_h264_720p30.mp4'; Marker = '1'; Seconds = '1.000'; Timecode = '00:00:01.000'; Frame = '30'; FPS = '30' },
                @{ File = 'clip01_h264_720p30.mp4'; Marker = '2'; Seconds = '3.000'; Timecode = '00:00:03.000'; Frame = '90'; FPS = '30' },
                @{ File = 'clip04_hevc_multitrack.mkv'; Marker = '1'; Seconds = '2.000'; Timecode = '00:00:02.000'; Frame = '48'; FPS = '24' })
            for ($i = 0; $i -lt 3; $i++) {
                $row = $rows[$i]; $e = $exp[$i]
                $ok = $row.File -eq $e.File -and $row.Folder -eq $media -and $row.Marker -eq $e.Marker -and $row.Seconds -eq $e.Seconds -and
                      $row.Timecode -eq $e.Timecode -and $row.Frame -eq $e.Frame -and $row.FPS -eq $e.FPS -and $row.Approximate -eq 'no'
                Check "CSV row $($i + 1) ($($e.File) #$($e.Marker))" $ok (($row | ConvertTo-Json -Compress))
            }
        }
        $d = Dump
        Check 'dump: last export is the CSV' ($d.markerExport.lastKind -eq 'csv' -and $d.markerExport.lastPath -eq $csv) "$($d.markerExport.lastKind) $($d.markerExport.lastPath)"
    }

    # EDL: the playing clip (clip04, 24 fps) in Resolve's marker layout.
    $edl = Join-Path $work 'extras-clip04.edl'
    $job = Start-ExtrasAction $Pipe 'ExportMarkersEdl'
    $r = Complete-ExtrasSaveDialog $proc.Id 'Export Markers as EDL' $edl
    $resp = Receive-Job $job -Wait -AutoRemoveJob
    Check 'EDL save dialog driven' ($r -eq 'ok') "$r / action: $resp"
    if (Test-Path $edl) {
        $text = [IO.File]::ReadAllText($edl)
        $want = "TITLE: clip04_hevc_multitrack`r`nFCM: NON-DROP FRAME`r`n`r`n" +
                "001  001      V     C        01:00:02:00 01:00:02:01 01:00:02:00 01:00:02:01  `r`n" +
                " |C:ResolveColorBlue |M:Marker 1 |D:1`r`n"
        Check 'EDL content (marker at frame 48 -> 01:00:02:00)' ($text -ceq $want) ($text -replace "`r`n", '\n')
        $bytes = [IO.File]::ReadAllBytes($edl)
        Check 'EDL has no BOM' ($bytes[0] -eq [byte][char]'T')
    } else { Check 'EDL written' $false $edl }
    Check 'OSD names the EDL' ("$((Dump).lastOsd)" -like '*extras-clip04.edl*24 fps*') "$((Dump).lastOsd)"

    # Cancel leaves nothing behind and reports nothing.
    $before = (Dump).markerExport.lastPath
    $job = Start-ExtrasAction $Pipe 'ExportMarkersEdl'
    $dlg = Wait-ExtrasWindow $proc.Id 'Export Markers as EDL'
    $closed = $dlg -ne [IntPtr]::Zero -and (Close-ExtrasWindow $dlg)
    Receive-Job $job -Wait -AutoRemoveJob | Out-Null
    Check 'cancelled export writes nothing' ($closed -and (Dump).markerExport.lastPath -eq $before) "closed=$closed"

    # OSD status line: plain "F" for this constant-frame-rate clip.
    $msg = Ctl 'get' @('osd-status-msg')
    Check 'osd-status-msg uses "F" on a CFR clip' ($msg -match '   F \$\{estimated-frame-number\}' -and $msg -notmatch '≈') $msg

    Act 'ClearMarkers'
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' } 8000 'clip01 again' | Out-Null
    Act 'ClearMarkers'
    $r = Ctl 'action' @('ExportMarkersCsv')
    Check 'CSV export disabled without markers' ($r -match 'disabled') $r
}

# Drop-frame EDL and the variable-frame-rate marks, with the frame-accuracy test clips (skipped
# when testmedia\frames is not there).
Test 'extras-df-vfr' {
    $df = Join-Path $media 'frames\fr_cfr_29.97fps.mp4'
    $vfr = Join-Path $media 'frames\fr_vfr_steps.mp4'
    if (-not (Test-Path $df) -or -not (Test-Path $vfr)) { Write-Host '  (skipped: testmedia\frames clips missing)' -ForegroundColor Yellow; return }

    Ctl 'open' @($df) | Out-Null
    WaitFor { param($d) $d.path -like '*fr_cfr_29.97fps.mp4' -and (Near $d.fps 29.97 0.01) } 8000 '29.97 clip' | Out-Null
    if (-not (Dump).paused) { Act 'PlayPause' }
    Ctl 'action' @('ClearMarkers') | Out-Null
    Ctl 'mpv' @('seek', '2.002', 'absolute+exact') | Out-Null   # frame 60 = 60 x 1001/30000 s
    WaitFor { param($d) Near $d.position 2.002 0.0005 } 3000 'frame 60' | Out-Null
    Act 'AddMarker'
    $edl = Join-Path $work 'extras-2997.edl'
    $job = Start-ExtrasAction $Pipe 'ExportMarkersEdl'
    $r = Complete-ExtrasSaveDialog $proc.Id 'Export Markers as EDL' $edl
    Receive-Job $job -Wait -AutoRemoveJob | Out-Null
    $text = if (Test-Path $edl) { [IO.File]::ReadAllText($edl) } else { '' }
    Check '29.97: drop-frame EDL, frame 60 -> 01:00:02;00' ($r -eq 'ok' -and $text -match 'FCM: DROP FRAME' -and
        $text -match '001  001      V     C        01:00:02;00 01:00:02;01 01:00:02;00 01:00:02;01  ') ($text -replace "`r`n", '\n')
    Act 'ClearMarkers'

    Ctl 'open' @($vfr) | Out-Null
    WaitFor { param($d) $d.path -like '*fr_vfr_steps.mp4' -and $d.fps -gt 0 } 8000 'VFR clip' | Out-Null
    # The frame probe's verdict: the dump's "vfr" when present, else the user-data VidFlow mirrors it to.
    $sw = [Diagnostics.Stopwatch]::StartNew(); $v = ''
    while ($sw.ElapsedMilliseconds -lt 10000 -and $v -ne 'yes') {
        Start-Sleep -Milliseconds 200
        $d = Dump
        if ($null -ne $d.PSObject.Properties['vfr']) { $v = if ($d.vfr -eq $true) { 'yes' } else { "$($d.vfr)" } }
        else { $v = (Ctl 'get' @('user-data/vidflow/vfr')).Trim('"') }
    }
    if ($v -ne 'yes') { Write-Host "  (skipped: the frame probe did not report VFR: '$v')" -ForegroundColor Yellow; return }
    Start-Sleep -Milliseconds 300   # the next time-pos update re-sends the status line
    if (-not (Dump).paused) { Act 'PlayPause' }
    Ctl 'mpv' @('seek', '1', 'absolute+exact') | Out-Null
    WaitFor { param($d) Near $d.position 1 0.1 } 3000 'pos 1' | Out-Null
    $msg = Ctl 'get' @('osd-status-msg')
    Check 'VFR: osd-status-msg shows "≈F"' ($msg -match '≈F \$\{estimated-frame-number\}') $msg
    Ctl 'action' @('ClearMarkers') | Out-Null
    Act 'AddMarker'
    Check 'VFR: marker OSD says "≈F"' ("$((Dump).lastOsd)" -match '≈F \d+') "$((Dump).lastOsd)"
    $csv = Join-Path $work 'extras-vfr.csv'
    $job = Start-ExtrasAction $Pipe 'ExportMarkersCsv'
    $r = Complete-ExtrasSaveDialog $proc.Id 'Export Markers as CSV' $csv
    Receive-Job $job -Wait -AutoRemoveJob | Out-Null
    $rows = if (Test-Path $csv) { @(Import-Csv $csv) } else { @() }
    Check 'VFR: CSV marks the frame approximate' ($r -eq 'ok' -and $rows.Count -eq 1 -and $rows[0].Approximate -eq 'yes' -and $rows[0].File -eq 'fr_vfr_steps.mp4') (($rows | ConvertTo-Json -Compress))
    Act 'ClearMarkers'
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' } 8000 'clip01 again' | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do { Start-Sleep -Milliseconds 200; $msg = Ctl 'get' @('osd-status-msg') } while ($msg -match '≈' -and $sw.ElapsedMilliseconds -lt 3000)
    Check 'back on a CFR clip: plain "F" again' ($msg -match '   F \$\{estimated-frame-number\}' -and $msg -notmatch '≈') $msg
}

Test 'extras-update-disabled' {
    $d = Dump
    $r = Ctl 'action' @('CheckForUpdates')
    Check 'Check for Updates disabled without UpdateRepo' ($r -match 'disabled' -and $d.update.enabled -eq $false -and $d.update.checks -eq 0) "$r enabled=$($d.update.enabled)"
}

Test 'extras-quickstart-update' {
    $server = New-Object VidFlowExtrasHttp
    $Pipe = "$Pipe-extras"   # Ctl/Dump/Act/WaitFor below talk to this test's own instance
    $ini = Join-Path $work 'extras.ini'
    @"
AutoFitWindow=false
WindowX=60
WindowY=40
WindowWidth=1100
WindowHeight=860
QuickStartShown=false
UpdateRepo=example/vidflow
OsdMessages=true
ExtraMpvOptions=ao=null
recent.0=$clip1
recent.1=$clip4
"@ | Set-Content -Encoding UTF8 $ini
    $env:VIDFLOW_UPDATE_API = "http://127.0.0.1:$($server.Port)"
    $p = Start-Process -FilePath $Exe -ArgumentList @('--automation', '--pipe', $Pipe, '--settings', "`"$ini`"", '--log', "`"$work\extras-vidflow.log`"") -PassThru
    $env:VIDFLOW_UPDATE_API = $null
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew(); $up = $false
        while (-not $up -and $sw.ElapsedMilliseconds -lt 15000) { try { $up = (Ctl 'activate') -eq 'ok' } catch { Start-Sleep -Milliseconds 150 } }
        Check 'second instance up' $up

        # ---- quick start card ----
        $d = WaitFor { param($d) $d.quickStart.shown } 6000 'quick start card painted'
        $q = $d.quickStart
        Check 'card shown on first run (idle)' ($q.visible -and $q.shown -and -not $q.settingShown) (($q | ConvertTo-Json -Compress))
        Check 'all six key rows fit' ($q.rows -eq 6) "rows=$($q.rows)"
        $c = $q.card; $o = $q.open; $g = $q.gotIt; $hs = $d.videoHostSize
        $inside = { param($a, $b) $a[0] -ge $b[0] -and $a[1] -ge $b[1] -and ($a[0] + $a[2]) -le ($b[0] + $b[2]) -and ($a[1] + $a[3]) -le ($b[1] + $b[3]) }
        Check 'buttons inside the card, card inside the video area' ((& $inside $o $c) -and (& $inside $g $c) -and (& $inside $c @(0, 0, $hs[0], $hs[1]))) "card=$c open=$o gotIt=$g host=$hs"
        $shot = Join-Path $work 'shots\extras-quickstart.png'
        & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Client -Out $shot | Out-Null
        Check 'quick start screenshot' (Test-Path $shot) $shot

        $hostH = [IntPtr][long]$d.hostHandle
        $gx = [int]($g[0] + $g[2] / 2); $gy = [int]($g[1] + $g[3] / 2)
        $ox = [int]($o[0] + $o[2] / 2); $oy = [int]($o[1] + $o[3] / 2)
        # (Hover is not checked: a posted WM_MOUSEMOVE is followed at once by WM_MOUSELEAVE, the
        # real cursor being elsewhere.)

        # "Open a folder" runs OpenFolder (its picker is cancelled again).
        [void][VidFlowExtrasWin]::PostMouse($hostH, 0x0201, 1, $ox, $oy)   # WM_LBUTTONDOWN
        [void][VidFlowExtrasWin]::PostMouse($hostH, 0x0202, 0, $ox, $oy)   # WM_LBUTTONUP
        $picker = Wait-ExtrasWindow $p.Id 'Open Folder' 6000
        Check '"Open a folder" opens the folder picker' ($picker -ne [IntPtr]::Zero)
        if ($picker -ne [IntPtr]::Zero) { Check 'folder picker cancelled' (Close-ExtrasWindow $picker) }
        $d = Dump
        Check 'card still shown after cancelling' ($d.quickStart.shown -and $null -eq $d.path) "path=$($d.path)"

        # "Got it" dismisses it for good (saved).
        [void][VidFlowExtrasWin]::PostMouse($hostH, 0x0201, 1, $gx, $gy)
        [void][VidFlowExtrasWin]::PostMouse($hostH, 0x0202, 0, $gx, $gy)
        $d = WaitFor { param($d) -not $d.quickStart.visible } 3000 'dismissed'
        Check '"Got it" hides the card' (-not $d.quickStart.shown -and $d.quickStart.settingShown)
        Check '"Got it" saved QuickStartShown' ((Get-Content $ini -Raw) -match '(?im)^QuickStartShown=true') ''
        Act 'ShowQuickStart'
        $d = WaitFor { param($d) $d.quickStart.shown } 3000 'shown again'
        Check 'ShowQuickStart re-shows the card' ($d.quickStart.visible -and -not $d.quickStart.settingShown)
        Check 'ShowQuickStart saved QuickStartShown=false' ((Get-Content $ini -Raw) -match '(?im)^QuickStartShown=false') ''
        # A press that starts on "Got it" but is released elsewhere does nothing.
        [void][VidFlowExtrasWin]::PostMouse($hostH, 0x0201, 1, $gx, $gy)
        [void][VidFlowExtrasWin]::PostMouse($hostH, 0x0202, 0, 5, 5)
        Start-Sleep -Milliseconds 400
        Check 'release off the button does not dismiss' ((Dump).quickStart.visible)

        # ---- update check: never automatic ----
        Check 'no request before the user asks' ($server.Requests -eq 0) "requests=$($server.Requests)"

        $closeDialog = {
            $h = Wait-ExtrasWindow $p.Id 'Check for Updates' 4000
            if ($h -eq [IntPtr]::Zero) { return $false }
            return (Close-ExtrasWindow $h)
        }
        $server.Body = '{"tag_name":"v9.8.7","name":"VidFlow 9.8.7","html_url":"https://github.com/example/vidflow/releases/tag/v9.8.7","draft":false,"prerelease":false,"assets":[]}'
        Act 'CheckForUpdates'
        $d = WaitFor { param($d) $d.update.checks -eq 1 -and -not $d.update.busy -and $d.update.dialogOpen } 8000 'update dialog (available)'
        Check 'newer release found' ($d.update.status -eq 'Available' -and $d.update.latest -eq '9.8.7' -and $d.update.current -eq '1.0.0') "$($d.update.status) $($d.update.latest) cur=$($d.update.current)"
        Check 'one request to /repos/<repo>/releases/latest' ($server.Requests -eq 1 -and $server.LastPath -eq '/repos/example/vidflow/releases/latest') "n=$($server.Requests) path=$($server.LastPath)"
        Check 'User-Agent and GitHub Accept header sent' ($server.LastUserAgent -like 'VidFlow/1.0.0*' -and $server.LastAccept -like '*github*') "UA=$($server.LastUserAgent) Accept=$($server.LastAccept)"
        Check 'release page is the GitHub page' ($d.update.pageUrl -eq 'https://github.com/example/vidflow/releases/tag/v9.8.7') "$($d.update.pageUrl)"
        $dshot = Join-Path $work 'shots\extras-update-available.png'
        & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Title 'Check for Updates' -IncludeOwned -Out $dshot | Out-Null
        Check 'dialog closed with Cancel' (& $closeDialog)
        $d = Dump
        Check 'dialog state cleared, release page not opened' (-not $d.update.dialogOpen -and $null -eq $d.update.pageNotOpened)

        # The build's own version (e.g. v1.0.0-beta.4) is up to date; the final release of the same
        # number is an update for a pre-release build only.
        $exeVersion = ((Get-Item $Exe).VersionInfo.ProductVersion -split '\+')[0]
        $server.Body = '{"tag_name":"v' + $exeVersion + '","html_url":"https://github.com/example/vidflow/releases/tag/v' + $exeVersion + '"}'
        Act 'CheckForUpdates'
        $d = WaitFor { param($d) $d.update.checks -eq 2 -and -not $d.update.busy -and $d.update.dialogOpen } 8000 'update dialog (current)'
        Check 'same version: up to date' ($d.update.status -eq 'UpToDate') "$($d.update.status) (build $exeVersion)"
        & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Title 'Check for Updates' -IncludeOwned -Out (Join-Path $work 'shots\extras-update-current.png') | Out-Null
        Check 'up-to-date dialog closed' (& $closeDialog)

        $final = ($exeVersion -split '-')[0]
        $expect = if ($exeVersion -ne $final) { 'Available' } else { 'UpToDate' }
        $server.Body = '{"tag_name":"v' + $final + '","html_url":"https://github.com/example/vidflow/releases/tag/v' + $final + '"}'
        Act 'CheckForUpdates'
        $d = WaitFor { param($d) $d.update.checks -eq 3 -and -not $d.update.busy -and $d.update.dialogOpen } 8000 'update dialog (final of same version)'
        Check "final v$final against build $($exeVersion): $expect" ($d.update.status -eq $expect) "$($d.update.status)"
        Check 'final-release dialog closed' (& $closeDialog)

        $server.Status = 404; $server.Body = '{"message":"Not Found"}'
        Act 'CheckForUpdates'
        $d = WaitFor { param($d) $d.update.checks -eq 4 -and -not $d.update.busy -and $d.update.dialogOpen } 8000 'update dialog (404)'
        Check '404: no releases yet' ($d.update.status -eq 'NoReleases') "$($d.update.status)"
        Check '404 dialog closed' (& $closeDialog)

        $server.Status = 200; $server.Body = '<html>not json</html>'
        Act 'CheckForUpdates'
        $d = WaitFor { param($d) $d.update.checks -eq 5 -and -not $d.update.busy -and $d.update.dialogOpen } 8000 'update dialog (garbage)'
        Check 'unreadable answer reported' ($d.update.status -eq 'Failed' -and "$($d.update.error)" -like 'Unreadable*') "$($d.update.status): $($d.update.error)"
        Check 'error dialog closed' (& $closeDialog)

        # A server that never answers: the request gives up after 10 s, the UI stays responsive.
        $server.DelayMs = 20000; $server.Body = '{"tag_name":"v9.9.9"}'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Act 'CheckForUpdates'
        Start-Sleep -Milliseconds 500
        Act 'CheckForUpdates'   # while busy: ignored
        $d = Dump
        Check 'busy: second click ignored' ($d.update.busy -and $d.update.checks -eq 6 -and "$($d.lastOsd)" -like 'Already checking*') "busy=$($d.update.busy) checks=$($d.update.checks) osd=$($d.lastOsd)"
        $d = WaitFor { param($d) $d.update.checks -eq 6 -and -not $d.update.busy -and $d.update.dialogOpen } 16000 'update timeout'
        $secs = $sw.Elapsed.TotalSeconds
        Check 'timeout after ~10 s' ($d.update.status -eq 'Failed' -and "$($d.update.error)" -like 'No answer within 10*' -and $secs -ge 9.5 -and $secs -le 13) ("{0}: {1} after {2:N1} s" -f $d.update.status, $d.update.error, $secs)
        Check 'timeout dialog closed' (& $closeDialog)
        Check 'exactly one request per click' ($server.Requests -eq 6) "requests=$($server.Requests)"

        # Over a playing file the card cannot be seen: ShowQuickStart puts the keys on the OSD.
        Ctl 'open' @($clip1) | Out-Null
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.videoHostSize[0] -gt 0 } 8000 'clip01 in 2nd instance' | Out-Null
        WaitFor { param($d) -not $d.quickStart.shown } 3000 'card gone under video' | Out-Null
        Act 'ShowQuickStart'
        Check 'ShowQuickStart over video -> OSD keys' ("$((Dump).lastOsd)" -like '*six keys*Ctrl+Enter*apply*') "$((Dump).lastOsd)"
    } finally {
        try { Ctl 'quit' | Out-Null } catch { }
        if (-not $p.WaitForExit(6000)) { try { $p.Kill() } catch { } }
        $server.Dispose()
    }
}
