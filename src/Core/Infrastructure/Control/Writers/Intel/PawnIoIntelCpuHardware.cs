using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers.Intel;

internal sealed class PawnIoIntelCpuHardware : IIntelCpuHardware
{
    private readonly IntPtr library;
    private readonly IntPtr session;
    private readonly Execute execute;
    private readonly Close close;
    private bool disposed;

    internal PawnIoIntelCpuHardware(string contentRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Intel MSR 控制需要 Windows PawnIO。");
        var root = PackagePathResolver.ResolvePackageRoot(contentRoot);
        var modulePath = Path.Combine(root, "Dependencies", "intel-msr-pawnio-provider", "IntelMSR.bin");
        if (!File.Exists(modulePath)) throw new FileNotFoundException("需要安装 Intel CPU / PawnIO 组件及官方签名 IntelMSR 模块。", modulePath);
        var libraryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
        if (!File.Exists(libraryPath)) throw new FileNotFoundException("需要显式安装官方签名 PawnIO 驱动及运行库。", libraryPath);
        library = NativeLibrary.Load(libraryPath);
        try
        {
            var open = Export<Open>("pawnio_open");
            var load = Export<Load>("pawnio_load");
            execute = Export<Execute>("pawnio_execute");
            close = Export<Close>("pawnio_close");
            Check(open(out session), "打开 PawnIO");
            try
            {
                var bytes = File.ReadAllBytes(modulePath);
                Check(load(session, bytes, (UIntPtr)bytes.Length), "加载官方签名 IntelMSR 模块");
            }
            catch { close(session); throw; }
        }
        catch { NativeLibrary.Free(library); throw; }
    }

    // The pinned official 0.2.11 module controls its own write allowlist.
    // Never probe writability by performing a hardware setting write.
    public bool CanWriteMsr(uint register) => register is 0x610 or 0x150 or 0x601 or 0x607 or 0x608;
    public ulong ReadMsr(uint register)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ulong[] output = [0];
        Check(execute(session, "ioctl_read_msr", [register], (UIntPtr)1, output, (UIntPtr)1, out var length), "读取 Intel MSR");
        if (length != (UIntPtr)1) throw new IOException("IntelMSR 模块返回的读数长度不正确。");
        return output[0];
    }
    public void WriteMsr(uint register, ulong value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!CanWriteMsr(register)) throw new NotSupportedException("当前官方签名 IntelMSR 模块未开放该寄存器写入。");
        Check(execute(session, "ioctl_write_msr", [register, value], (UIntPtr)2, null, UIntPtr.Zero, out _), "写入 Intel MSR");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        close(session);
        NativeLibrary.Free(library);
    }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException($"{operation}失败：0x{result:X8}。", Marshal.GetExceptionForHR(result));
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Open(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Load(IntPtr handle, [In] byte[] blob, UIntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)] private delegate int Execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string function, [In] ulong[] input, UIntPtr inputSize, [Out] ulong[]? output, UIntPtr outputSize, out UIntPtr returned);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Close(IntPtr handle);
}
