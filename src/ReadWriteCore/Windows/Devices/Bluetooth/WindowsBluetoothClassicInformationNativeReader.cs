using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Bluetooth;

/// <summary>Read-only Classic Bluetooth enumeration through bluetoothapis.h. Device searches
/// use fIssueInquiry=FALSE; this source neither discovers unknown peers nor pairs or connects.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBluetoothClassicInformationNativeReader : IWindowsBluetoothClassicInformationReader
{
    private const int ErrorNoMoreItems = 259;
    private const int BluetoothMaxNameSize = 248;
    private const ulong BluetoothAddressMask = 0x0000FFFFFFFFFFFFUL;

    public WindowsBluetoothClassicInformationReadResult Read(
        WindowsBluetoothClassicInformationScope scope,
        WindowsBluetoothClassicInformationFields selectedFields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if ((selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedFields == WindowsBluetoothClassicInformationFields.None)
            return new([]);

        var controllerSelected = (selectedFields & WindowsBluetoothClassicInformationFields.ControllerId) != 0;
        var deviceSelected = (selectedFields & WindowsBluetoothClassicInformationFields.DeviceState) != 0;
        var radioHandles = new List<IntPtr>();
        try
        {
            List<NativeRadio> radios;
            try
            {
                radioHandles = EnumerateRadioHandles(scope.MaximumControllers, cancellationToken);
                radios = ReadRadioAddresses(radioHandles, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsNativeReadFailure(exception))
            {
                return Failed([], selectedFields, FailureReason(exception));
            }

            var addresses = radios.Select(static radio => radio.Address).ToArray();
            var controllerDirectory = controllerSelected
                ? new WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicControllerInformation>(true,
                    [.. radios.Select(static radio => new DeviceTopologyBluetoothClassicControllerInformation(radio.Address))])
                : null;
            if (!deviceSelected)
                return new(addresses, controllerDirectory);

            try
            {
                var devices = ReadDevices(radios, scope.MaximumDevices, cancellationToken);
                return new(addresses, controllerDirectory,
                    new WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicDeviceInformation>(true, devices));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsNativeReadFailure(exception))
            {
                return new(addresses, controllerDirectory,
                    new WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicDeviceInformation>(false, [], FailureReason(exception)));
            }
        }
        finally { CloseRadioHandles(radioHandles); }
    }

    private static List<IntPtr> EnumerateRadioHandles(int maximumControllers, CancellationToken cancellationToken)
    {
        var parameters = new NativeBluetoothFindRadioParams { Size = checked((uint)Marshal.SizeOf<NativeBluetoothFindRadioParams>()) };
        IntPtr findHandle = IntPtr.Zero;
        IntPtr nextRadio = IntPtr.Zero;
        var radios = new List<IntPtr>();
        try
        {
            findHandle = BluetoothFindFirstRadio(ref parameters, out nextRadio);
            if (findHandle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems) return radios;
                throw new Win32Exception(error, "BluetoothFindFirstRadio failed.");
            }
            if (nextRadio == IntPtr.Zero)
                throw new InvalidDataException("BluetoothFindFirstRadio returned no radio handle.");
            radios.Add(nextRadio);
            nextRadio = IntPtr.Zero;
            if (radios.Count > maximumControllers)
                throw new InvalidDataException("Bluetooth radio count exceeds the explicit controller bound.");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!BluetoothFindNextRadio(findHandle, out nextRadio))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    throw new Win32Exception(error, "BluetoothFindNextRadio failed.");
                }
                if (nextRadio == IntPtr.Zero)
                    throw new InvalidDataException("BluetoothFindNextRadio returned no radio handle.");
                radios.Add(nextRadio);
                nextRadio = IntPtr.Zero;
                if (radios.Count > maximumControllers)
                    throw new InvalidDataException("Bluetooth radio count exceeds the explicit controller bound.");
            }
            return radios;
        }
        catch
        {
            if (nextRadio != IntPtr.Zero) _ = CloseHandle(nextRadio);
            CloseRadioHandles(radios);
            throw;
        }
        finally
        {
            if (findHandle != IntPtr.Zero) _ = BluetoothFindRadioClose(findHandle);
        }
    }

    private static List<NativeRadio> ReadRadioAddresses(IReadOnlyList<IntPtr> radioHandles,
        CancellationToken cancellationToken)
    {
        var radios = new List<NativeRadio>(radioHandles.Count);
        var seenAddresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in radioHandles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var information = new NativeBluetoothRadioInfo
            {
                Size = checked((uint)Marshal.SizeOf<NativeBluetoothRadioInfo>()),
                Name = string.Empty
            };
            var status = BluetoothGetRadioInfo(handle, ref information);
            if (status != 0) throw new Win32Exception(checked((int)status), "BluetoothGetRadioInfo failed.");
            var address = FormatAddress(information.Address.Value);
            if (!seenAddresses.Add(address))
                throw new InvalidDataException("Two enumerated Bluetooth radios reported the same local address.");
            radios.Add(new(handle, address));
        }
        return radios;
    }

    private static IReadOnlyList<DeviceTopologyBluetoothClassicDeviceInformation> ReadDevices(
        IReadOnlyList<NativeRadio> radios, int maximumDevices, CancellationToken cancellationToken)
    {
        var devices = new List<DeviceTopologyBluetoothClassicDeviceInformation>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var radio in radios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var search = new NativeBluetoothDeviceSearchParams
            {
                Size = checked((uint)Marshal.SizeOf<NativeBluetoothDeviceSearchParams>()),
                ReturnAuthenticated = 1,
                ReturnRemembered = 1,
                ReturnUnknown = 0,
                ReturnConnected = 1,
                IssueInquiry = 0,
                TimeoutMultiplier = 0,
                Radio = radio.Handle
            };
            var information = NewDeviceInfo();
            var findHandle = BluetoothFindFirstDevice(ref search, ref information);
            if (findHandle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems) continue;
                throw new Win32Exception(error, "BluetoothFindFirstDevice failed.");
            }

            try
            {
                AddDevice(information);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    information = NewDeviceInfo();
                    if (!BluetoothFindNextDevice(findHandle, ref information))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreItems) break;
                        throw new Win32Exception(error, "BluetoothFindNextDevice failed.");
                    }
                    AddDevice(information);
                }
            }
            finally { _ = BluetoothFindDeviceClose(findHandle); }

            void AddDevice(NativeBluetoothDeviceInfo item)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (devices.Count >= maximumDevices)
                    throw new InvalidDataException("Bluetooth device count exceeds the explicit device bound.");
                var deviceAddress = FormatAddress(item.Address.Value);
                var identity = radio.Address + "|" + deviceAddress;
                if (!identities.Add(identity))
                    throw new InvalidDataException("A radio returned the same Bluetooth device address more than once.");
                devices.Add(new(radio.Address, deviceAddress, item.Connected != 0, item.Authenticated != 0));
            }
        }
        return devices;
    }

    private static NativeBluetoothDeviceInfo NewDeviceInfo() => new()
    {
        Size = checked((uint)Marshal.SizeOf<NativeBluetoothDeviceInfo>()),
        Name = string.Empty
    };

    private static string FormatAddress(ulong address)
    {
        if (address == 0 || (address & ~BluetoothAddressMask) != 0)
            throw new InvalidDataException("Windows Bluetooth returned an invalid 48-bit address.");
        return $"{(address >> 40) & 0xFF:X2}:{(address >> 32) & 0xFF:X2}:{(address >> 24) & 0xFF:X2}:"
            + $"{(address >> 16) & 0xFF:X2}:{(address >> 8) & 0xFF:X2}:{address & 0xFF:X2}";
    }

    private static WindowsBluetoothClassicInformationReadResult Failed(IReadOnlyList<string> addresses,
        WindowsBluetoothClassicInformationFields selectedFields, string failure)
    {
        var controllers = (selectedFields & WindowsBluetoothClassicInformationFields.ControllerId) != 0
            ? new WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicControllerInformation>(false, [], failure)
            : null;
        var devices = (selectedFields & WindowsBluetoothClassicInformationFields.DeviceState) != 0
            ? new WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicDeviceInformation>(false, [], failure)
            : null;
        return new(addresses, controllers, devices);
    }

    private static string FailureReason(Exception exception)
        => $"{exception.GetType().Name}: {exception.Message}";

    private static bool IsNativeReadFailure(Exception exception)
        => exception is Win32Exception or InvalidDataException or OverflowException or ArgumentException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
            or TypeLoadException or NotSupportedException or MarshalDirectiveException;

    private static void CloseRadioHandles(IEnumerable<IntPtr> radios)
    {
        foreach (var radio in radios) if (radio != IntPtr.Zero) _ = CloseHandle(radio);
    }

    private sealed record NativeRadio(IntPtr Handle, string Address);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBluetoothFindRadioParams { public uint Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBluetoothAddress { public ulong Value; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeBluetoothRadioInfo
    {
        public uint Size;
        public NativeBluetoothAddress Address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = BluetoothMaxNameSize)] public string Name;
        public uint ClassOfDevice;
        public ushort LmpSubversion;
        public ushort Manufacturer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBluetoothDeviceSearchParams
    {
        public uint Size;
        public int ReturnAuthenticated;
        public int ReturnRemembered;
        public int ReturnUnknown;
        public int ReturnConnected;
        public int IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeBluetoothDeviceInfo
    {
        public uint Size;
        public NativeBluetoothAddress Address;
        public uint ClassOfDevice;
        public int Connected;
        public int Remembered;
        public int Authenticated;
        public NativeSystemTime LastSeen;
        public NativeSystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = BluetoothMaxNameSize)] public string Name;
    }

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindFirstRadio", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref NativeBluetoothFindRadioParams parameters, out IntPtr radio);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindNextRadio", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextRadio(IntPtr findHandle, out IntPtr radio);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindRadioClose", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr findHandle);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothGetRadioInfo", ExactSpelling = true)]
    private static extern uint BluetoothGetRadioInfo(IntPtr radio, ref NativeBluetoothRadioInfo information);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindFirstDevice", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref NativeBluetoothDeviceSearchParams search,
        ref NativeBluetoothDeviceInfo information);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindNextDevice", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr findHandle, ref NativeBluetoothDeviceInfo information);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindDeviceClose", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr findHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
