using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Operations.Effects;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task CopiedInstalledV1BankMigratesAtStartupAndFirstOperationWorksWithoutReplacingBackup()
    {
        var log = new OperationMigrationLogger();
        var original = ReadInstalledOperationFixture("operation-coordinator-v1.bin");
        var manifest = ReadInstalledOperationFixture("operation-coordinator-v1.bin.root.manifest");
        Assert.Equal("693C9A4E35CAF30A7F29641BB6CFE0FE29F3D310945393E07AE18CBF17981252", Convert.ToHexString(SHA256.HashData(original)));
        Assert.Equal("AD229855857EDACD037384E0A220AD3ED6EA1A6E3E0C8090E46AA609DE230AE8", Convert.ToHexString(SHA256.HashData(manifest)));
        var plan = CreateSmallPlan();
        var old = HostManagerOperationCanonicalEnvelopeCodec.DecodeLegacyV1(original,
            plan.OperationCoordinator.Recreate.Capacity, plan.OperationCoordinator.HotPublish.ConfigurationGeneration);
        var clock = new OperationMigrationClock(DateTimeOffset.FromUnixTimeMilliseconds(old.NativeImage.Header.LastObservedUtcMilliseconds).AddSeconds(1));
        await using var fixture = await Fixture.CreateUnstartedWithMigrationLogAsync(log, clock);
        Assert.Equal(2, old.NativeImage.Records.Length);
        Assert.Equal(12, old.Payloads.Count);
        Assert.Equal(2, old.Receipts.Count);
        Assert.All(old.Receipts.ExportCanonical(), receipt => Assert.Equal(HostManagerOperationEffectReceiptState.Settled, receipt.State));
        Assert.All(old.NativeImage.Records, record =>
        {
            var request = HostManagerOperationRequestCodec.DecodeUserAcquisitionRequest(old.Payloads.Require(record.RequestHandle).Payload);
            Assert.Equal("S-1-5-21-100-200-300-1001", request.Caller.WindowsSid);
        });
        Assert.All(old.NativeImage.Records, record => Assert.Equal(NativeOperationState.Succeeded, (NativeOperationState)record.State));
        Assert.False(HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(old));
        // Freeze the real v1 writer/schema, not guessed offsets: reproduce the installed-bank fixture with its synthetic caller byte for byte.
        Assert.Equal(original, EncodeFrozenOperationV1(old, fixture.Plan.OperationCoordinator.Recreate.Capacity));
        InstallLegacyOperationBank(fixture.EnvelopePath, original, manifest);
        Assert.False(HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(fixture.EnvelopePath));

        await fixture.Owner.StartAsync(CancellationToken.None);

        Assert.True(fixture.Owner.GetHealth().Ready);
        var backup = Assert.Single(OperationBackups(fixture.EnvelopePath));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(manifest, File.ReadAllBytes(backup + ".root.manifest"));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(fixture.EnvelopePath).AsSpan(8)));
        var current = fixture.ReadEnvelope();
        Assert.Equal(2, current.NativeImage.Records.Length);
        Assert.Equal(old.NativeImage.Records.Select(record => $"{record.OperationId.High:x16}{record.OperationId.Low:x16}").Order(StringComparer.Ordinal),
            current.NativeImage.Records.Select(record => $"{record.OperationId.High:x16}{record.OperationId.Low:x16}").Order(StringComparer.Ordinal));
        Assert.All(current.NativeImage.Records, record =>
        {
            var previous = Assert.Single(old.NativeImage.Records, row => row.OperationId == record.OperationId);
            Assert.Equal(previous.State, record.State);
            Assert.Equal(previous.RequestHandle, record.RequestHandle);
            Assert.Equal(previous.AttemptToken, record.AttemptToken);
            Assert.Equal(previous.AttemptNumber, record.AttemptNumber);
            var subject = HostManagerOperationRequestCodec.DecodeSubject(current.Payloads.Require(record.SubjectHandle).Payload);
            Assert.Contains(subject.TargetId, new[] { "hardware-bridge", "fan-control-core" });
        });
        var submitted = await fixture.SubmitComponentDownloadAsync();
        Assert.Equal(HostManagerOperationStates.Succeeded, (await fixture.WaitForTerminalAsync(submitted.Id)).State);
        Assert.Contains(log.Messages, text => text.Contains("migrated from envelope-v1/ABI1", StringComparison.Ordinal));
        var backupWriteTime = File.GetLastWriteTimeUtc(backup);
        var migrationLogCount = log.Messages.Count(text => text.Contains("migrated from envelope-v1/ABI1", StringComparison.Ordinal));
        await fixture.Owner.StopAsync(CancellationToken.None);
        await using var restarted = await Fixture.CreateStartedWithMigrationLogAsync(fixture.Root, fixture.Plan, log, clock);
        Assert.True(restarted.Owner.GetHealth().Ready);
        Assert.Equal(backup, Assert.Single(OperationBackups(fixture.EnvelopePath)));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(manifest, File.ReadAllBytes(backup + ".root.manifest"));
        Assert.Equal(backupWriteTime, File.GetLastWriteTimeUtc(backup));
        Assert.Equal(migrationLogCount, log.Messages.Count(text => text.Contains("migrated from envelope-v1/ABI1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RealStartedV1SchemaPreservesExactRecoveryReceiptAndDoesNotReexecuteEffect()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        var produced = await CreateOperationImportImageAsync(Path.Combine(fixture.Root, "v1-source"), HostManagerOperationStates.Running);
        var legacy = ConvertFixtureToV1Schema(produced.Envelope);
        var original = EncodeFrozenOperationV1(legacy, fixture.Plan.OperationCoordinator.Recreate.Capacity);
        InstallLegacyOperationBank(fixture.EnvelopePath, original);
        Assert.True(HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(fixture.EnvelopePath));
        var prior = Assert.Single(legacy.NativeImage.Records);
        Assert.Equal(NativeOperationState.Running, (NativeOperationState)prior.State);
        var originalReceipt = Assert.Single(legacy.Receipts.ExportCanonical(), receipt => receipt.State == HostManagerOperationEffectReceiptState.Started);

        await fixture.Owner.StartAsync(CancellationToken.None);

        var id = $"{prior.OperationId.High:x16}{prior.OperationId.Low:x16}";
        var recovered = await fixture.WaitForTerminalAsync(id);
        Assert.Equal(0, fixture.ComponentExecutor.ExecuteCount);
        Assert.Equal(originalReceipt, fixture.ComponentExecutor.LastReadbackEvidence);
        Assert.Equal(HostManagerOperationStates.StateUncertain, recovered.State);
        Assert.Equal("fixture-component", recovered.Subject!.TargetId);
        var current = fixture.ReadEnvelope();
        var retained = current.Receipts.Require(originalReceipt.ReceiptId);
        Assert.Equal(originalReceipt, retained);
        var record = Assert.Single(current.NativeImage.Records);
        Assert.Equal(prior.OperationId, record.OperationId);
        Assert.Equal(prior.RequestHandle, record.RequestHandle);
        Assert.Equal(prior.AttemptToken, record.AttemptToken);
        Assert.Equal(legacy.SessionInstanceId, current.SessionInstanceId);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(OperationBackups(fixture.EnvelopePath))));
    }

    [Fact]
    public async Task LegacyBackupVerificationIoFailureLeavesSourceUntouchedAndDefersUntilExplicitSubmit()
    {
        var log = new OperationMigrationLogger();
        await using var fixture = await Fixture.CreateUnstartedWithMigrationLogAsync(log);
        var original = ReadInstalledOperationFixture("operation-coordinator-v1.bin");
        var manifest = ReadInstalledOperationFixture("operation-coordinator-v1.bin.root.manifest");
        InstallLegacyOperationBank(fixture.EnvelopePath, original, manifest);
        var backup = fixture.EnvelopePath + ".legacy-v1.byte-exact." + Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant() + ".backup";
        File.WriteAllBytes(backup, original);
        File.WriteAllBytes(backup + ".root.manifest", manifest);
        using (var blocked = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await fixture.Owner.StartAsync(CancellationToken.None);
            Assert.True(fixture.Owner.GetHealth().Ready);
            Assert.False(fixture.Owner.GetHealth().PersistenceFaulted);
            Assert.Empty(fixture.Owner.GetRecent());
            Assert.Equal(original, File.ReadAllBytes(fixture.EnvelopePath));
            Assert.Equal(manifest, File.ReadAllBytes(fixture.EnvelopePath + ".root.manifest"));
        }
        Assert.Contains(log.Messages, text => text.Contains("current operation state is empty", StringComparison.Ordinal));
        var submitted = await fixture.SubmitComponentDownloadAsync();
        Assert.Equal(HostManagerOperationStates.Succeeded, (await fixture.WaitForTerminalAsync(submitted.Id)).State);
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(manifest, File.ReadAllBytes(backup + ".root.manifest"));
        var resetBackup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.EnvelopePath)!,
            Path.GetFileName(fixture.EnvelopePath) + ".unconverted.byte-exact.*.backup"));
        Assert.Equal(original, File.ReadAllBytes(resetBackup));
        Assert.Equal(manifest, File.ReadAllBytes(resetBackup + ".root.manifest"));
    }

    [Fact]
    public async Task InterruptedV1DiskReadbackClosesOriginalAttemptAndRetriesExactlyOnceWithoutLeavingImportBlocked()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        var produced = await CreateOperationImportImageAsync(Path.Combine(fixture.Root, "v1-disk-source"), HostManagerOperationStates.Running, diskScan: true);
        var legacy = ConvertFixtureToV1Schema(produced.Envelope);
        var prior = Assert.Single(legacy.NativeImage.Records);
        Assert.Equal(NativeOperationState.Running, (NativeOperationState)prior.State);
        var originalReceipt = Assert.Single(legacy.Receipts.ExportCanonical(), row => row.State == HostManagerOperationEffectReceiptState.Started);
        Assert.Equal(1u, prior.AttemptNumber);
        Assert.Equal(2u, prior.MaximumAttempts);
        var original = EncodeFrozenOperationV1(legacy, fixture.Plan.OperationCoordinator.Recreate.Capacity);
        InstallLegacyOperationBank(fixture.EnvelopePath, original);
        Assert.True(HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(fixture.EnvelopePath));
        var actualDiskReadback = new DiskUsageScanEffectExecutor(null!, null!);
        var startsAtReadback = -1;
        fixture.DiskExecutor.OnReadback = (ticket, receipt, token) =>
        {
            startsAtReadback = fixture.DiskExecutor.ExecuteCount;
            return actualDiskReadback.ReadbackAsync(ticket, receipt, token);
        };

        await fixture.Owner.StartAsync(CancellationToken.None);
        var result = await fixture.WaitForTerminalAsync($"{prior.OperationId.High:x16}{prior.OperationId.Low:x16}");

        Assert.Equal(HostManagerOperationStates.Succeeded, result.State);
        Assert.Equal(2u, result.AttemptNumber);
        Assert.Equal(2u, result.MaximumAttempts);
        Assert.Equal(0, startsAtReadback);
        Assert.Equal(originalReceipt, fixture.DiskExecutor.LastReadbackEvidence);
        Assert.Equal(1, fixture.DiskExecutor.ExecuteCount);
        var current = fixture.ReadEnvelope();
        var settled = current.Receipts.Require(originalReceipt.ReceiptId);
        Assert.Equal(HostManagerOperationEffectReceiptState.Settled, settled.State);
        Assert.Equal(HostManagerOperationEffectReceiptOutcome.EffectNotObserved, settled.Outcome);
        Assert.Equal(originalReceipt.AttemptToken, settled.AttemptToken);
        Assert.Equal(originalReceipt.ExpectedBeforeHandle, settled.ExpectedBeforeHandle);
        Assert.True(settled.ReceiptRevision > originalReceipt.ReceiptRevision);
        Assert.All(current.Receipts.ExportCanonical(), receipt => Assert.Equal(HostManagerOperationEffectReceiptState.Settled, receipt.State));
        Assert.False(HostManagerOperationCanonicalEnvelopeCodec.HasOutstandingObligations(fixture.EnvelopePath));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(OperationBackups(fixture.EnvelopePath))));
    }

    [Theory]
    [InlineData("bad-bank")]
    [InlineData("bad-manifest")]
    [InlineData("missing-bank")]
    [InlineData("oversized-bank")]
    [InlineData("interrupted-unsupported-request")]
    public async Task UnreadableLegacyPublishesLoggedEmptyWithoutWritingUntilExplicitSubmit(string source)
    {
        var log = new OperationMigrationLogger();
        await using var fixture = await Fixture.CreateUnstartedWithMigrationLogAsync(log);
        var original = ReadInstalledOperationFixture("operation-coordinator-v1.bin");
        var manifest = ReadInstalledOperationFixture("operation-coordinator-v1.bin.root.manifest");
        if (source == "interrupted-unsupported-request")
        {
            var produced = await CreateOperationImportImageAsync(Path.Combine(fixture.Root, "v1-source"), HostManagerOperationStates.Running, 0x7fff);
            original = EncodeFrozenOperationV1(ConvertFixtureToV1Schema(produced.Envelope), fixture.Plan.OperationCoordinator.Recreate.Capacity);
            InstallLegacyOperationBank(fixture.EnvelopePath, original);
            manifest = File.ReadAllBytes(fixture.EnvelopePath + ".root.manifest");
        }
        else
        {
            if (source == "bad-bank") original[0] ^= 1;
            if (source == "bad-manifest") manifest[0] ^= 1;
            if (source == "oversized-bank") original = new byte[checked((int)fixture.Plan.OperationCoordinator.Recreate.Capacity.MaximumEnvelopeByteCount + 1)];
            InstallLegacyOperationBank(fixture.EnvelopePath, original, manifest);
            if (source == "missing-bank") File.Delete(fixture.EnvelopePath);
        }
        await fixture.Owner.StartAsync(CancellationToken.None);
        Assert.True(fixture.Owner.GetHealth().Ready);
        Assert.Empty(fixture.Owner.GetRecent());
        Assert.Empty(OperationBackups(fixture.EnvelopePath));
        if (source == "missing-bank") Assert.False(File.Exists(fixture.EnvelopePath));
        else Assert.Equal(original, File.ReadAllBytes(fixture.EnvelopePath));
        Assert.Equal(manifest, File.ReadAllBytes(fixture.EnvelopePath + ".root.manifest"));
        Assert.Contains(log.Messages, text => text.Contains("current operation state is empty", StringComparison.Ordinal));
        if (source == "interrupted-unsupported-request")
            Assert.Contains(log.Messages, text => text.Contains("request schema 7fff", StringComparison.Ordinal));

        var submitted = await fixture.SubmitComponentDownloadAsync();
        Assert.Equal(HostManagerOperationStates.Succeeded, (await fixture.WaitForTerminalAsync(submitted.Id)).State);
        var backupFiles = Directory.GetFiles(Path.GetDirectoryName(fixture.EnvelopePath)!, Path.GetFileName(fixture.EnvelopePath) + ".unconverted.byte-exact.*.backup*");
        var manifestBackup = Assert.Single(backupFiles, path => path.EndsWith(".root.manifest", StringComparison.Ordinal));
        Assert.Equal(manifest, File.ReadAllBytes(manifestBackup));
        if (source != "missing-bank") Assert.Equal(original, File.ReadAllBytes(Assert.Single(backupFiles, path => !path.EndsWith(".root.manifest", StringComparison.Ordinal))));
        Assert.Contains(log.Messages, text => text.Contains("explicit new operation", StringComparison.Ordinal) && text.Contains("byte-exact", StringComparison.Ordinal));
        var current = fixture.ReadEnvelope();
        Assert.Single(current.NativeImage.Records);
        Assert.Equal(HostManagerOperationStates.Succeeded, fixture.Owner.Get(submitted.Id)!.State);
        var beforeRestart = backupFiles.ToDictionary(path => path, File.ReadAllBytes);
        await fixture.Owner.StopAsync(CancellationToken.None);
        await using var restarted = await Fixture.CreateAsync(fixture.Root, fixture.Plan);
        Assert.True(restarted.Owner.GetHealth().Ready);
        foreach (var saved in beforeRestart) Assert.Equal(saved.Value, File.ReadAllBytes(saved.Key));
        Assert.Equal(beforeRestart.Keys.Order(StringComparer.Ordinal),
            Directory.GetFiles(Path.GetDirectoryName(fixture.EnvelopePath)!, Path.GetFileName(fixture.EnvelopePath) + ".unconverted.byte-exact.*.backup*").Order(StringComparer.Ordinal));
    }

    private static byte[] ReadInstalledOperationFixture(string name)
    {
        var assembly = typeof(HostManagerOperationCoordinatorOwnerTests).Assembly;
        using var source = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(value => value.EndsWith(".Operations." + name, StringComparison.Ordinal)))!;
        using var output = new MemoryStream();
        source.CopyTo(output);
        return output.ToArray();
    }

    private static void InstallLegacyOperationBank(string path, byte[] bank, byte[]? manifest = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bank);
        if (manifest is not null) File.WriteAllBytes(path + ".root.manifest", manifest);
        else new HostManagerDurableRootManifest(path, "operation-coordinator").ValidateOrAdoptCanonical(bank);
    }

    private static string[] OperationBackups(string path)
        => Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".legacy-v1.byte-exact.*.backup");

    // v1 has the same native/catalog/receipt field layout. This frozen schema writer is also checked byte-for-byte against the installed v1 bank above.
    // Interrupted records are produced by the real native engine and effect receipt owner, never fabricated by changing state flags.
    private static byte[] EncodeFrozenOperationV1(HostManagerOperationCanonicalEnvelope envelope,
        ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerOperationCoordinatorCapacityPlan capacity)
    {
        envelope = envelope with { NativeImage = NativeOperationCanonicalChecksum.Rewrite(envelope.NativeImage.Header, envelope.NativeImage.Records, 0x0001_0000) };
        var image = HostManagerOperationCanonicalEnvelopeCodec.Encode(envelope, capacity);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 0x0001_0000);
        SHA256.HashData(image.AsSpan(0, image.Length - SHA256.HashSizeInBytes)).CopyTo(image, image.Length - SHA256.HashSizeInBytes);
        return image;
    }

    private static HostManagerOperationCanonicalEnvelope ConvertFixtureToV1Schema(HostManagerOperationCanonicalEnvelope envelope)
    {
        var payloads = envelope.Payloads.Clone();
        var records = envelope.NativeImage.Records.ToArray();
        for (var index = 0; index < records.Length; index++)
        {
            ref var record = ref records[index];
            record.SubjectHandle = payloads.Add(HostManagerOperationRequestSchemas.Subject, 1, HostManagerOperationPayloadRole.Subject,
                envelope.SessionInstanceId, record.OperationId, default, HostManagerOperationRequestCodec.EncodeText("旧版操作标题，不用于恢复事实")).Handle;
        }
        var roots = HostManagerOperationPayloadCatalog.CollectNativeRoots(records);
        envelope.Receipts.AddPayloadRoots(roots);
        payloads.PruneTo(roots);
        return envelope with { NativeImage = NativeOperationCanonicalChecksum.Rewrite(envelope.NativeImage.Header, records, 0x0001_0000), Payloads = payloads };
    }

    private sealed class OperationMigrationLogger : ILogger<HostManagerOperationCoordinatorOwner>
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception) + " " + exception?.Message);
    }

    private sealed class OperationMigrationClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed partial class Fixture
    {
        internal RecordingEffectExecutor DiskExecutor
            => (RecordingEffectExecutor)OwnerField<HostManagerOperationEffectRouter>(Owner, "effectRouter")!.Require(HostManagerOperationKinds.DiskUsageScan);

        internal static Task<Fixture> CreateUnstartedWithMigrationLogAsync(ILogger<HostManagerOperationCoordinatorOwner> logger, TimeProvider? clock = null)
            => CreateAsync(CreateTemporaryRoot(), CreateSmallPlan(), ownsRoot: true, start: false, operationLogger: logger, operationClock: clock);

        internal static Task<Fixture> CreateStartedWithMigrationLogAsync(string root,
            ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan plan, ILogger<HostManagerOperationCoordinatorOwner> logger, TimeProvider clock)
            => CreateAsync(root, plan, ownsRoot: false, operationLogger: logger, operationClock: clock);
    }
}
