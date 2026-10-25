using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSoftwareIssueSignalProjectionTests
{
    [Fact]
    public void MemoryReportProjectsItsExactSoftwareTarget()
    {
        var report = Report(
            "memory-report",
            OptimizationReportTypes.BackgroundHighUsage,
            OptimizationResourceKinds.Memory,
            new OptimizationReportTarget(
                OptimizationReportTargetTypes.Software,
                "software:fixture",
                "Fixture",
                "software-fixture",
                "Fixture",
                SoftwareKinds.Other,
                "other",
                [],
                [],
                null));

        var signal = HostManagerSoftwareIssueSignalProjection.ProjectReport(
            report,
            HostManagerReportFactKind.CpuUsagePercent,
            SystemInterruptSnapshot.Unavailable("test", "test"), 10);

        Assert.NotNull(signal);
        Assert.Equal(SoftwareIssueKinds.AbnormalMemoryUsage, signal.Kind);
        Assert.Equal("software-fixture", signal.SoftwareId);
        Assert.Null(signal.ArtifactPath);
        Assert.Equal("memory", Assert.Single(signal.Evidence).MetricKind);
    }

    [Fact]
    public void InterruptReportUsesMaximumDriverFromTheSameSnapshot()
    {
        var report = Report(
            "interrupt-report",
            OptimizationReportTypes.SystemInterruptPressure,
            OptimizationResourceKinds.SystemInterrupt,
            new OptimizationReportTarget(
                OptimizationReportTargetTypes.System,
                "system-interrupts:1",
                "系统中断",
                null,
                null,
                null,
                null,
                [],
                [],
                null));
        var snapshot = Interrupts([
            new SystemInterruptDriverSnapshot(
                "small.sys",
                @"C:\Drivers\small.sys",
                1,
                1,
                2,
                1,
                0.01),
            new SystemInterruptDriverSnapshot(
                "large.sys",
                @"C:\Drivers\large.sys",
                5,
                12,
                8,
                6,
                0.04)
        ]);

        var signal = HostManagerSoftwareIssueSignalProjection.ProjectReport(
            report,
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds,
            snapshot, 10);

        Assert.NotNull(signal);
        Assert.Equal(SoftwareIssueKinds.LongSystemInterrupts, signal.Kind);
        Assert.Equal(@"C:\Drivers\large.sys", signal.ArtifactPath);
        Assert.Equal("large.sys", Assert.Single(signal.Evidence).ModuleName);
        Assert.Equal("interrupt_maximum_single_duration_milliseconds", Assert.Single(signal.Evidence).MetricKind);
    }

    [Fact]
    public void InterruptReportWithoutAnAttributablePathProducesNoSignal()
    {
        var report = Report(
            "interrupt-report",
            OptimizationReportTypes.SystemInterruptPressure,
            OptimizationResourceKinds.SystemInterrupt,
            new OptimizationReportTarget(
                OptimizationReportTargetTypes.System,
                "system-interrupts:1",
                "系统中断",
                null,
                null,
                null,
                null,
                [],
                [],
                null));
        var snapshot = Interrupts([
            new SystemInterruptDriverSnapshot(
                "unknown.sys",
                null,
                5,
                12,
                8,
                6,
                0.04)
        ]);

        var signal = HostManagerSoftwareIssueSignalProjection.ProjectReport(
            report,
            HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond,
            snapshot, 10);

        Assert.Null(signal);
    }

    [Fact]
    public void TrustedReportNeverProducesAnIssueAndNormalDriverCannotInheritGlobalAnomaly()
    {
        var target = new OptimizationReportTarget(OptimizationReportTargetTypes.Software,
            "software:fixture", "Fixture", "fixture", "Fixture", SoftwareKinds.Other, "other", [], [], null);
        var memory = Report("memory", OptimizationReportTypes.BackgroundHighUsage, OptimizationResourceKinds.Memory, target);
        Assert.Null(HostManagerSoftwareIssueSignalProjection.ProjectReport(memory with { State = OptimizationReportStates.TrustedSuppressed },
            HostManagerReportFactKind.SoftwareMemoryBytes, SystemInterruptSnapshot.Unavailable("test", "test"), 4_000));
        var report = Report("interrupt", OptimizationReportTypes.SystemInterruptPressure, OptimizationResourceKinds.SystemInterrupt,
            target with { TargetType = OptimizationReportTargetTypes.System, SoftwareId = null });
        var snapshot = Interrupts([
            new SystemInterruptDriverSnapshot("unknown.sys", null, 1, 12, 12, 1, 0.1),
            new SystemInterruptDriverSnapshot("normal.sys", @"C:\Drivers\normal.sys", 1, 0.2, 0, 0, 0.01)]);
        Assert.Null(HostManagerSoftwareIssueSignalProjection.ProjectReport(report,
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds, snapshot, 10));
        // First safe window: native hysteresis may keep the global report active,
        // but a normal current driver must not acquire the former driver's label.
        Assert.Null(HostManagerSoftwareIssueSignalProjection.ProjectReport(report,
            HostManagerReportFactKind.InterruptCpuCapacityPercent, snapshot, 5));
    }

    private static OptimizationReportItem Report(
        string id,
        string type,
        string resourceKind,
        OptimizationReportTarget target)
        => new(
            id,
            type,
            OptimizationReportStates.Active,
            SoftwareIssueSeverities.Warning,
            OptimizationConfidence.High,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            OptimizationActivityContext.Unknown(),
            target,
            new OptimizationReportEvidence(
                resourceKind,
                1,
                1,
                1,
                OptimizationValueUnits.Percent,
                1,
                1,
                1),
            []);

    private static SystemInterruptSnapshot Interrupts(
        IReadOnlyList<SystemInterruptDriverSnapshot> drivers)
        => new(
            DateTimeOffset.UtcNow,
            true,
            1,
            TimeSpan.FromSeconds(60),
            16,
            10,
            12,
            SystemInterruptEventKinds.Dpc,
            "large.sys",
            10,
            7,
            0.05,
            [],
            drivers,
            new SystemInterruptProviderState(
                "test",
                SystemInterruptProviderStates.Running,
                "test"));
}
