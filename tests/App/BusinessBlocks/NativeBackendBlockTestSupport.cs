using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

internal static class NativeBackendBlockTestSupport
{
    internal static NativeControl CreateControl(IEnumerable<BusinessBlockRegistration> registrations,
        uint profile = 1, int historyCapacity = 256)
        => new(BackendNativeControlFactory.WithWrappers(registrations, NativeControl.GetProfileHostBlockUids(profile))
            .Select(value => value.Id), new NativeControlOptions
        {
            Profile = profile,
            DemandItemUid = StateItemUids.BlockDemand,
            ShutdownItemUid = StateItemUids.ProgramShuttingDown,
            HistoryCapacity = historyCapacity
        }, NullLogger<NativeControl>.Instance);

    internal static StateAuthority CreateAuthority(NativeControl control)
        => control.CreateAuthority(StateRegistry.Items, StateRegistry.Blocks);

    /// <summary>Creates the full compiled tree with only explicitly targeted factories replaced.</summary>
    internal static NativeBackendBlockFixture CreateFullTreeFixture(
        IReadOnlyDictionary<int, IBusinessBlock> replacements, int historyCapacity = 256)
        => CreateFullTreeFixture(replacements.ToDictionary(value => value.Key,
            value => new Func<IServiceProvider, IBusinessBlock>(_ => value.Value)), historyCapacity);

    internal static NativeBackendBlockFixture CreateFullTreeFixture(
        IReadOnlyDictionary<int, Func<IServiceProvider, IBusinessBlock>> replacements, int historyCapacity = 256)
    {
        var hostUids = StateRegistry.Blocks.Where(uid => uid != BlockUids.Program
            && uid != BlockUids.AutomaticProductUpdateService).ToArray();
        var hostUidSet = hostUids.ToHashSet();
        if (replacements.Keys.Any(uid => !hostUidSet.Contains(uid)))
            throw new ArgumentException("Every custom fixture factory must name a full-tree host UID.", nameof(replacements));
        var registrations = hostUids.Select(uid => new BusinessBlockRegistration(uid,
            provider => replacements.TryGetValue(uid, out var factory) ? factory(provider) : new IdleNativeBlock())).ToArray();
        var native = CreateControl(registrations, historyCapacity: historyCapacity);
        var authority = CreateAuthority(native);
        var availability = new NativeEtwSessionAvailability(authority);
        var provider = new ServiceCollection().AddSingleton(availability).BuildServiceProvider();
        try
        {
            var scheduler = new BusinessBlockScheduler(provider, registrations, native, authority);
            return new(registrations, native, authority, availability, scheduler, provider);
        }
        catch
        {
            provider.Dispose();
            native.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Completes a DI host with the compiled full backend UID set and inert entries.</summary>
    internal static void AddFullTreeHostFixture(IServiceCollection services)
    {
        ResourceManagerServiceCollectionExtensions.AddBusinessBlockController(services);
        var registrations = services.Select(value => value.ImplementationInstance)
            .OfType<BusinessBlockRegistration>().ToArray();
        var included = registrations.Select(value => value.Id).ToHashSet();
        var hostUids = StateRegistry.Blocks.Where(uid => uid != BlockUids.Program
            && uid != BlockUids.AutomaticProductUpdateService).ToArray();
        foreach (var uid in hostUids)
        {
            if (included.Add(uid))
                services.AddSingleton(new BusinessBlockRegistration(uid, _ => new IdleNativeBlock()));
        }

        var complete = services.Select(value => value.ImplementationInstance)
            .OfType<BusinessBlockRegistration>().ToArray();
        services.AddSingleton<NativeControl>(_ => CreateControl(complete));
        services.AddSingleton<StateAuthority>(provider => CreateAuthority(provider.GetRequiredService<NativeControl>()));
        services.AddSingleton<NativeEtwSessionAvailability>(provider =>
            new(provider.GetRequiredService<StateAuthority>()));
    }

    private sealed class IdleNativeBlock : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
}

internal sealed class NativeBackendBlockFixture(
    IReadOnlyList<BusinessBlockRegistration> registrations,
    NativeControl native,
    StateAuthority authority,
    NativeEtwSessionAvailability availability,
    BusinessBlockScheduler scheduler,
    ServiceProvider provider) : IAsyncDisposable
{
    private IBusinessBlockDependency? nativeEtwGroupDemand;
    internal IReadOnlyList<BusinessBlockRegistration> Registrations { get; } = registrations;
    internal NativeControl Native { get; } = native;
    internal StateAuthority Authority { get; } = authority;
    internal NativeEtwSessionAvailability Availability { get; } = availability;
    internal BusinessBlockScheduler Scheduler { get; } = scheduler;

    internal void HoldNativeEtwGroup()
    {
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration,
            nameof(NativeBackendBlockFixture), "hold-native-etw-group", 0);
        SetNativeEtwGroupDemandAsync(true, reason).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        BusinessBlockTestSupport.Until(() =>
            Scheduler.ReadState(BlockUids.NativeEtwLifetimeService) == BusinessBlockState.Running)
            .GetAwaiter().GetResult();
    }

    internal Task SetNativeEtwGroupDemandAsync(bool hold, BusinessBlockReason reason)
    {
        if (hold)
        {
            nativeEtwGroupDemand ??= Scheduler.AcquireDependency(BlockUids.NativeEtwLifetimeService, reason);
            return nativeEtwGroupDemand.Ready;
        }

        var demand = nativeEtwGroupDemand;
        nativeEtwGroupDemand = null;
        return demand?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (nativeEtwGroupDemand is { } demand)
        {
            nativeEtwGroupDemand = null;
            await demand.DisposeAsync();
        }
        await Scheduler.DisposeAsync();
        await Native.DisposeAsync();
        await provider.DisposeAsync();
    }
}
