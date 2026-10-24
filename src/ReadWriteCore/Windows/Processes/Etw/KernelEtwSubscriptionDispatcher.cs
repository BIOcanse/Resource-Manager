using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal sealed class KernelEtwSubscriptionDispatcher
{
    private BoundSubscription[] bindings = [];
    private bool initialized;
    private KernelTraceEventParser.Keywords keywords;

    // Called exactly once, before Source.Process. The parser's template table
    // remains fixed for the lifetime of the processing runtime.
    internal void AttachParser(KernelTraceEventParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        parser.ProcessStartGroup += data => Dispatch(data, static (source, value) => source.DeliverProcessStartGroup(value));
        parser.ProcessEndGroup += data => Dispatch(data, static (source, value) => source.DeliverProcessEndGroup(value));
        parser.ThreadStartGroup += data => Dispatch(data, static (source, value) => source.DeliverThreadStartGroup(value));
        parser.ThreadEndGroup += data => Dispatch(data, static (source, value) => source.DeliverThreadEndGroup(value));
        parser.ThreadCSwitch += data => Dispatch(data, static (source, value) => source.DeliverThreadCSwitch(value));
        parser.DiskIORead += data => Dispatch(data, static (source, value) => source.DeliverDiskIORead(value));
        parser.DiskIOWrite += data => Dispatch(data, static (source, value) => source.DeliverDiskIOWrite(value));
        parser.FileIOCreate += data => Dispatch(data, static (source, value) => source.DeliverFileIOCreate(value));
        parser.FileIOFileCreate += data => Dispatch(data, static (source, value) => source.DeliverFileIOFileCreate(value));
        parser.FileIOFileDelete += data => Dispatch(data, static (source, value) => source.DeliverFileIOFileDelete(value));
        parser.FileIOWrite += data => Dispatch(data, static (source, value) => source.DeliverFileIOWrite(value));
        parser.FileIORename += data => Dispatch(data, static (source, value) => source.DeliverFileIORename(value));
        parser.FileIODelete += data => Dispatch(data, static (source, value) => source.DeliverFileIODelete(value));
        parser.TcpIpSend += data => Dispatch(data, static (source, value) => source.DeliverTcpIpSend(value));
        parser.TcpIpRecv += data => Dispatch(data, static (source, value) => source.DeliverTcpIpRecv(value));
        parser.TcpIpSendIPV6 += data => Dispatch(data, static (source, value) => source.DeliverTcpIpSendIPV6(value));
        parser.TcpIpRecvIPV6 += data => Dispatch(data, static (source, value) => source.DeliverTcpIpRecvIPV6(value));
        parser.UdpIpSend += data => Dispatch(data, static (source, value) => source.DeliverUdpIpSend(value));
        parser.UdpIpRecv += data => Dispatch(data, static (source, value) => source.DeliverUdpIpRecv(value));
        parser.UdpIpSendIPV6 += data => Dispatch(data, static (source, value) => source.DeliverUdpIpSendIPV6(value));
        parser.UdpIpRecvIPV6 += data => Dispatch(data, static (source, value) => source.DeliverUdpIpRecvIPV6(value));
        parser.ImageDCStart += data => Dispatch(data, static (source, value) => source.DeliverImageDCStart(value));
        parser.ImageLoad += data => Dispatch(data, static (source, value) => source.DeliverImageLoad(value));
        parser.ImageUnload += data => Dispatch(data, static (source, value) => source.DeliverImageUnload(value));
        parser.PerfInfoISR += data => Dispatch(data, static (source, value) => source.DeliverPerfInfoISR(value));
        parser.PerfInfoDPC += data => Dispatch(data, static (source, value) => source.DeliverPerfInfoDPC(value));
        parser.PerfInfoThreadedDPC += data => Dispatch(data, static (source, value) => source.DeliverPerfInfoThreadedDPC(value));
        parser.PerfInfoTimerDPC += data => Dispatch(data, static (source, value) => source.DeliverPerfInfoTimerDPC(value));
    }

    internal void InitializeSubscriptions(
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context)
    {
        if (initialized) throw new InvalidOperationException("Kernel subscriptions are already initialized.");
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
            // Withdrawing the last callbacks does not change the native flags.
            // The owner attempts stop and retains this session if control is denied.
            CancelAll();
            return;
        }
        var desiredKeywords = subscriptions.Aggregate(KernelTraceEventParser.Keywords.None,
            static (all, subscription) => all | subscription.Keywords);
        var desiredFlags = KernelEtwSessionKeywordControl.CompileSystemTraceFlags(desiredKeywords);
        try
        {
            if (updateKeywords is not null
                && desiredFlags != KernelEtwSessionKeywordControl.CompileSystemTraceFlags(keywords))
                updateKeywords(desiredKeywords);
        }
        catch (Exception failure) when (KernelEtwSessionKeywordControl.IsAccessDenied(failure))
        {
            // Control denial leaves the old flags enabled. Bind only covered leases;
            // pending leases must not initialize consumer state on each retry.
            PublishSubscriptions(subscriptions, context,
                KernelEtwSessionKeywordControl.CompileSystemTraceFlags(keywords));
            throw;
        }

        keywords = desiredKeywords;
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
        foreach (var subscription in subscriptions)
        {
            if ((KernelEtwSessionKeywordControl.CompileSystemTraceFlags(subscription.Keywords) & ~enabledFlags) != 0)
                continue;
            if (retained.Remove(subscription.LeaseId, out var existing))
            {
                next.Add(existing);
                continue;
            }
            var source = new KernelEtwEventSource();
            subscription.Bind(source, context);
            next.Add(new(subscription.LeaseId, source));
            added.Add(source);
        }

        foreach (var removed in retained.Values) removed.Source.Cancel();
        foreach (var source in added) source.Activate();
        Volatile.Write(ref bindings, next.ToArray());
    }

    internal void CancelAll()
    {
        foreach (var binding in Interlocked.Exchange(ref bindings, []))
            binding.Source.Cancel();
    }

    internal void Dispatch<T>(T data, Action<KernelEtwEventSource, T> deliver)
    {
        foreach (var binding in Volatile.Read(ref bindings))
            binding.Source.Dispatch(data, deliver);
    }

    private sealed record BoundSubscription(long LeaseId, KernelEtwEventSource Source);
}
