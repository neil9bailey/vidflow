# Cull workflow (roadmap L3-L6): the cull key layer, pick/reject/rate with auto-advance, decision
# filters, undo/redo, the decisions index across restarts (originals byte-identical, nothing written
# beside them), F2 rename while playing, bins (F5-F9) and Apply (moves with name collisions, read-only
# files, a second volume, Recycle Bin) plus the Apply and Rename dialogs.
# Every test runs its own VidFlow (own pipe, own settings folder) on copies under obj\e2e\cull;
# testmedia is never written. Dot-sourced by tools\e2e.ps1 (helpers: Test, Check, Ctl, Dump, Act, Key,
# WaitFor, Near; variables $Exe, $Pipe, $media, $work).

$cullRoot = Join-Path $work 'cull'
$cullPipe = "$Pipe-Cull"
$cullNames = @('clip01_h264_720p30.mp4', 'clip02_portrait_60fps.mp4', 'clip03_vp9.webm', 'clip04_hevc_multitrack.mkv', 'clip05_av1_noaudio.mkv')
$cullMediaHashes = @{}
foreach ($n in $cullNames) { $cullMediaHashes[$n] = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $media $n)).Hash }

if (-not ('CullWin' -as [type])) {
    Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class CullWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static IntPtr Find(int pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      if (sb.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  public static IntPtr FindEdit(IntPtr dialog) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(dialog, (h, l) => { var sb = new StringBuilder(256); GetClassName(h, sb, 256);
      if (sb.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  public static IntPtr FindChild(IntPtr dialog, string text) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(dialog, (h, l) => { var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      if (sb.ToString() == text && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  public static string Text(IntPtr h) { var sb = new StringBuilder(1024); GetWindowText(h, sb, 1024); return sb.ToString(); }
}
'@
}

function Get-CullHash([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

# A fresh set: <name>\clips holds copies of the five test clips, <name>\cfg the settings (and with
# them the decisions index). Returns @{ Clips; Cfg; Files }.
function New-CullSet([string]$name, [string[]]$settings = @()) {
    $dir = Join-Path $cullRoot $name
    if (Test-Path -LiteralPath $dir) {
        Get-ChildItem -LiteralPath $dir -Recurse -Force -File | ForEach-Object { $_.Attributes = 'Normal' }
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
    $clips = Join-Path $dir 'clips'; $cfg = Join-Path $dir 'cfg'
    New-Item -ItemType Directory -Force $clips, $cfg | Out-Null
    $files = foreach ($n in $cullNames) { $t = Join-Path $clips $n; Copy-Item -LiteralPath (Join-Path $media $n) -Destination $t; $t }
    $ini = @('AutoFitWindow=false', 'WindowX=80', 'WindowY=60', 'WindowWidth=1100', 'WindowHeight=640', 'Repeat=Off',
             'ThumbnailPreview=false', 'OsdMessages=true', 'ShowPlaylist=true', 'PlaylistWidth=300', 'ExtraMpvOptions=ao=null',
             'CullMode=true', 'CullAutoAdvance=true') + $settings
    $ini | Set-Content -Encoding UTF8 (Join-Path $cfg 'settings.ini')
    return @{ Clips = $clips; Cfg = $cfg; Files = @($files) }
}

# Runs $body against a VidFlow started on $open with the set's settings; always quits it afterwards
# (killing it if it does not exit) and restores the main suite's pipe.
function Invoke-CullSession($set, [string[]]$open, [scriptblock]$body) {
    $savedPipe = $script:Pipe
    $p = $null
    try {
        $argList = @('--automation', '--pipe', $cullPipe, '--settings', "`"$(Join-Path $set.Cfg 'settings.ini')`"",
                     '--log', "`"$(Join-Path $set.Cfg 'vidflow.log')`"") + @($open | ForEach-Object { "`"$_`"" })
        $p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
        $script:Pipe = $cullPipe
        $sw = [Diagnostics.Stopwatch]::StartNew(); $up = $false
        while (-not $up -and $sw.ElapsedMilliseconds -lt 15000) { try { $up = (Ctl 'activate') -eq 'ok' } catch { Start-Sleep -Milliseconds 150 } }
        if (-not $up) { throw "cull VidFlow did not open its control pipe" }
        & $body $p
    } finally {
        if ($p) {
            try { Ctl 'quit' | Out-Null } catch { }
            if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        }
        $script:Pipe = $savedPipe
    }
}

function Wait-CullIdle([int]$ms = 15000) { WaitFor { param($d) -not $d.cull.busy } $ms 'cull file job finished' }
function CullMk($d, [int]$i) { return "$($d.cull.marks[$i][0]),$($d.cull.marks[$i][1])" }

Test 'cull-decisions' {
    $set = New-CullSet 'decide'
    Invoke-CullSession $set @($set.Files[0]) {
        $d = WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 -and $d.duration -gt 0 } 10000 'clip01 + folder'
        Check 'cull mode on (settings) and pushed to the bar' ($d.cull.mode -and $d.cull.bar.mode) "mode=$($d.cull.mode) bar=$($d.cull.bar.mode)"
        Check 'progress "0 / 5 decided", chip "0/5"' ($d.cull.progressText -eq '0 / 5 decided' -and $d.cull.bar.progress -eq '0/5') "$($d.cull.progressText) / $($d.cull.bar.progress)"
        $r = Key @('P')
        $d = WaitFor { param($d) $d.path -like '*clip02*' } 5000 'advance to clip02'
        Check 'P runs CullPick in the cull layer' ($r -eq 'ok CullPick') $r
        Check 'clip01 picked' ((CullMk $d 0) -eq '1,0') (CullMk $d 0)
        Check 'OSD "✓ Pick"' ($d.lastOsd -eq '✓ Pick') $d.lastOsd
        Check 'bar: 1/5 decided, clip02 undecided' ($d.cull.bar.progress -eq '1/5' -and $d.cull.bar.flag -eq 0) "$($d.cull.bar.progress) flag=$($d.cull.bar.flag)"
        $r = Key @('X')
        $d = WaitFor { param($d) $d.path -like '*clip03*' } 5000 'advance to clip03'
        Check 'X rejects clip02 and advances' ($r -eq 'ok CullReject' -and (CullMk $d 1) -eq '-1,0') "$r $(CullMk $d 1)"
        Check 'OSD "✕ Reject"' ($d.lastOsd -eq '✕ Reject') $d.lastOsd
        $r = Key @('3'); $d = Dump
        Check '3 rates clip03 ★★★, no advance' ($r -eq 'ok CullRate:3' -and (CullMk $d 2) -eq '0,3' -and $d.path -like '*clip03*') "$r $(CullMk $d 2) $($d.path)"
        Check 'OSD "★★★☆☆" and bar rating 3' ($d.lastOsd -eq '★★★☆☆' -and $d.cull.bar.rating -eq 3) "$($d.lastOsd) bar=$($d.cull.bar.rating)"
        Key @('0') | Out-Null; $d = Dump
        Check '0 clears the rating' ((CullMk $d 2) -eq '0,0' -and $d.lastOsd -eq 'Rating cleared') "$(CullMk $d 2) $($d.lastOsd)"
        Key @('4') | Out-Null
        Act 'PlayIndex:0'; WaitFor { param($d) $d.path -like '*clip01*' } 5000 'back to clip01' | Out-Null
        Key @('P') | Out-Null; Start-Sleep -Milliseconds 300; $d = Dump
        Check 'P on a pick takes it back (no advance)' ((CullMk $d 0) -eq '0,0' -and $d.path -like '*clip01*' -and $d.lastOsd -eq 'Pick removed') "$(CullMk $d 0) $($d.path) $($d.lastOsd)"
        Key @('X') | Out-Null
        $d = WaitFor { param($d) $d.path -like '*clip02*' } 5000 'advance to clip02'
        Key @('U') | Out-Null; $d = Dump
        Check 'U clears the reject' ((CullMk $d 1) -eq '0,0' -and $d.lastOsd -eq 'Flag cleared') "$(CullMk $d 1) $($d.lastOsd)"
        # marks now: -1,0 | 0,0 | 0,4 | 0,0 | 0,0 ; on clip02
        Key @('N') | Out-Null
        $d = WaitFor { param($d) $d.path -like '*clip03*' } 5000 'N -> clip03'
        Check 'N jumps to the next undecided clip' ($d.path -like '*clip03*') $d.path
        Act 'PlayIndex:4'; WaitFor { param($d) $d.path -like '*clip05*' } 5000 'clip05' | Out-Null
        Key @('P') | Out-Null; Start-Sleep -Milliseconds 300; $d = Dump
        Check 'end of list: no wrap, says what is left' ($d.path -like '*clip05*' -and $d.lastOsd -like '✓ Pick · Last clip · 3 undecided*') "$($d.path) | $($d.lastOsd)"
        Act 'PlayIndex:1'; WaitFor { param($d) $d.path -like '*clip02*' } 5000 'clip02' | Out-Null
        Key @('P') | Out-Null; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null
        Key @('X') | Out-Null; WaitFor { param($d) $d.path -like '*clip04*' } 5000 'clip04' | Out-Null
        Act 'PlayIndex:4'; WaitFor { param($d) $d.path -like '*clip05*' } 5000 'clip05' | Out-Null
        Key @('U') | Out-Null
        Act 'PlayIndex:3'; WaitFor { param($d) $d.path -like '*clip04*' } 5000 'clip04' | Out-Null
        Key @('P') | Out-Null; WaitFor { param($d) $d.path -like '*clip05*' } 5000 'clip05' | Out-Null
        Key @('P') | Out-Null; Start-Sleep -Milliseconds 300; $d = Dump
        Check 'last decision: "All clips decided"' ($d.lastOsd -eq '✓ Pick · All clips decided') $d.lastOsd
        Check '5 / 5 decided, panel "3 ✓ · 2 ✕ · 0 left"' ($d.cull.progressText -eq '5 / 5 decided' -and $d.cull.panelSummary -eq '3 ✓ · 2 ✕ · 0 left') "$($d.cull.progressText) | $($d.cull.panelSummary)"
        # Outside cull mode the layer is off: X is the global subtitle-delay key again.
        Act 'ToggleCullMode'; $d = Dump
        Check 'C toggles cull mode off (bar chip hidden)' (-not $d.cull.mode -and -not $d.cull.bar.mode -and $d.cull.bar.progress -eq '') "mode=$($d.cull.mode) bar=$($d.cull.bar.mode)"
        $before = (Dump).cull.marks | ForEach-Object { "$($_[0]),$($_[1])" }
        $r = Key @('X'); $after = (Dump).cull.marks | ForEach-Object { "$($_[0]),$($_[1])" }
        Check 'X = SubDelayUp outside cull mode, decisions untouched' ($r -eq 'ok SubDelayUp' -and ($before -join ' ') -eq ($after -join ' ')) "$r"
        Act 'SubDelayReset'
        $r = Key @('C'); Check 'C turns it back on' ($r -eq 'ok ToggleCullMode' -and (Dump).cull.mode) $r
    }
}

Test 'cull-undo' {
    $set = New-CullSet 'undo' @('CullAutoAdvance=false')
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
        Key @('P', '2', 'X') | Out-Null; $d = Dump
        Check 'P, 2, X -> reject ★★ (3 undo steps)' ((CullMk $d 0) -eq '-1,2' -and $d.cull.undo -eq 3) "$(CullMk $d 0) undo=$($d.cull.undo)"
        Key @('Ctrl+Z') | Out-Null; $d = Dump
        Check 'Ctrl+Z -> pick ★★' ((CullMk $d 0) -eq '1,2' -and $d.lastOsd -like 'Undo: ✕ Reject*') "$(CullMk $d 0) $($d.lastOsd)"
        Key @('Ctrl+Z', 'Ctrl+Z') | Out-Null; $d = Dump
        Check 'two more -> nothing; redo has 3' ((CullMk $d 0) -eq '0,0' -and $d.cull.undo -eq 0 -and $d.cull.redo -eq 3) "$(CullMk $d 0) u=$($d.cull.undo) r=$($d.cull.redo)"
        Key @('Ctrl+Y', 'Ctrl+Y') | Out-Null; $d = Dump
        Check 'Ctrl+Y x2 -> pick ★★' ((CullMk $d 0) -eq '1,2' -and $d.cull.redo -eq 1) "$(CullMk $d 0) r=$($d.cull.redo)"
        Key @('5') | Out-Null; $d = Dump
        Check 'a new decision clears redo' ((CullMk $d 0) -eq '1,5' -and $d.cull.redo -eq 0) "$(CullMk $d 0) r=$($d.cull.redo)"
        Act 'PlayIndex:2'; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null
        Key @('Ctrl+Z') | Out-Null
        $d = WaitFor { param($d) $d.path -like '*clip01*' } 5000 'undo returns to clip01'
        Check 'undo returns to the clip it changed' ((CullMk $d 0) -eq '1,2') "$(CullMk $d 0) $($d.path)"
    }
}

Test 'cull-filter' {
    $set = New-CullSet 'filter'
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
        Key @('P') | Out-Null; WaitFor { param($d) $d.path -like '*clip02*' } 5000 'clip02' | Out-Null
        Key @('X') | Out-Null; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null
        Key @('3') | Out-Null
        $counts = @{}
        foreach ($f in 'picked', 'rejected', 'undecided', 'rated', 'all') { Act "CullFilter:$f"; $d = Dump; $counts[$f] = $d.cull.panelVisible; if ($d.cull.panelFilter -ne $f) { $counts[$f] = "panel=$($d.cull.panelFilter)" } }
        Check 'filters: picks 1, rejects 1, undecided 3, rated 1, all 5' ($counts.picked -eq 1 -and $counts.rejected -eq 1 -and $counts.undecided -eq 3 -and $counts.rated -eq 1 -and $counts.all -eq 5) (($counts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
        $m = ((Ctl 'menu' @('context')) | ConvertFrom-Json)
        $flat = New-Object System.Collections.Generic.List[string]
        function Walk($items) { foreach ($i in $items) { if ($i.text) { $flat.Add($i.text) }; if ($i.children) { Walk $i.children } } }
        Walk $m
        Check 'context menu offers the cull commands' (($flat | Where-Object { $_ -like '*Picks Only*' }).Count -ge 1 -and ($flat | Where-Object { $_ -like 'Apply Decisions*' }).Count -ge 1) "$($flat.Count) items"
        # Auto-advance follows the filter: with "undecided", a decided clip is skipped.
        Act 'CullFilter:undecided'
        Act 'PlayIndex:3'; WaitFor { param($d) $d.path -like '*clip04*' } 5000 'clip04' | Out-Null
        Key @('P') | Out-Null; WaitFor { param($d) $d.path -like '*clip05*' } 5000 'clip05' | Out-Null
        Act 'PlayIndex:2'; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null
        Key @('P') | Out-Null
        $d = WaitFor { param($d) $d.path -like '*clip05*' } 5000 'skips picked clip04'
        Check 'auto-advance skips clips the filter hides' ($d.path -like '*clip05*') $d.path
        Check 'undecided view shrinks as clips get decided' ($d.cull.panelVisible -eq 1) "$($d.cull.panelVisible)"
        Key @('X') | Out-Null; Start-Sleep -Milliseconds 300; $d = Dump
        Check 'empty undecided view, "All clips decided"' ($d.cull.panelVisible -eq 0 -and $d.lastOsd -eq '✕ Reject · All clips decided') "$($d.cull.panelVisible) $($d.lastOsd)"
        & (Join-Path $root 'tools\capture.ps1') -ProcessId $args[0].Id -Out (Join-Path $work 'shots\cull-panel-undecided.png') | Out-Null
        Act 'CullFilter:all'
        & (Join-Path $root 'tools\capture.ps1') -ProcessId $args[0].Id -Out (Join-Path $work 'shots\cull-panel.png') | Out-Null
        Check 'panel screenshots taken' ((Test-Path (Join-Path $work 'shots\cull-panel.png')) -and (Test-Path (Join-Path $work 'shots\cull-panel-undecided.png')))
    }
}

Test 'cull-persist' {
    $set = New-CullSet 'persist'
    $hashes = @{}; foreach ($f in $set.Files) { $hashes[$f] = Get-CullHash $f }
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
        Key @('P') | Out-Null; WaitFor { param($d) $d.path -like '*clip02*' } 5000 'clip02' | Out-Null
        Key @('X') | Out-Null; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null
        Key @('5') | Out-Null
    }
    $index = Join-Path $set.Cfg 'cull-decisions.tsv'
    Check 'decisions index saved in the settings folder' (Test-Path $index) $index
    $lines = @(Get-Content -LiteralPath $index -Encoding UTF8 | Where-Object { $_ -and -not $_.StartsWith('#') })
    Check 'index holds the 3 decided clips' ($lines.Count -eq 3) "$($lines.Count) lines"
    $listing = @(Get-ChildItem -LiteralPath $set.Clips -Force | ForEach-Object Name)
    Check 'nothing written beside the clips' ($listing.Count -eq 5) ($listing -join ', ')
    $same = $true; foreach ($f in $set.Files) { if ((Get-CullHash $f) -ne $hashes[$f]) { $same = $false } }
    Check 'clips byte-identical (SHA-256)' $same
    Invoke-CullSession $set @($set.Files[0]) {
        $d = WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01 again'
        $got = (0..4 | ForEach-Object { CullMk $d $_ }) -join ' '
        Check 'restart: same decisions' ($got -eq '1,0 -1,0 0,5 0,0 0,0') $got
    }
    # A clip edited since its decision (size/time change) loses the stale decision.
    [IO.File]::AppendAllText($set.Files[1], 'x')
    Invoke-CullSession $set @($set.Files[0]) {
        $d = WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01 third run'
        $got = (0..4 | ForEach-Object { CullMk $d $_ }) -join ' '
        Check 'edited clip: stale decision ignored, others kept' ($got -eq '1,0 0,0 0,5 0,0 0,0') $got
    }
}

Test 'cull-rename' {
    $set = New-CullSet 'rename' @('CullAutoAdvance=false')
    $orig = $set.Files[0]
    $renamed = Join-Path $set.Clips 'renamed clip.mp4'
    Invoke-CullSession $set @($orig) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 -and $d.frameCount -gt 0 } 10000 'clip01' | Out-Null
        Key @('P') | Out-Null
        if (-not (Dump).paused) { Act 'PlayPause' }
        Ctl 'mpv' @('seek', '2', 'absolute+exact') | Out-Null
        WaitFor { param($d) $d.paused -and $d.frame -eq 60 } 5000 'paused at frame 60' | Out-Null
        Act 'RenameFile:renamed clip.mp4'
        $d = WaitFor { param($d) $d.path -eq $renamed -and $d.frame -eq 60 -and -not $d.cull.busy } 10000 'reloaded under the new name at frame 60'
        Check 'renamed on disk' ((Test-Path -LiteralPath $renamed) -and -not (Test-Path -LiteralPath $orig))
        Check 'same frame (60) and still paused' ($d.frame -eq 60 -and $d.paused) "frame=$($d.frame) paused=$($d.paused)"
        Check 'playlist entry replaced in place' ($d.playlist[0] -eq $renamed -and $d.playlistCount -eq 5 -and $d.playlistIndex -eq 0 -and -not ($d.playlist -contains $orig)) "$($d.playlist[0]) idx=$($d.playlistIndex)"
        Check 'title shows the new name' ($d.title -like 'renamed clip.mp4*') $d.title
        Check 'decision followed the rename' ((CullMk $d 0) -eq '1,0') (CullMk $d 0)
        Key @('Ctrl+Z') | Out-Null
        $d = WaitFor { param($d) $d.path -eq $orig -and $d.frame -eq 60 -and -not $d.cull.busy } 10000 'undo rename'
        Check 'Ctrl+Z renames it back (position kept)' ((Test-Path -LiteralPath $orig) -and -not (Test-Path -LiteralPath $renamed) -and (CullMk $d 0) -eq '1,0') "$($d.lastOsd)"
        Key @('Ctrl+Y') | Out-Null
        $d = WaitFor { param($d) $d.path -eq $renamed -and -not $d.cull.busy } 10000 'redo rename'
        Check 'Ctrl+Y renames it again' (Test-Path -LiteralPath $renamed) $d.lastOsd
        # Renaming while playing keeps playing. Wait until the redo's reload has landed back on its
        # frame first: a toggle sent mid-reload is overridden when the reload restores the pause state.
        WaitFor { param($d) $d.path -eq $renamed -and $d.frame -ne $null -and (Near $d.position 2 0.1) } 10000 'redo reload settled' | Out-Null
        Act 'PlayPause'
        WaitFor { param($d) -not $d.paused } 5000 'playing' | Out-Null
        Act 'RenameFile:r2.mp4'
        $r2 = Join-Path $set.Clips 'r2.mp4'
        $d = WaitFor { param($d) $d.path -eq $r2 -and -not $d.cull.busy -and $d.duration -gt 0 } 10000 'renamed while playing'
        Start-Sleep -Milliseconds 400; $d = Dump
        Check 'renamed while playing, still playing' ((Test-Path -LiteralPath $r2) -and -not $d.paused) "paused=$($d.paused)"
        $bad = Ctl 'action' @('RenameFile:bad/name.mp4')
        $taken = Ctl 'action' @('RenameFile:clip02_portrait_60fps.mp4')
        Check 'invalid or taken names are refused' ($bad -like 'error*' -and $taken -like 'error*' -and (Test-Path -LiteralPath $r2)) "$bad | $taken"
        Check 'refusals carry the reason' ($bad -like "error: RenameFile:bad/name.mp4 failed: A file name can't contain*" -and $taken -like '*already exists in this folder*') "$bad | $taken"
        Act 'PlayPause'
    }
    $ini = Get-Content -LiteralPath (Join-Path $set.Cfg 'settings.ini') -Raw
    Check 'recent files follow the rename' ($ini -match 'r2\.mp4' -and $ini -notmatch 'clip01_h264_720p30\.mp4' -and $ini -notmatch 'renamed clip') ''
}

Test 'cull-bins' {
    $bin = Join-Path $cullRoot 'bins\bin1'
    if (Test-Path -LiteralPath (Split-Path $bin)) { Remove-Item -LiteralPath (Split-Path $bin) -Recurse -Force }
    $set = New-CullSet 'bins' @('CullAutoAdvance=false', "CullBin1=$bin")
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
        Act 'PlayIndex:1'; WaitFor { param($d) $d.path -like '*clip02*' -and $d.duration -gt 0 } 5000 'clip02' | Out-Null
        Key @('P') | Out-Null
        $r = Key @('F5')
        $d = WaitFor { param($d) $d.path -like '*clip03*' -and $d.playlistCount -eq 4 -and -not $d.cull.busy } 10000 'F5 moved clip02 and advanced'
        Check 'F5 = CullToBin:1, playlist moves on' ($r -eq 'ok CullToBin:1' -and $d.path -like '*clip03*') "$r $($d.path)"
        Check 'clip02 now in bin 1' ((Test-Path -LiteralPath (Join-Path $bin $cullNames[1])) -and -not (Test-Path -LiteralPath $set.Files[1])) $d.cull.lastResult
        Key @('Ctrl+Z') | Out-Null
        $d = WaitFor { param($d) $d.playlistCount -eq 5 -and $d.path -like '*clip02*' -and -not $d.cull.busy } 10000 'undo bin move'
        Check 'undo: back in place, in the list and on screen' ((Test-Path -LiteralPath $set.Files[1]) -and $d.playlist[1] -eq $set.Files[1]) "$($d.playlist[1])"
        Check 'decision came back with it' ((CullMk $d 1) -eq '1,0') (CullMk $d 1)
        Key @('Ctrl+Y') | Out-Null
        $d = WaitFor { param($d) $d.playlistCount -eq 4 -and -not $d.cull.busy } 10000 'redo bin move'
        Check 'redo moves it again' (Test-Path -LiteralPath (Join-Path $bin $cullNames[1])) $d.cull.lastResult
        # A name already in the bin gets " (2)".
        Set-Content -LiteralPath (Join-Path $bin $cullNames[2]) -Value 'dummy'
        WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null
        Act 'CullToBin:1'
        Wait-CullIdle | Out-Null
        Check 'collision -> "clip03_vp9 (2).webm"' (Test-Path -LiteralPath (Join-Path $bin 'clip03_vp9 (2).webm')) (((Get-ChildItem -LiteralPath $bin).Name) -join ', ')
    }
}

Test 'cull-apply' {
    $set = New-CullSet 'apply' @('CullAutoAdvance=false', 'CullPickAction=Move', 'CullRejectAction=RecycleBin')
    $keep = Join-Path $set.Clips 'Keep'
    New-Item -ItemType Directory -Force $keep | Out-Null
    Set-Content -LiteralPath (Join-Path $keep $cullNames[0]) -Value 'already here'   # name collision for clip01
    Set-ItemProperty -LiteralPath $set.Files[3] -Name IsReadOnly -Value $true          # read-only pick
    Set-ItemProperty -LiteralPath $set.Files[2] -Name IsReadOnly -Value $true          # read-only reject
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
        Key @('P') | Out-Null
        Act 'PlayIndex:3'; WaitFor { param($d) $d.path -like '*clip04*' } 5000 'clip04' | Out-Null; Key @('P') | Out-Null
        Act 'PlayIndex:2'; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null; Key @('X') | Out-Null
        Act 'PlayIndex:4'; WaitFor { param($d) $d.path -like '*clip05*' -and $d.duration -gt 0 } 5000 'clip05' | Out-Null; Key @('X') | Out-Null
        Act 'CullCommit:apply'
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Applied*' } 20000 'apply finished'
        Check 'result: 2 moved, 2 to the Recycle Bin, no problems' ($d.cull.lastResult -eq 'Applied: 2 moved · 2 sent to the Recycle Bin') $d.cull.lastResult
        $k1 = Join-Path $keep 'clip01_h264_720p30 (2).mp4'; $k4 = Join-Path $keep $cullNames[3]
        Check 'pick with a taken name -> "(2)", content intact' ((Test-Path -LiteralPath $k1) -and (Get-CullHash $k1) -eq $cullMediaHashes[$cullNames[0]]) ''
        Check 'existing Keep file untouched' ((Get-Content -LiteralPath (Join-Path $keep $cullNames[0])) -eq 'already here')
        Check 'read-only pick moved, still read-only' ((Test-Path -LiteralPath $k4) -and (Get-Item -LiteralPath $k4).IsReadOnly -and (Get-CullHash $k4) -eq $cullMediaHashes[$cullNames[3]]) ''
        Check 'rejects (one read-only) gone from the folder' (-not (Test-Path -LiteralPath $set.Files[2]) -and -not (Test-Path -LiteralPath $set.Files[4])) ''
        Check 'originals of picks gone, undecided clip untouched' (-not (Test-Path -LiteralPath $set.Files[0]) -and -not (Test-Path -LiteralPath $set.Files[3]) -and (Test-Path -LiteralPath $set.Files[1])) ''
        Check 'playlist: picks at their new place, rejects out' ($d.playlistCount -eq 3 -and $d.playlist[0] -eq $k1 -and $d.playlist[1] -eq $set.Files[1] -and $d.playlist[2] -eq $k4) (($d.playlist) -join ' | ')
        Check 'decisions followed the moved picks' ((CullMk $d 0) -eq '1,0' -and (CullMk $d 2) -eq '1,0') "$(CullMk $d 0) $(CullMk $d 2)"
        $d = WaitFor { param($d) $d.path -and $d.duration -gt 0 } 6000 'a remaining clip plays'
        Check 'playback continues on a remaining clip' ((Test-Path -LiteralPath $d.path) -and $d.playlist -contains $d.path) "$($d.path)"
        Key @('Ctrl+Z') | Out-Null
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Undo:*' } 20000 'undo apply'
        Check 'undo: picks moved back, Recycle Bin note' ((Test-Path -LiteralPath $set.Files[0]) -and (Test-Path -LiteralPath $set.Files[3]) -and -not (Test-Path -LiteralPath $k1) -and $d.cull.lastResult -like '*2 files stay in the Recycle Bin*') $d.cull.lastResult
        Check 'undo: playlist points at the restored files' ($d.playlist[0] -eq $set.Files[0] -and $d.playlist[2] -eq $set.Files[3]) (($d.playlist) -join ' | ')
    }
    Get-ChildItem -LiteralPath $set.Clips -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }

    # Second volume: moves go through a verified copy (timestamps kept, no partial files left).
    $tempRoot = [IO.Path]::GetTempPath()
    if ([IO.Path]::GetPathRoot($tempRoot) -ne [IO.Path]::GetPathRoot($cullRoot)) {
        $far = Join-Path $tempRoot ('vidflow-e2e-cull-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        $farKeep = Join-Path $far 'Keep'
        try {
            $set2 = New-CullSet 'apply2' @('CullAutoAdvance=false', 'CullPickAction=Move', "CullKeepFolder=$farKeep")
            $times = @{}; foreach ($i in 1, 2) { $times[$i] = (Get-Item -LiteralPath $set2.Files[$i]).LastWriteTimeUtc }
            Invoke-CullSession $set2 @($set2.Files[0]) {
                WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
                Act 'PlayIndex:1'; WaitFor { param($d) $d.path -like '*clip02*' } 5000 'clip02' | Out-Null; Key @('P') | Out-Null
                Act 'PlayIndex:2'; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null; Key @('P') | Out-Null
                Act 'CullCommit:apply'
                $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Applied*' } 30000 'cross-volume apply'
                $ok = $true
                foreach ($i in 1, 2) {
                    $t = Join-Path $farKeep $cullNames[$i]
                    if (-not (Test-Path -LiteralPath $t) -or (Test-Path -LiteralPath $set2.Files[$i]) -or (Get-CullHash $t) -ne $cullMediaHashes[$cullNames[$i]] -or
                        (Get-Item -LiteralPath $t).LastWriteTimeUtc -ne $times[$i]) { $ok = $false }
                }
                Check "moved to another volume ($([IO.Path]::GetPathRoot($far))), identical, timestamps kept" $ok $d.cull.lastResult
                Check 'no partial files left' (@(Get-ChildItem -LiteralPath $farKeep -Filter '*.vidflow-partial').Count -eq 0)
                Key @('Ctrl+Z') | Out-Null
                $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Undo:*' } 30000 'cross-volume undo'
                Check 'undo brings them back across volumes (and removes the Keep folder Apply made)' ((Test-Path -LiteralPath $set2.Files[1]) -and (Test-Path -LiteralPath $set2.Files[2]) -and -not (Test-Path -LiteralPath $farKeep)) $d.cull.lastResult
            }
        } finally {
            if (Test-Path -LiteralPath $far) { Get-ChildItem -LiteralPath $far -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }; Remove-Item -LiteralPath $far -Recurse -Force }
        }
    } else {
        Check 'second volume available for the cross-volume test' $false "temp folder is on the same drive as $cullRoot"
    }
}

Test 'cull-dialogs' {
    $set = New-CullSet 'dialogs' @('CullAutoAdvance=false', 'CullRejectAction=Folder')
    New-Item -ItemType Directory -Force (Join-Path $set.Clips 'Keep') | Out-Null
    Set-Content -LiteralPath (Join-Path $set.Clips "Keep\$($cullNames[0])") -Value 'taken'   # the summary lists the name conflict
    Invoke-CullSession $set @($set.Files[0]) {
        $p = $args[0]
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 -and $d.duration -gt 0 } 10000 'clip01' | Out-Null
        Key @('P', '4') | Out-Null
        Act 'PlayIndex:2'; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null; Key @('X') | Out-Null
        Act 'PlayIndex:0'; WaitFor { param($d) $d.path -like '*clip01*' -and $d.duration -gt 0 } 5000 'clip01' | Out-Null
        & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Out (Join-Path $work 'shots\cull-window.png') | Out-Null

        # The 'action' call blocks while a modal is open (and the pipe serves one request at a time),
        # so it runs in a job; nothing else talks to the pipe until the dialog is closed.
        $job = Start-Job -ScriptBlock { param($r, $pipe) & "$r\tools\vidflowctl.ps1" -Pipe $pipe -TimeoutMs 5000 action CullCommit } -ArgumentList $root, $cullPipe
        $h = [IntPtr]::Zero
        for ($i = 0; $i -lt 200 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 150; $h = [CullWin]::Find($p.Id, 'Apply Decisions') }
        Check 'Apply dialog opens (Ctrl+Enter / CullCommit)' ($h -ne [IntPtr]::Zero)
        if ($h -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 400
            & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Title 'Apply Decisions' -IncludeOwned -Out (Join-Path $work 'shots\cull-apply-dialog.png') | Out-Null
            [CullWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE = Cancel
        }
        Wait-Job $job -Timeout 20 | Out-Null; $jr = Receive-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force
        $left = @($set.Files | Where-Object { Test-Path -LiteralPath $_ }).Count
        $kept = @(Get-ChildItem -LiteralPath (Join-Path $set.Clips 'Keep') -File).Count
        Check 'Cancel leaves every file where it was' ($left -eq 5 -and $kept -eq 1) "files=$left keep=$kept action=$jr"

        $job = Start-Job -ScriptBlock { param($r, $pipe) & "$r\tools\vidflowctl.ps1" -Pipe $pipe -TimeoutMs 5000 action RenameFile } -ArgumentList $root, $cullPipe
        $h = [IntPtr]::Zero
        for ($i = 0; $i -lt 200 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 150; $h = [CullWin]::Find($p.Id, 'Rename File') }
        Check 'F2 opens the rename prompt' ($h -ne [IntPtr]::Zero)
        if ($h -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 500
            $edit = [CullWin]::FindEdit($h)
            $sel = [CullWin]::SendMessage($edit, 0x00B0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()   # EM_GETSEL
            $start = $sel -band 0xFFFF; $end = ($sel -shr 16) -band 0xFFFF
            $text = [CullWin]::Text($edit)
            Check 'prefilled with the file name, name part selected' ($text -eq $cullNames[0] -and $start -eq 0 -and $end -eq 'clip01_h264_720p30'.Length) "text=$text sel=$start..$end"
            & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Title 'Rename File' -IncludeOwned -Out (Join-Path $work 'shots\cull-rename-dialog.png') | Out-Null
            [CullWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
        Wait-Job $job -Timeout 20 | Out-Null; Remove-Job $job -Force
        Check 'cancelled rename changes nothing' (Test-Path -LiteralPath $set.Files[0])

        # Apply through the dialog itself: its button runs the batch on a worker thread with progress.
        $job = Start-Job -ScriptBlock { param($r, $pipe) & (Join-Path $r 'tools\vidflowctl.ps1') -Pipe $pipe -TimeoutMs 5000 action CullCommit } -ArgumentList $root, $cullPipe
        $h = [IntPtr]::Zero
        for ($i = 0; $i -lt 200 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 150; $h = [CullWin]::Find($p.Id, 'Apply Decisions') }
        $btn = if ($h -ne [IntPtr]::Zero) { [CullWin]::FindChild($h, 'Apply') } else { [IntPtr]::Zero }
        $why = if ($h -eq [IntPtr]::Zero) { 'dialog not found: job ' + $job.State + ' ' + (Receive-Job $job -Keep -ErrorAction SilentlyContinue) + ' osd=' + (Dump).lastOsd } else { "dialog $h" }
        Check 'Apply button found' ($btn -ne [IntPtr]::Zero) $why
        if ($btn -ne [IntPtr]::Zero) { [CullWin]::PostMessage($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }   # BM_CLICK
        $closed = $false
        for ($i = 0; $i -lt 100 -and -not $closed; $i++) { Start-Sleep -Milliseconds 150; $closed = [CullWin]::Find($p.Id, 'Apply Decisions') -eq [IntPtr]::Zero }
        Wait-Job $job -Timeout 20 | Out-Null; Remove-Job $job -Force
        Check 'dialog closes by itself when everything went through' $closed
        $d = WaitFor { param($d) $d.cull.lastResult -like 'Applied*' } 8000 'dialog apply result'
        Check 'dialog Apply: pick moved (taken name -> (2)), reject moved to Rejected' ((Test-Path -LiteralPath (Join-Path $set.Clips 'Keep\clip01_h264_720p30 (2).mp4')) -and
            (Test-Path -LiteralPath (Join-Path $set.Clips "Rejected\$($cullNames[2])")) -and $d.cull.lastResult -eq 'Applied: 2 moved') $d.cull.lastResult
        Check 'undo is available for the dialog batch' ($d.cull.undo -ge 1 -and $d.cull.undoLabel -eq 'Apply decisions') "$($d.cull.undoLabel)"
    }
}

Test 'cull-apply-twice' {
    # A second Apply must not nest picks into Keep\Keep; sidecars travel with their clip (and only theirs);
    # the Apply plan counts only what will move; undo removes the Keep folder it created; a relative bin
    # refuses a clip that is already in it; rename carries sidecars; undo after another list was opened
    # does not slot the clip into that list.
    $set = New-CullSet 'twice' @('CullAutoAdvance=false', 'CullPickAction=Move', 'CullRejectAction=None', 'CullBin2=Maybe')
    $c = $set.Clips
    $keep = Join-Path $c 'Keep'
    $s1 = Join-Path $c 'clip01_h264_720p30.srt'; Copy-Item -LiteralPath (Join-Path $media 'clip01_h264_720p30.srt') -Destination $s1
    $l1 = Join-Path $c 'clip01_h264_720p30.LRF'; Set-Content -LiteralPath $l1 -Value 'lrf'
    $x1 = Join-Path $c 'clip01_h264_720p30.XMP'; Set-Content -LiteralPath $x1 -Value 'xmp'
    $decoy = Join-Path $c 'clip01_h264_720p30_notes.srt'; Set-Content -LiteralPath $decoy -Value 'another base name'
    $x2 = Join-Path $c 'clip02_portrait_60fps.xmp'; Set-Content -LiteralPath $x2 -Value 'xmp of clip02'
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 -and $d.duration -gt 0 } 10000 'clip01' | Out-Null
        Key @('P') | Out-Null
        Act 'PlayIndex:3'; WaitFor { param($d) $d.path -like '*clip04*' } 5000 'clip04' | Out-Null; Key @('P') | Out-Null
        Act 'CullCommit:apply'
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Applied*' } 20000 'first apply'
        Check 'first Apply: 2 moved, plan 2 picks' ($d.cull.lastResult -eq 'Applied: 2 moved' -and $d.cull.lastPlan.picks -eq 2 -and $d.cull.lastPlan.alreadyKept -eq 0) "$($d.cull.lastResult) | $($d.cull.lastPlan | ConvertTo-Json -Compress)"
        $k1 = Join-Path $keep $cullNames[0]
        $carried = (Test-Path -LiteralPath $k1) -and (Test-Path -LiteralPath (Join-Path $keep 'clip01_h264_720p30.srt')) -and
                   (Test-Path -LiteralPath (Join-Path $keep 'clip01_h264_720p30.LRF')) -and (Test-Path -LiteralPath (Join-Path $keep 'clip01_h264_720p30.XMP')) -and
                   -not (Test-Path -LiteralPath $s1) -and -not (Test-Path -LiteralPath $l1) -and -not (Test-Path -LiteralPath $x1)
        Check 'sidecars (.srt .LRF .XMP) moved with the pick' $carried ((Get-ChildItem -LiteralPath $keep -Name) -join ', ')
        Check 'other base names and other clips'' sidecars stay' ((Test-Path -LiteralPath $decoy) -and (Test-Path -LiteralPath $x2)) ((Get-ChildItem -LiteralPath $c -File -Name) -join ', ')
        Check 'picks still listed, at Keep' ($d.playlist[0] -eq $k1 -and $d.playlist[3] -eq (Join-Path $keep $cullNames[3])) (($d.playlist) -join ' | ')

        Act 'CullCommit:apply'
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -eq 'Nothing to apply' } 10000 'second apply'
        Check 'second Apply: nothing to do, no Keep\Keep' (-not (Test-Path -LiteralPath (Join-Path $keep 'Keep')) -and (Test-Path -LiteralPath $k1)) ((Get-ChildItem -LiteralPath $keep -Name) -join ', ')
        Check 'plan: 0 picks, 2 already kept (counted apart)' ($d.cull.lastPlan.picks -eq 0 -and $d.cull.lastPlan.alreadyKept -eq 2) ($d.cull.lastPlan | ConvertTo-Json -Compress)

        Act 'PlayIndex:2'; WaitFor { param($d) $d.path -like '*clip03*' -and $d.duration -gt 0 } 5000 'clip03' | Out-Null; Key @('P') | Out-Null
        # The Apply dialog counts the one pick that moves; the two already kept are a separate line.
        $p = $args[0]
        $job = Start-Job -ScriptBlock { param($r, $pipe) & (Join-Path $r 'tools\vidflowctl.ps1') -Pipe $pipe -TimeoutMs 5000 action CullCommit } -ArgumentList $root, $cullPipe
        $h = [IntPtr]::Zero
        for ($i = 0; $i -lt 200 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 150; $h = [CullWin]::Find($p.Id, 'Apply Decisions') }
        $kept = if ($h -ne [IntPtr]::Zero) { [CullWin]::FindChild($h, '2 picks are already in the Keep folder: left where they are.') } else { [IntPtr]::Zero }
        if ($h -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 400
            & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Title 'Apply Decisions' -IncludeOwned -Out (Join-Path $work 'shots\cull-apply-already-kept.png') | Out-Null
            [CullWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE = Cancel
        }
        Wait-Job $job -Timeout 20 | Out-Null; Remove-Job $job -Force
        Check 'Apply dialog: already-kept picks shown on their own line' ($h -ne [IntPtr]::Zero -and $kept -ne [IntPtr]::Zero) "dialog=$h keptLine=$kept"
        Act 'CullCommit:apply'
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Applied*' } 20000 'third apply'
        Check 'third Apply: only the new pick moves' ($d.cull.lastResult -eq 'Applied: 1 moved' -and $d.cull.lastPlan.picks -eq 1 -and $d.cull.lastPlan.alreadyKept -eq 2 -and
            -not (Test-Path -LiteralPath (Join-Path $keep 'Keep'))) "$($d.cull.lastResult) | $($d.cull.lastPlan | ConvertTo-Json -Compress)"

        Key @('Ctrl+Z') | Out-Null
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Undo:*' } 20000 'undo third apply'
        Check 'undo: clip03 back, Keep kept (not empty)' ((Test-Path -LiteralPath $set.Files[2]) -and (Test-Path -LiteralPath $keep)) $d.cull.lastResult
        Key @('Ctrl+Z') | Out-Null   # the pick of clip03
        WaitFor { param($d) $d.cull.undo -eq 3 -and (CullMk $d 2) -eq '0,0' } 5000 'undo the clip03 pick' | Out-Null
        Key @('Ctrl+Z') | Out-Null   # the first Apply
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.undo -eq 2 -and (Test-Path -LiteralPath $set.Files[0]) } 20000 'undo first apply'
        $back = (Test-Path -LiteralPath $set.Files[0]) -and (Test-Path -LiteralPath $set.Files[3]) -and (Test-Path -LiteralPath $s1) -and (Test-Path -LiteralPath $l1) -and (Test-Path -LiteralPath $x1)
        Check 'undo: picks and their sidecars back' $back ((Get-ChildItem -LiteralPath $c -File -Name) -join ', ')
        Check 'undo: the Keep folder Apply created is gone' (-not (Test-Path -LiteralPath $keep)) ''

        # Relative bin "Maybe": F6 moves clip02 (+ its .xmp) to clips\Maybe; F6 there again is refused.
        Act 'PlayIndex:1'; WaitFor { param($d) $d.path -like '*clip02*' -and $d.duration -gt 0 } 5000 'clip02 again' | Out-Null
        $r = Key @('F6')
        $d = WaitFor { param($d) $d.playlistCount -eq 4 -and -not $d.cull.busy } 10000 'F6 bin move'
        $maybe = Join-Path $c 'Maybe'; $m2 = Join-Path $maybe $cullNames[1]
        Check 'F6 = CullToBin:2 (cull layer), clip02 and its .xmp in Maybe' ($r -eq 'ok CullToBin:2' -and (Test-Path -LiteralPath $m2) -and (Test-Path -LiteralPath (Join-Path $maybe 'clip02_portrait_60fps.xmp')) -and -not (Test-Path -LiteralPath $x2)) "$r | $($d.cull.lastResult)"
        Ctl 'open' @($m2) | Out-Null
        WaitFor { param($d) $d.path -eq $m2 -and $d.duration -gt 0 } 6000 'clip02 in Maybe' | Out-Null
        $r = Key @('F6'); Start-Sleep -Milliseconds 300; $d = Dump
        Check 'F6 on a clip already in its relative bin is refused' ($r -eq 'ok CullToBin:2' -and $d.lastOsd -like '*is already in Bin 2*' -and (Test-Path -LiteralPath $m2) -and -not (Test-Path -LiteralPath (Join-Path $maybe 'Maybe'))) "$r | $($d.lastOsd)"

        # Rename carries the sidecar; undo brings both names back.
        Act 'RenameFile:c2 renamed.mp4'
        $rn = Join-Path $maybe 'c2 renamed.mp4'
        $d = WaitFor { param($d) $d.path -eq $rn -and -not $d.cull.busy } 10000 'rename in Maybe'
        Check 'rename: sidecar renamed too' ((Test-Path -LiteralPath (Join-Path $maybe 'c2 renamed.xmp')) -and -not (Test-Path -LiteralPath (Join-Path $maybe 'clip02_portrait_60fps.xmp'))) ((Get-ChildItem -LiteralPath $maybe -Name) -join ', ')
        Key @('Ctrl+Z') | Out-Null
        $d = WaitFor { param($d) $d.path -eq $m2 -and -not $d.cull.busy } 10000 'undo rename'
        Check 'undo rename: clip and sidecar names back' ((Test-Path -LiteralPath $m2) -and (Test-Path -LiteralPath (Join-Path $maybe 'clip02_portrait_60fps.xmp'))) ((Get-ChildItem -LiteralPath $maybe -Name) -join ', ')

        # Undo after another list was opened: the file goes back on disk, not into the unrelated list.
        Ctl 'open' @($set.Files[4]) | Out-Null
        WaitFor { param($d) $d.path -eq $set.Files[4] -and $d.duration -gt 0 } 6000 'clip05 list' | Out-Null
        Act 'PlayIndex:0'; WaitFor { param($d) $d.path -eq $set.Files[0] -and $d.duration -gt 0 } 6000 'clip01' | Out-Null
        Act 'CullToBin:2'
        $d = WaitFor { param($d) -not $d.cull.busy -and (Test-Path -LiteralPath (Join-Path $maybe $cullNames[0])) } 10000 'clip01 to Maybe'
        $elsewhere = Join-Path $cullRoot 'twice\elsewhere'
        New-Item -ItemType Directory -Force $elsewhere | Out-Null
        $o1 = Join-Path $elsewhere 'o1.mkv'; Copy-Item -LiteralPath (Join-Path $media $cullNames[4]) -Destination $o1
        Ctl 'open' @($o1) | Out-Null
        $d = WaitFor { param($d) $d.path -eq $o1 -and $d.duration -gt 0 } 6000 'another list'
        Key @('Ctrl+Z') | Out-Null
        $d = WaitFor { param($d) -not $d.cull.busy -and $d.cull.lastResult -like 'Undo:*' } 10000 'undo bin move from another list'
        Check 'undo from another list: file restored on disk, not inserted here' ((Test-Path -LiteralPath $set.Files[0]) -and $d.playlistCount -eq 1 -and $d.playlist[0] -eq $o1 -and $d.cull.lastResult -like '*another list is open now*') "$($d.cull.lastResult) | $(($d.playlist) -join ' | ')"
    }
}

Test 'cull-skip-folders' {
    # In cull mode an opened folder (walked recursively) leaves out the Keep / Rejected / bin folders.
    $set = New-CullSet 'skip' @('CullRejectAction=Folder', 'CullBin1=Bins\Maybe')
    $c = $set.Clips
    $abs = Join-Path $cullRoot 'skip\absbin'
    Add-Content -LiteralPath (Join-Path $set.Cfg 'settings.ini') -Value "CullBin2=$abs" -Encoding UTF8
    foreach ($sub in 'Keep', 'Rejected', 'Bins\Maybe', 'Other') { New-Item -ItemType Directory -Force (Join-Path $c $sub) | Out-Null }
    New-Item -ItemType Directory -Force (Join-Path $c 'absbin') | Out-Null
    Copy-Item -LiteralPath (Join-Path $media $cullNames[4]) -Destination (Join-Path $c 'Keep\k1.mkv')
    Copy-Item -LiteralPath (Join-Path $media $cullNames[4]) -Destination (Join-Path $c 'Rejected\r1.mkv')
    Copy-Item -LiteralPath (Join-Path $media $cullNames[4]) -Destination (Join-Path $c 'Bins\Maybe\b1.mkv')
    Copy-Item -LiteralPath (Join-Path $media $cullNames[4]) -Destination (Join-Path $c 'Other\o1.mkv')
    # The absolute bin lives elsewhere; a folder of the same name inside the shoot is not it.
    New-Item -ItemType Directory -Force $abs | Out-Null
    Copy-Item -LiteralPath (Join-Path $media $cullNames[4]) -Destination (Join-Path $c 'absbin\n1.mkv')
    Invoke-CullSession $set @($c) {
        $d = WaitFor { param($d) $d.playlistCount -ge 5 -and $d.duration -gt 0 } 10000 'folder loaded'
        $names = @($d.playlist | ForEach-Object { $_.Substring($c.Length + 1) })
        $skipped = -not ($names | Where-Object { $_ -like 'Keep\*' -or $_ -like 'Rejected\*' -or $_ -like 'Bins\*' })
        Check 'cull mode: Keep, Rejected and the relative bin are skipped' ($skipped -and $d.playlistCount -eq 7 -and ($names -contains 'Other\o1.mkv') -and ($names -contains 'absbin\n1.mkv')) ($names -join ', ')
        Act 'ToggleCullMode'
        Ctl 'open' @($c) | Out-Null
        $d = WaitFor { param($d) $d.playlistCount -eq 10 } 6000 'everything outside cull mode'
        Check 'outside cull mode the whole tree loads' ($d.playlistCount -eq 10) "$($d.playlistCount)"
    }
}

Test 'cull-filter-nav' {
    $set = New-CullSet 'filternav' @('CullAutoAdvance=false')
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
        Key @('P') | Out-Null
        Act 'PlayIndex:2'; WaitFor { param($d) $d.path -like '*clip03*' } 5000 'clip03' | Out-Null; Key @('P') | Out-Null
        Act 'PlayIndex:4'; WaitFor { param($d) $d.path -like '*clip05*' } 5000 'clip05' | Out-Null; Key @('X') | Out-Null
        Act 'CullFilter:picked'
        Act 'PlayIndex:0'; WaitFor { param($d) $d.path -like '*clip01*' } 5000 'clip01' | Out-Null
        Act 'NextFile'
        $d = WaitFor { param($d) $d.path -like '*clip03*' } 5000 'NextFile -> next pick'
        Check 'NextFile follows the filter (skips undecided clip02)' ($d.path -like '*clip03*') $d.path
        $r = Ctl 'action' @('NextFile')
        Check 'NextFile disabled after the last pick' ($r -eq 'error: NextFile disabled' -and (Dump).path -like '*clip03*') $r
        Act 'PrevFile'; $d = WaitFor { param($d) $d.path -like '*clip01*' } 5000 'PrevFile -> clip01'
        Check 'PrevFile follows the filter' ($d.path -like '*clip01*') $d.path
        Act 'LastFile'; $d = WaitFor { param($d) $d.path -like '*clip03*' } 5000 'LastFile -> clip03'
        Check 'LastFile = last pick' ($d.path -like '*clip03*') $d.path
        Act 'FirstFile'; $d = WaitFor { param($d) $d.path -like '*clip01*' } 5000 'FirstFile -> clip01'
        Check 'FirstFile = first pick' ($d.path -like '*clip01*') $d.path
        $r = Key @('PageDown'); $d = WaitFor { param($d) $d.path -like '*clip03*' } 5000 'PageDown -> clip03'
        Check 'PageDown (NextFile key) follows the filter' ($r -eq 'ok NextFile' -and $d.path -like '*clip03*') "$r $($d.path)"
        Act 'CullFilter:rejected'; Act 'FirstFile'; $d = WaitFor { param($d) $d.path -like '*clip05*' } 5000 'first reject'
        Check 'rejects view: FirstFile = clip05' ($d.path -like '*clip05*') $d.path
        Act 'ToggleCullMode'; $d = Dump
        Check 'leaving cull mode clears the filter' ($d.cull.filter -eq 'all' -and $d.cull.panelVisible -eq 5) "filter=$($d.cull.filter) visible=$($d.cull.panelVisible)"
        Act 'PlayIndex:0'; WaitFor { param($d) $d.path -like '*clip01*' } 5000 'clip01' | Out-Null
        Act 'NextFile'; $d = WaitFor { param($d) $d.path -like '*clip02*' } 5000 'plain NextFile'
        Check 'outside cull mode NextFile is the plain next file' ($d.path -like '*clip02*') $d.path
        $r = Ctl 'action' @('CullFilter:picked')
        Check 'decision filters are off outside cull mode' ($r -eq 'error: CullFilter:picked disabled') $r
    }
}

Test 'cull-autorepeat' {
    # The pipe cannot send a key's auto-repeat flag; CullAsRepeat:<action> runs an action exactly as a
    # repeating key would (automation only).
    $bin = Join-Path $cullRoot 'repeatbin'
    $set = New-CullSet 'repeat' @('CullAutoAdvance=true', "CullBin1=$bin", 'CullPickAction=Move', 'CullRejectAction=None')
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 -and $d.duration -gt 0 } 10000 'clip01' | Out-Null
        $before = Dump
        $resp = foreach ($id in 'CullReject', 'CullPick', 'CullUnflag', 'CullRate:3', 'CullNextUndecided', 'CullToBin:1', 'RenameFile:held.mp4', 'CullCommit:apply', 'ToggleCullMode') { Ctl 'action' @("CullAsRepeat:$id") }
        Start-Sleep -Milliseconds 500; $d = Dump
        $same = ($d.path -eq $before.path) -and (CullMk $d 0) -eq '0,0' -and $d.cull.mode -and $d.playlistCount -eq 5 -and $d.cull.undo -eq 0 -and
                (Test-Path -LiteralPath $set.Files[0]) -and -not (Test-Path -LiteralPath $bin)
        Check 'auto-repeat ignored: decisions, N, bin, rename, Apply, cull toggle' $same "resp=$($resp -join ',') path=$($d.path) mark=$(CullMk $d 0) mode=$($d.cull.mode) undo=$($d.cull.undo)"
        $v0 = $d.volume
        Ctl 'action' @('CullAsRepeat:VolumeDown') | Out-Null; $d = Dump
        Check 'other actions still repeat (VolumeDown ran)' ($d.volume -lt $v0) "$v0 -> $($d.volume)"
        $r = Key @('X')
        $d = WaitFor { param($d) $d.path -like '*clip02*' } 5000 'a real press still decides'
        Check 'a single press still rejects and advances' ($r -eq 'ok CullReject' -and (CullMk $d 0) -eq '-1,0') "$r $(CullMk $d 0)"
    }
}

