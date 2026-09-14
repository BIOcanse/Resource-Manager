using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.Tools.BackendLifetime
{
    public sealed class ConsoleStopResult
    {
        public int ProcessId { get; set; }
        public long CreationFileTimeUtc { get; set; }
        public string ImagePath { get; set; }
        public uint[] ConsoleMembers { get; set; }
        public bool SignalSent { get; set; }
        public bool Exited { get; set; }
        public uint? ExitCode { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public static class ConsoleStop
    {
        // This API belongs in a disposable sender process, never the UI or long-lived owner.
        public static ConsoleStopResult Request(int processId, long creationFileTimeUtc,
            string imagePath, int timeoutMilliseconds)
        {
            if (processId <= 0 || processId == Process.GetCurrentProcess().Id || creationFileTimeUtc <= 0)
                throw new ArgumentException("An owned external process identity is required.");
            if (!Path.IsPathRooted(imagePath) || timeoutMilliseconds < 1 || timeoutMilliseconds > 60000)
                throw new ArgumentException("An absolute image and bounded shutdown wait are required.");
            string expectedPath = Path.GetFullPath(imagePath);
            using (SafeProcessHandle target = OpenProcess(0x00101000, false, (uint)processId))
            {
                if (target.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the owned backend.");
                long creation, exit, kernel, user;
                if (!GetProcessTimes(target, out creation, out exit, out kernel, out user))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var path = new StringBuilder(32768);
                uint length = (uint)path.Capacity;
                if (!QueryFullProcessImageNameW(target, 0, path, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (creation != creationFileTimeUtc || !String.Equals(expectedPath, path.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The backend process identity differs; no signal was sent.");
                if (WaitForSingleObject(target, 0) != 258)
                    throw new InvalidOperationException("The backend is not live; no graceful-stop claim is possible.");

                var result = new ConsoleStopResult { ProcessId = processId, CreationFileTimeUtc = creation,
                    ImagePath = path.ToString() };
                FreeConsole();
                if (!AttachConsole((uint)processId))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The backend has no attachable console.");
                try
                {
                    uint[] members = new uint[64];
                    uint count = GetConsoleProcessList(members, (uint)members.Length);
                    if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (count != 2) throw new InvalidOperationException("The backend console is not exclusive; no signal was sent.");
                    uint senderId = (uint)Process.GetCurrentProcess().Id;
                    if (!((members[0] == processId && members[1] == senderId) ||
                          (members[1] == processId && members[0] == senderId)))
                        throw new InvalidOperationException("The backend console contains an unexpected process; no signal was sent.");
                    result.ConsoleMembers = new uint[] { members[0], members[1] };
                    if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (WaitForSingleObject(target, 0) != 258)
                        throw new InvalidOperationException("The backend exited before the signal.");
                    var clock = Stopwatch.StartNew();
                    if (!GenerateConsoleCtrlEvent(0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    result.SignalSent = true;
                    uint wait = WaitForSingleObject(target, (uint)timeoutMilliseconds);
                    if (wait != 0 && wait != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
                    result.Exited = wait == 0;
                    if (result.Exited)
                    {
                        uint code;
                        if (!GetExitCodeProcess(target, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        result.ExitCode = code;
                    }
                    result.ElapsedMilliseconds = clock.ElapsedMilliseconds;
                    return result;
                }
                finally { FreeConsole(); }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
        [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll")] public static extern uint GetErrorMode();
    }
}
