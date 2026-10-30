using Microsoft.Diagnostics.Tracing.Parsers;
using System.Collections.Concurrent;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal sealed class KernelEtwSubscriptionDispatcher(
    NativeKernelEtwSession nativeSession,
    Action<KernelEtwNativeOutputs>? updateOutputs = null)
{
    private BoundSubscription[] bindings = [];
    internal readonly EtwEventDropCounter[] Counters = Enum.GetNames<KernelEtwEventKind>()
        .Select((name, index) => new EtwEventDropCounter(name, () => nativeSession.ReadPhysicalCounter((uint)index, name))).ToArray();
    private KernelEtwSessionContext? runContext;
    private EtwEventDropSession? eventDrops;
    private readonly ConcurrentDictionary<string, EtwEventDropCounter> consumerCounters = new(StringComparer.Ordinal);
    private bool initialized;
    private KernelTraceEventParser.Keywords keywords;
    private KernelEtwNativeOutputs outputs;
    private IDisposable? publicationObservation;
    private readonly Action<KernelEtwNativeOutputs> applyOutputs = updateOutputs ?? nativeSession.UpdateOutputs;
    internal readonly EtwEventDropCounter PublicationCounter = new("PublishedWindow");

    internal void InitializeSubscriptions(
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context)
    {
        if (initialized) throw new InvalidOperationException("Kernel subscriptions are already initialized.");
        runContext = context;
        ReplaceSubscriptions(subscriptions, context, null);
        initialized = true;
    }

    internal void UpdateSubscriptions(
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context,
        Action<KernelTraceEventParser.Keywords> updateKeywords)
    {
        if (!initialized) throw new InvalidOperationException("Kernel subscriptions are not initialized.");
        ArgumentNullException.ThrowIfNull(updateKeywords);
        ReplaceSubscriptions(subscriptions, context, updateKeywords);
    }

    private void ReplaceSubscriptions(
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context,
        Action<KernelTraceEventParser.Keywords>? updateKeywords)
    {
        if (subscriptions.Count == 0)
        {
            // A denied provider stop can leave physical flags enabled, but no
            // child output remains in use after the last holder returns it.
            ApplyOutputs(KernelEtwNativeOutputs.None);
            CancelAll();
            return;
        }
        var desiredKeywords = subscriptions.Aggregate(KernelTraceEventParser.Keywords.None,
            static (all, subscription) => all | subscription.Keywords);
        var desiredFlags = KernelEtwSessionKeywordControl.CompileSystemTraceFlags(desiredKeywords);
        var previousOutputs = outputs;
        try
        {
            if (updateKeywords is not null
                && desiredFlags != KernelEtwSessionKeywordControl.CompileSystemTraceFlags(keywords))
            {
                // A newly enabled ImageLoad flag emits its one-time rundown, kernel
                // modules first, during this update. Queue the requested child
                // outputs before it so the consumer records that rundown; holder
                // bindings still wait until the new flags are enabled.
                ApplyOutputs(previousOutputs | subscriptions.Aggregate(KernelEtwNativeOutputs.None,
                    static (mask, subscription) => mask | subscription.NativeOutputs));
                updateKeywords(desiredKeywords);
            }
        }
        catch (Exception failure) when (KernelEtwSessionKeywordControl.IsAccessDenied(failure))
        {
            // Control denial leaves the old flags enabled. Bind only covered leases;
            // pending leases must not initialize consumer state on each retry, and
            // their prepared outputs are returned with the covered set.
            PublishSubscriptions(subscriptions, context,
                KernelEtwSessionKeywordControl.CompileSystemTraceFlags(keywords));
            throw;
        }
        catch
        {
            // Any other control failure stops this runtime. Return the prepared
            // outputs first; no binding uses them.
            ApplyOutputs(previousOutputs);
            throw;
        }

        keywords = desiredKeywords;
        eventDrops?.EnableProvider("Windows Kernel Trace", KernelEtwSessionKeywordControl.CompileSystemTraceFlags(keywords));
        PublishSubscriptions(subscriptions, context, desiredFlags);
    }

    private void PublishSubscriptions(
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context,
        ulong enabledFlags)
    {
        var previous = Volatile.Read(ref bindings);
        var retained = previous.ToDictionary(static binding => binding.LeaseId);
        var next = new List<BoundSubscription>();
        var added = new List<KernelEtwEventSource>();
        var covered = subscriptions.Where(subscription =>
            (KernelEtwSessionKeywordControl.CompileSystemTraceFlags(subscription.Keywords) & ~enabledFlags) == 0).ToArray();
        var desiredOutputs = covered.Aggregate(KernelEtwNativeOutputs.None, static (mask, subscription) => mask | subscription.NativeOutputs);
        var previousOutputs = outputs;
        try
        {
            // New child state is prepared before Bind queues its reset commands.
            // Old outputs remain until every new binding has been built.
            ApplyOutputs(previousOutputs | desiredOutputs);
            foreach (var subscription in covered)
            {
                if (retained.Remove(subscription.LeaseId, out var existing))
                {
                    next.Add(existing);
                    continue;
                }
                var source = new KernelEtwEventSource(nativeSession, subscription.Id, subscription.NativeOutputs, context.Generation, ConsumerCounter);
                added.Add(source);
                subscription.Bind(source, context);
                next.Add(new(subscription.LeaseId, source));
            }
            ApplyOutputs(desiredOutputs);
            publicationObservation ??= nativeSession.ObservePublications(DispatchPublication);
            foreach (var source in added) source.Activate();
        }
        catch
        {
            // Binding is unpublished until it succeeds. Never leave observers
            // installed on a partially built source after a failed callback.
            foreach (var source in added) source.Cancel();
            ApplyOutputs(previousOutputs);
            throw;
        }

        foreach (var removed in retained.Values) removed.Source.Cancel();
        Volatile.Write(ref bindings, next.ToArray());
    }

    internal void CancelAll()
    {
        foreach (var binding in Interlocked.Exchange(ref bindings, []))
            binding.Source.Cancel();
        Interlocked.Exchange(ref publicationObservation, null)?.Dispose();
    }

    private void ApplyOutputs(KernelEtwNativeOutputs desired)
    {
        if (outputs == desired) return;
        applyOutputs(desired);
        outputs = desired;
    }

    // This is the production native publication signal. Payloads stay leased
    // in NativeCore and are read by each holder's existing business worker.
    private void DispatchPublication()
    {
        PublicationCounter.Receive();
        var used = false;
        foreach (var binding in Volatile.Read(ref bindings))
            used |= binding.Source.DispatchPublication();
        if (!used) PublicationCounter.Drop();
    }

    internal EtwEventDropSession RegisterDiagnostics(string name, Func<int> eventsLost)
    {
        eventDrops = EtwEventDropDiagnostics.Start(name, eventsLost, Counters, runContext?.Generation, runContext?.StartedAt);
        foreach (var counter in consumerCounters) eventDrops.RegisterCounter(counter.Key, counter.Value);
        eventDrops.RegisterCounter("PublishedWindow", PublicationCounter);
        eventDrops.EnableProvider("Windows Kernel Trace", keywords == KernelTraceEventParser.Keywords.None
            ? 0 : KernelEtwSessionKeywordControl.CompileSystemTraceFlags(keywords));
        return eventDrops;
    }

    internal void ConfirmNativeRelease() => eventDrops?.ConfirmNativeRelease();

    private EtwEventDropCounter ConsumerCounter(string name, Func<EtwEventKindDrops?>? readNative, bool supplementReceived)
    {
        var counter = consumerCounters.GetOrAdd(name, kind => new(kind, readNative, supplementReceived));
        eventDrops?.RegisterCounter(name, counter);
        return counter;
    }

    private sealed record BoundSubscription(long LeaseId, KernelEtwEventSource Source);
}

internal enum KernelEtwEventKind
{
    ProcessStartGroup,
    ProcessEndGroup,
    ThreadStartGroup,
    ThreadEndGroup,
    ThreadCSwitch,
    DiskIORead,
    DiskIOWrite,
    FileIOCreate,
    FileIOFileCreate,
    FileIOFileDelete,
    FileIOWrite,
    FileIORename,
    FileIODelete,
    TcpIpSend,
    TcpIpRecv,
    TcpIpSendIPV6,
    TcpIpRecvIPV6,
    UdpIpSend,
    UdpIpRecv,
    UdpIpSendIPV6,
    UdpIpRecvIPV6,
    ImageDCStart,
    ImageLoad,
    ImageUnload,
    PerfInfoISR,
    PerfInfoDPC,
    PerfInfoThreadedDPC,
    PerfInfoTimerDPC
}
