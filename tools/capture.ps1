<#
.SYNOPSIS
  Screenshot helper for visual checks (works with mpv's D3D11 output).
.EXAMPLE
  .\tools\capture.ps1 -ProcessId 1234 -Out obj\shot.png            # whole top-level window of a process
  .\tools\capture.ps1 -Title "VidFlow" -Out obj\shot.png -Client    # client area only, window found by title substring
  .\tools\capture.ps1 -Screen 0,0,800,600 -Out obj\shot.png         # raw screen region (physical pixels), e.g. popups/menus
  .\tools\capture.ps1 -ProcessId 1234 -Out obj\all.png -WithPopups  # window rect from the screen, incl. menus/popups above it
#>
param(
    [int]$ProcessId,
    [string]$Title,
    [string]$Screen,      # "x,y,w,h" in physical pixels
    [switch]$Client,
    [switch]$WithPopups,
    [switch]$IncludeOwned,  # also match owned windows (dialogs, popups)
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    # System.Drawing interop is simplest under Windows PowerShell 5.1; re-run there.
    $fwd = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Out', $Out)
    if ($ProcessId) { $fwd += '-ProcessId', $ProcessId }
    if ($Title) { $fwd += '-Title', $Title }
    if ($Screen) { $fwd += '-Screen', $Screen }
    if ($Client) { $fwd += '-Client' }
    if ($WithPopups) { $fwd += '-WithPopups' }
    if ($IncludeOwned) { $fwd += '-IncludeOwned' }
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @fwd
    exit $LASTEXITCODE
}
Add-Type -AssemblyName System.Drawing
if (-not ('VidFlowCapture' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class VidFlowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    public static void Aware() { SetThreadDpiAwarenessContext(new IntPtr(-4)); }

    public static IntPtr Find(int pid, string title, bool owned) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h) || (!owned && GetWindow(h, 4) != IntPtr.Zero)) return true; // skip owned popups unless asked
            uint p; GetWindowThreadProcessId(h, out p);
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            if (pid != 0 && p != pid) return true;
            if (!string.IsNullOrEmpty(title) && sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (sb.Length == 0 && string.IsNullOrEmpty(title)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static Rectangle Bounds(IntPtr h, bool client) {
        if (client) {
            RECT c; GetClientRect(h, out c);
            var p = new POINT(); ClientToScreen(h, ref p);
            return new Rectangle(p.X, p.Y, c.R - c.L, c.B - c.T);
        }
        RECT r;
        if (DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
        return new Rectangle(r.L, r.T, r.R - r.L, r.B - r.T);
    }

    public static void SaveWindow(IntPtr h, bool client, string path) {
        RECT wr; GetWindowRect(h, out wr);
        int w = wr.R - wr.L, ht = wr.B - wr.T;
        using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                PrintWindow(h, hdc, 2 /*PW_RENDERFULLCONTENT*/);
                g.ReleaseHdc(hdc);
            }
            Rectangle crop;
            if (client) { var cb = Bounds(h, true); crop = new Rectangle(cb.X - wr.L, cb.Y - wr.T, cb.Width, cb.Height); }
            else { var eb = Bounds(h, false); crop = new Rectangle(eb.X - wr.L, eb.Y - wr.T, eb.Width, eb.Height); }
            crop.Intersect(new Rectangle(0, 0, w, ht));
            using (var outBmp = bmp.Clone(crop, bmp.PixelFormat)) outBmp.Save(path, ImageFormat.Png);
        }
    }

    public static void SaveScreen(Rectangle r, string path) {
        using (var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
'@
}
[VidFlowCapture]::Aware()
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
if ($Screen) {
    $s = $Screen.Split(",") | ForEach-Object { [int]$_.Trim() }
    [VidFlowCapture]::SaveScreen((New-Object Drawing.Rectangle($s[0], $s[1], $s[2], $s[3])), $Out)
} else {
    $h = [VidFlowCapture]::Find($ProcessId, $Title, [bool]$IncludeOwned)
    if ($h -eq [IntPtr]::Zero) { throw "window not found (pid=$ProcessId title=$Title)" }
    if ($WithPopups) { [VidFlowCapture]::SaveScreen([VidFlowCapture]::Bounds($h, $Client), $Out) }
    else { [VidFlowCapture]::SaveWindow($h, $Client, $Out) }
}
"saved $Out"
