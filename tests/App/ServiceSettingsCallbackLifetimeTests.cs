using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class ServiceSettingsCallbackLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RecoveryCapturedPublicationDoesNotTouchScmAfterStop()
    {
        var plans = Plans();
        var delayedPlan = Plan(1, true, 10);
        using var publication = new BlockedPublication(plans, delayedPlan);
        var control = new RecoveryControl();
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(true, true),
            control, CrashReportTestSupport.DumpSettings(), new CrashReportTestSupport.RecordingOwner(),
            NullLogger<ServiceRecoverySettings>.Instance);
        await service.StartAsync(default);
        var publishing = Task.Run(() => plans.Publish(delayedPlan));
        try
        {
            await publication.Entered.Task.WaitAsync(Timeout);
            await service.StopAsync(default).WaitAsync(Timeout);
        }
        finally
        {
            publication.Release.Set();
            await publishing.WaitAsync(Timeout);
            await service.StopAsync(default);
        }

        Assert.Empty((await publishing).DeliveryFailures);
        Assert.Equal(0, control.IdentityChecks);
        Assert.Empty(control.Writes);
        Assert.Equal(0, control.Reads);
        Assert.Equal("notApplied", service.Diagnostics.State);
    }

    [Fact]
    public async Task RecoveryCapturedPublicationCannotEnterRestartedRunAndNextPublishApplies()
    {
        var plans = Plans();
        var delayedPlan = Plan(1, true, 10);
        using var publication = new BlockedPublication(plans, delayedPlan);
        var control = new RecoveryControl();
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(true, true),
            control, CrashReportTestSupport.DumpSettings(), new CrashReportTestSupport.RecordingOwner(),
            NullLogger<ServiceRecoverySettings>.Instance);
        await service.StartAsync(default);
        var publishing = Task.Run(() => plans.Publish(delayedPlan));
        try
        {
            await publication.Entered.Task.WaitAsync(Timeout);
            await service.StopAsync(default).WaitAsync(Timeout);
            Assert.Empty(plans.Publish(Plan(2, false, 20)).DeliveryFailures);
            await service.StartAsync(default);
            Assert.Empty(Assert.Single(control.Writes).Actions);

            publication.Release.Set();
            Assert.Empty((await publishing.WaitAsync(Timeout)).DeliveryFailures);
            Assert.Single(control.Writes);
            // One check for the crash dump state (its first attempt) and one for the recovery configuration.
        Assert.Equal(2, control.IdentityChecks);
            Assert.Equal(1, control.Reads);

            Assert.Empty(plans.Publish(Plan(3, true, 30)).DeliveryFailures);
            Assert.Equal(2, control.Writes.Count);
            Assert.Equal(new ServiceRecoveryAction(1, 30000), control.Writes[1].Actions[0]);
        }
        finally
        {
            publication.Release.Set();
            await publishing.WaitAsync(Timeout);
            await service.StopAsync(default);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoStartCapturedPublicationDoesNotApplyAfterStop(bool isService)
    {
        var plans = Plans();
        var delayedPlan = Plan(1, true, 10) with { AutoStartEnabled = true };
        using var publication = new BlockedPublication(plans, delayedPlan);
        // The development branch rejects autostart; the installed-service branch
        // records fake SCM writes. Neither case can open a real Windows service.
        var writes = new List<bool>();
        var service = new ServiceAutoStartSettings(plans, BackendHostEnvironment.Compile(isService, true),
            NullLogger<ServiceAutoStartSettings>.Instance, writes.Add);
        await service.StartAsync(default);
        var publishing = Task.Run(() => plans.Publish(delayedPlan));
        try
        {
            await publication.Entered.Task.WaitAsync(Timeout);
            await service.StopAsync(default).WaitAsync(Timeout);
        }
        finally
        {
            publication.Release.Set();
            await publishing.WaitAsync(Timeout);
            await service.StopAsync(default);
        }

        Assert.Empty((await publishing).DeliveryFailures);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task AutoStartCapturedPublicationCannotEnterRestartedRunAndNextPublishApplies()
    {
        var plans = Plans();
        var delayedPlan = Plan(1, true, 10) with { AutoStartEnabled = true };
        using var publication = new BlockedPublication(plans, delayedPlan);
        var writes = new List<bool>();
        var service = new ServiceAutoStartSettings(plans, BackendHostEnvironment.Compile(true, true),
            NullLogger<ServiceAutoStartSettings>.Instance, writes.Add);
        await service.StartAsync(default);
        var publishing = Task.Run(() => plans.Publish(delayedPlan));
        try
        {
            await publication.Entered.Task.WaitAsync(Timeout);
            await service.StopAsync(default).WaitAsync(Timeout);
            Assert.Empty(plans.Publish(Plan(2, false, 20) with { AutoStartEnabled = false }).DeliveryFailures);
            await service.StartAsync(default);
            Assert.False(Assert.Single(writes));

            publication.Release.Set();
            Assert.Empty((await publishing.WaitAsync(Timeout)).DeliveryFailures);
            Assert.Single(writes);

            Assert.Empty(plans.Publish(Plan(3, true, 30) with { AutoStartEnabled = true }).DeliveryFailures);
            Assert.Equal(new[] { false, true }, writes);
        }
        finally
        {
            publication.Release.Set();
            await publishing.WaitAsync(Timeout);
            await service.StopAsync(default);
        }
    }

    private static RuntimePlanProvider Plans() => new(new HostManagerDeploymentState());

    private static CompiledRuntimePlan Plan(long version, bool enabled, int delay) => CompiledRuntimePlan.Default with
    {
        Version = version,
        ServiceRecovery = new(false, [], false, enabled, delay, 2),
        HostManager = HostManagerTestPlanFactory.CreatePlan() with
        {
            BindingProvenance = HostManagerTestPlanFactory.CreatePlan().BindingProvenance with
            { SettingsEffectiveSha256 = version.ToString().PadLeft(64, '0') }
        }
    };

    private sealed class BlockedPublication : IDisposable
    {
        private readonly RuntimePlanProvider plans;
        private readonly Action<CompiledRuntimePlan> callback;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public BlockedPublication(RuntimePlanProvider plans, CompiledRuntimePlan delayedPlan)
        {
            this.plans = plans;
            callback = plan =>
            {
                if (!ReferenceEquals(plan, delayedPlan)) return;
                Entered.TrySetResult();
                if (!Release.Wait(Timeout)) throw new TimeoutException("The captured publication was not released.");
            };
            plans.Published += callback;
        }

        public void Dispose()
        {
            plans.Published -= callback;
            Release.Dispose();
        }
    }

    private sealed class RecoveryControl : IServiceRecoveryControl
    {
        public int IdentityChecks, Reads;
        public List<ServiceRecoveryConfiguration> Writes { get; } = [];
        private ServiceRecoveryConfiguration actual = new(0, [], false);
        public string? GetSkipReason() { IdentityChecks++; return null; }
        public void Write(ServiceRecoveryConfiguration configuration)
        {
            Writes.Add(configuration);
            actual = configuration;
        }
        public ServiceRecoveryConfiguration Read() { Reads++; return actual; }
    }
}
