// Runs a command on a separate, never-shown Windows desktop ("winsta0\VidFlowTest") so test
// windows, fullscreen switches and focus changes never appear on the user's screen.
// Child processes inherit the desktop, so running a whole test script this way hides
// everything it launches.
//
// Usage: RunHidden.exe [--desktop Name] [--wait] [--timeout ms] -- <program> [args...]
//   --wait     wait for the program and return its exit code (stdout/stderr are inherited)
//   --timeout  with --wait: kill it after this many ms (exit code 124)
// Without --wait it prints the new process id and returns immediately.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal static class RunHidden
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("user32.dll")] private static extern uint WaitForInputIdle(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint GENERIC_ALL = 0x10000000;

    private static int Main(string[] args)
    {
        string desktop = "VidFlowTest";
        bool wait = false;
        uint timeout = 0xFFFFFFFF;
        int i = 0;
        for (; i < args.Length; i++)
        {
            if (args[i] == "--") { i++; break; }
            if (args[i] == "--wait") wait = true;
            else if (args[i] == "--desktop" && i + 1 < args.Length) desktop = args[++i];
            else if (args[i] == "--timeout" && i + 1 < args.Length) timeout = uint.Parse(args[++i]);
            else break;
        }
        if (i >= args.Length) { Console.Error.WriteLine("usage: RunHidden [--desktop N] [--wait] [--timeout ms] -- program [args]"); return 2; }

        var cmd = new StringBuilder();
        for (int j = i; j < args.Length; j++)
        {
            if (cmd.Length > 0) cmd.Append(' ');
            cmd.Append(Quote(args[j]));
        }

        // Creates the desktop, or opens it if it already exists.
        IntPtr hDesk = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (hDesk == IntPtr.Zero) { Console.Error.WriteLine("CreateDesktop failed: " + new Win32Exception().Message); return 3; }

        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = "winsta0\\" + desktop };
        PROCESS_INFORMATION pi;
        // Inherit our console handles only when we wait: a detached child holding our stdout pipe
        // would keep the caller (e.g. PowerShell capturing the printed pid) waiting forever.
        if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, wait, 0, IntPtr.Zero, Environment.CurrentDirectory, ref si, out pi))
        {
            Console.Error.WriteLine("CreateProcess failed: " + new Win32Exception().Message + " : " + cmd);
            CloseDesktop(hDesk);
            return 3;
        }
        CloseHandle(pi.hThread);
        if (!wait)
        {
            // A desktop dies with its last handle unless a thread is attached to it, so hold ours
            // until the child is up (GUI: input idle; console: give it a moment to attach).
            if (WaitForInputIdle(pi.hProcess, 15000) != 0) WaitForSingleObject(pi.hProcess, 3000);
            Console.WriteLine(pi.dwProcessId);
            CloseHandle(pi.hProcess);
            CloseDesktop(hDesk);
            return 0;
        }
        uint code;
        if (WaitForSingleObject(pi.hProcess, timeout) != 0)
        {
            TerminateProcess(pi.hProcess, 124);
            WaitForSingleObject(pi.hProcess, 5000);
            code = 124;
        }
        else GetExitCodeProcess(pi.hProcess, out code);
        CloseHandle(pi.hProcess);
        CloseDesktop(hDesk);
        return (int)code;
    }

    private static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char c in a)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
            sb.Append('\\', bs); bs = 0; sb.Append(c);
        }
        sb.Append('\\', bs * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
