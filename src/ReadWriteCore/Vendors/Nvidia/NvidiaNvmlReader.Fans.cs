using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

/// <summary>A physical Windows GPU and an NVML selector in the current catalog session.</summary>
public sealed record NvidiaNvmlFanIdentity(string GpuPnpInstanceId, uint FanIndex)
{
    public string ExactIdentityKey =>
        WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(GpuPnpInstanceId, out var canonical)
            ? "windows-nvml-fan|" + canonical + "|fan:" + FanIndex.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException("A canonical Windows physical GPU identity is required.", nameof(GpuPnpInstanceId));
}

public sealed record NvidiaNvmlFanReadTarget(NvidiaNvmlFanIdentity Fan, bool IncludePercent, bool IncludeRpm);

/// <summary>NVML intended operating speeds; neither value guarantees mechanical tachometer speed.</summary>
public sealed record NvidiaNvmlFanReading(
    NvidiaNvmlFanIdentity Fan, uint? PercentOfMaxNoiseTolerance, uint? IntendedRpm);

internal interface INvmlFanApi
{
    bool Initialize();
    int Count(out uint count);
    int Handle(uint index, out IntPtr device);
    int Pci(IntPtr device, out NvmlPciInfo pci);
    int FanCount(IntPtr device, out uint count);
    int Percent(IntPtr device, uint fan, out uint percent);
    int Rpm(IntPtr device, ref NvmlFanSpeedInfoV1 speed);
}

public sealed partial class NvidiaNvmlReader
{
    // Engineering capacity limits, not claims about NVIDIA hardware fan counts.
    public const uint MaxFanDeviceCount = 128;
    public const uint MaxFansPerDevice = 64;
    public const int MaxFanCount = 256;

    /// <summary>Null means an incomplete/unavailable catalog; an empty list is a successful empty catalog.</summary>
    public IReadOnlyList<NvidiaNvmlFanIdentity>? ReadFanIdentities(WindowsGpuAdapterInventoryRead inventory)
    {
        lock (gate) return ReadFanIdentities(inventory, new FanApi(this));
    }

    public Task<IReadOnlyList<NvidiaNvmlFanReading>> ReadFansAsync(
        IReadOnlyList<NvidiaNvmlFanReadTarget> targets,
        WindowsGpuAdapterInventoryRead inventory,
        CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult(ReadFans(targets, inventory, new FanApi(this), cancellationToken));
    }

    internal static IReadOnlyList<NvidiaNvmlFanIdentity>? ReadFanIdentities(
        WindowsGpuAdapterInventoryRead inventory, INvmlFanApi api)
    {
        var devices = ResolveFanDevices(inventory, api, CancellationToken.None);
        if (devices is null) return null;
        var fans = new List<NvidiaNvmlFanIdentity>();
        foreach (var (identity, device) in devices.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var count = ReadBoundedFanCount(api, device);
            if (count is null || fans.Count + count.Value > MaxFanCount) return null;
            for (uint fan = 0; fan < count.Value; fan++) fans.Add(new(identity, fan));
            if (ReadBoundedFanCount(api, device) != count
                || !FanDeviceStillMatches(api, device, inventory, identity)) return null;
        }
        return fans.ToArray();
    }