Test 'cull-fullscreen' {
    $set = New-CullSet 'fullscreen' @('CullAutoAdvance=false')
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 -and $d.duration -gt 0 } 10000 'clip01' | Out-Null
        if (-not (Dump).paused) { Act 'PlayPause' }
        Check 'windowed: no indicator' ($null -eq (Dump).cull.indicator)
        Act 'ToggleFullscreen'
        $d = WaitFor { param($d) $d.fullscreen -and $d.cull.indicator } 4000 'fullscreen indicator'
        Check 'fullscreen: "– undecided" indicator' ($d.cull.indicator -like '*– undecided') $d.cull.indicator
        Key @('P', '3') | Out-Null; $d = Dump
        Check 'pick + 3 stars: "✓ Pick ★★★"' ($d.cull.indicator -like '*✓ Pick*★★★' -and $d.cull.indicator -notlike '*★★★★*') $d.cull.indicator
        Start-Sleep -Milliseconds 1600   # let the transient OSD fade so the shot shows the indicator alone
        $shot = Join-Path $work 'shots\cull-fullscreen-indicator.png'
        $r = Ctl 'screenshot' @($shot)
        Check 'fullscreen screenshot with the indicator' ($r -eq 'ok') $r
        Key @('X') | Out-Null; $d = Dump
        Check 'reject: "✕ Reject ★★★"' ($d.cull.indicator -like '*✕ Reject*★★★') $d.cull.indicator
        Act 'ToggleCullMode'; $d = Dump
        Check 'leaving cull mode removes it' ($null -eq $d.cull.indicator) "$($d.cull.indicator)"
        Act 'ToggleCullMode'; $d = Dump
        Check 'back in cull mode, it is back' ($d.cull.indicator -like '*✕ Reject*') "$($d.cull.indicator)"
        Act 'ExitFullscreen'
        $d = WaitFor { param($d) -not $d.fullscreen } 4000 'windowed again'
        Check 'windowed again: indicator removed' ($null -eq $d.cull.indicator) "$($d.cull.indicator)"
    }
}

