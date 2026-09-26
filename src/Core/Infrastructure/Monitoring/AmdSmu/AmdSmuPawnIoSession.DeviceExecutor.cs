using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

internal sealed partial class AmdSmuPawnIoSession
{
    private sealed class DirectPawnIoDeviceExecutor : IAmdSmuPawnIoExecutor
    {
        private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
        private const uint DeviceType = 41394u << 16;
        private const uint LoadBinaryControlCode = DeviceType | (0x821u << 2);
        private const uint ExecuteControlCode = DeviceType | (0x841u << 2);
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const int FunctionNameLength = 32;
        private readonly SafeFileHandle handle;

        private DirectPawnIoDeviceExecutor(SafeFileHandle handle)
        {
            this.handle = handle;
        }

        public string Name => "PawnIO device";

        public static DirectPawnIoDeviceExecutor Open(byte[] module)
        {
            var handle = CreateFile(
                DevicePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new AmdSmuProviderUnavailableException(
                    $"无法打开 PawnIO 设备：0x{HResultFromWin32(error):X8} {new Win32Exception(error).Message}");
            }

            var executor = new DirectPawnIoDeviceExecutor(handle);
            try
            {
                if (!DeviceIoControl(
                    handle,
                    LoadBinaryControlCode,
                    module,
                    (uint)module.Length,
                    [],
                    0,
                    out _,
                    IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new AmdSmuProviderUnavailableException(
                        $"PawnIO 加载 RyzenSMU 模块失败：0x{HResultFromWin32(error):X8} {new Win32Exception(error).Message}");
                }

                return executor;
            }
            catch
            {
                executor.Dispose();
                throw;
            }
        }

        public ulong[] Execute(string functionName, ulong[] input, int outputLength)
        {
            var outputBytes = outputLength > 0 ? new byte[outputLength * sizeof(ulong)] : [];
            var inputBytes = new byte[FunctionNameLength + input.Length * sizeof(ulong)];
            var nameBytes = Encoding.ASCII.GetBytes(functionName);
            Array.Copy(nameBytes, inputBytes, Math.Min(nameBytes.Length, FunctionNameLength - 1));
            if (input.Length > 0)
            {
                Buffer.BlockCopy(input, 0, inputBytes, FunctionNameLength, input.Length * sizeof(ulong));
            }

            if (!DeviceIoControl(
                handle,
                ExecuteControlCode,
                inputBytes,
                (uint)inputBytes.Length,
                outputBytes,
                (uint)outputBytes.Length,
                out var bytesReturned,
                IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                throw new AmdSmuProviderUnavailableException(
                    $"{functionName} 执行失败：0x{HResultFromWin32(error):X8} {new Win32Exception(error).Message}");
            }

            var output = new ulong[bytesReturned / sizeof(ulong)];
            if (output.Length > 0)
            {
                Buffer.BlockCopy(outputBytes, 0, output, 0, output.Length * sizeof(ulong));
            }

            return output;
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            [In] byte[] inputBuffer,
            uint inputBufferSize,
            [Out] byte[] outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
