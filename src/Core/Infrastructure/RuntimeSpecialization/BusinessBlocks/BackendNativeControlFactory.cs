using ResourceManager.App.StateItems;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

internal static class BackendNativeControlFactory
{
    private static readonly HashSet<int> WrapperUids =
    [
        BlockUids.Backend, BlockUids.ServiceHost, BlockUids.Monitoring, BlockUids.HardwareControl,
        BlockUids.Optimization, BlockUids.Reports, BlockUids.SoftwareManagement,
        BlockUids.TrustedAccess, BlockUids.PublicServices
    ];

    internal static BusinessBlockRegistration[] WithWrappers(
        IEnumerable<BusinessBlockRegistration> registrations, IEnumerable<int> profileHostUids)
    {
        var bindings = registrations.ToDictionary(value => value.Id);
        foreach (var uid in profileHostUids)
        {
            if (WrapperUids.Contains(uid))
                bindings.TryAdd(uid, new BusinessBlockRegistration(uid, _ => new BackendDomainBlock()));
        }
        return bindings.Values.ToArray();
    }

    internal static NativeControl Create(IServiceProvider services)
    {
        var registrations = services.GetServices<BusinessBlockRegistration>().ToArray();
        var limits = BackendControlSettings.ProductDefaults;
        var capabilities = services.GetRequiredService<StartupCapabilitySet>();
        var environment = services.GetRequiredService<BackendHostEnvironment>();
        var profile = capabilities.ProfileId switch
        {
            StartupCapabilitySet.NormalReadOnlyProfileId => 3u,
            StartupCapabilitySet.FullProfileId => environment.IsWindowsService && capabilities.Allows(StartupCapability.MutablePersistence) ? 2u : 1u,
            _ => throw new ArgumentException($"No native control tree is declared for startup profile '{capabilities.ProfileId}'.")
        };
        var bindings = WithWrappers(registrations, NativeControl.GetProfileHostBlockUids(profile));
        return new(bindings.Select(value => value.Id), new NativeControlOptions
        {
            Profile = profile,
            DemandItemUid = StateItemUids.BlockDemand, ShutdownItemUid = StateItemUids.ProgramShuttingDown,
            WorkerCount = limits.WorkerCount, HistoryCapacity = limits.HistoryCapacity,
            DemandCapacity = limits.DemandCapacity, DemandBytes = limits.DemandBytes,
            CompletionCapacity = limits.CompletionCapacity, ReaderCapacity = limits.ReaderCapacity,
            ReaderBytes = limits.ReaderBytes, WorkerArenaBytes = limits.WorkerArenaBytes,
            ReasonTextCapacity = limits.ReasonTextCapacity
        }, services.GetRequiredService<ILogger<NativeControl>>());
    }
}