Test 'cull-panel-open' {
    # Entering cull mode opens the playlist panel once, when the window is wide enough.
    $set = New-CullSet 'panel' @('CullMode=false', 'ShowPlaylist=false')
    Invoke-CullSession $set @($set.Files[0]) {
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 10000 'clip01' | Out-Null
        Check 'panel closed before cull mode' (-not (Dump).playlistVisible)
        Act 'ToggleCullMode'; $d = Dump
        Check 'entering cull mode opens the panel' ($d.cull.mode -and $d.playlistVisible) "mode=$($d.cull.mode) panel=$($d.playlistVisible)"
        Act 'TogglePlaylist'; Act 'ToggleCullMode'; Act 'ToggleCullMode'; $d = Dump
        Check 'only once: closed again, it stays closed' ($d.cull.mode -and -not $d.playlistVisible) "mode=$($d.cull.mode) panel=$($d.playlistVisible)"
    }
}

Test 'cull-bin-explain' {
    # First use of an unset bin explains what a bin is before any folder picker; Cancel changes nothing.
    $set = New-CullSet 'binexplain' @('CullAutoAdvance=false')
    Invoke-CullSession $set @($set.Files[0]) {
        $p = $args[0]
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 -and $d.duration -gt 0 } 10000 'clip01' | Out-Null
        $job = Start-Job -ScriptBlock { param($r, $pipe) & (Join-Path $r 'tools\vidflowctl.ps1') -Pipe $pipe -TimeoutMs 5000 action 'CullToBin:3' } -ArgumentList $root, $cullPipe
        $h = [IntPtr]::Zero
        for ($i = 0; $i -lt 200 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 150; $h = [CullWin]::Find($p.Id, 'Bin 3 (F7)') }
        Check 'unset bin: explanation dialog first ("Bin 3 (F7)")' ($h -ne [IntPtr]::Zero)
        if ($h -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 400
            & (Join-Path $root 'tools\capture.ps1') -ProcessId $p.Id -Title 'Bin 3 (F7)' -IncludeOwned -Out (Join-Path $work 'shots\cull-bin-explain.png') | Out-Null
            [CullWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE = Cancel
        }
        Wait-Job $job -Timeout 20 | Out-Null; Remove-Job $job -Force
        $d = Dump
        $ini = Get-Content -LiteralPath (Join-Path $set.Cfg 'settings.ini') -Raw
        Check 'Cancel: nothing moved, bin still unset' ($d.playlistCount -eq 5 -and (Test-Path -LiteralPath $set.Files[0]) -and $ini -notmatch 'CullBin3=\S') "count=$($d.playlistCount)"
    }
}

Test 'cull-originals' {
    $same = $true
    foreach ($n in $cullNames) { if ((Get-CullHash (Join-Path $media $n)) -ne $cullMediaHashes[$n]) { $same = $false } }
    Check 'testmedia originals unchanged (SHA-256)' $same
}
