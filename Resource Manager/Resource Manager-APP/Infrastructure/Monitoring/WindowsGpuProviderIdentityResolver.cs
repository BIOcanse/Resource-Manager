using System.Globalization;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class WindowsGpuProviderIdentityResolver
{
    internal static WindowsGpuProviderIdentityMatch ResolvePnp(
        WindowsGpuAdapterInventoryRead inventory,
        string providerPnpInstanceId)
    {
        if (!IsComplete(inventory)
            || !TryCanonicalizePnpInstanceId(
                providerPnpInstanceId,
                out var canonicalProviderPnp))
        {
            return WindowsGpuProviderIdentityMatch.Unavailable;
        }

        var matches = new List<WindowsGpuProviderIdentityBinding>(1);
        foreach (var adapter in inventory.Adapters)
        {
            if (adapter.PhysicalIdentityStatus != SamplingObservationStatus.Current)
            {
                return WindowsGpuProviderIdentityMatch.Unavailable;
            }

            foreach (var physical in adapter.PhysicalIdentities)
            {
                if (physical.Status is not (
                        WindowsGpuPhysicalIdentityStatus.Bound
                        or WindowsGpuPhysicalIdentityStatus.NonPci)
                    || !TryCanonicalizePnpInstanceId(
                        physical.PnpInstanceId,
                        out var canonicalWindowsPnp)
                    || !string.Equals(
                        canonicalProviderPnp,
                        canonicalWindowsPnp,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                matches.Add(new WindowsGpuProviderIdentityBinding(
                    adapter,
                    physical));
            }
        }

        return CreateMatch(matches);
    }

    internal static WindowsGpuProviderIdentityMatch ResolvePci(
        WindowsGpuAdapterInventoryRead inventory,
        WindowsGpuProviderPciEvidence evidence)
    {
        if (!IsComplete(inventory)
            || !evidence.IsValid)
        {
            return WindowsGpuProviderIdentityMatch.Unavailable;
        }

        var matches = new List<WindowsGpuProviderIdentityBinding>(1);
        foreach (var adapter in inventory.Adapters)
        {
            if (adapter.PhysicalIdentityStatus != SamplingObservationStatus.Current)
            {
                return WindowsGpuProviderIdentityMatch.Unavailable;
            }

            foreach (var physical in adapter.PhysicalIdentities)
            {
                if (physical.Status != WindowsGpuPhysicalIdentityStatus.Bound
                    || !physical.PciAddressValid
                    || physical.PciBus != evidence.Bus
                    || physical.PciDevice != evidence.Device
                    || (evidence.FunctionValid
                        && physical.PciFunction != evidence.Function)
                    || (evidence.SegmentValid
                        && physical.PciSegmentValid
                        && physical.PciSegment != evidence.Segment)
                    || physical.VendorId != evidence.VendorId
                    || physical.DeviceId != evidence.DeviceId
                    || (evidence.SubSystemIdValid
                        && adapter.SubSystemId != evidence.SubSystemId))
                {
                    continue;
                }

                matches.Add(new WindowsGpuProviderIdentityBinding(
                    adapter,
                    physical));
            }
        }

        return CreateMatch(matches);
    }

    internal static bool TryParseNvmlPciEvidence(
        NvmlPciInfo pci,
        out WindowsGpuProviderPciEvidence evidence)
    {
        evidence = default;
        if (!TryParsePciAddress(
                pci.BusId,
                out var segment,
                out var bus,
                out var device,
                out var function)
            || pci.Domain != segment
            || pci.Bus != bus
            || pci.Device != device
            || !TrySplitCombinedVendorDevice(
                pci.PciDeviceId,
                expectedVendorId: 0x10DE,
                out var vendorId,
                out var deviceId))
        {
            return false;
        }

        evidence = new WindowsGpuProviderPciEvidence(
            SegmentValid: true,
            segment,
            bus,
            device,
            FunctionValid: true,
            function,
            vendorId,
            deviceId,
            SubSystemIdValid: pci.PciSubSystemId != 0,
            pci.PciSubSystemId);
        return evidence.IsValid;
    }

    internal static bool TryCreateNvapiPciEvidence(
        uint bus,
        uint slot,
        uint combinedDeviceId,
        uint subSystemId,
        out WindowsGpuProviderPciEvidence evidence)
    {
        evidence = default;
        if (bus > byte.MaxValue
            || slot > 31
            || !TrySplitCombinedVendorDevice(
                combinedDeviceId,
                expectedVendorId: 0x10DE,
                out var vendorId,
                out var deviceId))
        {
            return false;
        }

        evidence = new WindowsGpuProviderPciEvidence(
            SegmentValid: false,
            Segment: 0,
            bus,
            Device: slot,
            FunctionValid: false,
            Function: 0,
            vendorId,
            deviceId,
            SubSystemIdValid: subSystemId != 0,
            subSystemId);
        return evidence.IsValid;
    }

    internal static bool TryCanonicalizePnpInstanceId(
        string value,
        out string canonical)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOf('\0') >= 0)
        {
            canonical = string.Empty;
            return false;
        }

        var candidate = value.Trim().Replace('/', '\\');
        if (candidate.Length is 0 or > 512
            || candidate.StartsWith('\\')
            || candidate.EndsWith('\\')
            || candidate.Contains(@"\\", StringComparison.Ordinal)
            || candidate.Count(static character => character == '\\') < 2)
        {
            canonical = string.Empty;
            return false;
        }

        canonical = candidate.ToUpperInvariant();
        return true;
    }

    private static WindowsGpuProviderIdentityMatch CreateMatch(
        IReadOnlyList<WindowsGpuProviderIdentityBinding> matches)
    {
        return matches.Count switch
        {
            1 => new WindowsGpuProviderIdentityMatch(
                WindowsGpuProviderIdentityMatchStatus.Current,
                matches[0]),
            > 1 => WindowsGpuProviderIdentityMatch.Conflict,
            _ => WindowsGpuProviderIdentityMatch.Unavailable
        };
    }

    private static bool IsComplete(
        WindowsGpuAdapterInventoryRead inventory)
    {
        if (inventory.Status != SamplingObservationStatus.Current
            || inventory.Generation == 0
            || inventory.ObservedAtUtcTicks <= 0
            || inventory.SkippedCount != 0
            || inventory.OverflowCount != 0
            || inventory.ObservedCount != inventory.Adapters.Count)
        {
            return false;
        }

        var indexes = new HashSet<int>();
        var luids = new HashSet<ulong>();
        foreach (var adapter in inventory.Adapters)
        {
            var luid = Pack(adapter.Luid);
            if (adapter.Index < 0
                || luid == 0
                || adapter.IsSoftware
                || adapter.PhysicalIdentityStatus
                    != SamplingObservationStatus.Current
                || adapter.PhysicalIdentities.Count == 0
                || !indexes.Add(adapter.Index)
                || !luids.Add(luid))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParsePciAddress(
        string value,
        out uint segment,
        out uint bus,
        out uint device,
        out uint function)
    {
        segment = 0;
        bus = 0;
        device = 0;
        function = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        var firstColon = span.IndexOf(':');
        if (firstColon is <= 0)
        {
            return false;
        }
        var secondColonRelative = span[(firstColon + 1)..].IndexOf(':');
        if (secondColonRelative <= 0)
        {
            return false;
        }
        var secondColon = firstColon + 1 + secondColonRelative;
        var dotRelative = span[(secondColon + 1)..].IndexOf('.');
        if (dotRelative <= 0)
        {
            return false;
        }
        var dot = secondColon + 1 + dotRelative;
        if (span[(dot + 1)..].IndexOfAny(':', '.') >= 0)
        {
            return false;
        }

        return TryParseHex(span[..firstColon], uint.MaxValue, out segment)
            && TryParseHex(
                span[(firstColon + 1)..secondColon],
                byte.MaxValue,
                out bus)
            && TryParseHex(
                span[(secondColon + 1)..dot],
                31,
                out device)
            && TryParseHex(span[(dot + 1)..], 7, out function);
    }

    private static bool TryParseHex(
        ReadOnlySpan<char> value,
        uint maximum,
        out uint parsed)
    {
        parsed = 0;
        return value.Length > 0
            && uint.TryParse(
                value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out parsed)
            && parsed <= maximum;
    }

    private static bool TrySplitCombinedVendorDevice(
        uint combined,
        uint expectedVendorId,
        out uint vendorId,
        out uint deviceId)
    {
        var low = combined & 0xFFFFU;
        var high = combined >> 16;
        if (low == expectedVendorId
            && high is > 0 and <= 0xFFFF)
        {
            vendorId = low;
            deviceId = high;
            return true;
        }
        if (high == expectedVendorId
            && low is > 0 and <= 0xFFFF)
        {
            vendorId = high;
            deviceId = low;
            return true;
        }

        vendorId = 0;
        deviceId = 0;
        return false;
    }

    private static ulong Pack(AdapterLuid luid)
    {
        return ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;
    }
}

internal readonly record struct WindowsGpuProviderPciEvidence(
    bool SegmentValid,
    uint Segment,
    uint Bus,
    uint Device,
    bool FunctionValid,
    uint Function,
    uint VendorId,
    uint DeviceId,
    bool SubSystemIdValid,
    uint SubSystemId)
{
    internal bool IsValid =>
        Bus <= byte.MaxValue
        && Device <= 31
        && (!FunctionValid || Function <= 7)
        && VendorId is > 0 and <= 0xFFFF
        && DeviceId is > 0 and <= 0xFFFF;
}

internal readonly record struct WindowsGpuProviderIdentityBinding(
    WindowsGpuAdapter Adapter,
    WindowsGpuPhysicalIdentity PhysicalIdentity);

internal readonly record struct WindowsGpuProviderIdentityMatch(
    WindowsGpuProviderIdentityMatchStatus Status,
    WindowsGpuProviderIdentityBinding Binding)
{
    internal static WindowsGpuProviderIdentityMatch Unavailable { get; } =
        new(WindowsGpuProviderIdentityMatchStatus.Unavailable, default);

    internal static WindowsGpuProviderIdentityMatch Conflict { get; } =
        new(WindowsGpuProviderIdentityMatchStatus.Conflict, default);

    internal bool IsCurrent =>
        Status == WindowsGpuProviderIdentityMatchStatus.Current;
}

internal enum WindowsGpuProviderIdentityMatchStatus : byte
{
    Invalid = 0,
    Current = 1,
    Unavailable = 2,
    Conflict = 3
}
