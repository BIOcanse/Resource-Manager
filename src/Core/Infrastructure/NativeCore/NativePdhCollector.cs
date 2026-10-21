using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

/// <summary>Product policy adapter for the independent PDH session.</summary>
internal sealed class NativePdhCollector : NativePdhSession
{
    private static readonly TimeSpan DefaultFrameReuseWindow = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan DefaultLastGoodLifetime = TimeSpan.FromSeconds(10);
    internal NativePdhCollector(HostManagerPdhCollectorRuntime runtime, ResourceManagerLedgerOwner owner)
        : base(new NativePdhApi(), new(DefaultFrameReuseWindow, DefaultLastGoodLifetime, 5_000), TimeProvider.System, runtime,
            new PdhParsingScratch(owner.CreateScratch, owner.NativePageSize)) { }
    internal NativePdhCollector(HostManagerPdhCollectorRuntime runtime, INativePdhApi api,
        TimeSpan frameReuseWindow, TimeProvider timeProvider, TimeSpan lastGoodLifetime)
        : base(api, new(frameReuseWindow, lastGoodLifetime, 5_000), timeProvider, runtime) { }
    internal NativePdhCollector(INativePdhApi api, TimeSpan frameReuseWindow)
        : base(api, new(frameReuseWindow, DefaultLastGoodLifetime, 5_000), TimeProvider.System) { }
    internal NativePdhCollector(INativePdhApi api, TimeSpan frameReuseWindow,
        TimeProvider timeProvider, TimeSpan lastGoodLifetime)
        : base(api, new(frameReuseWindow, lastGoodLifetime, 5_000), timeProvider) { }
}
