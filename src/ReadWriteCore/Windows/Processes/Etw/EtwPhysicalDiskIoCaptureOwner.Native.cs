using System.Globalization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class EtwPhysicalDiskIoCaptureOwner
{
    private NativeKernelEtwSession? nativeSession;
    private ulong nativeVersion;
    private ulong publishedNativeLoss;
    private ulong requiredNativeBinding, publishedNativeBinding;
    private PhysicalDiskIoAttributionSnapshot? nativeObservedSnapshot, nativeLegacySnapshot;

    private void BindNative(NativeKernelEtwSession? session)
    {
        nativeSession = null;
        nativeVersion = 0;
        publishedNativeLoss = 0;
        requiredNativeBinding = publishedNativeBinding = 0;
        nativeObservedSnapshot = nativeLegacySnapshot = null;
        if (session is not null)
        {
            var actual = KernelEtwNativeIoReaders.GetDiskConfiguration(session);
            var selected = new PhysicalDiskIoCaptureConfiguration(checked((int)actual.BucketMilliseconds), checked((int)actual.MaximumPairs));
            if (requestedNativeConfiguration is { } requested && requested != selected)
                throw new InvalidOperationException($"DiskIO configuration conflicts with the shared native owner: requested bucket {requested.BucketWidthMilliseconds} ms / {requested.MaximumActivePairs} pairs; actual bucket {selected.BucketWidthMilliseconds} ms / {selected.MaximumActivePairs} pairs (byte cap {actual.ByteCap}).");
            Volatile.Write(ref selectedConfiguration, selected);
            requiredNativeBinding = KernelEtwNativeIoReaders.Reset(session, 4, startedTick);
            session.RequestRefresh(startedTick);
            nativeSession = session;
        }
        else Volatile.Write(ref selectedConfiguration, requestedNativeConfiguration ?? PhysicalDiskIoCaptureConfiguration.Default);
    }

    // The caller holds eventGate, which protects only publication/cache access;
    // the native event loop never enters this managed monitor.
    private PhysicalDiskIoAttributionSnapshot ReadNative(bool includeObserved)
    {
        var session = nativeSession!;
        var before = subscription?.GetSnapshot() ?? broker.GetSnapshot();
        if (!before.IsRunning || before.Generation != generation) return EmptyNative(before, includeObserved);
        if (KernelEtwNativeIoReaders.Read(session, ref nativeVersion, view => CopyNative(view, session), out var next, out var failure))
        {
            nativeObservedSnapshot = next;
            nativeLegacySnapshot = next! with { ObservedTraffic = null };
        }
        if (failure != 0)
        {
            nativeObservedSnapshot = nativeLegacySnapshot = null;
            state = PhysicalDiskIoProviderStates.Unavailable;
            message = $"Native kernel ETW owner failed ({failure}).";
            return new(utcNow(), new Dictionary<int, PhysicalDiskProcessAttribution>(), new("etw-fileio-disk", state, message));
        }
        var after = subscription?.GetSnapshot() ?? broker.GetSnapshot();
        if (!after.IsRunning || after.Generation != before.Generation || after.EventsLost != before.EventsLost)
            return EmptyNative(after, includeObserved);
        if (nativeObservedSnapshot is null || publishedNativeLoss != (ulong)after.EventsLost || publishedNativeBinding < requiredNativeBinding)
            return EmptyNative(after, includeObserved);
        state = nativeObservedSnapshot.ProviderState.State; message = nativeObservedSnapshot.ProviderState.Message;
        return (includeObserved ? nativeObservedSnapshot : nativeLegacySnapshot)!;
    }

    private PhysicalDiskIoAttributionSnapshot CopyNative(KernelEtwNativeIoReaders.View view, NativeKernelEtwSession session)
    {
        var capturedAt = session.GetPublicationTimestamp(view.ThroughTick);
        publishedNativeLoss = view.EventsLost;
        publishedNativeBinding = view.DiskTransitionVersion;
        var complete = (view.Flags & 1) != 0 && publishedNativeBinding >= requiredNativeBinding;
        var legacy = new Dictionary<int, PhysicalDiskProcessAttribution>();
        if (complete)
            foreach (var row in view.ProcessDiskRows)
                if ((row.Flags & 1) != 0)
                    legacy.Add(checked((int)row.Pid), new(checked((int)row.Pid), row.ReadRate, row.WriteRate,
                        checked((int)row.ReadCount), checked((int)row.WriteCount)) { ProcessStartKey = row.Birth });
        var scopes = new PhysicalDiskIoScope[view.KnownDiskNumbers.Length];
        for (var i = 0; i < scopes.Length; i++) scopes[i] = new(generation, view.KnownDiskNumbers[i]);
        var rows = new PhysicalDiskProcessIoObservation[complete ? view.DiskRows.Length : 0];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = view.DiskRows[i]; var available = (row.Flags & 1) != 0;
            rows[i] = new(new(generation, row.Number), checked((int)row.Pid), available ? Number(row.Birth) : null,
                available ? Number(row.ReadBytes) : null, available ? Number(row.WriteBytes) : null);
        }
        return new(capturedAt, legacy, new("etw-fileio-disk", complete ? PhysicalDiskIoProviderStates.Running : PhysicalDiskIoProviderStates.Warming,
            complete ? "ETW DiskIO 正在采集磁盘传输完成事件。" : "ETW DiskIO 采样窗口不完整。"))
        { ObservedTraffic = new(capturedAt, view.WindowMilliseconds, rows) { Complete = complete, Disks = scopes } };
    }

    private PhysicalDiskIoAttributionSnapshot EmptyNative(KernelEtwSessionSnapshot snapshot, bool includeObserved)
    {
        var running = snapshot.IsRunning && snapshot.Generation == generation;
        state = running ? PhysicalDiskIoProviderStates.Warming : PhysicalDiskIoProviderStates.FromKernelEtwState(snapshot.State);
        message = running ? "ETW DiskIO 采样窗口不完整。" : snapshot.Message;
        var now = utcNow();
        return new(now, new Dictionary<int, PhysicalDiskProcessAttribution>(), new("etw-fileio-disk", state, message))
        { ObservedTraffic = includeObserved && running ? new(now, WindowMilliseconds, []) { Complete = false } : null };
    }

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