    internal static IReadOnlyList<NvidiaNvmlFanReading> ReadFans(
        IReadOnlyList<NvidiaNvmlFanReadTarget> targets,
        WindowsGpuAdapterInventoryRead inventory,
        INvmlFanApi api,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readings = targets.Select(static target => new NvidiaNvmlFanReading(target.Fan, null, null)).ToArray();
        if (!targets.Any(static target => target.IncludePercent || target.IncludeRpm)) return readings;
        var devices = ResolveFanDevices(inventory, api, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (devices is null) return readings;

        var selected = targets.Select((target, index) => (Target: target, Index: index))
            .Where(static item => item.Target.IncludePercent || item.Target.IncludeRpm)
            .Where(static item => WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(item.Target.Fan.GpuPnpInstanceId, out _))
            .GroupBy(static item => CanonicalFanParent(item.Target.Fan.GpuPnpInstanceId), StringComparer.Ordinal);
        foreach (var parent in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!devices.TryGetValue(parent.Key, out var device)) continue;
            var count = ReadBoundedFanCount(api, device);
            if (count is null) continue;
            var pending = new List<(int Index, NvidiaNvmlFanReading Reading)>();
            foreach (var item in parent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = item.Target;
                if (target.Fan.FanIndex >= count.Value) continue;
                var percent = target.IncludePercent ? ReadSelectedFanPercent(api, device, target.Fan.FanIndex) : null;
                cancellationToken.ThrowIfCancellationRequested();
                var rpm = target.IncludeRpm ? ReadSelectedFanRpm(api, device, target.Fan.FanIndex) : null;
                pending.Add((item.Index, new(target.Fan, percent, rpm)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Stage the entire parent before publishing any value. Count changes or a reset invalidate all of its readings.
            if (ReadBoundedFanCount(api, device) != count
                || !FanDeviceStillMatches(api, device, inventory, parent.Key)) continue;
            foreach (var item in pending) readings[item.Index] = item.Reading;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return readings;
    }

    private static Dictionary<string, IntPtr>? ResolveFanDevices(
        WindowsGpuAdapterInventoryRead inventory, INvmlFanApi api, CancellationToken cancellationToken)
    {
        if (inventory.Status != SamplingObservationStatus.Current || inventory.Generation == 0
            || inventory.ObservedAtUtcTicks <= 0 || inventory.ObservedCount != inventory.Adapters.Count
            || inventory.SkippedCount != 0 || inventory.OverflowCount != 0
            || inventory.Adapters.Count > MaxFanDeviceCount) return null;
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<int>();
        var luids = new HashSet<ulong>();
        foreach (var adapter in inventory.Adapters)
        {
            var luid = ((ulong)(uint)adapter.Luid.HighPart << 32) | adapter.Luid.LowPart;
            if (adapter.IsSoftware || adapter.Index < 0 || luid == 0 || !indexes.Add(adapter.Index) || !luids.Add(luid)
                || adapter.PhysicalIdentityStatus != SamplingObservationStatus.Current || adapter.PhysicalIdentities.Count == 0)
                return null;
            if (adapter.VendorId != 0x10DE) continue;
            foreach (var physical in adapter.PhysicalIdentities)
            {
                if (physical.Status != WindowsGpuPhysicalIdentityStatus.Bound || physical.VendorId != 0x10DE
                    || !WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(physical.PnpInstanceId, out var parent)
                    || !WindowsGpuProviderIdentityResolver.ResolvePnp(inventory, parent).IsCurrent
                    || !parents.Add(parent) || parents.Count > MaxFanDeviceCount) return null;
            }
        }
        var devices = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        if (parents.Count == 0) return devices;
        try
        {
            if (!api.Initialize() || api.Count(out var count) != NvmlSuccess || count > MaxFanDeviceCount) return null;
            var handles = new HashSet<IntPtr>();
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (api.Handle(index, out var device) != NvmlSuccess || device == IntPtr.Zero || !handles.Add(device)
                    || api.Pci(device, out var pci) != NvmlSuccess
                    || !WindowsGpuProviderIdentityResolver.TryParseNvmlPciEvidence(pci, out var evidence)) return null;
                var match = WindowsGpuProviderIdentityResolver.ResolvePci(inventory, evidence);
                if (!match.IsCurrent
                    || !WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(match.Binding.PhysicalIdentity.PnpInstanceId, out var parent)
                    || !parents.Contains(parent) || !devices.TryAdd(parent, device)) return null;
            }
            return devices.Count == parents.Count ? devices : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static string CanonicalFanParent(string identity)
    {
        WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(identity, out var parent);
        return parent;
    }

    private static bool FanDeviceStillMatches(INvmlFanApi api, IntPtr device, WindowsGpuAdapterInventoryRead inventory, string parent)
    {
        try
        {
            if (api.Pci(device, out var pci) != NvmlSuccess
                || !WindowsGpuProviderIdentityResolver.TryParseNvmlPciEvidence(pci, out var evidence)) return false;
            var match = WindowsGpuProviderIdentityResolver.ResolvePci(inventory, evidence);
            return match.IsCurrent && CanonicalFanParent(match.Binding.PhysicalIdentity.PnpInstanceId) == parent;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static uint? ReadBoundedFanCount(INvmlFanApi api, IntPtr device)
    {
        try { return api.FanCount(device, out var count) == NvmlSuccess && count <= MaxFansPerDevice ? count : null; }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static uint? ReadSelectedFanPercent(INvmlFanApi api, IntPtr device, uint fan)
    {
        try { return api.Percent(device, fan, out var percent) == NvmlSuccess ? percent : null; }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static uint? ReadSelectedFanRpm(INvmlFanApi api, IntPtr device, uint fan)
    {
        try
        {
            var speed = new NvmlFanSpeedInfoV1 { Version = NvmlFanSpeedInfoV1.StructVersion, Fan = fan };
            return api.Rpm(device, ref speed) == NvmlSuccess ? speed.Speed : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private sealed class FanApi(NvidiaNvmlReader owner) : INvmlFanApi
    {
        public bool Initialize() => owner.EnsureInitialized();
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows GPU adapter topology changed")]
        public int Count(out uint count) => NativeMethods.nvmlDeviceGetCount(out count);
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows GPU adapter topology changed")]
        public int Handle(uint index, out IntPtr device) => NativeMethods.nvmlDeviceGetHandleByIndex(index, out device);
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
        public int Pci(IntPtr device, out NvmlPciInfo pci) => NativeMethods.nvmlDeviceGetPciInfo(device, out pci);
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
        public int FanCount(IntPtr device, out uint count) => NativeMethods.nvmlDeviceGetNumFans(device, out count);
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
        public int Percent(IntPtr device, uint fan, out uint percent) => NativeMethods.nvmlDeviceGetFanSpeedV2(device, fan, out percent);
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
        public int Rpm(IntPtr device, ref NvmlFanSpeedInfoV1 speed) => NativeMethods.GetFanSpeedRpm(device, ref speed);
    }
}
