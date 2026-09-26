using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationReportMaintenancePolicyTests
{
    [Fact]
    public void DynamicReport_HidesBeforeItIsRemoved()
    {
        var now = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
        var record = CreateRecord(OptimizationReportTypes.BackgroundHighUsage, now - TimeSpan.FromMinutes(6));

        Assert.False(OptimizationReportMaintenancePolicy.IsWithinActiveWindow(record, now));
        Assert.True(OptimizationReportMaintenancePolicy.ShouldRetain(record, now));
    }

    [Fact]
    public void Advisory_UsesItsExplicitStaleWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
        var active = CreateRecord(OptimizationReportTypes.DeviceDriverProblem, now - TimeSpan.FromMinutes(14)) with
        {
            AdvisoryFamily = "device-driver",
            StaleAfterSeconds = 15 * 60
        };
        var stale = active with { LastObservedAt = now - TimeSpan.FromMinutes(16) };

        Assert.True(OptimizationReportMaintenancePolicy.IsWithinActiveWindow(active, now));
        Assert.False(OptimizationReportMaintenancePolicy.IsWithinActiveWindow(stale, now));
    }

    [Fact]
    public void DynamicReport_IsRemovedAfterRetentionWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
        var record = CreateRecord(OptimizationReportTypes.VramResidency, now - TimeSpan.FromDays(2));

        Assert.False(OptimizationReportMaintenancePolicy.ShouldRetain(record, now));
    }

    private static OptimizationObservationRecord CreateRecord(string type, DateTimeOffset observedAt)
    {
        return new OptimizationObservationRecord(
            Key: "key",
            ReportId: "report",
            Type: type,
            ResourceKind: OptimizationResourceKinds.Cpu,
            TargetType: OptimizationReportTargetTypes.Software,
            TargetKey: "software",
            DisplayName: "Software",
            SoftwareId: "software",
            SoftwareKind: null,
            DisplayKind: null,
            ProcessNames: [],
            DriveLetter: null,
            FirstObservedAt: observedAt,
            LastObservedAt: observedAt,
            SampleCount: 1,
            ActiveSampleCount: 1,
            ValueSum: 1,
            PeakValue: 1,
            CurrentValue: 1,
            Unit: "%",
            IsBytes: false);
    }
}
