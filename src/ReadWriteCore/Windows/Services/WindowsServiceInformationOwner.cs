using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Services;

/// <summary>One explicit SCM capture/current publication. There is no polling
/// worker or retained native session. Default coverage is the original Win32 scope.</summary>
public sealed class WindowsServiceInformationOwner : IDisposable
{
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly Func<bool, IReadOnlySet<string>?, CancellationToken, WindowsServiceCapture> read;
    private readonly TimeProvider clock;
    private readonly SamplingPublicationSignal publications = new();
    private readonly InformationReadWriteBackend backend;
    private sealed record Capture(WindowsServiceCapture Facts, DeviceInformationSnapshot Fields);
    private Capture? current;
    private int disposed;
    public WindowsServiceScope Scope { get; }
    public SamplingPublicationSignal Publications => publications;

    public WindowsServiceInformationOwner(WindowsServiceScope scope = WindowsServiceScope.Win32, TimeProvider? clock = null)
        : this(scope, (includeConfiguration, names, token) => new WindowsServiceReader().Read(scope, includeConfiguration, token, names), clock) { }
    internal WindowsServiceInformationOwner(WindowsServiceScope scope,
        Func<bool, CancellationToken, WindowsServiceCapture> read, TimeProvider? clock = null)
        : this(scope, (includeConfiguration, _, token) => read(includeConfiguration, token), clock) { }
    private WindowsServiceInformationOwner(WindowsServiceScope scope,
        Func<bool, IReadOnlySet<string>?, CancellationToken, WindowsServiceCapture> read, TimeProvider? clock)
    {
        if (scope is not WindowsServiceScope.Win32 and not WindowsServiceScope.Drivers and not WindowsServiceScope.Win32AndDrivers)
            throw new ArgumentOutOfRangeException(nameof(scope));
        Scope = scope;
        this.read = read;
        this.clock = clock ?? TimeProvider.System;
        backend = new(ReadInformation, token => CaptureInformationAsync(false, token), static _ => new(true, null, []),
            (selection, token) => CaptureInformationAsync(RequiresConfiguration(selection), token, configurationServiceNames: ConfigurationNames(selection)));
    }
    public InformationReadWriteBackend CreateBackend() => backend;
    public DeviceInformationSnapshot ReadInformation() => Volatile.Read(ref current)?.Fields ?? DeviceInformationSnapshot.Empty;
    /// <summary>Same publication retains original SCM name/display name and scope facts.</summary>
    public WindowsServiceCapture? ReadServices() => Volatile.Read(ref current)?.Facts;
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new InformationCollectionBorrow(backend, publications, "information.services",
            async (selection, permit, token) =>
            {
                var committed = false;
                var snapshot = await CaptureInformationAsync(RequiresConfiguration(selection), token, commit =>
                {
                    committed = permit.TryPublish(commit);
                    return committed;
                }, ConfigurationNames(selection));
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, true));
            }, FieldCollectionSourceKind.SourceQuery);
    }
    public async ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(bool includeConfiguration = false,
        CancellationToken cancellationToken = default, Func<Action, bool>? tryPublish = null,
        IReadOnlySet<string>? configurationServiceNames = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await captureGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var facts = read(includeConfiguration, configurationServiceNames, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var next = new Capture(facts, facts.Available
                ? ServiceInformationFieldProjection.Create(facts.Services, clock.GetUtcNow())
                : SoftwareInformationFieldProjection.Clear(ReadInformation()));
            void Commit()
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                Volatile.Write(ref current, next);
                publications.Publish(["information.services"]);
            }
            if (tryPublish is null) Commit();
            else _ = tryPublish(Commit);
            return ReadInformation();
        }
        finally { captureGate.Release(); }
    }
    private static bool RequiresConfiguration(System.Collections.Immutable.ImmutableArray<InformationFieldSelection> selection)
        => selection.Any(static field => field.Definition.Name == "service_all_start_type_configured");
    private static IReadOnlySet<string> ConfigurationNames(System.Collections.Immutable.ImmutableArray<InformationFieldSelection> selection)
        => selection.Where(static field => field.Definition.Name == "service_all_start_type_configured")
            .Select(static field => field.Field.DeviceIdentity["windows-service|".Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        captureGate.Wait();
        try { /* Per-call SCM handles/buffers are already drained by their finally blocks. */ }
        finally { captureGate.Release(); }
        // No AvailableWaitHandle/native semaphore exists. Keep managed gate usable
        // by a caller that passed admission just before Dispose and queued late.
    }
}


