using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class WindowsGpuAdapterOrderReader
{
    private const ulong IntegratedDedicatedMemoryThresholdBytes = 1024UL * 1024UL * 1024UL;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;
    private const uint MaximumKmtAdapterCount = 128;
    private static readonly Guid IdxgiFactory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private readonly WindowsGpuIdentityBridge identityBridge = new();
    private readonly WindowsGpuTopologyGenerationTracker topologyGeneration =
        new();

    public WindowsGpuAdapterInventoryRead ReadInventory()
    {
        var observedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        var dxgi = ReadDxgiAdapters();
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

        var hardwareAdapters = dxgi.Adapters
            .Where(static adapter => !adapter.IsSoftware)
            .ToArray();
        var stableAdapters = hardwareAdapters
            .GroupBy(static adapter => NativePdhAdapterIdentity.Pack(adapter.Luid))
            .Where(static group => group.Key != 0 && group.Count() == 1)
            .SelectMany(static group => group)
            .ToArray();
        var publicAdapters = ApplyPublicAdapterOrder(
            stableAdapters,
            ReadTaskManagerAdapterOrder());
        var adapters = publicAdapters
            .Select(adapter =>
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
                    PhysicalIdentityNativeStatus = identity.NativeStatus
                };
            })
            .ToArray();
        var skippedCount = checked(
            dxgi.SkippedCount
            + (uint)(hardwareAdapters.Length - stableAdapters.Length));
        var topologyFingerprint =
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(adapters);
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

    internal static IReadOnlyList<WindowsGpuAdapter> ApplyPublicAdapterOrder(
        IReadOnlyList<WindowsGpuAdapter> adapters,
        IReadOnlyDictionary<ulong, int>? taskManagerOrder)
    {
        if (taskManagerOrder is null
            || taskManagerOrder.Count == 0
            || adapters.Any(adapter =>
                !taskManagerOrder.ContainsKey(NativePdhAdapterIdentity.Pack(adapter.Luid))))
        {
            return adapters
                .Select((adapter, index) => adapter with
                {
                    Index = index,
                    PublicIndexSource = WindowsGpuPublicIndexSource.DxgiFallback
                })
                .ToArray();
        }

        var ordered = adapters
            .Select(adapter => adapter with
            {
                Index = taskManagerOrder[NativePdhAdapterIdentity.Pack(adapter.Luid)],
                PublicIndexSource = WindowsGpuPublicIndexSource.TaskManagerKmt
            })
            .OrderBy(static adapter => adapter.Index)
            .ToArray();
        return ordered.Select(static adapter => adapter.Index).Distinct().Count() == ordered.Length
            ? ordered
            : adapters
                .Select((adapter, index) => adapter with
                {
                    Index = index,
                    PublicIndexSource = WindowsGpuPublicIndexSource.DxgiFallback
                })
                .ToArray();
    }

    private static IReadOnlyDictionary<ulong, int>? ReadTaskManagerAdapterOrder()
    {
        try
        {
            return ReadTaskManagerAdapterOrderCore();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<ulong, int>? ReadTaskManagerAdapterOrderCore()
    {
        var enumeration = new D3dkmtEnumAdapters2();
        var status = NativeMethods.EnumAdapters2(ref enumeration);
        if (status != 0
            || enumeration.NumberOfAdapters == 0
            || enumeration.NumberOfAdapters > MaximumKmtAdapterCount)
        {
            return null;
        }

        var adapterSize = Marshal.SizeOf<D3dkmtAdapterInfo>();
        var capacity = enumeration.NumberOfAdapters;
        var byteCount = checked((int)capacity * adapterSize);
        var buffer = Marshal.AllocHGlobal(byteCount);
        var handles = new uint[capacity];
        try
        {
            Marshal.Copy(new byte[byteCount], 0, buffer, byteCount);
            enumeration.AdapterPointer = buffer;
            status = NativeMethods.EnumAdapters2(ref enumeration);
            if (status != 0 || enumeration.NumberOfAdapters > capacity)
            {
                return null;
            }

            var order = new Dictionary<ulong, int>(checked((int)enumeration.NumberOfAdapters));
            for (var index = 0; index < enumeration.NumberOfAdapters; index++)
            {
                var adapter = Marshal.PtrToStructure<D3dkmtAdapterInfo>(
                    IntPtr.Add(buffer, checked((int)index * adapterSize)));
                handles[index] = adapter.AdapterHandle;
                var luid = NativePdhAdapterIdentity.Pack(adapter.AdapterLuid);
                if (luid == 0 || !order.TryAdd(luid, checked((int)index)))
                {
                    return null;
                }
            }
            return order;
        }
        finally
        {
            foreach (var handle in handles)
            {
                if (handle == 0)
                {
                    continue;
                }

                var close = new D3dkmtCloseAdapter { AdapterHandle = handle };
                _ = NativeMethods.CloseAdapter(ref close);
            }
            Marshal.FreeHGlobal(buffer);
        }
    }

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

    private static WindowsGpuAdapterKind ClassifyAdapter(
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
        return name.Contains("Radeon(TM)", StringComparison.OrdinalIgnoreCase)
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
            || name.Contains("Intel(R) Arc", StringComparison.OrdinalIgnoreCase);
    }

    private static class NativeMethods
    {
        [DllImport("dxgi.dll")]
        internal static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

        [DllImport("gdi32.dll", EntryPoint = "D3DKMTEnumAdapters2")]
        internal static extern int EnumAdapters2(ref D3dkmtEnumAdapters2 enumeration);

        [DllImport("gdi32.dll", EntryPoint = "D3DKMTCloseAdapter")]
        internal static extern int CloseAdapter(ref D3dkmtCloseAdapter close);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtEnumAdapters2
    {
        public uint NumberOfAdapters;
        public IntPtr AdapterPointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtAdapterInfo
    {
        public uint AdapterHandle;
        public AdapterLuid AdapterLuid;
        public uint NumberOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtCloseAdapter
    {
        public uint AdapterHandle;
    }
}

internal sealed class WindowsGpuTopologyGenerationTracker
{
    private readonly object gate = new();
    private ulong generation;
    private ulong topologyFingerprint;
    private bool hasTopology;

    internal ulong Current
    {
        get
        {
            lock (gate)
            {
                return generation;
            }
        }
    }

    internal ulong Observe(ulong fingerprint)
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

internal enum WindowsGpuPublicIndexSource
{
    DxgiFallback,
    TaskManagerKmt
}

[StructLayout(LayoutKind.Sequential)]
internal struct AdapterLuid
{
    public uint LowPart;
    public int HighPart;
}

internal sealed record WindowsGpuAdapter(
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
    internal WindowsGpuPublicIndexSource PublicIndexSource { get; init; } =
        WindowsGpuPublicIndexSource.DxgiFallback;

    internal SamplingObservationStatus PhysicalIdentityStatus { get; init; } =
        SamplingObservationStatus.Unavailable;

    internal IReadOnlyList<WindowsGpuPhysicalIdentity> PhysicalIdentities
    {
        get;
        init;
    } = [];

    internal int PhysicalIdentityNativeStatus { get; init; }

    public bool SupportsDedicatedMemoryMetrics => Kind == WindowsGpuAdapterKind.Dedicated;
}

internal readonly record struct WindowsGpuHardwareId(uint VendorId, uint DeviceId, uint SubSystemId);

internal enum WindowsGpuAdapterKind
{
    Unknown,
    Integrated,
    Dedicated
}

internal sealed record WindowsGpuAdapterInventoryRead(
    SamplingObservationStatus Status,
    ulong Generation,
    long ObservedAtUtcTicks,
    uint ObservedCount,
    uint SkippedCount,
    uint OverflowCount,
    ulong TopologyFingerprint,
    IReadOnlyList<WindowsGpuAdapter> Adapters);

internal sealed record WindowsGpuAdapterEnumeration(
    SamplingObservationStatus Status,
    uint SkippedCount,
    IReadOnlyList<WindowsGpuAdapter> Adapters);
