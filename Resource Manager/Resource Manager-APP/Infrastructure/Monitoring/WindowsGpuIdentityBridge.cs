using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class WindowsGpuIdentityBridge
{
    internal WindowsGpuIdentityRead Read(
        AdapterLuid luid,
        uint expectedVendorId,
        uint expectedDeviceId,
        uint maximumPhysicalAdapterCount)
    {
        if ((luid.HighPart == 0 && luid.LowPart == 0)
            || maximumPhysicalAdapterCount == 0)
        {
            return WindowsGpuIdentityRead.Invalid;
        }

        var open = new NativeMethods.OpenAdapterFromLuid
        {
            AdapterLuid = luid
        };
        var openStatus = NativeMethods.OpenAdapter(ref open);
        if (openStatus != NativeMethods.StatusSuccess
            || open.AdapterHandle == 0)
        {
            return WindowsGpuIdentityRead.Unavailable(openStatus);
        }

        try
        {
            var count = new NativeMethods.PhysicalAdapterCount();
            var countStatus = NativeMethods.Query(
                open.AdapterHandle,
                NativeMethods.QueryPhysicalAdapterCount,
                ref count);
            if (countStatus != NativeMethods.StatusSuccess
                || count.Count == 0
                || count.Count > maximumPhysicalAdapterCount)
            {
                return WindowsGpuIdentityRead.Unavailable(countStatus);
            }

            var logicalAddress = new NativeMethods.AdapterAddress();
            var logicalAddressStatus = count.Count == 1
                ? NativeMethods.Query(
                    open.AdapterHandle,
                    NativeMethods.QueryAdapterAddress,
                    ref logicalAddress)
                : NativeMethods.StatusNotSupported;
            var rows = new List<WindowsGpuPhysicalIdentity>(
                checked((int)count.Count));
            for (uint physicalIndex = 0;
                 physicalIndex < count.Count;
                 physicalIndex++)
            {
                var row = ReadPhysical(
                    open.AdapterHandle,
                    physicalIndex,
                    expectedVendorId,
                    expectedDeviceId,
                    count.Count == 1
                        && logicalAddressStatus == NativeMethods.StatusSuccess
                            ? logicalAddress
                            : null);
                if (row.Status is WindowsGpuPhysicalIdentityStatus.Unavailable
                    or WindowsGpuPhysicalIdentityStatus.Conflict
                    or WindowsGpuPhysicalIdentityStatus.Removed)
                {
                    return new WindowsGpuIdentityRead(
                        SamplingObservationStatus.Unavailable,
                        row.NativeStatus,
                        [],
                        1);
                }
                rows.Add(row);
            }

            return new WindowsGpuIdentityRead(
                SamplingObservationStatus.Current,
                NativeMethods.StatusSuccess,
                rows,
                0);
        }
        finally
        {
            var close = new NativeMethods.CloseAdapter
            {
                AdapterHandle = open.AdapterHandle
            };
            _ = NativeMethods.CloseAdapterHandle(ref close);
        }
    }

    private static WindowsGpuPhysicalIdentity ReadPhysical(
        uint adapterHandle,
        uint physicalIndex,
        uint expectedVendorId,
        uint expectedDeviceId,
        NativeMethods.AdapterAddress? logicalAddress)
    {
        var ids = new NativeMethods.QueryDeviceIds
        {
            PhysicalAdapterIndex = physicalIndex
        };
        var status = NativeMethods.Query(
            adapterHandle,
            NativeMethods.QueryPhysicalAdapterDeviceIds,
            ref ids);
        if (status != NativeMethods.StatusSuccess)
        {
            return WindowsGpuPhysicalIdentity.Unavailable(
                physicalIndex,
                status);
        }
        if ((expectedVendorId != 0 && ids.VendorId != expectedVendorId)
            || (expectedDeviceId != 0 && ids.DeviceId != expectedDeviceId))
        {
            return WindowsGpuPhysicalIdentity.Conflict(
                physicalIndex,
                status);
        }

        var pnpRead = ReadHardwarePnpKey(adapterHandle, physicalIndex);
        if (pnpRead.Status != NativeMethods.StatusSuccess
            || string.IsNullOrEmpty(pnpRead.InstanceId))
        {
            return WindowsGpuPhysicalIdentity.Unavailable(
                physicalIndex,
                pnpRead.Status);
        }
        var instanceId = pnpRead.InstanceId;
        var locateStatus = NativeMethods.LocateDevNode(
            out var devInst,
            instanceId,
            0);
        if (locateStatus == NativeMethods.CrNoSuchDevNode)
        {
            return WindowsGpuPhysicalIdentity.Removed(
                physicalIndex,
                unchecked((int)locateStatus));
        }
        if (locateStatus != NativeMethods.CrSuccess)
        {
            return WindowsGpuPhysicalIdentity.Unavailable(
                physicalIndex,
                unchecked((int)locateStatus));
        }

        var canonicalRead = ReadCanonicalInstanceId(devInst);
        if (canonicalRead.Status != NativeMethods.CrSuccess
            || !string.Equals(
                instanceId,
                canonicalRead.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            return WindowsGpuPhysicalIdentity.Conflict(
                physicalIndex,
                unchecked((int)canonicalRead.Status));
        }

        var kind = ClassifyPnp(instanceId);
        var evidence = WindowsGpuIdentityEvidence.KmtLuidOpen
            | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
            | WindowsGpuIdentityEvidence.CmExactDevNode
            | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip
            | WindowsGpuIdentityEvidence.KmtDeviceIds;
        var pciAddressValid = false;
        uint pciBus = 0;
        uint pciDevice = 0;
        uint pciFunction = 0;
        if (kind == WindowsGpuPnpKind.Pci)
        {
            var bus = ReadUint32Property(
                devInst,
                NativeMethods.DeviceBusNumber);
            var address = ReadUint32Property(
                devInst,
                NativeMethods.DeviceAddress);
            if (bus.Status == NativeMethods.CrSuccess
                && address.Status == NativeMethods.CrSuccess)
            {
                pciAddressValid = true;
                pciBus = bus.Value;
                pciDevice = address.Value >> 16;
                pciFunction = address.Value & 0xFFFFU;
                evidence |= WindowsGpuIdentityEvidence.CmPciAddress;
                if (logicalAddress is { } logical)
                {
                    evidence |= WindowsGpuIdentityEvidence.KmtAddress;
                    if (logical.BusNumber != pciBus
                        || logical.DeviceNumber != pciDevice
                        || logical.FunctionNumber != pciFunction)
                    {
                        return WindowsGpuPhysicalIdentity.Conflict(
                            physicalIndex,
                            NativeMethods.StatusConflict);
                    }
                    evidence |= WindowsGpuIdentityEvidence.AddressCrossCheck;
                }
            }
            else if (bus.Status != NativeMethods.CrNoSuchValue
                || address.Status != NativeMethods.CrNoSuchValue)
            {
                return WindowsGpuPhysicalIdentity.Unavailable(
                    physicalIndex,
                    unchecked((int)(
                        bus.Status != NativeMethods.CrSuccess
                            ? bus.Status
                            : address.Status)));
            }
        }

        return new WindowsGpuPhysicalIdentity(
            physicalIndex,
            instanceId,
            kind,
            pciAddressValid,
            PciSegmentValid: false,
            PciSegment: 0,
            pciBus,
            pciDevice,
            pciFunction,
            ids.VendorId,
            ids.DeviceId,
            ids.SubVendorId,
            ids.SubsystemId,
            ids.RevisionId,
            evidence,
            kind == WindowsGpuPnpKind.Pci
                ? WindowsGpuPhysicalIdentityStatus.Bound
                : WindowsGpuPhysicalIdentityStatus.NonPci,
            NativeMethods.StatusSuccess);
    }

    internal static bool TryExtractHardwarePnpInstance(
        string value,
        out string instanceId)
    {
        const string enumBoundary = @"\Enum\";
        const string suffix = @"\Device Parameters";
        var start = value.IndexOf(
            enumBoundary,
            StringComparison.OrdinalIgnoreCase);
        if (start < 0
            || !value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            instanceId = string.Empty;
            return false;
        }
        start += enumBoundary.Length;
        var length = value.Length - suffix.Length - start;
        if (length <= 0)
        {
            instanceId = string.Empty;
            return false;
        }

        var candidate = value.Substring(start, length);
        if (candidate.Length > MaximumPnpInstanceCharacterCount
            || candidate[0] == '\\'
            || candidate[^1] == '\\'
            || candidate.Contains(@"\\", StringComparison.Ordinal)
            || candidate.Any(static character =>
                character == '\0' || char.IsControl(character)))
        {
            instanceId = string.Empty;
            return false;
        }

        instanceId = candidate;
        return true;
    }

    private static unsafe PnpKeyRead ReadHardwarePnpKey(
        uint adapterHandle,
        uint physicalIndex)
    {
        uint characterCount = 0;
        var query = new NativeMethods.QueryPhysicalAdapterPnpKey
        {
            PhysicalAdapterIndex = physicalIndex,
            PnpKeyType = NativeMethods.PnpKeyHardware,
            Destination = null,
            DestinationCharacterCount = &characterCount
        };
        var status = NativeMethods.Query(
            adapterHandle,
            NativeMethods.QueryPhysicalAdapterPnpKeyType,
            ref query);
        if (status is not (
                NativeMethods.StatusSuccess
                or NativeMethods.StatusBufferOverflow
                or NativeMethods.StatusBufferTooSmall)
            || characterCount is 0
                or > MaximumHardwarePnpKeyCharacterCount)
        {
            return new PnpKeyRead(status, string.Empty);
        }

        var buffer = new char[checked((int)characterCount)];
        fixed (char* destination = buffer)
        {
            query.Destination = destination;
            query.DestinationCharacterCount = &characterCount;
            status = NativeMethods.Query(
                adapterHandle,
                NativeMethods.QueryPhysicalAdapterPnpKeyType,
                ref query);
        }
        if (status != NativeMethods.StatusSuccess
            || characterCount == 0
            || characterCount > buffer.Length
            || buffer[checked((int)characterCount - 1)] != '\0')
        {
            return new PnpKeyRead(status, string.Empty);
        }

        var key = new string(buffer, 0, checked((int)characterCount - 1));
        return TryExtractHardwarePnpInstance(key, out var instanceId)
            ? new PnpKeyRead(status, instanceId)
            : new PnpKeyRead(NativeMethods.StatusInvalidParameter, string.Empty);
    }

    private static CanonicalDeviceRead ReadCanonicalInstanceId(uint devInst)
    {
        var status = NativeMethods.GetDeviceIdSize(
            out var characterCount,
            devInst,
            0);
        if (status != NativeMethods.CrSuccess
            || characterCount is 0 or > MaximumPnpInstanceCharacterCount)
        {
            return new CanonicalDeviceRead(status, string.Empty);
        }
        var buffer = new StringBuilder(checked((int)characterCount + 1));
        status = NativeMethods.GetDeviceId(
            devInst,
            buffer,
            checked(characterCount + 1),
            0);
        return status == NativeMethods.CrSuccess
            ? new CanonicalDeviceRead(status, buffer.ToString())
            : new CanonicalDeviceRead(status, string.Empty);
    }

    private static PropertyRead ReadUint32Property(
        uint devInst,
        NativeMethods.DevPropKey key)
    {
        var value = new byte[sizeof(uint)];
        uint size = sizeof(uint);
        var status = NativeMethods.GetDevNodeProperty(
            devInst,
            in key,
            out var propertyType,
            value,
            ref size,
            0);
        if (status != NativeMethods.CrSuccess
            || propertyType != NativeMethods.DevPropTypeUint32
            || size != sizeof(uint))
        {
            return new PropertyRead(status, 0);
        }
        return new PropertyRead(
            status,
            BitConverter.ToUInt32(value));
    }

    private static WindowsGpuPnpKind ClassifyPnp(string instanceId)
        => instanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
            ? WindowsGpuPnpKind.Pci
            : instanceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
                ? WindowsGpuPnpKind.Root
                : instanceId.StartsWith(
                    @"VMBUS\",
                    StringComparison.OrdinalIgnoreCase)
                    ? WindowsGpuPnpKind.Virtual
                    : WindowsGpuPnpKind.Other;

    private readonly record struct PnpKeyRead(int Status, string InstanceId);

    private readonly record struct CanonicalDeviceRead(
        uint Status,
        string Value);

    private readonly record struct PropertyRead(uint Status, uint Value);

    internal static unsafe class NativeMethods
    {
        internal const int StatusSuccess = 0;
        internal const int StatusBufferOverflow = unchecked((int)0x80000005);
        internal const int StatusInvalidParameter = unchecked((int)0xC000000D);
        internal const int StatusBufferTooSmall = unchecked((int)0xC0000023);
        internal const int StatusNotSupported = unchecked((int)0xC00000BB);
        internal const int StatusConflict = unchecked((int)0xC01E0009);
        internal const uint CrSuccess = 0;
        internal const uint CrNoSuchDevNode = 13;
        internal const uint CrNoSuchValue = 37;
        internal const uint DevPropTypeUint32 = 7;
        internal const uint QueryAdapterAddress = 6;
        internal const uint QueryPhysicalAdapterCount = 30;
        internal const uint QueryPhysicalAdapterDeviceIds = 31;
        internal const uint QueryPhysicalAdapterPnpKeyType = 41;
        internal const uint PnpKeyHardware = 1;
        private static readonly Guid DevicePropertyGuid =
            new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        internal static readonly DevPropKey DeviceBusNumber =
            new(DevicePropertyGuid, 23);
        internal static readonly DevPropKey DeviceAddress =
            new(DevicePropertyGuid, 30);

        [DllImport(
            "gdi32.dll",
            EntryPoint = "D3DKMTOpenAdapterFromLuid")]
        internal static extern int OpenAdapter(ref OpenAdapterFromLuid data);

        [DllImport(
            "gdi32.dll",
            EntryPoint = "D3DKMTQueryAdapterInfo")]
        private static extern int QueryAdapter(ref QueryAdapterInfo data);

        [DllImport(
            "gdi32.dll",
            EntryPoint = "D3DKMTCloseAdapter")]
        internal static extern int CloseAdapterHandle(ref CloseAdapter data);

        [DllImport(
            "cfgmgr32.dll",
            EntryPoint = "CM_Locate_DevNodeW",
            CharSet = CharSet.Unicode)]
        internal static extern uint LocateDevNode(
            out uint devInst,
            string deviceInstanceId,
            uint flags);

        [DllImport(
            "cfgmgr32.dll",
            EntryPoint = "CM_Get_Device_ID_Size")]
        internal static extern uint GetDeviceIdSize(
            out uint characterCount,
            uint devInst,
            uint flags);

        [DllImport(
            "cfgmgr32.dll",
            EntryPoint = "CM_Get_Device_IDW",
            CharSet = CharSet.Unicode)]
        internal static extern uint GetDeviceId(
            uint devInst,
            StringBuilder buffer,
            uint bufferLength,
            uint flags);

        [DllImport(
            "cfgmgr32.dll",
            EntryPoint = "CM_Get_DevNode_PropertyW")]
        internal static extern uint GetDevNodeProperty(
            uint devInst,
            in DevPropKey propertyKey,
            out uint propertyType,
            [Out] byte[] propertyBuffer,
            ref uint propertyBufferSize,
            uint flags);

        internal static int Query<T>(
            uint adapterHandle,
            uint type,
            ref T payload)
            where T : unmanaged
        {
            fixed (T* payloadPointer = &payload)
            {
                var query = new QueryAdapterInfo
                {
                    AdapterHandle = adapterHandle,
                    Type = type,
                    PrivateDriverData = payloadPointer,
                    PrivateDriverDataSize = checked((uint)sizeof(T))
                };
                return QueryAdapter(ref query);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OpenAdapterFromLuid
        {
            internal AdapterLuid AdapterLuid;
            internal uint AdapterHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct QueryAdapterInfo
        {
            internal uint AdapterHandle;
            internal uint Type;
            internal void* PrivateDriverData;
            internal uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CloseAdapter
        {
            internal uint AdapterHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AdapterAddress
        {
            internal uint BusNumber;
            internal uint DeviceNumber;
            internal uint FunctionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PhysicalAdapterCount
        {
            internal uint Count;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct QueryDeviceIds
        {
            internal uint PhysicalAdapterIndex;
            internal uint VendorId;
            internal uint DeviceId;
            internal uint SubVendorId;
            internal uint SubsystemId;
            internal uint RevisionId;
            internal uint BusType;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct QueryPhysicalAdapterPnpKey
        {
            internal uint PhysicalAdapterIndex;
            internal uint PnpKeyType;
            internal char* Destination;
            internal uint* DestinationCharacterCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct DevPropKey(
            Guid FormatId,
            uint PropertyId);
    }

    private const uint MaximumHardwarePnpKeyCharacterCount = 2048;
    private const uint MaximumPnpInstanceCharacterCount = 512;
}

internal sealed record WindowsGpuIdentityRead(
    SamplingObservationStatus Status,
    int NativeStatus,
    IReadOnlyList<WindowsGpuPhysicalIdentity> Identities,
    uint SkippedCount)
{
    internal static WindowsGpuIdentityRead Invalid { get; } =
        new(SamplingObservationStatus.Unavailable, -1, [], 1);

    internal static WindowsGpuIdentityRead Unavailable(int nativeStatus)
        => new(
            SamplingObservationStatus.Unavailable,
            nativeStatus,
            [],
            1);
}

internal sealed record WindowsGpuPhysicalIdentity(
    uint PhysicalAdapterIndex,
    string PnpInstanceId,
    WindowsGpuPnpKind PnpKind,
    bool PciAddressValid,
    bool PciSegmentValid,
    uint PciSegment,
    uint PciBus,
    uint PciDevice,
    uint PciFunction,
    uint VendorId,
    uint DeviceId,
    uint SubVendorId,
    uint SubsystemId,
    uint RevisionId,
    WindowsGpuIdentityEvidence Evidence,
    WindowsGpuPhysicalIdentityStatus Status,
    int NativeStatus)
{
    internal static WindowsGpuPhysicalIdentity Unavailable(
        uint physicalAdapterIndex,
        int nativeStatus)
        => Empty(
            physicalAdapterIndex,
            WindowsGpuPhysicalIdentityStatus.Unavailable,
            nativeStatus);

    internal static WindowsGpuPhysicalIdentity Conflict(
        uint physicalAdapterIndex,
        int nativeStatus)
        => Empty(
            physicalAdapterIndex,
            WindowsGpuPhysicalIdentityStatus.Conflict,
            nativeStatus);

    internal static WindowsGpuPhysicalIdentity Removed(
        uint physicalAdapterIndex,
        int nativeStatus)
        => Empty(
            physicalAdapterIndex,
            WindowsGpuPhysicalIdentityStatus.Removed,
            nativeStatus);

    private static WindowsGpuPhysicalIdentity Empty(
        uint physicalAdapterIndex,
        WindowsGpuPhysicalIdentityStatus status,
        int nativeStatus)
        => new(
            physicalAdapterIndex,
            string.Empty,
            WindowsGpuPnpKind.Other,
            false,
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            WindowsGpuIdentityEvidence.None,
            status,
            nativeStatus);
}

[Flags]
internal enum WindowsGpuIdentityEvidence : uint
{
    None = 0,
    KmtLuidOpen = 1U << 0,
    KmtHardwarePnpKey = 1U << 1,
    CmExactDevNode = 1U << 2,
    CmCanonicalInstanceRoundTrip = 1U << 3,
    KmtDeviceIds = 1U << 4,
    KmtAddress = 1U << 5,
    CmPciAddress = 1U << 6,
    AddressCrossCheck = 1U << 7
}

internal enum WindowsGpuPnpKind : uint
{
    Pci = 1,
    Root = 2,
    Virtual = 3,
    Other = 4
}

internal enum WindowsGpuPhysicalIdentityStatus : uint
{
    Bound = 1,
    NonPci = 2,
    Unavailable = 3,
    Conflict = 4,
    Removed = 5
}
