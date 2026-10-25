# Opens each modal dialog in a real VidFlow (automation mode), captures it, then closes it.
param([string]$Exe = (Join-Path $PSScriptRoot '..\dist\dev\VidFlow.exe'), [string]$Out = (Join-Path $PSScriptRoot '..\obj\dialogs'))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Run on the hidden desktop so the dialogs never appear on the user's screen.
if ($env:VIDFLOW_HIDDEN -ne '1') {
    & (Join-Path $root 'tools\ensure-runhidden.ps1')
    $env:VIDFLOW_HIDDEN = '1'
    & (Join-Path $root 'obj\tools\RunHidden.exe') --wait --timeout 600000 -- (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Exe (Resolve-Path $Exe).Path -Out $Out
    $code = $LASTEXITCODE; $env:VIDFLOW_HIDDEN = $null; exit $code
}
New-Item -ItemType Directory -Force $Out | Out-Null
"AutoFitWindow=false`nWindowX=100`nWindowY=80`nWindowWidth=1000`nWindowHeight=640`nExtraMpvOptions=ao=null" | Set-Content -Encoding UTF8 "$Out\s.ini"
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class DlgWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static IntPtr FindOwned(int pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
'@
$p = Start-Process $Exe -ArgumentList @('--automation', '--pipe', 'VidFlowDlg', '--settings', "`"$Out\s.ini`"", "`"$root\testmedia\clip04_hevc_multitrack.mkv`"") -PassThru
Start-Sleep 3
$dialogs = @(
  @{ Action = 'Settings'; Title = 'Settings' },
  @{ Action = 'Shortcuts'; Title = 'Shortcuts' },
  @{ Action = 'GoTo'; Title = 'Go to' },
  @{ Action = 'MediaInfo'; Title = 'Media Info' },
  @{ Action = 'About'; Title = 'About' },
  @{ Action = 'DeleteFile'; Title = 'Delete' },
  @{ Action = 'OpenUrl'; Title = 'Open' }
)
foreach ($d in $dialogs) {
  # 'action' blocks while the modal is open, so fire it from a background job.
  $job = Start-Job -ScriptBlock { param($r, $a) & "$r\tools\vidflowctl.ps1" -Pipe VidFlowDlg -TimeoutMs 3000 action $a } -ArgumentList $root, $d.Action
  $h = [IntPtr]::Zero
  for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 150; $h = [DlgWin]::FindOwned($p.Id, $d.Title) }
  if ($h -eq [IntPtr]::Zero) { "{0}: dialog not found" -f $d.Action; Stop-Job $job -ErrorAction SilentlyContinue; continue }
  Start-Sleep -Milliseconds 500
  & "$root\tools\capture.ps1" -ProcessId $p.Id -Title $d.Title -IncludeOwned -Out "$Out\$($d.Action).png" | Out-Null
  [DlgWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE = cancel
  Wait-Job $job -Timeout 10 | Out-Null; Remove-Job $job -Force
  "{0}: captured" -f $d.Action
}
& "$root\tools\vidflowctl.ps1" -Pipe VidFlowDlg quit | Out-Null
