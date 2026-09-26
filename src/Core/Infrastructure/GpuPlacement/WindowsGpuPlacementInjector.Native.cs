using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    private static class NativeMethods
    {
        public const uint RequiredProcessAccess = ProcessCreateThread
            | ProcessQueryInformation
            | ProcessVmOperation
            | ProcessVmWrite
            | ProcessVmRead
            | Synchronize;
        public const uint MemCommit = 0x00001000;
        public const uint MemReserve = 0x00002000;
        public const uint MemRelease = 0x00008000;
        public const uint PageReadWrite = 0x04;
        public const uint WaitObject0 = 0;
        public const uint WaitTimeout = 258;
        public const int ErrorNoMoreFiles = 18;
        public const uint Th32csSnapModule = 0x00000008;
        public const uint Th32csSnapModule32 = 0x00000010;
        public const ushort ImageFileMachineUnknown = 0;
        public const ushort ImageFileMachineAmd64 = 0x8664;
        public const uint ProtectionLevelNone = 0xfffffffe;

        private const uint ProcessCreateThread = 0x0002;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint Synchronize = 0x00100000;

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, SafeKernelHandle source, IntPtr targetProcess,
            out SafeKernelHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

        [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateWaitHandle(IntPtr sourceProcess, SafeKernelHandle source, IntPtr targetProcess,
            out SafeWaitHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetThreadTimes(SafeKernelHandle thread, out ulong creationTime,
            out ulong exitTime, out ulong kernelTime, out ulong userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeKernelHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(
            SafeKernelHandle process,
            out ulong creationTime,
            out ulong exitTime,
            out ulong kernelTime,
            out ulong userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(
            SafeKernelHandle process,
            IntPtr address,
            nuint size,
            uint allocationType,
            uint protection);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool VirtualFreeEx(
            SafeKernelHandle process,
            IntPtr address,
            nuint size,
            uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteProcessMemory(
            SafeKernelHandle process,
            IntPtr baseAddress,
            byte[] buffer,
            nuint size,
            out nuint bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadProcessMemory(
            SafeKernelHandle process, IntPtr baseAddress, [Out] byte[] buffer, nuint size, out nuint bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeKernelHandle CreateRemoteThread(
            SafeKernelHandle process,
            IntPtr threadAttributes,
            nuint stackSize,
            IntPtr startAddress,
            IntPtr parameter,
            uint creationFlags,
            out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeThread(SafeKernelHandle thread, out uint exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process2(
            SafeKernelHandle process,
            out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessMitigationPolicy(
            SafeKernelHandle process,
            ProcessMitigationPolicy mitigationPolicy,
            out ProcessMitigationBinarySignaturePolicy buffer,
            nuint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessMitigationPolicy(
            SafeKernelHandle process,
            ProcessMitigationPolicy mitigationPolicy,
            out ProcessMitigationDynamicCodePolicy buffer,
            nuint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessInformation(
            SafeKernelHandle process,
            ProcessInformationClass processInformationClass,
            out ProcessProtectionLevelInformation processInformation,
            uint processInformationSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            SafeKernelHandle process,
            uint flags,
            System.Text.StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeKernelHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Module32First(
            SafeKernelHandle snapshot,
            ref ModuleEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Module32Next(
            SafeKernelHandle snapshot,
            ref ModuleEntry32 entry);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ModuleEntry32
        {
            public uint Size;
            public uint ModuleId;
            public uint ProcessId;
            public uint GlobalUsageCount;
            public uint ProcessUsageCount;
            public IntPtr BaseAddress;
            public uint BaseSize;
            public IntPtr ModuleHandle;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ModuleName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutablePath;
        }

        public enum ProcessMitigationPolicy
        {
            DynamicCode = 2,
            BinarySignature = 8
        }

        public enum ProcessInformationClass
        {
            ProtectionLevel = 7
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessMitigationBinarySignaturePolicy
        {
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessMitigationDynamicCodePolicy
        {
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessProtectionLevelInformation
        {
            public uint ProtectionLevel;
        }
    }

    internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeKernelHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
