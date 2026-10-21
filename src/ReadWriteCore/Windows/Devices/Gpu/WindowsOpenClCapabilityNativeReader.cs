using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>Read-only OpenCL ICD inventory through platform/device enumeration and clGetDeviceInfo.
/// It creates no OpenCL context, queue, buffer, kernel, or compute workload.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsOpenClCapabilityNativeReader : IWindowsOpenClCapabilityReader
{
    private const int ClSuccess = 0;
    private const int ClDeviceNotFound = -1;
    private const int ClPlatformNotFoundKhr = -1001;
    private const ulong ClDeviceTypeGpu = 1UL << 2;
    private const uint ClDeviceVersion = 0x102F;
    private const uint ClDeviceExtensions = 0x1030;
    private const uint ClDeviceUuidKhr = 0x106A;
    private const uint ClDriverUuidKhr = 0x106B;
    private const uint ClDeviceLuidValidKhr = 0x106C;
    private const uint ClDeviceLuidKhr = 0x106D;
    private const uint ClDeviceNodeMaskKhr = 0x106E;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const int UuidBytes = 16;
    private const int LuidBytes = 8;
    private const int UInt32Bytes = 4;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly object ApiGate = new();
    private static OpenClApi? api;

    public WindowsOpenClCapabilityReadResult Read(WindowsOpenClCapabilityScope scope,
        WindowsOpenClCapabilityFields selectedFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (selectedFields == WindowsOpenClCapabilityFields.None || (selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return WindowsOpenClCapabilityReadResult.Unavailable;

        try
        {
            return ReadFromIcd(scope, selectedFields, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return WindowsOpenClCapabilityReadResult.Unavailable;
        }
    }

    private static WindowsOpenClCapabilityReadResult ReadFromIcd(WindowsOpenClCapabilityScope scope,
        WindowsOpenClCapabilityFields selectedFields, CancellationToken cancellationToken)
    {
        var api = GetApi();
        var platforms = ReadPlatforms(api, scope.MaximumPlatformCount, cancellationToken);
        if (platforms.IsEmpty) return new(true, []);

        var targets = scope.Targets.ToHashSet();
        var rows = new Dictionary<string, WindowsOpenClDeviceCapabilityRead>(StringComparer.Ordinal);
        var totalDevices = 0;
        foreach (var platform in platforms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = ReadGpuDevices(api, platform, scope.MaximumDeviceCount - totalDevices, cancellationToken);
            totalDevices = checked(totalDevices + devices.Length);
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extensionsText = ReadText(api, device, ClDeviceExtensions, scope.MaximumTextBytes, cancellationToken);
                var extensionNames = ParseExtensions(extensionsText, scope.MaximumExtensionCount);
                if (!extensionNames.Contains("cl_khr_device_uuid", StringComparer.Ordinal))
                    throw new OpenClIdentityException("The GPU device does not declare cl_khr_device_uuid.");

                if (ReadUInt32(api, device, ClDeviceLuidValidKhr, cancellationToken) != 1)
                    throw new OpenClIdentityException("The OpenCL device does not expose a valid DXGI LUID.");
                var luid = BinaryPrimitives.ReadUInt64LittleEndian(
                    ReadFixedBytes(api, device, ClDeviceLuidKhr, LuidBytes, cancellationToken));
                var nodeMask = ReadUInt32(api, device, ClDeviceNodeMaskKhr, cancellationToken);
                if (nodeMask == 0 || (nodeMask & (nodeMask - 1)) != 0)
                    throw new OpenClIdentityException("The OpenCL device returned an invalid node mask.");
                var target = new WindowsOpenClAdapterTarget(luid, nodeMask);
                var deviceUuid = Convert.ToHexString(ReadFixedBytes(api, device, ClDeviceUuidKhr, UuidBytes, cancellationToken));
                var driverUuid = Convert.ToHexString(ReadFixedBytes(api, device, ClDriverUuidKhr, UuidBytes, cancellationToken));

                if (!targets.Contains(target)) continue;
                string? version = null;
                if ((selectedFields & WindowsOpenClCapabilityFields.DeviceVersion) != 0)
                {
                    try
                    {
                        version = ReadText(api, device, ClDeviceVersion, scope.MaximumTextBytes, cancellationToken);
                        if (version.Length == 0) version = null;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        version = null;
                    }
                }

                ImmutableArray<string>? selectedExtensions = (selectedFields & WindowsOpenClCapabilityFields.Extensions) != 0
                    ? extensionNames : null;
                var identity = new WindowsOpenClDeviceIdentity(target, deviceUuid, driverUuid);
                var row = new WindowsOpenClDeviceCapabilityRead(identity, version, selectedExtensions);
                var key = WindowsInformationFieldProjection.OpenClDeviceIdentity(identity);
                if (rows.TryGetValue(key, out var prior))
                {
                    if (!SameObservation(prior, row))
                        throw new OpenClIdentityException("The same OpenCL UUID/LUID identity returned conflicting observations.");
                }
                else rows.Add(key, row);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(true, [.. rows.Values.OrderBy(static row => row.Identity.Adapter.DxgiLuid)
            .ThenBy(static row => row.Identity.Adapter.NodeMask)
            .ThenBy(static row => row.Identity.DeviceUuidHex, StringComparer.Ordinal)
            .ThenBy(static row => row.Identity.DriverUuidHex, StringComparer.Ordinal)]);
    }

    private static ImmutableArray<IntPtr> ReadPlatforms(OpenClApi api, int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = api.GetPlatformIds(0, null, out var count);
        cancellationToken.ThrowIfCancellationRequested();
        if (result == ClPlatformNotFoundKhr || result == ClSuccess && count == 0) return [];
        Check(result, "clGetPlatformIDs(count)");
        if (count > (uint)maximumCount) throw new OpenClDataException("OpenCL platform enumeration exceeded its configured bound.");

        var platforms = new IntPtr[checked((int)count)];
        cancellationToken.ThrowIfCancellationRequested();
        Check(api.GetPlatformIds(count, platforms, out var returned), "clGetPlatformIDs(values)");
        cancellationToken.ThrowIfCancellationRequested();
        if (returned != count || platforms.Any(static platform => platform == IntPtr.Zero)
            || platforms.Distinct().Count() != platforms.Length)
            throw new OpenClDataException("OpenCL platform enumeration changed or returned malformed handles.");
        return [.. platforms];
    }

    private static ImmutableArray<IntPtr> ReadGpuDevices(OpenClApi api, IntPtr platform, int remainingCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = api.GetDeviceIds(platform, ClDeviceTypeGpu, 0, null, out var count);
        cancellationToken.ThrowIfCancellationRequested();
        if (result == ClDeviceNotFound || result == ClSuccess && count == 0) return [];
        Check(result, "clGetDeviceIDs(count)");
        if (remainingCount < 0 || count > (uint)remainingCount)
            throw new OpenClDataException("OpenCL GPU device enumeration exceeded its configured bound.");

        var devices = new IntPtr[checked((int)count)];
        cancellationToken.ThrowIfCancellationRequested();
        Check(api.GetDeviceIds(platform, ClDeviceTypeGpu, count, devices, out var returned), "clGetDeviceIDs(values)");
        cancellationToken.ThrowIfCancellationRequested();
        if (returned != count || devices.Any(static device => device == IntPtr.Zero)
            || devices.Distinct().Count() != devices.Length)
            throw new OpenClDataException("OpenCL GPU device enumeration changed or returned malformed handles.");
        return [.. devices];
    }

    private static uint ReadUInt32(OpenClApi api, IntPtr device, uint parameter, CancellationToken cancellationToken)
    {
        var bytes = ReadFixedBytes(api, device, parameter, UInt32Bytes, cancellationToken);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static byte[] ReadFixedBytes(OpenClApi api, IntPtr device, uint parameter, int expectedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = Marshal.AllocHGlobal(expectedBytes);
        try
        {
            var result = api.GetDeviceInfo(device, parameter, (nuint)expectedBytes, buffer, out var returnedBytes);
            cancellationToken.ThrowIfCancellationRequested();
            Check(result, "clGetDeviceInfo(fixed identity)");
            if (returnedBytes != (nuint)expectedBytes)
                throw new OpenClDataException("OpenCL returned a fixed identity value with the wrong ABI size.");
            var bytes = new byte[expectedBytes];
            Marshal.Copy(buffer, bytes, 0, expectedBytes);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadText(OpenClApi api, IntPtr device, uint parameter, int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check(api.GetDeviceInfo(device, parameter, 0, IntPtr.Zero, out var requiredBytes), "clGetDeviceInfo(size)");
        cancellationToken.ThrowIfCancellationRequested();
        if (requiredBytes == 0 || requiredBytes > (nuint)maximumBytes || requiredBytes > (nuint)int.MaxValue)
            throw new OpenClDataException("OpenCL text result is empty or exceeds its configured byte bound.");

        var allocationSize = checked((int)requiredBytes);
        var buffer = Marshal.AllocHGlobal(allocationSize);
        try
        {
            var result = api.GetDeviceInfo(device, parameter, requiredBytes, buffer, out var returnedBytes);
            cancellationToken.ThrowIfCancellationRequested();
            Check(result, "clGetDeviceInfo(text)");
            if (returnedBytes == 0 || returnedBytes > requiredBytes || returnedBytes > (nuint)int.MaxValue)
                throw new OpenClDataException("OpenCL text result changed beyond its allocated bound.");
            var bytes = new byte[checked((int)returnedBytes)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            if (bytes[^1] != 0 || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)0))
                throw new OpenClDataException("OpenCL text result is not exactly one null-terminated UTF-8 string.");
            try { return StrictUtf8.GetString(bytes, 0, bytes.Length - 1); }
            catch (DecoderFallbackException exception)
            {
                throw new OpenClDataException("OpenCL text result is not valid UTF-8.", exception);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ImmutableArray<string> ParseExtensions(string text, int maximumCount)
    {
        if (text.Length == 0) return [];
        var names = text.Split(' ');
        if (names.Length > maximumCount || names.Any(static name => !IsExtensionToken(name))
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new OpenClDataException("CL_DEVICE_EXTENSIONS is malformed or exceeds its configured token bound.");
        return [.. names.OrderBy(static name => name, StringComparer.Ordinal)];
    }

    private static bool IsExtensionToken(string value)
        => value.Length > 0 && !value.Contains(' ') && !value.Contains('\0');

    private static bool SameObservation(WindowsOpenClDeviceCapabilityRead left,
        WindowsOpenClDeviceCapabilityRead right)
    {
        if (!string.Equals(left.DeviceVersion, right.DeviceVersion, StringComparison.Ordinal)) return false;
        if (left.Extensions is null || right.Extensions is null) return left.Extensions is null && right.Extensions is null;
        return left.Extensions.Value.SequenceEqual(right.Extensions.Value, StringComparer.Ordinal);
    }

    private static void Check(int result, string operation)
    {
        if (result != ClSuccess) throw new OpenClCallException($"{operation} failed with OpenCL status {result}.");
    }

    private static OpenClApi CreateApi()
    {
        var module = LoadLibraryExW("OpenCL.dll", IntPtr.Zero, LoadLibrarySearchSystem32);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system OpenCL ICD loader is unavailable.");
        try
        {
            var getPlatformIds = Marshal.GetDelegateForFunctionPointer<GetPlatformIdsDelegate>(
                NativeLibrary.GetExport(module, "clGetPlatformIDs"));
            var getDeviceIds = Marshal.GetDelegateForFunctionPointer<GetDeviceIdsDelegate>(
                NativeLibrary.GetExport(module, "clGetDeviceIDs"));
            var getDeviceInfo = Marshal.GetDelegateForFunctionPointer<GetDeviceInfoDelegate>(
                NativeLibrary.GetExport(module, "clGetDeviceInfo"));
            return new(module, getPlatformIds, getDeviceIds, getDeviceInfo);
        }
        catch
        {
            _ = FreeLibrary(module);
            throw;
        }
    }

    private static OpenClApi GetApi()
    {
        var current = Volatile.Read(ref api);
        if (current is not null) return current;
        lock (ApiGate)
        {
            current = api;
            if (current is not null) return current;
            current = CreateApi();
            Volatile.Write(ref api, current);
            return current;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPlatformIdsDelegate(uint numberOfEntries,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[]? platforms, out uint numberOfPlatforms);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceIdsDelegate(IntPtr platform, ulong deviceType, uint numberOfEntries,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] IntPtr[]? devices, out uint numberOfDevices);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceInfoDelegate(IntPtr device, uint parameter, nuint parameterValueSize,
        IntPtr parameterValue, out nuint parameterValueSizeReturned);

    private sealed record OpenClApi(IntPtr Module, GetPlatformIdsDelegate GetPlatformIds,
        GetDeviceIdsDelegate GetDeviceIds, GetDeviceInfoDelegate GetDeviceInfo);

    private sealed class OpenClCallException(string message) : Exception(message);
    private sealed class OpenClDataException(string message, Exception? inner = null) : Exception(message, inner);
    private sealed class OpenClIdentityException(string message) : Exception(message);
}
