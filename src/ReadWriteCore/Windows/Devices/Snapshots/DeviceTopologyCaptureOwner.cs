using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

/// <summary>Original shared device/ACPI capture and immutable current owner. No worker, demand tracker or journal.
/// The caller stops and drains its sampling worker and queued explicit captures before disposal.
/// Disposal waits for an in-flight capture; callbacks must not dispose their own owner.</summary>
public sealed partial class DeviceTopologyCaptureOwner(
    IAcpiFirmwareTableReader acpiReader,
    DeviceTopologyCapturePolicy? topology = null,
    Action<Exception>? logAcpiFailure = null,
    Func<DeviceInformationSnapshot, Task>? volumeFreeSpacePublished = null) : IDisposable
{
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly CurrentValuePublicationSignal topologyChangeSignal = new();
    private DeviceTopologySnapshotState changeState = DeviceTopologySnapshotState.Warming;
    private byte[] changeFingerprint = [];
    private readonly CurrentValuePublicationSignal acpiPublicationSignal = new();
    private DeviceTopologySnapshotState state = DeviceTopologySnapshotState.Warming;
    private string? semanticHash;
    private long contentGeneration;
    private long stateRevision;
    private string? failureFingerprint;
    private int disposed;

    public DeviceTopologySnapshotState ReadState() => Volatile.Read(ref state);
    public CurrentValuePublicationSignal.Subscription SubscribeTopologyPublications() => publicationSignal.Subscribe();
    [RuntimeRead(ReadVolatilityKind.EventDriven, nameof(SubscribeTopologyChanges))]
    public DeviceTopologySnapshotState ReadChangeState() => Volatile.Read(ref changeState);
    public CurrentValuePublicationSignal.Subscription SubscribeTopologyChanges() => topologyChangeSignal.Subscribe();
    public CurrentValuePublicationSignal.Subscription SubscribeAcpiPublications() => acpiPublicationSignal.Subscribe();

    /// <summary>Seed the original journal state before starting the caller's worker.</summary>
    public void InitializePersisted(DeviceTopologySnapshot snapshot, string hash, long generation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (topology is null) throw new InvalidOperationException("No topology policy is bound to this capture owner.");
        semanticHash = hash;
        contentGeneration = Math.Max(1, generation);
        stateRevision = 1;
        Publish(new DeviceTopologySnapshotState(DeviceTopologySnapshotState.CurrentSchemaVersion,
            DeviceTopologySnapshotStatus.Ready, snapshot, contentGeneration, stateRevision,
            DeviceTopologySnapshotSource.Persisted, snapshot.CapturedAt, null, null, [])
        {
            // Loading topology does not own the independently once-read firmware value.
            AcpiInformationFields = ReadState().AcpiInformationFields
        });
        // This synchronous data initialization also settles its reference publication.
        CompleteVolumeFreeSpacePublicationAsync().GetAwaiter().GetResult();
    }

    public async ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(CancellationToken cancellationToken,
        DeviceTopologyCapturePermission? permission = null, Action? sourceQueryStarted = null)
    {
        await SampleTopologyAsync(permission, cancellationToken, requireDemand: false,
            sourceQueryStarted: sourceQueryStarted);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadState().InformationFields;
    }

    public async ValueTask<DeviceInformationSnapshot> CaptureAcpiInformationAsync(CancellationToken cancellationToken,
        DeviceTopologyCapturePermission? permission = null)
    {
        await SampleAcpiAsync(permission, cancellationToken, requireDemand: false);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadState().AcpiInformationFields;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        sampleGate.Wait();
        sampleGate.Release();
        // No native wait handle is created. Late admitted callers must acquire/reject/release, not hang.
    }

    public async Task<DeviceTopologySampleCompletion> SampleTopologyAsync(
        DeviceTopologyCapturePermission? permission,
        CancellationToken cancellationToken, bool requireDemand = true, Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var policy = topology ?? throw new InvalidOperationException("No topology policy is bound to this capture owner.");
        try
        {
            if (requireDemand && !await sampleGate.WaitAsync(0, cancellationToken))
            {
                return DeviceTopologySampleCompletion.SkippedBusy;
            }
            if (!requireDemand) await sampleGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeviceTopologySampleCompletion.Canceled;
        }

        var attemptedAt = DateTimeOffset.UtcNow;
        if (permission is not null && !permission.IsActive())
        {
            sampleGate.Release();
            return DeviceTopologySampleCompletion.Canceled;
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            // A co-due item can lose its subscribers while the preceding synchronous read runs.
            if (requireDemand && permission is not null && !permission.HasCurrentDemand())
                return DeviceTopologySampleCompletion.Sampled;
            sourceQueryStarted?.Invoke();
            var snapshot = policy.Reader.ReadSnapshot();
            if (snapshot.NpuInformation is null)
            {
                snapshot = snapshot with { NpuInformation = (ReadState().Snapshot?.NpuInformation ?? [])
                    .Select(static row => row with { PnpInstanceId = null, NameDeclared = null,
                        DriverVersionDeclared = null, LocationInformationReported = null, LocationPathsDeclared = null }).ToArray() };
            }
            if (snapshot.DriverInformation is null)
                snapshot = snapshot with { DriverInformation = (ReadState().Snapshot?.DriverInformation ?? []).Select(static row => row with { ProviderDeclared = null, DateDeclared = null, VersionDeclared = null }).ToArray() };
            if (snapshot.PageFileInformation is null)
            {
                snapshot = snapshot with { PageFileInformation = (ReadState().Snapshot?.PageFileInformation ?? [])
                    .Select(static file => new DeviceTopologyPageFileInformation(file.DeviceIdentity)).ToArray() };
            }
            if (snapshot.DiskLayoutInformation is null)
            {
                snapshot = snapshot with { DiskLayoutInformation = (ReadState().Snapshot?.DiskLayoutInformation ?? [])
                    .Select(static disk => new DeviceTopologyDiskLayoutInformation(disk.DeviceIdentity)).ToArray() };
            }
            if (snapshot.VolumeSpaceInformation is null)
            {
                snapshot = snapshot with { VolumeSpaceInformation = (ReadState().Snapshot?.VolumeSpaceInformation ?? [])
                    .Select(static volume => new DeviceTopologyVolumeSpaceInformation(volume.DeviceIdentity)).ToArray() };
            }
            if (snapshot.FirmwareDeclarations is null)
            {
                var previous = ReadState().Snapshot;
                snapshot = snapshot with {
                    FirmwareDeclarations = new(null, (previous?.FirmwareDeclarations?.Slots ?? [])
                        .Select(static slot => new FirmwareSystemSlot(slot.DeviceIdentity,
                            null, null, null, null, null, null, null, null, null, null, null, null)).ToArray())
                    {
                        ProcessorSockets = (previous?.FirmwareDeclarations?.ProcessorSockets ?? [])
                            .Select(static socket => socket with { Designation = null }).ToArray()
                    },
                    MemoryModules = (previous?.MemoryModules ?? []).Select(static module => module with {
                        CapacityBytes = null, Locator = null, Manufacturer = null, PartNumber = null,
                        SerialNumber = null, MemoryType = null, DeclaredDataRateMtps = null,
                        ConfiguredDataRateMtps = null, TotalWidthBits = null, DataWidthBits = null,
                        ConfiguredVoltageVolts = null
                    }).ToArray()
                };
                policy.LogSmbiosUnavailable();
            }
            if (snapshot.Batteries is null)
            {
                snapshot = snapshot with { Batteries = (ReadState().Snapshot?.Batteries ?? [])
                    .Select(static battery => battery with { Manufacturer = null, SerialNumber = null, Chemistry = null }).ToArray() };
            }
            if (snapshot.StorageInformation is null)
            {
                snapshot = snapshot with { StorageInformation = (ReadState().Snapshot?.StorageInformation ?? [])
                    .Select(static storage => storage with { Ata = null, Nvme = null, HardwareIdentityEvidence = null }).ToArray() };
            }
            if (snapshot.NvidiaGpuInformation is null)
            {
                snapshot = snapshot with { NvidiaGpuInformation = (ReadState().Snapshot?.NvidiaGpuInformation ?? [])
                    .Select(static gpu => gpu with { VbiosVersion = null, CudaComputeCapability = null }).ToArray() };
            }
            if (snapshot.GpuInformation is null)
            {
                snapshot = snapshot with { GpuInformation = (ReadState().Snapshot?.GpuInformation ?? [])
                    .Select(static gpu => gpu with { AdapterLuid = null, Name = null, PciVendorId = null,
                        PciDeviceId = null, DriverVersion = null, AdapterTypeFlags = null }).ToArray() };
            }
            if (snapshot.AmdGpuInformation is null)
            {
                snapshot = snapshot with { AmdGpuInformation = (ReadState().Snapshot?.AmdGpuInformation ?? [])
                    .Select(static gpu => gpu with { VbiosVersion = null, VramType = null, VbiosPartNumber = null, VbiosDateRaw = null }).ToArray() };
            }
            if (snapshot.HidInformation is null)
            {
                snapshot = snapshot with { HidInformation = (ReadState().Snapshot?.HidInformation ?? [])
                    .Select(static hid => hid with { PnpInstanceId = null, Usage = null, ReportLengths = null,
                        Buttons = null, Values = null }).ToArray() };
            }
            if (snapshot.NetworkInterfaceInformation is null)
            {
                snapshot = snapshot with { NetworkInterfaceInformation = (ReadState().Snapshot?.NetworkInterfaceInformation ?? [])
                    .Select(static row => row with { InterfaceGuid = null, InterfaceLuid = null, InterfaceIndex = null,
                        UnicastAddresses = null, Routes = null }).ToArray() };
            }
            var nextHash = policy.ComputeSemanticHash(snapshot);
            var changed = false;
            DeviceTopologySnapshotState? nextState = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryPublish(permission, () =>
            {
                changed = !string.Equals(semanticHash, nextHash, StringComparison.Ordinal);
                if (changed)
                {
                    semanticHash = nextHash;
                    contentGeneration = Interlocked.Increment(ref contentGeneration);
                }

                var current = ReadState();
                var publishedSnapshot = changed || current.Snapshot is null
                    ? DeviceTopologyStructure.Create(snapshot)
                    : current.Snapshot;
                nextState = current with
                {
                    State = DeviceTopologySnapshotStatus.Ready,
                    Snapshot = publishedSnapshot,
                    ContentGeneration = contentGeneration,
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    Source = DeviceTopologySnapshotSource.Live,
                    LastSuccessAt = snapshot.CapturedAt,
                    LastAttemptAt = attemptedAt,
                    FailureCode = null,
                    AttemptDiagnostics = snapshot.SourceDiagnostics.ToArray()
                };
                Publish(nextState, snapshot);
                failureFingerprint = null;
                publicationSignal.Publish();
            }))
            {
                return DeviceTopologySampleCompletion.Canceled;
            }

            if (changed)
            {
                try
                {
                    await policy.PersistChanged(new DeviceTopologyChangedCapture(contentGeneration, nextHash, nextState!.Snapshot!), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    policy.LogPersistenceFailure(ex);
                }
            }

            return DeviceTopologySampleCompletion.Sampled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeviceTopologySampleCompletion.Canceled;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { throw; }
        catch (Exception ex)
        {
            var failure = policy.ClassifyFailure(ex);
            return PublishFailure(permission, ex, attemptedAt, failure.Code, failure.Diagnostics)
                ? DeviceTopologySampleCompletion.Failed
                : DeviceTopologySampleCompletion.Canceled;
        }
        finally
        {
            try { await CompleteVolumeFreeSpacePublicationAsync(); }
            finally { sampleGate.Release(); }
        }
    }

    private bool PublishFailure(
        DeviceTopologyCapturePermission? permission,
        Exception exception,
        DateTimeOffset attemptedAt,
        string failureCode,
        IReadOnlyList<DeviceTopologySourceDiagnostic> diagnostics)
    {
        return TryPublish(permission, () =>
        {
            var fingerprint = $"{exception.GetType().FullName}:{exception.HResult}:{failureCode}";
            topology!.LogFailure(exception, failureCode, diagnostics,
                !string.Equals(failureFingerprint, fingerprint, StringComparison.Ordinal));

            failureFingerprint = fingerprint;
            var current = ReadState();
            Publish(current with
            {
                State = DeviceTopologySnapshotStatus.Failed,
                StateRevision = Interlocked.Increment(ref stateRevision),
                LastAttemptAt = attemptedAt,
                FailureCode = failureCode,
                AttemptDiagnostics = diagnostics.ToArray()
            });
            publicationSignal.Publish();
        });
    }

    private static bool TryPublish(DeviceTopologyCapturePermission? permission, Action publish)
    {
        if (permission is null)
        {
            publish();
            return true;
        }

        return permission.TryPublish(publish);
    }

    private void Publish(DeviceTopologySnapshotState next, DeviceTopologySnapshot? informationSnapshot = null)
    {
        var projectedFields = topology!.ProjectInformation(
            informationSnapshot ?? next.Snapshot,
            next.State == DeviceTopologySnapshotStatus.Ready && next.Source == DeviceTopologySnapshotSource.Live
                ? next.LastSuccessAt
                : null);
        var previousInformation = ReadState().InformationFields;
        var fields = WindowsInformationFieldProjection.PreservePrinterFields(
            WindowsInformationFieldProjection.PreserveMediaFields(projectedFields, previousInformation),
            previousInformation);
        fields = WindowsInformationFieldProjection.PreserveBluetoothFields(fields, previousInformation);
        fields = WindowsInformationFieldProjection.PreserveWpdFields(fields, previousInformation);
        fields = WindowsInformationFieldProjection.PreserveD3dCapabilityFields(fields, previousInformation);
        fields = WindowsInformationFieldProjection.PreserveAtaSmartInformationFields(fields, previousInformation);
        fields = WindowsInformationFieldProjection.PreserveOpenClCapabilityFields(fields, previousInformation);
        fields = WindowsInformationFieldProjection.PreserveVulkanCapabilityFields(fields, previousInformation);
        fields = WindowsInformationFieldProjection.PreserveOpenGlCapabilityFields(fields, previousInformation);
        fields = WindowsInformationFieldProjection.PreserveNvidiaDrsInformation(fields, previousInformation);
        Volatile.Write(ref state, next with { InformationFields = fields });
        PublishVolumeFreeSpaceCurrent();
        InformationPublications.Publish([InformationCollectionItemId]);
        PublishTopologyChange();
    }

    private void PublishTopologyChange()
    {
        var current = ReadState();
        var fields = new DeviceInformationSnapshot(current.InformationFields.ObservedAt,
            current.InformationFields.Devices.Select(device => device with
            { Fields = device.Fields.Where(static field => field.Definition.Volatility.Kind != ReadVolatilityKind.Sampled)
                .OrderBy(static field => field.Definition.Name, StringComparer.Ordinal).ToArray() })
                .Where(static device => device.Fields.Count > 0).OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
        var next = current with { InformationFields = fields,
            Snapshot = current.Snapshot is null ? null : DeviceTopologyStructure.Create(current.Snapshot) };
        var comparable = next with { StateRevision = 0, LastAttemptAt = null, LastSuccessAt = null,
            InformationFields = fields with { ObservedAt = null } };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(comparable,
            DeviceFingerprintJsonContext.Default.DeviceTopologySnapshotState);
        var fingerprint = System.Security.Cryptography.SHA256.HashData(bytes);
        if (changeFingerprint.AsSpan().SequenceEqual(fingerprint)) return;
        changeFingerprint = fingerprint;
        Volatile.Write(ref changeState, next);
        topologyChangeSignal.Publish();
    }
}
