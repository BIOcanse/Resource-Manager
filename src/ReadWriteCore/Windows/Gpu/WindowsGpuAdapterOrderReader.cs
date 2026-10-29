using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Gpu;

public sealed class WindowsGpuAdapterOrderReader
{
    private const ulong IntegratedDedicatedMemoryThresholdBytes = 1024UL * 1024UL * 1024UL;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;
    private static readonly Guid IdxgiFactory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private readonly WindowsGpuIdentityBridge identityBridge = new();
    private readonly WindowsGpuTopologyGenerationTracker topologyGeneration =
        new();
    // Null in production, which reads DXGI and the identity bridge. Tests supply a recorded enumeration and identity step.
    private readonly Func<WindowsGpuAdapterEnumeration>? enumerate;
    private readonly Func<WindowsGpuAdapter, WindowsGpuAdapter>? identify;

    public WindowsGpuAdapterOrderReader() { }

    internal WindowsGpuAdapterOrderReader(
        Func<WindowsGpuAdapterEnumeration> enumerate,
        Func<WindowsGpuAdapter, WindowsGpuAdapter> identify)
        => (this.enumerate, this.identify) = (enumerate, identify);

    public WindowsGpuAdapterInventoryRead ReadInventory()
    {
        var observedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        var dxgi = enumerate is not null ? enumerate()
            : OperatingSystem.IsWindows() ? ReadDxgiAdapters()
            : new WindowsGpuAdapterEnumeration(SamplingObservationStatus.Unsupported, 0, []);
        if (dxgi.Status != SamplingObservationStatus.Current)
        {
            return new WindowsGpuAdapterInventoryRead(
                dxgi.Status,
                topologyGeneration.Current,
                observedAtUtcTicks,
                0,
                dxgi.SkippedCount,
                0,
                0,
                []);
        }

        // GPU n is the n-th hardware adapter of this DXGI enumeration (user ruling 2026-10-27). Software adapters
        // take no number, no other program's numbering is imitated, and an adapter dropped below for an unusable
        // LUID leaves its number unused, so no other adapter moves into it.
        var hardwareAdapters = dxgi.Adapters
            .Where(static adapter => !adapter.IsSoftware)
            .Select(static (adapter, index) => adapter with { Index = index })
            .ToArray();
        var stableAdapters = hardwareAdapters
            .GroupBy(static adapter => WindowsGpuAdapterIdentity.Pack(adapter.Luid))
            .Where(static group => group.Key != 0 && group.Count() == 1)
            .SelectMany(static group => group)
            .ToArray();
        var adapters = stableAdapters
            .Select(adapter => identify is not null ? identify(adapter) : Identify(adapter))
            .ToArray();
        var skippedCount = checked(
            dxgi.SkippedCount
            + (uint)(hardwareAdapters.Length - stableAdapters.Length));
        var topologyFingerprint =
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters);
        var currentGeneration =
            topologyGeneration.Observe(topologyFingerprint);
        return new WindowsGpuAdapterInventoryRead(
            SamplingObservationStatus.Current,
            currentGeneration,
            observedAtUtcTicks,
            checked((uint)adapters.Length),
            skippedCount,
            0,
            topologyFingerprint,
            adapters);
    }

    private WindowsGpuAdapter Identify(WindowsGpuAdapter adapter)
    {
        var identity = identityBridge.Read(
            adapter.Luid,
            adapter.VendorId,
            adapter.DeviceId,
            maximumPhysicalAdapterCount: 16);
        return adapter with
        {
            PhysicalIdentityStatus = identity.Status,
            PhysicalIdentities = identity.Identities,
            PhysicalIdentityNativeStatus = identity.NativeStatus,
            AdapterTypeFlags = identity.AdapterTypeFlags
        };
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows GPU adapter topology changed")]
    private static WindowsGpuAdapterEnumeration ReadDxgiAdapters()
    {
        var iid = IdxgiFactory1Id;
        var result = NativeMethods.CreateDXGIFactory1(ref iid, out var factory);
        if (result != 0)
        {
            return new WindowsGpuAdapterEnumeration(SamplingObservationStatus.Unavailable, 0, []);
        }

        try
        {
            var adapters = new List<WindowsGpuAdapter>();
            uint skippedCount = 0;
            for (uint index = 0; ; index++)
            {
                result = factory.EnumAdapters1(index, out var adapter);
                if (result == DxgiErrorNotFound)
                {
                    break;
                }

                if (result != 0)
                {
                    return new WindowsGpuAdapterEnumeration(
                        SamplingObservationStatus.Unavailable,
                        checked((uint)adapters.Count) + skippedCount,
                        []);
                }

                try
                {
                    if (adapter.GetDesc1(out var description) == 0)
                    {
                        adapters.Add(new WindowsGpuAdapter(
                            (int)index,
                            description.Description.TrimEnd('\0'),
                            description.VendorId,
                            description.DeviceId,
                            description.SubSysId,
                            description.AdapterLuid,
                            (description.Flags & DxgiAdapterFlagSoftware) != 0,
                            description.DedicatedVideoMemory.ToUInt64(),
                            ClassifyAdapter(
                                description.Description,
                                new WindowsGpuHardwareId(description.VendorId, description.DeviceId, description.SubSysId),
                                description.DedicatedVideoMemory.ToUInt64())));
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            return new WindowsGpuAdapterEnumeration(
                SamplingObservationStatus.Current,
                skippedCount,
                adapters);
        }
        catch
        {
            return new WindowsGpuAdapterEnumeration(SamplingObservationStatus.Unavailable, 0, []);
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    public static WindowsGpuAdapterKind ClassifyAdapter(
        string name,
        WindowsGpuHardwareId hardwareId,
        ulong dedicatedVideoMemoryBytes)
    {
        if (IsIntegratedName(name))
        {
            return WindowsGpuAdapterKind.Integrated;
        }

        if (IsDedicatedName(name))
        {
            return WindowsGpuAdapterKind.Dedicated;
        }

        if (dedicatedVideoMemoryBytes >= IntegratedDedicatedMemoryThresholdBytes)
        {
            return WindowsGpuAdapterKind.Dedicated;
        }

        if (hardwareId.VendorId is 0x8086 or 0x1002 && dedicatedVideoMemoryBytes < IntegratedDedicatedMemoryThresholdBytes)
        {
            return WindowsGpuAdapterKind.Integrated;
        }

        return WindowsGpuAdapterKind.Unknown;
    }

    private static bool IsIntegratedName(string name)
    {
        return GpuAdapterNameClassifier.IsLikelyIntegratedGpuName(name)
            || name.Contains("Radeon(TM)", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AMD Radeon 7", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intel(R) UHD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intel(R) HD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intel(R) Iris", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDedicatedName(string name)
    {
        return name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc A", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc(TM) A", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc B", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc(TM) B", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc Pro", StringComparison.OrdinalIgnoreCase);
    }

    private static class NativeMethods
    {
        [DllImport("dxgi.dll")]
        internal static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);
    }

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig]
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

        [PreserveSig]
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);

        [PreserveSig]
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);

        [PreserveSig]
        int GetParent(ref Guid riid, out IntPtr parent);

        [PreserveSig]
        int EnumAdapters(uint adapter, out IntPtr adapterPointer);

        [PreserveSig]
        int MakeWindowAssociation(IntPtr windowHandle, uint flags);

        [PreserveSig]
        int GetWindowAssociation(out IntPtr windowHandle);

        [PreserveSig]
        int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);

        [PreserveSig]
        int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);

        [PreserveSig]
        int EnumAdapters1(uint adapter, out IDXGIAdapter1 adapterPointer);

        [PreserveSig]
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig]
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

        [PreserveSig]
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);

        [PreserveSig]
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);

        [PreserveSig]
        int GetParent(ref Guid riid, out IntPtr parent);

        [PreserveSig]
        int EnumOutputs(uint output, out IntPtr outputPointer);

        [PreserveSig]
        int GetDesc(out DxgiAdapterDesc description);

        [PreserveSig]
        int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);

        [PreserveSig]
        int GetDesc1(out DxgiAdapterDesc1 description);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public AdapterLuid AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public AdapterLuid AdapterLuid;
        public uint Flags;
    }
}

