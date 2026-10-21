using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public static partial class WindowsCpuThreadCpuidReader
{
    internal static ICpuThreadCpuidAccess CreateNativeAccess() => new NativeAccess();
    private sealed class NativeAccess : ICpuThreadCpuidAccess
    {
        public bool Select(CpuProcessorAddress address)
        {
            var affinity = new GroupAffinity { Mask = 1UL << address.GroupRelativeIndex, Group = address.ProcessorGroup };
            return SetThreadGroupAffinity(GetCurrentThread(), in affinity, out _);
        }
        public CpuProcessorAddress Current()
        { GetCurrentProcessorNumberEx(out var processor); return new(processor.Group, processor.Number); }
        public (int Eax, int Ebx, int Ecx, int Edx) Read(int leaf, int subleaf) => X86Base.CpuId(leaf, subleaf);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public ulong Mask;
        public ushort Group;
        public ushort Reserved0, Reserved1, Reserved2;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber { public ushort Group; public byte Number, Reserved; }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadGroupAffinity(nint thread, in GroupAffinity affinity, out GroupAffinity previousAffinity);
    [LibraryImport("kernel32.dll")]
    private static partial void GetCurrentProcessorNumberEx(out ProcessorNumber processor);
}
