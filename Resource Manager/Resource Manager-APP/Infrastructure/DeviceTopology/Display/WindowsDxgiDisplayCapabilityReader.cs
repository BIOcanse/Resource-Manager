using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static unsafe class WindowsDxgiDisplayCapabilityReader
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int GetDesc1VtableIndex = 27;
    private static readonly Guid Factory1InterfaceId = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid Output6InterfaceId = new("068346E8-AAEC-4B84-ADD7-137F513F77A1");

    internal static DeviceTopologyDxgiDisplayReadResult ReadAll()
    {
        var result = new Dictionary<string, DeviceTopologyDxgiDisplayCapabilities>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return new DeviceTopologyDxgiDisplayReadResult(result, false);
        }
        var interfaceId = Factory1InterfaceId;
        if (CreateDXGIFactory1(ref interfaceId, out var factory) < 0)
        {
            return new DeviceTopologyDxgiDisplayReadResult(result, false);
        }

        var complete = true;
        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (adapterResult == DxgiErrorNotFound)
                {
                    break;
                }
                if (adapterResult < 0 || adapter is null)
                {
                    complete = false;
                    continue;
                }

                try
                {
                    complete &= ReadAdapterOutputs(adapter, result);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(adapter);
                }
            }

        }
        finally
        {
            Marshal.FinalReleaseComObject(factory);
        }

        return new DeviceTopologyDxgiDisplayReadResult(result, complete);
    }

    private static bool ReadAdapterOutputs(
        IDXGIAdapter1 adapter,
        IDictionary<string, DeviceTopologyDxgiDisplayCapabilities> result)
    {
        var complete = true;
        for (uint outputIndex = 0; ; outputIndex++)
        {
            var outputResult = adapter.EnumOutputs(outputIndex, out var outputPointer);
            if (outputResult == DxgiErrorNotFound)
            {
                break;
            }
            if (outputResult < 0 || outputPointer == IntPtr.Zero)
            {
                complete = false;
                continue;
            }

            IntPtr output6Pointer = IntPtr.Zero;
            try
            {
                var interfaceId = Output6InterfaceId;
                if (Marshal.QueryInterface(outputPointer, in interfaceId, out output6Pointer) < 0
                    || output6Pointer == IntPtr.Zero
                    || !TryReadDescription(output6Pointer, out var description))
                {
                    continue;
                }

                var deviceName = ReadDeviceName(description);
                if (deviceName.Length == 0)
                {
                    continue;
                }

                result[deviceName] = new DeviceTopologyDxgiDisplayCapabilities(
                    description.BitsPerColor == 0 ? null : description.BitsPerColor,
                    DescribeColorSpace(description.ColorSpace),
                    NormalizeLuminance(description.MinLuminance, allowZero: true),
                    NormalizeLuminance(description.MaxLuminance, allowZero: false),
                    NormalizeLuminance(description.MaxFullFrameLuminance, allowZero: false));
            }
            finally
            {
                if (output6Pointer != IntPtr.Zero)
                {
                    Marshal.Release(output6Pointer);
                }
                Marshal.Release(outputPointer);
            }
        }

        return complete;
    }

    private static bool TryReadDescription(IntPtr output6Pointer, out DxgiOutputDesc1 description)
    {
        description = default;
        var vtable = Marshal.ReadIntPtr(output6Pointer);
        if (vtable == IntPtr.Zero)
        {
            return false;
        }

        var methodPointer = Marshal.ReadIntPtr(vtable, GetDesc1VtableIndex * IntPtr.Size);
        if (methodPointer == IntPtr.Zero)
        {
            return false;
        }

        var getDescription = (delegate* unmanaged[Stdcall]<IntPtr, DxgiOutputDesc1*, int>)methodPointer;
        var nativeDescription = default(DxgiOutputDesc1);
        var result = getDescription(output6Pointer, &nativeDescription);
        description = nativeDescription;
        return result >= 0;
    }

    private static string ReadDeviceName(in DxgiOutputDesc1 description)
    {
        fixed (char* deviceName = description.DeviceName)
        {
            return new string(deviceName, 0, 32).TrimEnd('\0').Trim();
        }
    }

    private static double? NormalizeLuminance(float value, bool allowZero)
    {
        return float.IsFinite(value) && (allowZero ? value >= 0 : value > 0)
            ? Math.Round(value, 3)
            : null;
    }

    internal static string DescribeColorSpace(int colorSpace)
    {
        return colorSpace switch
        {
            0 => "RGB / sRGB gamma / BT.709",
            1 => "RGB / linear / BT.709",
            2 => "RGB studio / BT.709",
            3 => "RGB studio / BT.2020",
            5 => "YCbCr full / BT.601",
            6 => "YCbCr studio / BT.601",
            8 => "YCbCr studio / BT.709",
            9 => "YCbCr full / BT.709",
            10 => "YCbCr studio / BT.2020",
            11 => "YCbCr full / BT.2020",
            12 => "RGB / PQ / BT.2020",
            13 => "YCbCr studio / PQ / BT.2020",
            14 => "RGB studio / PQ / BT.2020",
            17 => "RGB / sRGB gamma / BT.2020",
            18 => "YCbCr studio / HLG / BT.2020",
            19 => "YCbCr full / HLG / BT.2020",
            20 => "RGB studio / gamma 2.4 / BT.709",
            21 => "RGB studio / gamma 2.4 / BT.2020",
            _ => $"DXGI 色彩空间 {colorSpace}"
        };
    }

    internal static int GetNativeDescriptionSize() => Marshal.SizeOf<DxgiOutputDesc1>();

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    [ComImport]
    [Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr adapterPointer);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 adapterPointer);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038F61-3839-4626-91FD-086879011A05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr outputPointer);
        [PreserveSig] int GetDesc(out IntPtr description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        [PreserveSig] int GetDesc1(out IntPtr description);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiOutputDesc1
    {
        public fixed char DeviceName[32];
        public Rect DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float RedPrimary[2];
        public fixed float GreenPrimary[2];
        public fixed float BluePrimary[2];
        public fixed float WhitePoint[2];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }
}

internal sealed record DeviceTopologyDxgiDisplayCapabilities(
    uint? BitsPerColorChannel,
    string ColorSpace,
    double? MinimumLuminanceNits,
    double? MaximumLuminanceNits,
    double? MaximumFullFrameLuminanceNits);

internal sealed record DeviceTopologyDxgiDisplayReadResult(
    IReadOnlyDictionary<string, DeviceTopologyDxgiDisplayCapabilities> Capabilities,
    bool Complete);
