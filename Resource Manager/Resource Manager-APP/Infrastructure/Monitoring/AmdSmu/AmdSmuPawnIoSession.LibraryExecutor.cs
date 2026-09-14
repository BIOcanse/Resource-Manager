using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

internal sealed partial class AmdSmuPawnIoSession
{
    private sealed class PawnIoLibraryExecutor : IAmdSmuPawnIoExecutor
    {
        private readonly IntPtr libraryHandle;
        private readonly IntPtr pawnIoHandle;
        private readonly PawnIoExecuteDelegate execute;
        private readonly PawnIoCloseDelegate close;
        private bool disposed;

        private PawnIoLibraryExecutor(
            IntPtr libraryHandle,
            IntPtr pawnIoHandle,
            PawnIoExecuteDelegate execute,
            PawnIoCloseDelegate close)
        {
            this.libraryHandle = libraryHandle;
            this.pawnIoHandle = pawnIoHandle;
            this.execute = execute;
            this.close = close;
        }

        public string Name => "PawnIOLib";

        public static PawnIoLibraryExecutor Open(string libraryPath, byte[] module)
        {
            if (!File.Exists(libraryPath))
            {
                throw new DllNotFoundException($"未找到 {libraryPath}");
            }

            var libraryHandle = NativeLibrary.Load(libraryPath);
            try
            {
                var open = GetExport<PawnIoOpenDelegate>(libraryHandle, "pawnio_open");
                var load = GetExport<PawnIoLoadDelegate>(libraryHandle, "pawnio_load");
                var execute = GetExport<PawnIoExecuteDelegate>(libraryHandle, "pawnio_execute");
                var close = GetExport<PawnIoCloseDelegate>(libraryHandle, "pawnio_close");

                var openResult = open(out var pawnIoHandle);
                ThrowIfFailed(openResult, "pawnio_open");
                var loadResult = load(pawnIoHandle, module, (UIntPtr)module.Length);
                try
                {
                    ThrowIfFailed(loadResult, "pawnio_load");
                    return new PawnIoLibraryExecutor(libraryHandle, pawnIoHandle, execute, close);
                }
                catch
                {
                    close(pawnIoHandle);
                    throw;
                }
            }
            catch
            {
                NativeLibrary.Free(libraryHandle);
                throw;
            }
        }

        public ulong[] Execute(string functionName, ulong[] input, int outputLength)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var output = outputLength > 0 ? new ulong[outputLength] : [];
            var result = execute(
                pawnIoHandle,
                functionName,
                input.Length == 0 ? null : input,
                (UIntPtr)input.Length,
                output.Length == 0 ? null : output,
                (UIntPtr)output.Length,
                out var returnSize);
            ThrowIfFailed(result, functionName);
            var actualLength = Math.Min((int)returnSize, output.Length);
            if (actualLength == output.Length)
            {
                return output;
            }

            Array.Resize(ref output, actualLength);
            return output;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            close(pawnIoHandle);
            NativeLibrary.Free(libraryHandle);
        }

        private static T GetExport<T>(IntPtr libraryHandle, string name)
            where T : Delegate
        {
            var export = NativeLibrary.GetExport(libraryHandle, name);
            return Marshal.GetDelegateForFunctionPointer<T>(export);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int PawnIoOpenDelegate(out IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int PawnIoLoadDelegate(
            IntPtr handle,
            [In] byte[] blob,
            UIntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
        private delegate int PawnIoExecuteDelegate(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            [In] ulong[]? input,
            UIntPtr inputSize,
            [Out] ulong[]? output,
            UIntPtr outputSize,
            out UIntPtr returnSize);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int PawnIoCloseDelegate(IntPtr handle);
    }
}