public sealed class WindowsGpuTopologyGenerationTracker
{
    private readonly object gate = new();
    private ulong generation;
    private ulong topologyFingerprint;
    private bool hasTopology;

    public ulong Current
    {
        get
        {
            lock (gate)
            {
                return generation;
            }
        }
    }

    public ulong Observe(ulong fingerprint)
    {
        if (fingerprint == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprint));
        }

        lock (gate)
        {
            if (!hasTopology || topologyFingerprint != fingerprint)
            {
                generation = checked(generation + 1);
                topologyFingerprint = fingerprint;
                hasTopology = true;
            }
            return generation;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct AdapterLuid
{
    public uint LowPart;
    public int HighPart;
}

public sealed record WindowsGpuAdapter(
    int Index,
    string Name,
    uint VendorId,
    uint DeviceId,
    uint SubSystemId,
    AdapterLuid Luid,
    bool IsSoftware,
    ulong DedicatedVideoMemoryBytes,
    WindowsGpuAdapterKind Kind)
{
    // Raw D3DKMT_ADAPTERTYPE.Value. Null means the independent query was unavailable.
    public uint? AdapterTypeFlags { get; init; }
    public bool? IsDetachable => AdapterTypeFlags is { } flags ? (flags & (1u << 10)) != 0 : null;
    public string? AttachmentKind => IsDetachable == true ? "graphics_dock" : null;
    public string? AttachmentEvidenceSource => AdapterTypeFlags.HasValue ? "d3dkmt-adapter-type" : null;

    public SamplingObservationStatus PhysicalIdentityStatus { get; init; } =
        SamplingObservationStatus.Unavailable;

    public IReadOnlyList<WindowsGpuPhysicalIdentity> PhysicalIdentities
    {
        get;
        init;
    } = [];

    public int PhysicalIdentityNativeStatus { get; init; }

    public bool SupportsDedicatedMemoryMetrics => Kind == WindowsGpuAdapterKind.Dedicated;
}

public readonly record struct WindowsGpuHardwareId(uint VendorId, uint DeviceId, uint SubSystemId);

public enum WindowsGpuAdapterKind
{
    Unknown,
    Integrated,
    Dedicated
}

public sealed record WindowsGpuAdapterInventoryRead(
    SamplingObservationStatus Status,
    ulong Generation,
    long ObservedAtUtcTicks,
    uint ObservedCount,
    uint SkippedCount,
    uint OverflowCount,
    ulong TopologyFingerprint,
    IReadOnlyList<WindowsGpuAdapter> Adapters);

public sealed record WindowsGpuAdapterEnumeration(
    SamplingObservationStatus Status,
    uint SkippedCount,
    IReadOnlyList<WindowsGpuAdapter> Adapters);
