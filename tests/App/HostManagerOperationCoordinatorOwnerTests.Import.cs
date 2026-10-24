using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    internal sealed record OperationImportImage(byte[] Canonical, byte[] Manifest,
        HostManagerOperationCanonicalEnvelope Envelope);

    // The owner writes every native record, payload, receipt, checksum and root
    // manifest. No import fixture synthesizes or edits the binary format.
    internal static async Task<OperationImportImage> CreateOperationImportImageAsync(string producerRoot, string state)
    {
        await using var fixture = await Fixture.CreateAsync(producerRoot, CreateSmallPlan());
        var paths = HostManagerDurableStorePaths.Resolve(producerRoot).Single(store => store.Kind == "operation-coordinator");
        Assert.Equal(paths.CanonicalPath, fixture.EnvelopePath);
        var blocked = state is HostManagerOperationStates.Running or HostManagerOperationStates.Queued or HostManagerOperationStates.Canceled;
        var captureLive = state is HostManagerOperationStates.Running or HostManagerOperationStates.Queued;
        var entered = NewSignal();
        var release = NewSignal();
        string? runningId = null;
        fixture.ComponentExecutor.OnExecute = async (_, cancellationToken) =>
        {
            if (blocked)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return state switch
            {
                HostManagerOperationStates.StateUncertain => new(HostManagerOperationEffectOutcome.Uncertain, "fixture uncertain"),
                _ => new(HostManagerOperationEffectOutcome.Succeeded, "fixture completed")
            };
        };
        try
        {
            var submitted = await fixture.SubmitComponentDownloadAsync();
            runningId = submitted.Id;
            if (blocked)
            {
                await entered.Task.WaitAsync(TestBound);
                Assert.Equal(HostManagerOperationStates.Running, fixture.Owner.Get(submitted.Id)!.State);
                if (state is HostManagerOperationStates.Queued or HostManagerOperationStates.Canceled)
                {
                    var queued = await fixture.Owner.SubmitAsync(
                        HostManagerOperationRequestCodec.ComponentDownload("second-fixture-component", new ComponentActionRequest(true)),
                        CancellationToken.None);
                    Assert.Equal(HostManagerOperationStates.Queued, fixture.Owner.Get(queued.Id)!.State);
                    if (state == HostManagerOperationStates.Canceled)
                    {
                        _ = await fixture.Owner.CancelAsync(queued.Id, CancellationToken.None);
                        Assert.Equal(HostManagerOperationStates.Canceled, (await fixture.WaitForTerminalAsync(queued.Id)).State);
                        release.TrySetResult();
                        Assert.Equal(HostManagerOperationStates.Succeeded, (await fixture.WaitForTerminalAsync(submitted.Id)).State);
                    }
                }
            }
            else
                Assert.Equal(state, (await fixture.WaitForTerminalAsync(submitted.Id)).State);

            // Pause mutations only while reading the canonical+manifest pair.
            // For live snapshots, the effect stays paused after Started is durable.
            var gate = OwnerGate(fixture.Owner);
            Assert.True(await gate.WaitAsync(TestBound), "The owner mutation gate did not become available.");
            try
            {
                var envelope = fixture.ReadEnvelope();
                Assert.NotEmpty(envelope.NativeImage.Records);
                Assert.NotEmpty(envelope.Payloads.ExportCanonical());
                Assert.NotEmpty(envelope.Receipts.ExportCanonical());
                if (captureLive)
                {
                    Assert.Contains(envelope.Receipts.ExportCanonical(),
                        receipt => receipt.State == HostManagerOperationEffectReceiptState.Started);
                    if (state == HostManagerOperationStates.Queued)
                        Assert.Contains(envelope.NativeImage.Records, record => (NativeOperationState)record.State == NativeOperationState.Queued);
                }
                else if (state != HostManagerOperationStates.StateUncertain)
                {
                    Assert.All(envelope.Receipts.ExportCanonical(),
                        receipt => Assert.Equal(HostManagerOperationEffectReceiptState.Settled, receipt.State));
                    if (state == HostManagerOperationStates.Canceled)
                        Assert.Contains(envelope.NativeImage.Records, record => (NativeOperationState)record.State == NativeOperationState.Canceled);
                }
                return new(File.ReadAllBytes(paths.CanonicalPath), File.ReadAllBytes(paths.ManifestPath), envelope);
            }
            finally { gate.Release(); }
        }
        finally
        {
            release.TrySetResult();
            if (blocked && runningId is not null) _ = await fixture.WaitForTerminalAsync(runningId);
        }
    }
}
