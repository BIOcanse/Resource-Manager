using System.Globalization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class EtwNetworkTcpIpCaptureOwner
{
    private NativeKernelEtwSession? nativeSession;
    private ulong nativeVersion;
    private ulong requiredNativeTransition;
    private ulong publishedNativeTransition;
    private ulong publishedNativeLoss;
    private NetworkAttributionSnapshot? nativeObservedSnapshot, nativeLegacySnapshot;

    private void BindNative(NativeKernelEtwSession? session)
    {
        nativeSession = session;
        nativeVersion = 0;
        requiredNativeTransition = 0;
        publishedNativeTransition = 0;
        publishedNativeLoss = 0;
        nativeObservedSnapshot = nativeLegacySnapshot = null;
        if (session is not null)
        {
            var tick = readTickMilliseconds();
            requiredNativeTransition = KernelEtwNativeIoReaders.Reset(session, 8, tick);
            session.RequestRefresh(tick);
        }
    }

    private NetworkAttributionSnapshot ReadNative(bool includeObserved)
    {
        var session = nativeSession!;
        var before = CaptureSnapshot();
        if (!before.IsRunning || before.Generation != sessionGeneration) return EmptyNative(before);
        if (KernelEtwNativeIoReaders.Read(session, ref nativeVersion, view => CopyNative(view, session), out var next, out var failure))
        {
            nativeObservedSnapshot = next;
            nativeLegacySnapshot = next! with { ObservedTraffic = null };
        }
        if (failure != 0)
        {
            nativeObservedSnapshot = nativeLegacySnapshot = null;
            state = NetworkAttributionProviderStates.Unavailable;
            message = $"Native kernel ETW owner failed ({failure}).";
            return new(utcNow(), new Dictionary<int, NetworkProcessAttribution>(), new("etw-network-tcpip", state, message), new Dictionary<int, NetworkProcessAttribution>());
        }
        var after = CaptureSnapshot();
        if (!after.IsRunning || after.Generation != before.Generation || after.EventsLost != before.EventsLost)
            return EmptyNative(after);
        if (nativeObservedSnapshot is null || publishedNativeTransition < requiredNativeTransition
            || publishedNativeLoss != (ulong)after.EventsLost)
            return EmptyNative(after);
        state = nativeObservedSnapshot.ProviderState.State; message = nativeObservedSnapshot.ProviderState.Message;
        return (includeObserved ? nativeObservedSnapshot : nativeLegacySnapshot)!;
    }

    private NetworkAttributionSnapshot CopyNative(KernelEtwNativeIoReaders.View view, NativeKernelEtwSession session)
    {
        var capturedAt = session.GetPublicationTimestamp(view.ThroughTick);
        publishedNativeTransition = view.NetworkTransitionVersion;
        publishedNativeLoss = view.EventsLost;
        var complete = (view.Flags & 2) != 0 && view.NetworkTransitionVersion >= requiredNativeTransition;
        var raw = new Dictionary<int, NetworkProcessAttribution>();
        var external = new Dictionary<int, NetworkProcessAttribution>();
        var rows = new ProcessNetworkTrafficObservation[complete ? view.NetworkRows.Length : 0];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = view.NetworkRows[i]; var available = (row.Flags & 1) != 0; var process = checked((int)row.Pid);
            rows[i] = new(process, available ? Number(row.Birth) : null, available ? Number(row.ReceiveBytes) : null,
                available ? Number(row.SendBytes) : null, available ? row.ReceiveRate : null, available ? row.SendRate : null);
            if (!available) continue;
            raw.Add(process, new(process, row.ReceiveRate, row.SendRate, 0, 0, false, false, checked((int)row.Count)) { ProcessStartKey = row.Birth });
            if (row.ExternalCount != 0)
                external.Add(process, new(process, row.ExternalReceiveRate, row.ExternalSendRate, 0, 0, false, false,
                    checked((int)row.ExternalCount)) { ProcessStartKey = row.Birth });
        }
        return new(capturedAt, external, new("etw-network-tcpip", complete ? NetworkAttributionProviderStates.Running : NetworkAttributionProviderStates.Warming,
            "ETW 网络事件归因正在采集进程收发事件。"), raw)
        { ObservedTraffic = complete ? new(capturedAt, view.WindowMilliseconds, rows) : null };
    }

    private NetworkAttributionSnapshot EmptyNative(KernelEtwSessionSnapshot snapshot)
    {
        state = snapshot.IsRunning ? NetworkAttributionProviderStates.Warming : NetworkAttributionProviderStates.FromKernelEtwState(snapshot.State);
        message = snapshot.Message;
        return new(utcNow(), new Dictionary<int, NetworkProcessAttribution>(), new("etw-network-tcpip", state, message), new Dictionary<int, NetworkProcessAttribution>());
    }

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
