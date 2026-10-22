using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Platform;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;
public static class WindowsNotebookFanProjection
{
    private const string ComponentName = "Notebook OEM Fan Provider";
    public const string CpuFanIdentityKey = "mechrevo|uwacpi|fan|cpu";
    public const string DiscreteGpuFanIdentityKey = "mechrevo|uwacpi|fan|discrete-gpu";
    internal static NativeMetricSnapshotOrdinalTopologyRead CreateFanCatalog(
        HardwareMonitorFanIdentityRead wmi, bool enableMechrevo, ulong generation)
    {
        var identities = wmi.Complete
            ? wmi.Identities.Select(item => new NativeMetricSnapshotOrdinalCatalogIdentity(item.Index, item.IdentityKey, generation)
            {
                Device = new("fan", item.Provider is not null && item.SensorIdentifier is not null
                    ? MetricDeviceAddress.SystemFan(item.Provider, item.SensorIdentifier)
                    : null, Clean(item.NameDeclared), item.Index)
            }).ToList()
            : [];
        if (enableMechrevo)
        {
            var lastIndex = identities.Count == 0 ? -1 : identities.Max(static item => item.DisplayIndex);
            identities.AddRange(CreateCatalogIdentities(checked(lastIndex + 1), generation));
        }
        return new(wmi.Complete ? NativeMetricSnapshotCatalogTopologyStatus.Complete : NativeMetricSnapshotCatalogTopologyStatus.Unavailable, identities);
    }

    public static NotebookOemFanSensorSnapshot Create(MechrevoUwAcpiSensorSnapshot mechrevo)
        => new(MergeProviderStates([mechrevo.ProviderState]), mechrevo.CpuFanSpeedRpm, mechrevo.CpuFanSpeedPercent,
            mechrevo.GpuFanSensor is null ? [] : [mechrevo.GpuFanSensor])
        {
            PhysicalFans =
            [
                new(CpuFanIdentityKey, NotebookOemPhysicalFanRole.Cpu,
                    mechrevo.CpuFanSpeedRpm, mechrevo.CpuFanSpeedPercent),
                new(DiscreteGpuFanIdentityKey, NotebookOemPhysicalFanRole.DiscreteGpu,
                    mechrevo.GpuFanSensor?.FanSpeedRpm, mechrevo.GpuFanSensor?.FanSpeedPercent)
            ]
        };

    public static IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> CreateCatalogIdentities(
        int firstDisplayIndex, ulong topologyGeneration)
    {
        if (firstDisplayIndex < 0 || firstDisplayIndex > int.MaxValue - 1 || topologyGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(firstDisplayIndex));
        return
        [
            new(firstDisplayIndex, CpuFanIdentityKey, topologyGeneration)
            {
                NotebookOemRole = NotebookOemPhysicalFanRole.Cpu,
                Device = new("fan", MetricDeviceAddress.OemFan("cpu"), null, firstDisplayIndex)
            },
            new(firstDisplayIndex + 1, DiscreteGpuFanIdentityKey, topologyGeneration)
            {
                NotebookOemRole = NotebookOemPhysicalFanRole.DiscreteGpu,
                Device = new("fan", MetricDeviceAddress.OemFan("discrete_gpu"), null, firstDisplayIndex + 1)
            }
        ];
    }

    private static HardwareSensorProviderState MergeProviderStates(IReadOnlyList<HardwareSensorProviderState> states)
    {
        if (states.Any(static state => state.State.Equals("Active", StringComparison.OrdinalIgnoreCase)))
        {
            return new HardwareSensorProviderState(
                ComponentName,
                "Active",
                string.Join("; ", states.Select(static state => $"{state.Provider}: {state.State}")));
        }

        if (states.Any(static state =>
            state.State.Equals("RuntimeAvailable", StringComparison.OrdinalIgnoreCase)
            || state.State.Equals("InstalledUnverified", StringComparison.OrdinalIgnoreCase)))
        {
            return new HardwareSensorProviderState(
                ComponentName,
                "InstalledUnverified",
                string.Join("; ", states.Select(static state => $"{state.Provider}: {state.Message}")));
        }

        return new HardwareSensorProviderState(
            ComponentName,
            "Unavailable",
            string.Join("; ", states.Select(static state => $"{state.Provider}: {state.Message}")));
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

