using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Diagnostics;

/// <summary>Explicit bounded WHEA history, shared by direct reads and original
/// subscription capture. Construction does not query or create native handles.</summary>
public sealed class WindowsWheaInformationOwner : IDisposable
{
    public const string CollectionItemId = "information.whea-history";
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly Func<WindowsWheaHistoryRequest, CancellationToken, WindowsWheaHistoryCapture> read;
    private readonly InformationReadWriteBackend backend;
    private readonly TimeProvider clock;
    private sealed record Publication(WindowsWheaHistoryCapture History, DeviceInformationSnapshot Fields);
    private Publication? current;
    private int disposed;
    public WindowsWheaHistoryRequest Request { get; }
    public SamplingPublicationSignal Publications { get; } = new();

    public WindowsWheaInformationOwner(WindowsWheaHistoryRequest request, TimeProvider? clock = null)
        : this(request, new WindowsWheaHistoryReader().Read, clock) { }
    internal WindowsWheaInformationOwner(WindowsWheaHistoryRequest request,
        Func<WindowsWheaHistoryRequest, CancellationToken, WindowsWheaHistoryCapture> read, TimeProvider? clock = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.clock = clock ?? TimeProvider.System;
        backend = new(ReadInformation, token => CaptureInformationAsync(token), static _ => new(true, null, []),
            (selection, token) => { ValidateSelection(selection); return CaptureInformationAsync(token); });
    }
    public InformationReadWriteBackend CreateBackend() => backend;
    public DeviceInformationSnapshot ReadInformation() => Volatile.Read(ref current)?.Fields ?? DeviceInformationSnapshot.Empty;
    public WindowsWheaHistoryCapture? ReadHistory() => Volatile.Read(ref current)?.History;
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new InformationCollectionBorrow(backend, Publications, CollectionItemId,
            async (selection, permit, token) =>
            {
                ValidateSelection(selection);
                var committed = false;
                var snapshot = await CaptureInformationAsync(token, commit => { committed = permit.TryPublish(commit); return committed; });
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, true));
            }, FieldCollectionSourceKind.SourceQuery);
    }
    public async ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(CancellationToken cancellationToken = default,
        Func<Action, bool>? tryPublish = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await captureGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var history = read(Request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = clock.GetUtcNow();
            var fields = history.Available ? Project(history.Events, observedAt) : Clear(ReadInformation());
            var next = new Publication(history, fields);
            void Commit()
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                Volatile.Write(ref current, next); Publications.Publish([CollectionItemId]);
            }
            if (tryPublish is null) Commit(); else _ = tryPublish(Commit);
            return ReadInformation();
        }
        finally { captureGate.Release(); }
    }
    private static void ValidateSelection(ImmutableArray<InformationFieldSelection> selection)
    {
        foreach (var field in selection)
            if (field.Field.FieldName != field.Definition.Name
                || field.Definition != DeviceInformationFieldRegistry.WheaAllEventTimeReported
                && field.Definition != DeviceInformationFieldRegistry.WheaAllRawRecordReported
                && field.Definition != DeviceInformationFieldRegistry.WheaAllSeverityReported)
                throw new ArgumentException("Unknown WHEA event field.", nameof(selection));
        // Exact event identities are already selected through backend's catalog;
        // history scope is immutable on this owner and field names are not parsed.
    }
    private static DeviceInformationSnapshot Project(IReadOnlyList<WindowsWheaEvent> events, DateTimeOffset observedAt)
        => new(observedAt, events.Select(item => new DeviceInformationObject(item.DeviceIdentity, "whea_record", [
            new(DeviceInformationFieldRegistry.WheaAllEventTimeReported, item.EventTimeUtc is { } time
                ? JsonSerializer.SerializeToElement(time.ToString("O", CultureInfo.InvariantCulture)) : null),
            new(DeviceInformationFieldRegistry.WheaAllRawRecordReported, item.RawRecord is { } bytes
                ? JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes.AsSpan())) : null),
            new(DeviceInformationFieldRegistry.WheaAllSeverityReported, item.Severity is { } severity
                ? JsonSerializer.SerializeToElement(((uint)severity).ToString(CultureInfo.InvariantCulture)) : null)
        ])).ToArray());
    private static DeviceInformationSnapshot Clear(DeviceInformationSnapshot previous)
        => new(null, previous.Devices.Select(device => device with
        { Fields = device.Fields.Select(static field => new DeviceInformationField(field.Definition, null)).ToArray() }).ToArray());
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        captureGate.Wait();
        try { /* Per-call native resources close before capture exits. */ }
        finally { captureGate.Release(); }
    }
}
