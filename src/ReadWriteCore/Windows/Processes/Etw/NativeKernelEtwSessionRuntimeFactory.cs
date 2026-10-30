using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class NativeKernelEtwSessionRuntimeFactory(
    IKernelEtwRegionProvider regionProvider,
    Func<NativeKernelEtwSessionConfig> readConfig,
    Action<string>? beforeCreate = null,
    IKernelEtwConsumerThreadObserver? observer = null) : IKernelEtwSessionRuntimeFactory
{
    public void ValidateRequest(KernelEtwSubscriptionRequest request)
        => NativeKernelEtwSessionRuntime.ValidateNativeOutputs(request.Id, request.NativeOutputs);

    public bool TryGetAvailability(out string state, out string message)
    {
        state = "Unavailable";
        if (regionProvider is not IKernelEtwAllocationProvider)
        { message = "Native kernel ETW requires an output-scoped allocation provider."; return false; }
        if (!OperatingSystem.IsWindows()) { message = "Native kernel ETW requires Windows."; return false; }
        if (TraceEventSession.IsElevated() != true) { message = "Native kernel ETW requires administrator or performance-log permissions."; return false; }
        // Resolve the actual packaged ABI without constructing TraceEvent's lazy Source.
        if (!NativeLibrary.TryLoad("ResourceManager.NativeCore", typeof(NativeKernelEtwSession).Assembly,
                DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, out var library))
        { message = "The native kernel ETW library is unavailable."; return false; }
        try
        {
            foreach (var entry in RequiredExports)
                if (!NativeLibrary.TryGetExport(library, entry, out _))
                { message = $"The native kernel ETW library lacks {entry}."; return false; }
        }
        finally { NativeLibrary.Free(library); }
        state = "Available";
        message = "Native kernel ETW is available.";
        return true;
    }

    private static readonly string[] RequiredExports =
    [
        "rm_kernel_etw_shared_create_with_allocator", "rm_kernel_etw_shared_set_outputs",
        "rm_kernel_etw_shared_open", "rm_kernel_etw_shared_process",
        "rm_kernel_etw_shared_stop", "rm_kernel_etw_shared_request_stop", "rm_kernel_etw_shared_close_trace", "rm_kernel_etw_shared_destroy",
        "rm_kernel_etw_shared_events_lost", "rm_kernel_etw_shared_request_refresh",
        "rm_kernel_etw_shared_set_publication_callback", "rm_kernel_etw_shared_query_owner_usage",
        "rm_kernel_etw_shared_query_region_usage", "rm_kernel_etw_shared_release",
        "rm_kernel_etw_shared_configure_processors", "rm_kernel_etw_shared_set_enabled",
        "rm_kernel_etw_shared_submit",
        "rm_kernel_etw_shared_submit_tracked", "rm_kernel_etw_shared_reset_io_at", "rm_kernel_etw_shared_query_failure",
        "rm_kernel_etw_shared_query_disk_config",
        "rm_kernel_etw_shared_configure_consumer_counter", "rm_kernel_etw_shared_query_consumer_counter",
        "rm_kernel_etw_shared_retire_consumer_counter", "rm_kernel_etw_shared_take_retired_consumer_counter",
        "rm_kernel_etw_shared_query_event_counter", "rm_kernel_etw_shared_query_residency_diagnostics",
        "rm_kernel_etw_shared_query_interrupt_metadata", "rm_kernel_etw_shared_acquire_outputs",
        "rm_kernel_etw_shared_query_renderer_stamp",
        "rm_kernel_etw_shared_query_failure_detail",
        "rm_kernel_etw_shared_request_residency", "rm_kernel_etw_shared_acquire_residency", "rm_kernel_etw_shared_acquire_interrupts",
        "rm_kernel_etw_shared_acquire_interrupts_at",
        "rm_kernel_etw_shared_acquire_thread_diagnostics_at", "rm_kernel_etw_shared_acquire_io",
        "rm_kernel_etw_shared_acquire_notifications", "rm_kernel_etw_shared_acquire_renderer"
    ];

    public IKernelEtwSessionRuntime Create(string sessionName, KernelTraceEventParser.Keywords keywords,
        KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        => CreateCore(sessionName, keywords, context, subscriptions, realtime: true);

    /// <summary>Uses the same shared owner, publication and release path against an existing ETL, without starting a provider.</summary>
    public IKernelEtwSessionRuntime CreateRecorded(string etlPath, KernelEtwSessionContext context,
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        => CreateCore(etlPath, KernelTraceEventParser.Keywords.None, context, subscriptions, realtime: false);

    private IKernelEtwSessionRuntime CreateCore(string name, KernelTraceEventParser.Keywords keywords,
        KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions, bool realtime)
    {
        NativeKernelEtwSessionRuntime.ValidateSubscriptions(subscriptions);
        var config = readConfig();
        if (realtime) config.StartedTickMilliseconds = Environment.TickCount64;
        var publicationOriginUtc = DateTimeOffset.UtcNow;
        if (realtime) config.Residency.QpcFrequency = Stopwatch.Frequency;
        config.StampAbi(context);
        if (regionProvider is not IKernelEtwAllocationProvider allocations)
            throw new InvalidOperationException("Native kernel ETW requires an output-scoped allocation provider.");
        // The first resource hold creates this runtime. Its native pointer
        // callbacks need the backing continuously until the last hold's Stop
        // joins processing, drains publications and disposes the session.
        // Failed creation below follows that same cleanup scope.
        var allocator = allocations.CreateAllocator(name, config.ByteCap);
        var session = new NativeKernelEtwSession(allocator, observer is null ? null : observer.ReportFailure);
        var dispatcher = new KernelEtwSubscriptionDispatcher(session);
        var runtime = new NativeKernelEtwSessionRuntime(name, session, dispatcher, observer);
        try
        {
            // Put allocator configuration under the same retained cleanup
            // obligation as native creation. A configuration error must not
            // orphan its callbacks or an unsuccessful Ledger release.
            allocator.ConfigureOwnerByteCaps([
                config.ByteCap, config.Residency.ByteCap, config.Interrupts.ByteCap,
                config.DiskByteCap, config.NetworkByteCap, config.FileByteCap,
                config.HistoryByteCap, config.ByteCap, config.ByteCap
            ]);
            session.Initialize(name, config, keywords, beforeCreate, publicationOriginUtc, realtime);
            dispatcher.InitializeSubscriptions(subscriptions, context);
            return runtime;
        }
        catch (Exception startFailure)
        {
            try { runtime.Dispose(); }
            catch (Exception cleanupFailure)
            {
                // Retain the partial runtime; subsequent explicit owner disposal retries
                // unconfirmed steps rather than orphaning its region/session handle.
                throw new KernelEtwSessionCreationCleanupException(startFailure, cleanupFailure, runtime);
            }
            throw;
        }
    }
}
