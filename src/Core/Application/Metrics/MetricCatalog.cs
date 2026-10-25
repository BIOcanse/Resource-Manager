using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Overlay;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    public static IReadOnlyList<MetricDefinition> FromSnapshot(HardwareMetricSnapshot snapshot)
    {
        var definitions = new Dictionary<string, MetricDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, unit) in BuiltInDeclarations)
            Add(id, unit, capabilityKnown: true);

        var cpuComponent = CpuComponent(snapshot.Cpu.Name);
        foreach (var (id, unit) in CpuDeclarations)
            Add(id, unit, id == "cpu.temperature" ? cpuComponent
                : id is "cpu.fanRpm" or "cpu.fanPercent" ? FanComponent(id)
                : cpuComponent == "amd-smu-pawnio-provider" ? cpuComponent : null);
        foreach (var (id, unit) in SensorDeclarations)
            Add(id, unit, "hardware-sensors-pawnio-provider");
        Add("virtualMemory.usage", MetricUnits.Bytes);
        Add("virtualMemory.percent", "%");

        var gpuIndexes = snapshot.Gpus.Select(static gpu => gpu.Index)
            .Concat(snapshot.Items.Keys.Select(GpuIndex).OfType<int>())
            .Concat(snapshot.FieldBindings.Values.SelectMany(static binding => binding.RequestAliases)
                .Select(GpuIndex).OfType<int>())
            .Where(static index => index >= 0).Distinct().Order();
        foreach (var index in gpuIndexes)
        {
            var gpu = snapshot.Gpus.FirstOrDefault(candidate => candidate.Index == index);
            var component = GpuComponent(gpu?.Name);
            foreach (var (suffix, unit) in GpuDeclarations)
            {
                var id = $"gpu.{index}.{suffix}";
                var dependency = suffix is "fanRpm" or "fanPercent" ? FanComponent(id, component)
                    : suffix is "coreVoltage" or "current" && component == "nvidia-nvml-provider"
                        ? "nvidia-nvapi-provider" : component;
                Add(id, unit, dependency, gpu is not null && suffix == "usage", "gpu", gpu?.IdentityKey);
            }
        }
        foreach (var (id, value) in snapshot.Items)
            if (!definitions.ContainsKey(id)) Add(id, value.Unit,
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
                definitions[id] = new(id, binding.Field.Unit, selectable ? null : dependency,
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
        return result;

        void Add(string id, string unit, string? dependency = null, bool capabilityKnown = false,
            string? scopeKind = null, string? scopeKey = null)
        {
            var value = snapshot.Items.GetValueOrDefault(id);
            var field = DeclaredField(id, value?.SourceId);
            var selectable = capabilityKnown || HasValue(value);
            dependency = selectable ? null : Dependency(value?.SourceId) ?? dependency;
            definitions[id] = new(id, unit, dependency, selectable,
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

    private static readonly (string Id, string Unit)[] BuiltInDeclarations =
    [ ("cpu.usage", "%"), ("cpu.frequency", "MHz"), ("cpu.frequencyPercent", "%"),
      ("memory.usage", MetricUnits.Bytes), ("memory.percent", "%"),
      ("disk.total.activePercent", "%"), ("disk.total.readBytesPerSec", "B/s"),
      ("disk.total.writeBytesPerSec", "B/s"), ("disk.total.queueLength", ""),
      ("network.total.receiveBytesPerSec", "B/s"), ("network.total.sendBytesPerSec", "B/s"),
      ("network.total.utilizationPercent", "%") ];

    private static readonly (string Id, string Unit)[] CpuDeclarations =
    [ ("cpu.packagePower", "W"), ("cpu.coreVoltage", "V"), ("cpu.packageCurrent", "A"),
      ("cpu.temperature", "°C"), ("cpu.stapmPower", "W"), ("cpu.actualPower", "W"),
      ("cpu.averagePower", "W"), ("cpu.tdcCurrent", "A"), ("cpu.edcCurrent", "A"),
      ("cpu.platformPower", "W"), ("cpu.platformVoltage", "V"), ("cpu.smuFrequency", "MHz"),
      ("cpu.fanRpm", "RPM"), ("cpu.fanPercent", "%") ];

    private static readonly (string Id, string Unit)[] SensorDeclarations =
    [ ("memory.temperature", "°C"), ("system.motherboardTemperature", "°C"), ("system.vrmTemperature", "°C"),
      ("system.chipsetTemperature", "°C"), ("system.motherboardVoltage", "V") ];

    private static readonly (string Id, string Unit)[] GpuDeclarations =
    [ ("usage", "%"), ("graphicsClock", "MHz"), ("graphicsClockPercent", "%"), ("power", "W"),
      ("boardPower", "W"), ("powerLimit", "W"), ("temperature", "°C"), ("hotspotTemperature", "°C"),
      ("intakeTemperature", "°C"), ("fanRpm", "RPM"), ("fanPercent", "%"), ("coreVoltage", "V"),
      ("current", "A"), ("vram", MetricUnits.Bytes), ("vramPercent", "%"), ("memoryClock", "MHz") ];

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
    [ new(PerformanceOverlayMetricIds.Fps, "FPS"), new(PerformanceOverlayMetricIds.FrameTime, "ms"),
      new(PerformanceOverlayMetricIds.OnePercentLow, "FPS"), new(PerformanceOverlayMetricIds.PointOnePercentLow, "FPS"),
      new(PerformanceOverlayMetricIds.Cpu, "%"), TargetGpu(PerformanceOverlayMetricIds.Gpu, "%", snapshot),
      new(PerformanceOverlayMetricIds.Memory, MetricUnits.Bytes), TargetGpu(PerformanceOverlayMetricIds.Vram, MetricUnits.Bytes, snapshot) ];

    private static MetricDefinition TargetGpu(string id, string unit, HardwareMetricSnapshot? snapshot)
        => new(id, unit, Selectable: snapshot is null || snapshot.Gpus.Count > 0,
            DisabledReason: snapshot is not null && snapshot.Gpus.Count == 0
                ? BackendMessage.Create(BackendMessageDomains.Metric, BackendMessageCodes.Metric.NotExposed) : null);
}
