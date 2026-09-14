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
                "普通软件",
                [],
                [],
                null));

        var signal = HostManagerSoftwareIssueSignalProjection.ProjectReport(
            report,
            HostManagerReportFactKind.CpuUsagePercent,
            SystemInterruptSnapshot.Unavailable("test", "test"));

        Assert.NotNull(signal);
        Assert.Equal(SoftwareIssueKinds.AbnormalMemoryUsage, signal.Kind);
        Assert.Equal("software-fixture", signal.SoftwareId);
        Assert.Null(signal.ArtifactPath);
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
            snapshot);

        Assert.NotNull(signal);
        Assert.Equal(SoftwareIssueKinds.LongSystemInterrupts, signal.Kind);
        Assert.Equal(@"C:\Drivers\large.sys", signal.ArtifactPath);
        Assert.Contains("large.sys", signal.Message, StringComparison.Ordinal);
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
            snapshot);

        Assert.Null(signal);
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
            id,
            id,
            OptimizationActivityContext.Unknown(),
            target,
            new OptimizationReportEvidence(
                resourceKind,
                1,
                1,
                1,
                "1",
                "1",
                "1",
                1,
                1,
                1,
                []),
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
                "Running",
                "test"));
}
