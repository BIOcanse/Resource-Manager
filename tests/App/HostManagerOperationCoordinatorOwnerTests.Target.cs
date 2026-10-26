using System.Text;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task EveryOperationTargetPersistsAcrossCompletionAndOwnerRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var accepted = new List<HostManagerOperationSnapshot>();
        foreach (var command in HostManagerOperationTargetTests.Commands())
        {
            var submitted = await fixture.Owner.SubmitAsync(command, CancellationToken.None);
            Assert.Equal(command.Subject, submitted.Subject);
            var terminal = await fixture.WaitForTerminalAsync(submitted.Id);
            Assert.Equal(HostManagerOperationStates.Succeeded, terminal.State);
            Assert.Equal(command.Subject, terminal.Subject);
            accepted.Add(terminal);
        }
        await fixture.Owner.StopAsync(CancellationToken.None);
        await using var restarted = await Fixture.CreateAsync(fixture.Root, fixture.Plan);
        foreach (var operation in accepted)
        {
            var restored = restarted.Owner.Get(operation.Id)!;
            Assert.Equal(operation.Subject, restored.Subject);
            Assert.Equal(operation.DomainKey, restored.DomainKey);
        }
        Assert.All(restarted.ReadEnvelope().NativeImage.Records, record =>
            Assert.Equal(HostManagerOperationRequestSchemas.SubjectVersion,
                restarted.ReadEnvelope().Payloads.Require(record.SubjectHandle).SchemaVersion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedSoftwareNameSurvivesSuccessOrUncertainOutcomeAndNewNamesDoNotRewriteHistory(bool fail)
    {
        await using var fixture = await Fixture.CreateAsync();
        var executor = (RecordingEffectExecutor)OwnerField<HostManagerOperationEffectRouter>(fixture.Owner, "effectRouter")!
            .Require(HostManagerOperationKinds.SoftwareUninstall);
        executor.OnExecute = (_, _) => ValueTask.FromResult(new HostManagerOperationEffectCompletion(
            fail ? HostManagerOperationEffectOutcome.TerminalFailure : HostManagerOperationEffectOutcome.Succeeded,
            fail ? HostManagerOperationFeedback.Reason(ResourceManager.App.Domain.Messages.BackendMessageCodes.Operation.UnexpectedFailure) : ""));
        var command = HostManagerOperationTargetTests.Commands().Single(item => item.Subject.TargetName == "Recorded 产品名称");
        var first = await fixture.Owner.SubmitAsync(command, CancellationToken.None);
        var terminal = await fixture.WaitForTerminalAsync(first.Id);
        // Started non-success is deliberately uncertain in the existing coordinator contract.
        Assert.Equal(fail ? HostManagerOperationStates.StateUncertain : HostManagerOperationStates.Succeeded, terminal.State);
        var second = await fixture.Owner.SubmitAsync(command with { Subject = command.Subject with { TargetName = "Renamed" } }, CancellationToken.None);
        await fixture.WaitForTerminalAsync(second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Recorded 产品名称", fixture.Owner.Get(first.Id)!.Subject!.TargetName);
        await fixture.Owner.StopAsync(CancellationToken.None);
        await using var restarted = await Fixture.CreateAsync(fixture.Root, fixture.Plan);
        Assert.Equal("Recorded 产品名称", restarted.Owner.Get(first.Id)!.Subject!.TargetName);
        Assert.Equal("Renamed", restarted.Owner.Get(second.Id)!.Subject!.TargetName);
        // No software catalog is attached to either owner: deletion cannot affect history reads.
    }

    [Fact]
    public async Task RecoveryConfirmedFailedSoftwareRetainsItsAcceptedNameAndHistoryAfterRename()
    {
        await using var producer = await Fixture.CreateAsync();
        await using var fixture = await Fixture.CreateUnstartedAsync();
        var entered = NewSignal();
        var release = NewSignal();
        var executor = (RecordingEffectExecutor)OwnerField<HostManagerOperationEffectRouter>(producer.Owner, "effectRouter")!
            .Require(HostManagerOperationKinds.SoftwareUninstall);
        executor.OnExecute = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(HostManagerOperationEffectOutcome.Succeeded, "");
        };
        var command = HostManagerOperationTargetTests.Commands().Single(item => item.Subject.TargetName == "Recorded 产品名称");
        var submitted = await producer.Owner.SubmitAsync(command, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TestBound);
            var gate = OwnerGate(producer.Owner);
            Assert.True(await gate.WaitAsync(TestBound));
            try
            {
                var image = HostManagerOperationCanonicalEnvelopeCodec.Encode(producer.ReadEnvelope(), fixture.Plan.OperationCoordinator.Recreate.Capacity);
                InstallLegacyOperationBank(fixture.EnvelopePath, image);
            }
            finally { gate.Release(); }
        }
        finally { release.TrySetResult(); }
        await producer.WaitForTerminalAsync(submitted.Id);
        var recovery = (RecordingEffectExecutor)OwnerField<HostManagerOperationEffectRouter>(fixture.Owner, "effectRouter")!
            .Require(HostManagerOperationKinds.SoftwareUninstall);
        recovery.OnReadback = (_, _, _) => ValueTask.FromResult(new HostManagerOperationEffectCompletion(
            HostManagerOperationEffectOutcome.TerminalFailure,
            HostManagerOperationFeedback.Reason(ResourceManager.App.Domain.Messages.BackendMessageCodes.Operation.UnexpectedFailure)));
        await fixture.Owner.StartAsync(CancellationToken.None);
        var failed = await fixture.WaitForTerminalAsync(submitted.Id);
        Assert.Equal(HostManagerOperationStates.Failed, failed.State);
        Assert.Equal(command.Subject, failed.Subject);
        Assert.Equal(0, recovery.ExecuteCount);
        var renamed = await fixture.Owner.SubmitAsync(command with { Subject = command.Subject with { TargetName = "Renamed" } }, CancellationToken.None);
        await fixture.WaitForTerminalAsync(renamed.Id);
        await fixture.Owner.StopAsync(CancellationToken.None);
        await using var restarted = await Fixture.CreateAsync(fixture.Root, fixture.Plan);
        Assert.Equal(HostManagerOperationStates.Failed, restarted.Owner.Get(submitted.Id)!.State);
        Assert.Equal("Recorded 产品名称", restarted.Owner.Get(submitted.Id)!.Subject!.TargetName);
        Assert.Equal("Renamed", restarted.Owner.Get(renamed.Id)!.Subject!.TargetName);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("running")]
    public async Task StoredV1SubjectMigratesToTypedIdentityAndExplicitNullWithoutChangingRequestOrReceipt(string state)
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        var produced = await CreateOperationImportImageAsync(Path.Combine(fixture.Root, "subject-v1-source"), state);
        var source = WithV1Subjects(produced.Envelope);
        var image = HostManagerOperationCanonicalEnvelopeCodec.Encode(source, fixture.Plan.OperationCoordinator.Recreate.Capacity);
        InstallLegacyOperationBank(fixture.EnvelopePath, image);
        var prior = Assert.Single(source.NativeImage.Records);
        var id = $"{prior.OperationId.High:x16}{prior.OperationId.Low:x16}";
        await fixture.Owner.StartAsync(CancellationToken.None);
        var restored = await fixture.WaitForTerminalAsync(id);
        Assert.Equal("fixture-component", restored.Subject!.TargetId);
        Assert.Equal(HostManagerOperationTargetKind.Component, restored.Subject.TargetKind);
        Assert.Null(restored.Subject.TargetName);
        var persisted = fixture.ReadEnvelope();
        var record = Assert.Single(persisted.NativeImage.Records);
        var entry = persisted.Payloads.Require(record.SubjectHandle);
        Assert.Equal(HostManagerOperationRequestSchemas.SubjectVersion, entry.SchemaVersion);
        Assert.Null(HostManagerOperationRequestCodec.DecodeSubject(entry.Payload).TargetName);
        Assert.Equal(prior.RequestHandle, record.RequestHandle);
        Assert.Equal(prior.DomainId, record.DomainId);
        Assert.Equal(prior.AttemptToken, record.AttemptToken);
        foreach (var receipt in source.Receipts.ExportCanonical())
            Assert.Equal(receipt, persisted.Receipts.Require(receipt.ReceiptId));
        Assert.Equal(0, fixture.ComponentExecutor.ExecuteCount);
        await fixture.Owner.StopAsync(CancellationToken.None);
        await using var restarted = await Fixture.CreateAsync(fixture.Root, fixture.Plan);
        Assert.Equal(restored.Subject, restarted.Owner.Get(id)!.Subject);
    }

    [Fact]
    public void EveryLegacySubjectKindMigratesWithNoRecordedNameIncludingPaths()
    {
        foreach (var command in HostManagerOperationTargetTests.Commands())
        {
            var result = HostManagerOperationRequestCodec.DecodeLegacySubject(EncodeV1Subject(command.Subject), command.Kind);
            Assert.Equal(command.Subject with { TargetName = null }, result);
        }
        Assert.Throws<InvalidDataException>(() => HostManagerOperationRequestCodec.DecodeLegacySubject(
            EncodeV1Subject(HostManagerOperationTargetTests.Commands()[0].Subject), "unknown"));
    }

    private static HostManagerOperationCanonicalEnvelope WithV1Subjects(HostManagerOperationCanonicalEnvelope source)
    {
        var payloads = source.Payloads.Clone();
        var records = source.NativeImage.Records.ToArray();
        for (var index = 0; index < records.Length; index++)
        {
            ref var record = ref records[index];
            var subject = HostManagerOperationRequestCodec.DecodeSubject(payloads.Require(record.SubjectHandle).Payload);
            record.SubjectHandle = payloads.Add(HostManagerOperationRequestSchemas.Subject, 1, HostManagerOperationPayloadRole.Subject,
                source.SessionInstanceId, record.OperationId, default, EncodeV1Subject(subject)).Handle;
        }
        var roots = HostManagerOperationPayloadCatalog.CollectNativeRoots(records);
        source.Receipts.AddPayloadRoots(roots);
        payloads.PruneTo(roots);
        return source with { Payloads = payloads,
            NativeImage = NativeOperationCanonicalChecksum.Rewrite(source.NativeImage.Header, records, NativeOperationCoordinatorAbi.Version) };
    }

    private static byte[] EncodeV1Subject(HostManagerOperationSubject subject)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        foreach (var value in new[] { subject.TargetId, subject.Scope, subject.Mode, subject.TargetPath })
        {
            writer.Write(value is not null);
            if (value is null) continue;
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
