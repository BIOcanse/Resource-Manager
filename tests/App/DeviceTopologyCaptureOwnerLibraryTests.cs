using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyCaptureOwnerLibraryTests
{
    [Fact]
    public async Task SharedOwnerPreservesBothFramesAndSignalsOnlyTheCapturedBranch()
    {
        var acpi = new FirmwareReader(() => AcpiFirmwareTestData.Tables());
        var persisted = 0;
        using var owner = new DeviceTopologyCaptureOwner(acpi, Policy(() => Snapshot(), _ => persisted++));
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(DeviceTopologyCaptureOwner).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(DeviceTopologySnapshot).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(IDeviceTopologyReader).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(BackendMessage).Assembly);
        Assert.Equal(0, acpi.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var topologySignal = owner.SubscribeTopologyPublications();
        using var acpiSignal = owner.SubscribeAcpiPublications();
        var topologyWait = topologySignal.WaitAsync(timeout.Token).AsTask();
        var acpiWait = acpiSignal.WaitAsync(timeout.Token).AsTask();
        var firmware = await owner.CaptureAcpiInformationAsync(timeout.Token);
        await acpiWait;
        Assert.False(topologyWait.IsCompleted);
        var topology = await owner.CaptureInformationAsync(timeout.Token);
        await topologyWait;
        Assert.Same(firmware, owner.ReadState().AcpiInformationFields);
        Assert.Same(topology, owner.ReadState().InformationFields);
        Assert.Equal(1, persisted);
        acpiWait = acpiSignal.WaitAsync(timeout.Token).AsTask();
        await owner.CaptureInformationAsync(timeout.Token);
        Assert.False(acpiWait.IsCompleted);
        Assert.Same(firmware, owner.ReadState().AcpiInformationFields);
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acpiWait);
    }

    [Fact]
    public async Task RetiredOwnerCannotCommitAfterAnAlreadyStartedSynchronousFirmwareRead()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var retirementGate = new object();
        var active = true;
        var permission = new DeviceTopologyCapturePermission(
            () => { lock (retirementGate) return active; },
            commit => { lock (retirementGate) { if (!active) return false; commit(); return true; } },
            () => true);
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return AcpiFirmwareTestData.Tables();
        }));
        var original = owner.ReadState();
        var capture = Task.Run(() => owner.SampleAcpiAsync(permission, CancellationToken.None));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            lock (retirementGate) active = false;
        }
        finally { release.Set(); }
        Assert.Equal(DeviceTopologySampleCompletion.Canceled, await capture);
        Assert.Same(original, owner.ReadState());
    }

    [Fact]
    public async Task TopologyAndAcpiExplicitCaptureUseTheSameGateAndCanceledWaitReadsNothing()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var acpi = new FirmwareReader(() => AcpiFirmwareTestData.Tables());
        using var owner = new DeviceTopologyCaptureOwner(acpi, Policy(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Snapshot();
        }));
        var topology = Task.Run(async () => await owner.CaptureInformationAsync(default));
        Task<DeviceInformationSnapshot>? firmware = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            firmware = owner.CaptureAcpiInformationAsync(default).AsTask();
            Assert.False(firmware.IsCompleted);
            using var cancellation = new CancellationTokenSource();
            var canceled = owner.CaptureAcpiInformationAsync(cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.Equal(0, acpi.ReadCount);
        }
        finally { release.Set(); }
        var topologyFrame = await topology;
        var firmwareFrame = await firmware!;
        Assert.Same(topologyFrame, owner.ReadState().InformationFields);
        Assert.Same(firmwareFrame, owner.ReadState().AcpiInformationFields);
        Assert.Equal(1, acpi.ReadCount);
    }

    [Fact]
    public async Task ExternalSubscriptionProjectsTheSameCanonicalCapturePublication()
    {
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => AcpiFirmwareTestData.Tables()));
        var backend = new InformationReadWriteBackend(() => owner.ReadState().AcpiInformationFields,
            token => owner.CaptureAcpiInformationAsync(token), static _ => new(true, null, []));
        await backend.DiscoverAsync(default);
        var field = backend.Catalog.Fields.Single(f => f.Field.FieldName == "firmware_all_acpi_table_revision").Field;
        var subscriptions = new InformationFieldSubscriptionBackend(backend,
            static (_, _) => new EmptyLease(), (_, _, token) => Stream(owner, token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = subscriptions.SubscribeAsync("selected", [field], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        var explicitResult = (await backend.CollectAsync([field], timeout.Token)).Single();
        Assert.True(await next);
        Assert.Equal(explicitResult, Assert.Single(stream.Current));
    }

    [Fact]
    public async Task CanonicalBackendUsesTheOwnersKnownIdentityFailureAndRecovery()
    {
        var count = 0;
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => ++count switch {
            1 => AcpiFirmwareTestData.Tables(),
            2 => null,
            3 => AcpiFirmwareTestData.Tables(7),
            _ => []
        }));
        var backend = new InformationReadWriteBackend(() => owner.ReadState().AcpiInformationFields,
            token => owner.CaptureAcpiInformationAsync(token), static _ => new(true, null, []));
        await backend.DiscoverAsync(default);
        var known = owner.ReadState().AcpiInformationFields;
        var field = backend.Catalog.Fields.Single(f => f.Field.FieldName == "firmware_all_acpi_table_revision").Field;
        Assert.Null((await backend.CollectAsync([field], default)).Single().Value);
        Assert.Equal(known.Devices.Select(d => d.DeviceIdentity), owner.ReadState().AcpiInformationFields.Devices.Select(d => d.DeviceIdentity));
        Assert.Null(owner.ReadState().AcpiInformationFields.ObservedAt);
        Assert.Equal("7", (await backend.CollectAsync([field], default)).Single().Value!.Integer);
        await owner.CaptureAcpiInformationAsync(default);
        Assert.Empty(owner.ReadState().AcpiInformationFields.Devices);
    }

    private static DeviceTopologyCapturePolicy Policy(Func<DeviceTopologySnapshot> read,
        Action<DeviceTopologyChangedCapture>? persist = null)
        => new(new TopologyReader(read), new DeviceTopologySemanticComparer().ComputeHash,
            DeviceTopologyInformationProjection.Create,
            (changed, _) => { persist?.Invoke(changed); return Task.CompletedTask; },
            static _ => new("device-topology-read-failed", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { });

    private static DeviceTopologySnapshot Snapshot()
        => new(DateTimeOffset.UtcNow, new("manufacturer", "model", "brand", "logo", null, null, null), [], []);

    private sealed class TopologyReader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }

    private sealed class FirmwareReader(Func<IReadOnlyList<AcpiFirmwareTable>?> read) : IAcpiFirmwareTableReader
    {
        public int ReadCount { get; private set; }
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() { ReadCount++; return read(); }
    }

    private static async IAsyncEnumerable<DeviceInformationSnapshot> Stream(DeviceTopologyCaptureOwner owner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var publication = owner.SubscribeAcpiPublications();
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return owner.ReadState().AcpiInformationFields;
        }
    }

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
}
