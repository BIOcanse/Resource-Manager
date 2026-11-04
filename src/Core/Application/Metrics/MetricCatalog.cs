using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.ReadWrite;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    public static IReadOnlyList<MetricDefinition> FromSnapshot(HardwareMetricSnapshot snapshot)
    {
        var definitions = new Dictionary<string, MetricDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, unit, volatility) in BuiltInDeclarations)
            Add(id, unit, volatility, capabilityKnown: true);

        var cpuComponent = CpuComponent(snapshot.Cpu.Name);
        foreach (var (id, unit, volatility) in CpuDeclarations)
            Add(id, unit, volatility, id == "cpu.temperature" ? cpuComponent
                : id is "cpu.fanRpm" or "cpu.fanPercent" ? FanComponent(id)
                : cpuComponent == "amd-smu-pawnio-provider" ? cpuComponent : null);
        foreach (var (id, unit, volatility) in SensorDeclarations)
            Add(id, unit, volatility, "hardware-sensors-pawnio-provider");
        Add("virtualMemory.usage", MetricUnits.Bytes, ReadVolatility.Sampled);
        Add("virtualMemory.percent", "%", ReadVolatility.Sampled);

        var gpuIndexes = snapshot.Gpus.Select(static gpu => gpu.Index)
            .Concat(snapshot.Items.Keys.Select(GpuIndex).OfType<int>())
            .Concat(snapshot.FieldBindings.Values.SelectMany(static binding => binding.RequestAliases)
                .Select(GpuIndex).OfType<int>())
            .Where(static index => index >= 0).Distinct().Order();
        foreach (var index in gpuIndexes)
        {
            var gpu = snapshot.Gpus.FirstOrDefault(candidate => candidate.Index == index);
            var component = GpuComponent(gpu?.Name);
            foreach (var (suffix, unit, volatility) in GpuDeclarations)
            {
                var id = $"gpu.{index}.{suffix}";
                var dependency = suffix is "fanRpm" or "fanPercent" ? FanComponent(id, component)
                    : suffix is "coreVoltage" or "current" && component == "nvidia-nvml-provider"
                        ? "nvidia-nvapi-provider" : component;
                Add(id, unit, volatility, dependency, gpu is not null && suffix == "usage", "gpu", gpu?.IdentityKey);
            }
        }
        foreach (var (id, value) in snapshot.Items)
            if (!definitions.ContainsKey(id)) Add(id, value.Unit, ReadVolatility.Sampled,
                id.StartsWith("fan.", StringComparison.OrdinalIgnoreCase) ? "hardware-sensors-pawnio-provider" : null);

        // A field binding adds raw addresses and aliases, including unavailable
        // fields. Its declaration and source capabilities own their availability.
        var aliasOwners = ResolveAliasOwners(snapshot);
        foreach (var binding in snapshot.FieldBindings.Values)
        {
            foreach (var id in binding.RequestAliases.Prepend(binding.RequestAddress))
            {
                if (id != binding.RequestAddress && !ReferenceEquals(aliasOwners[id].Binding, binding)) continue;
                var available = HasValue(snapshot.Items.GetValueOrDefault(id))
                    || binding.Field.Reading?.SourceAvailability == "available";
                var existing = definitions.GetValueOrDefault(id);
                var dependency = Dependency(binding.SourceId) ?? existing?.RequiredComponentId;
                var selectable = available || existing?.Selectable == true;
                definitions[id] = new(id, binding.Field.Unit, binding.Field.Volatility, selectable ? null : dependency,
                    Selectable: selectable, DisabledReason: selectable ? null : Reason(snapshot.Items.GetValueOrDefault(id), dependency),
                    ScopeKind: binding.Field.Resource == MetricResource.Gpu ? "gpu" : binding.Device.Kind,
                    ScopeKey: binding.DeviceIdentity)
                {
                    FieldCode = binding.Field.Code, FieldName = binding.Field.Name,
                    DeviceIdentity = binding.DeviceIdentity, SourceId = binding.SourceId, ValueKind = binding.Field.ValueKind
                };
            }
        }
        var result = definitions.Values.ToList();
        ApplyReadingAvailability(result, snapshot.FieldBindings.Values);
        for (var index = 0; index < result.Count; index++)
            if (GpuIndex(result[index].Id) is { } gpuIndex)
            {
                var dedicated = snapshot.Gpus.FirstOrDefault(gpu => gpu.Index == gpuIndex)?.HasDedicatedMemoryMetrics;
                result[index] = result[index] with
                { HasDedicatedMemoryMetrics = dedicated };
                if (dedicated == false && result[index].Id[(result[index].Id.IndexOf('.', 4) + 1)..]
                    is "vram" or "vramTotal" or "vramPercent")
                    result[index] = result[index] with
                    {
                        Selectable = false, RequiredComponentId = null,
                        DisabledReason = BackendMessage.Create(BackendMessageDomains.Metric, BackendMessageCodes.Metric.NotApplicable)
                    };
            }
        return result;

        void Add(string id, string unit, ReadVolatility volatility, string? dependency = null, bool capabilityKnown = false,
            string? scopeKind = null, string? scopeKey = null)
        {
            var value = snapshot.Items.GetValueOrDefault(id);
            var field = DeclaredField(id, value?.SourceId);
            var selectable = capabilityKnown || HasValue(value);
            dependency = selectable ? null : Dependency(value?.SourceId) ?? dependency;
            definitions[id] = new(id, unit, field?.Volatility ?? volatility, dependency, selectable,
                selectable ? null : Reason(value, dependency), scopeKind, scopeKey)
            {
                FieldCode = value is { FieldCode: not MetricFieldCode.Unknown } ? value.FieldCode : field?.Code ?? default,
                FieldName = value?.FieldName ?? field?.Name,
                DeviceIdentity = value?.DeviceIdentity, SourceId = value?.SourceId,
                ValueKind = value?.ValueKind ?? field?.ValueKind ?? "float64"
            };
        }

        string FanComponent(string id, string? gpuComponent = null)
            => Dependency(snapshot.Items.GetValueOrDefault(id)?.SourceId)
                ?? (gpuComponent == "nvidia-nvml-provider" ? "nvidia-nvapi-provider"
                    : gpuComponent ?? "hardware-sensors-pawnio-provider");
    }

    private static bool HasValue(MetricReadingValue? value) => value is { NumericValue: not null }
        or { IntegerValue: not null } or { BytesValue: not null } or { BooleanValue: not null } or { Percent: not null };

    private static Dictionary<string, (MetricDeviceFieldBinding Binding, int Rank)> ResolveAliasOwners(HardwareMetricSnapshot snapshot)
    {
        var owners = new Dictionary<string, (MetricDeviceFieldBinding Binding, int Rank)>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in snapshot.FieldBindings.Values)
        foreach (var alias in binding.RequestAliases)
        {
            var value = snapshot.Items.GetValueOrDefault(alias);
            var matchesValue = value is not null && value.SourceId == binding.SourceId
                && (value.FieldCode == MetricFieldCode.Unknown || value.FieldCode == binding.Field.Code)
                && (value.FieldName is null || value.FieldName == binding.Field.Name)
                && (value.DeviceIdentity is null || value.DeviceIdentity == binding.DeviceIdentity);
            var rank = matchesValue ? 3 : binding.Field.Reading?.IsPreferredSource == true ? 2
                : binding.Field.Reading?.SourceAvailability == "available" ? 1 : 0;
            if (!owners.TryGetValue(alias, out var prior) || rank > prior.Rank
                || rank == prior.Rank && string.CompareOrdinal(binding.RequestAddress, prior.Binding.RequestAddress) < 0)
                owners[alias] = (binding, rank);
        }
        return owners;
    }

    private static BackendMessage Reason(MetricReadingValue? value, string? dependency) => BackendMessage.Create(
        BackendMessageDomains.Metric, dependency is not null ? BackendMessageCodes.Metric.NeedsComponent
            : value is null ? BackendMessageCodes.Metric.NotExposed : BackendMessageCodes.Metric.NoValidReading,
        dependency is null ? [] : [dependency]);

    private static readonly (string Id, string Unit, ReadVolatility Volatility)[] BuiltInDeclarations =
    [ ("cpu.usage", "%", ReadVolatility.Sampled), ("cpu.frequency", "MHz", ReadVolatility.Sampled), ("cpu.frequencyPercent", "%", ReadVolatility.Sampled),
      ("memory.usage", MetricUnits.Bytes, ReadVolatility.Sampled), ("memory.percent", "%", ReadVolatility.Sampled),
      ("disk.total.activePercent", "%", ReadVolatility.Sampled), ("disk.total.readBytesPerSec", "B/s", ReadVolatility.Sampled),
      ("disk.total.writeBytesPerSec", "B/s", ReadVolatility.Sampled), ("disk.total.queueLength", "", ReadVolatility.Sampled),
      ("network.total.receiveBytesPerSec", "B/s", ReadVolatility.Sampled), ("network.total.sendBytesPerSec", "B/s", ReadVolatility.Sampled),
      ("network.total.utilizationPercent", "%", ReadVolatility.Sampled) ];

    private static readonly (string Id, string Unit, ReadVolatility Volatility)[] CpuDeclarations =
    [ ("cpu.packagePower", "W", ReadVolatility.Sampled), ("cpu.coreVoltage", "V", ReadVolatility.Sampled), ("cpu.packageCurrent", "A", ReadVolatility.Sampled),
      ("cpu.temperature", "°C", ReadVolatility.Sampled), ("cpu.stapmPower", "W", ReadVolatility.Sampled), ("cpu.actualPower", "W", ReadVolatility.Sampled),
      ("cpu.averagePower", "W", ReadVolatility.Sampled), ("cpu.tdcCurrent", "A", ReadVolatility.Sampled), ("cpu.edcCurrent", "A", ReadVolatility.Sampled),
      ("cpu.platformPower", "W", ReadVolatility.Sampled), ("cpu.platformVoltage", "V", ReadVolatility.Sampled), ("cpu.smuFrequency", "MHz", ReadVolatility.Sampled),
      ("cpu.fanRpm", "RPM", ReadVolatility.Sampled), ("cpu.fanPercent", "%", ReadVolatility.Sampled) ];

    private static readonly (string Id, string Unit, ReadVolatility Volatility)[] SensorDeclarations =
    [ ("memory.temperature", "°C", ReadVolatility.Sampled), ("system.motherboardTemperature", "°C", ReadVolatility.Sampled), ("system.vrmTemperature", "°C", ReadVolatility.Sampled),
      ("system.chipsetTemperature", "°C", ReadVolatility.Sampled), ("system.motherboardVoltage", "V", ReadVolatility.Sampled) ];

    private static readonly (string Id, string Unit, ReadVolatility Volatility)[] GpuDeclarations =
    [ ("usage", "%", ReadVolatility.Sampled), ("graphicsClock", "MHz", ReadVolatility.Sampled), ("graphicsClockPercent", "%", ReadVolatility.Sampled), ("power", "W", ReadVolatility.Sampled),
      ("boardPower", "W", ReadVolatility.Sampled), ("powerLimit", "W", ReadVolatility.Sampled), ("temperature", "°C", ReadVolatility.Sampled), ("hotspotTemperature", "°C", ReadVolatility.Sampled),
      ("intakeTemperature", "°C", ReadVolatility.Sampled), ("fanRpm", "RPM", ReadVolatility.Sampled), ("fanPercent", "%", ReadVolatility.Sampled), ("coreVoltage", "V", ReadVolatility.Sampled),
      ("current", "A", ReadVolatility.Sampled), ("vram", MetricUnits.Bytes, ReadVolatility.Sampled), ("vramPercent", "%", ReadVolatility.Sampled), ("memoryClock", "MHz", ReadVolatility.Sampled) ];

    private static int? GpuIndex(string id)
    {
        if (!id.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)) return null;
        var end = id.IndexOf('.', 4);
        return end > 4 && int.TryParse(id.AsSpan(4, end - 4), out var index) ? index : null;
    }

    private static MetricFieldDefinition? DeclaredField(string id, string? source)
    {
        if (MetricFieldRegistry.FindByTemplate(id, source) is { } direct) return direct;
        // These are the catalog's documented ordinal aliases, never physical
        // device identities or canonical field addresses.
        foreach (var resource in new[] { "gpu", "disk", "fan" })
        {
            var prefix = resource + ".";
            if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var end = id.IndexOf('.', prefix.Length);
            if (end > prefix.Length && int.TryParse(id.AsSpan(prefix.Length, end - prefix.Length), out _))
                return MetricFieldRegistry.FindByTemplate($"{resource}.{{{resource}}}{id[end..]}", source);
        }
        return null;
    }

    private static string? CpuComponent(string name) => name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? "amd-smu-pawnio-provider"
        : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "intel-msr-pawnio-provider" : null;

    private static string? GpuComponent(string? name) => name?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true
        ? "nvidia-nvml-provider" : name?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true
            || name?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) == true ? "amd-adlx-provider" : null;

    private static string? Dependency(string? source) => source switch
    {
        "intel.msr" => "intel-msr-pawnio-provider", "amd.smu" or "amd.smn" => "amd-smu-pawnio-provider",
        "nvidia.nvml" => "nvidia-nvml-provider", "nvidia.nvapi" => "nvidia-nvapi-provider",
        "amd.adlx" => "amd-adlx-provider", "notebook.oem-fan" => "notebook-oem-fan-provider",
        "hardware-monitor.core" => "hardware-sensors-pawnio-provider", _ => null
    };

    public static IReadOnlyList<MetricDefinition> ForPerformanceOverlay(HardwareMetricSnapshot? snapshot)
        => (snapshot is null ? [] : FromSnapshot(snapshot)).Concat(TargetSoftwareDefinitions(snapshot)).ToArray();

    public static IReadOnlyList<MetricDefinition> TargetSoftwareDefinitions(HardwareMetricSnapshot? snapshot) =>
    [ new(PerformanceOverlayMetricIds.Fps, "FPS", ReadVolatility.Sampled), new(PerformanceOverlayMetricIds.FrameTime, "ms", ReadVolatility.Sampled),
      new(PerformanceOverlayMetricIds.OnePercentLow, "FPS", ReadVolatility.Sampled), new(PerformanceOverlayMetricIds.PointOnePercentLow, "FPS", ReadVolatility.Sampled),
      new(PerformanceOverlayMetricIds.Cpu, "%", ReadVolatility.Sampled), TargetGpu(PerformanceOverlayMetricIds.Gpu, "%", snapshot),
      new(PerformanceOverlayMetricIds.Memory, MetricUnits.Bytes, ReadVolatility.Sampled), TargetGpu(PerformanceOverlayMetricIds.Vram, MetricUnits.Bytes, snapshot) ];

    private static MetricDefinition TargetGpu(string id, string unit, HardwareMetricSnapshot? snapshot)
        => new(id, unit, ReadVolatility.Sampled, Selectable: snapshot is null || snapshot.Gpus.Count > 0,
            DisabledReason: snapshot is not null && snapshot.Gpus.Count == 0
                ? BackendMessage.Create(BackendMessageDomains.Metric, BackendMessageCodes.Metric.NotExposed) : null);
}
