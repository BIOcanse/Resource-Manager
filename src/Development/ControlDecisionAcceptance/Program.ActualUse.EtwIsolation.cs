using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Application.CpuTopology;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private static void ConfigureActualUseEtw(IServiceCollection services)
    {
        // Existing production constructor overrides retain real native factories
        // and cleanup, but do not call the product's optional orphan reconciler.
        services.Replace(ServiceDescriptor.Singleton<KernelEtwSessionBroker>(provider =>
        {
            var allocations = new NativeKernelEtwLedgerRegionProvider(provider.GetRequiredService<ResourceManagerLedgerOwner>());
            var factory = new NativeKernelEtwSessionRuntimeFactory(allocations, () =>
            {
                var plan = provider.GetRequiredService<IRuntimePlanProvider>().Current.HostManager.RequirePublished();
                return KernelEtwNativeSettings.CreateConfiguration(
                    plan.KernelEtwLimits ?? throw new InvalidOperationException("Kernel ETW limits are unpublished."),
                    plan.CpuCoreResidency, checked((uint)plan.CpuCoreResidency.PhysicalCoreByLogicalProcessor.Count));
            }, name => RequireOwnSessionName(ResourceManagerEtwSessionNames.KernelPrefix, name),
                provider.GetRequiredService<KernelEtwConsumerThreadDiagnostics>());
            return new KernelEtwSessionBroker(factory, provider.GetRequiredService<ILogger<KernelEtwSessionBroker>>());
        }));
        services.AddSingleton<ActualUseEtwCounters>();
        services.Replace(ServiceDescriptor.Singleton<IKernelEtwSessionBroker>(provider => provider.GetRequiredService<ActualUseEtwCounters>()));
        services.AddSingleton<ActualUseSmartResidencyObserver>();
        services.Replace(ServiceDescriptor.Singleton<ICpuCoreResidencyReader>(provider => provider.GetRequiredService<ActualUseSmartResidencyObserver>()));
        services.Replace(ServiceDescriptor.Singleton<PresentFrameEtwSource>(provider => new(
            provider.GetRequiredService<ILogger<PresentFrameEtwSource>>(),
            provider.GetRequiredService<BusinessBlockScheduler>(),
            provider.GetRequiredService<BusinessBlockDiagnosticsRegistry>(),
            provider.GetRequiredService<NativeEtwSessionAvailability>(),
            name =>
            {
                RequireOwnSessionName(ResourceManagerEtwSessionNames.PresentFramesPrefix, name);
                return new TraceEventPresentFrameEtwSession(name, provider.GetRequiredService<ILogger<PresentFrameEtwSource>>());
            }, TimeProvider.System, provider.GetService<IEtwConsumerThreadObserver>())));
        services.Replace(ServiceDescriptor.Singleton<GpuAllocationCollector>(provider => new(
            provider.GetRequiredService<ILogger<GpuAllocationCollector>>(),
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<BusinessBlockDiagnosticsRegistry>(),
            provider.GetRequiredService<NativeEtwSessionAvailability>(),
            new ActualUseGpuAllocationFactory(provider.GetRequiredService<ILogger<GpuAllocationCollector>>(), provider.GetService<IEtwConsumerThreadObserver>()),
            TimeProvider.System, consumerThreadObserver: provider.GetService<IEtwConsumerThreadObserver>())));
        // VidMm already directly constructs TraceEventVidMmSession(name, provider,
        // keywords, logger). Neither that constructor nor the zone calls orphan recovery.
        // It keeps the product's exact-own PID/start-time session name and cleanup.
    }

    private static void RequireOwnSessionName(string prefix, string name)
    {
        if (!string.Equals(ResourceManagerEtwSessionNames.Create(prefix, ResourceManagerEtwSessionNames.CurrentOwner),
            name, StringComparison.Ordinal)) throw new InvalidDataException("Private probe factory received another process's session name.");
    }

    private sealed class ActualUseGpuAllocationFactory(ILogger logger, IEtwConsumerThreadObserver? observer) : IGpuAllocationSessionFactory
    {
        public IGpuAllocationSession Create(string name)
        {
            RequireOwnSessionName(ResourceManagerEtwSessionNames.GpuAllocationsPrefix, name);
            TraceEventSession? session = null;
            try
            {
                session = new TraceEventSession(name, TraceEventSessionOptions.Create)
                    { StopOnDispose = true, BufferSizeMB = 16, EnableProviderTimeoutMSec = 2000 };
                return new TraceEventGpuAllocationSession(session, logger, observer);
            }
            catch (Exception failure)
            {
                if (session is not null)
                {
                    // Creation has not returned, so no consumer processing thread
                    // has started. Use public session cleanup, then verify absence.
                    var cleanupFailures = new List<Exception>();
                    try
                    {
                        if (!session.Stop(noThrow: false) && QueryOwnedKernel(name).Exists)
                            cleanupFailures.Add(new IOException("Stop did not release the private GPU allocation session."));
                    }
                    catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 4201) { }
                    catch (Exception error) { cleanupFailures.Add(error); }
                    try { session.Dispose(); }
                    catch (Exception error) { cleanupFailures.Add(error); }
                    try
                    {
                        if (QueryOwnedKernel(name).Exists)
                            cleanupFailures.Add(new IOException("Private GPU allocation session still exists after creation cleanup."));
                    }
                    catch (Exception error) { cleanupFailures.Add(error); }
                    if (cleanupFailures.Count != 0)
                        throw new IOException("Private GPU allocation session creation cleanup was not confirmed.",
                            new AggregateException(cleanupFailures.Prepend(failure)));
                }
                throw;
            }
        }
    }
}
