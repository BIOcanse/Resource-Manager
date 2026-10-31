using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class ServiceRecoverySettingsTests
{
    [Theory]
    [InlineData(true, 10, 3)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 3600, 10)]
    [InlineData(false, 40, 7)]
    public async Task StartWritesExactlyThePreferenceThenReadsActual(bool enabled, int delay, int count)
    {
        var plans = Plans();
        var control = new Control();
        var plan = Plan(1, enabled, delay, count);
        plans.Publish(plan);
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(true, true), control, CrashReportTestSupport.DumpSettings(), new CrashReportTestSupport.RecordingOwner(), new TestLogger());
        await service.StartAsync(default);
        var written = Assert.Single(control.Writes);
        Assert.False(written.FailureActionsOnNonCrashFailures);
        Assert.Equal(86400u, written.ResetPeriodSeconds);
        Assert.Equal(enabled ? count + 1 : 0, written.Actions.Count);
        if (enabled)
        {
            Assert.All(written.Actions.Take(count), action => Assert.Equal(new ServiceRecoveryAction(1, (uint)delay * 1000), action));
            Assert.Equal(new ServiceRecoveryAction(0, 0), written.Actions[^1]);
        }
        Assert.Equal(1, control.Reads);
        Assert.Equal("applied", service.Diagnostics.State);
        Assert.Same(control.Actual, service.Diagnostics.Actual);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task PublishedSettingChangesApplyAndStopUnsubscribes()
    {
        var plans = Plans();
        var control = new Control();
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(true, true), control, CrashReportTestSupport.DumpSettings(), new CrashReportTestSupport.RecordingOwner(), new TestLogger());
        await service.StartAsync(default);
        Assert.Empty(control.Writes);
        Assert.Empty(plans.Publish(Plan(1, true, 30, 2)).DeliveryFailures);
        Assert.Empty(plans.Publish(Plan(2, false, 30, 2)).DeliveryFailures);
        Assert.Equal(2, control.Writes.Count);
        Assert.Empty(control.Writes[1].Actions);
        await service.StopAsync(default);
        plans.Publish(Plan(3, true, 50, 5));
        Assert.Equal(2, control.Writes.Count);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "installed-service-has-another-process")]
    public async Task NonInstalledHostsSkipAllConfigurationAndLogOnlyOnce(bool isService, string? reason)
    {
        var plans = Plans();
        var control = new Control { SkipReason = reason };
        var log = new TestLogger();
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(isService, true), control, CrashReportTestSupport.DumpSettings(), new CrashReportTestSupport.RecordingOwner(), log);
        await service.StartAsync(default);
        plans.Publish(Plan(1, true, 10, 3));
        plans.Publish(Plan(2, false, 10, 3));
        Assert.Empty(control.Writes);
        Assert.Equal(0, control.Reads);
        // Recovery checks once per changed preference; the crash dump state checks once, for its unchanged first attempt.
        Assert.Equal(isService ? 3 : 0, control.IdentityChecks);
        Assert.Equal("skipped", service.Diagnostics.State);
        Assert.NotNull(service.Diagnostics.SkipReason);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Information);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task WriteFailureDoesNotBlockStartupReadsActualAndDoesNotLoop()
    {
        var plans = Plans();
        var plan = Plan(1, true, 10, 3);
        plans.Publish(plan);
        var control = new Control { FailWrite = true, Actual = new(42, [], false) };
        var log = new TestLogger();
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(true, true), control, CrashReportTestSupport.DumpSettings(), new CrashReportTestSupport.RecordingOwner(), log);
        await service.StartAsync(default);
        Assert.Equal("failed", service.Diagnostics.State);
        Assert.Equal("write denied", service.Diagnostics.WriteError);
        Assert.Same(control.Actual, service.Diagnostics.Actual);
        Assert.Equal(1, control.Reads);
        // Same settings image rebuilt for an unrelated reason must not retry a failed configuration.
        plans.Publish(plan with { Version = 2 });
        Assert.Single(control.Writes);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task ReadFailureIsVisibleAndAReadBackMismatchCannotClaimSuccess()
    {
        var plans = Plans();
        var control = new Control { FailRead = true };
        var log = new TestLogger();
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(true, true), control, CrashReportTestSupport.DumpSettings(), new CrashReportTestSupport.RecordingOwner(), log);
        await service.StartAsync(default);
        plans.Publish(Plan(1, true, 10, 3));
        Assert.Equal("failed", service.Diagnostics.State);
        Assert.Null(service.Diagnostics.Actual);
        Assert.Equal("read denied", service.Diagnostics.ReadError);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error);
        control.FailRead = false;
        control.PreserveActual = true;
        control.Actual = new(60, [new(1, 99000)], true);
        plans.Publish(Plan(2, true, 15, 1));
        Assert.Equal("mismatch", service.Diagnostics.State);
        Assert.Same(control.Actual, service.Diagnostics.Actual);
        await service.StopAsync(default);
    }

    [Fact]
    public void CounterResetComesFromTheVisibleFreedomPoint()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        Assert.Equal(86400, plan.ServiceRecoveryResetPeriodSeconds);
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root,
                "resource-manager/backend/service_recovery/build/simple/0")["value"] = 172800);
        Assert.Equal(172800, changed.ServiceRecoveryResetPeriodSeconds);
    }

    private static RuntimePlanProvider Plans() => new(new HostManagerDeploymentState());

    private static CompiledRuntimePlan Plan(long version, bool enabled, int delay, int count) => CompiledRuntimePlan.Default with
    {
        Version = version,
        ServiceRecovery = new(false, [], false, enabled, delay, count),
        HostManager = HostManagerTestPlanFactory.CreatePlan() with
        {
            BindingProvenance = HostManagerTestPlanFactory.CreatePlan().BindingProvenance with
            { SettingsEffectiveSha256 = version.ToString().PadLeft(64, '0') }
        }
    };

    private sealed class Control : IServiceRecoveryControl
    {
        public string? SkipReason;
        public bool FailWrite, FailRead, PreserveActual;
        public int IdentityChecks, Reads;
        public ServiceRecoveryConfiguration Actual = new(0, [], false);
        public List<ServiceRecoveryConfiguration> Writes { get; } = [];
        public string? GetSkipReason() { IdentityChecks++; return SkipReason; }
        public void Write(ServiceRecoveryConfiguration configuration)
        {
            Writes.Add(configuration);
            if (FailWrite) throw new IOException("write denied");
            if (!PreserveActual) Actual = configuration;
        }
        public ServiceRecoveryConfiguration Read()
        {
            Reads++;
            if (FailRead) throw new IOException("read denied");
            return Actual;
        }
    }

    private sealed class TestLogger : ILogger<ServiceRecoverySettings>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, error)));
    }
}
