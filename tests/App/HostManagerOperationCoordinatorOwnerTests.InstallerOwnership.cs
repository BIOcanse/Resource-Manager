using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Theory]
    [InlineData(0, HostManagerOperationStates.Succeeded)]
    [InlineData(37, HostManagerOperationStates.StateUncertain)]
    public async Task CanceledInstallerWaitKeepsDomainOwnedUntilActualExitAndPublishesThatExit(
        int exitCode,
        string expectedState)
    {
        var childStarted = NewSignal();
        var cancellationObserved = NewSignal();
        using var gate = SyntheticOwnedProcessGate.Create(exitCode);
        Process? child = null;
        SafeWaitHandle? ownedWaitHandle = null;
        var installerExecutor = new RecordingEffectExecutor(
            HostManagerOperationKinds.DependencyLaunchInstaller);
        installerExecutor.OnExecute = async (_, cancellationToken) =>
        {
            if (installerExecutor.ExecuteCount > 1)
                return new(HostManagerOperationEffectOutcome.Succeeded, "next installer completed");

            child = Process.Start(gate.StartInfo)
                ?? throw new InvalidOperationException("The synthetic child process did not start.");
            var waitHandle = WindowsInteractiveUserSessionBroker.DuplicateProcessHandleForWaiting(
                child.SafeHandle.DangerousGetHandle());
            ownedWaitHandle = waitHandle;
            childStarted.TrySetResult();
            var observedExitCode = await WindowsInteractiveUserSessionBroker.WaitForProcessExitAsync(
                waitHandle,
                cancellationToken,
                waitSliceMilliseconds: 50,
                onCancellationObserved: () => cancellationObserved.TrySetResult());
            return observedExitCode == 0
                ? new(HostManagerOperationEffectOutcome.Succeeded, $"installer exited with code {observedExitCode}")
                : new(HostManagerOperationEffectOutcome.TerminalFailure, $"installer exited with code {observedExitCode}");
        };
        var plan = CreateSmallPlan(root =>
        {
            var operationCoordinator = root["hot_publish"]!["operation_coordinator"]!;
            operationCoordinator["maximum_global_running_count"] = 2;
            foreach (var kind in operationCoordinator["kinds"]!.AsArray())
            {
                var entry = kind!;
                if (entry["name"]!.GetValue<string>()
                    == HostManagerOperationKinds.DependencyLaunchInstaller)
                {
                    entry["cancel_grace_ms"] = 10;
                    break;
                }
            }
        });
        await using var fixture = await Fixture.CreateAsync(installerExecutor, plan);

        try
        {
            var submitted = await fixture.Owner.SubmitAsync(
                HostManagerOperationRequestCodec.DependencyInstaller(
                    "fixture-owned-installer",
                    new OptionalDependencyInstallRequest(AcknowledgeExternalTerms: true)),
                CancellationToken.None);
            await childStarted.Task.WaitAsync(TestBound);

            var cancelRequested = await fixture.Owner.CancelAsync(
                submitted.Id,
                CancellationToken.None);
            Assert.NotNull(cancelRequested);
            Assert.True(cancelRequested.CancelRequested);
            Assert.False(HostManagerOperationStates.IsTerminal(cancelRequested.State));
            await cancellationObserved.Task.WaitAsync(TestBound);
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            var waitingOperation = fixture.Owner.Get(submitted.Id)!;
            Assert.True(waitingOperation.CancelRequested);
            Assert.Equal(HostManagerOperationStates.CancelPending, waitingOperation.State);
            var attemptArbiter = OwnerField<HostManagerOperationAttemptArbiter>(
                fixture.Owner,
                "attemptArbiter")!;
            Assert.False(attemptArbiter.IsEmpty);
            var liveWaitHandle = ownedWaitHandle
                ?? throw new InvalidOperationException("The test child has no owned wait handle.");
            Assert.False(liveWaitHandle.IsClosed);
            var waitingReceipts = fixture.ReadEnvelope().Receipts.ExportCanonical()
                .Where(value => value.OperationId == ParseHandle(submitted.Id))
                .ToArray();
            Assert.Equal(2, waitingReceipts.Length);
            Assert.Contains(
                waitingReceipts,
                value => value.ActionKind == NativeOperationActionKind.Cancel);

            var unrelatedComponent = await fixture.SubmitComponentDownloadAsync();
            var unrelatedTerminal = await fixture.WaitForTerminalAsync(unrelatedComponent.Id);
            Assert.Equal(HostManagerOperationStates.Succeeded, unrelatedTerminal.State);
            Assert.Equal(1, installerExecutor.ExecuteCount);
            Assert.False(HostManagerOperationStates.IsTerminal(
                fixture.Owner.Get(submitted.Id)!.State));
            Assert.False(liveWaitHandle.IsClosed);

            gate.Release();
            var terminal = await fixture.WaitForTerminalAsync(submitted.Id);
            Assert.Equal(expectedState, terminal.State);
            if (exitCode == 0)
                Assert.Equal("installer exited with code 0", terminal.Result);
            else
                Assert.Equal($"installer exited with code {exitCode}", terminal.Error);
            Assert.True(liveWaitHandle.IsClosed);
            await WaitUntilAsync(() => attemptArbiter.IsEmpty, TestBound);
            var receipt = Assert.Single(
                fixture.ReadEnvelope().Receipts.ExportCanonical(),
                value => value.OperationId == ParseHandle(submitted.Id)
                    && value.ActionKind == NativeOperationActionKind.Start);
            Assert.Equal(
                exitCode == 0
                    ? HostManagerOperationEffectReceiptState.Settled
                    : HostManagerOperationEffectReceiptState.Uncertain,
                receipt.State);
            Assert.Equal(
                exitCode == 0
                    ? HostManagerOperationEffectReceiptOutcome.Succeeded
                    : HostManagerOperationEffectReceiptOutcome.Uncertain,
                receipt.Outcome);
        }
        finally
        {
            gate.Release();
            if (child is not null)
            {
                await child.WaitForExitAsync().WaitAsync(TestBound);
                child.Dispose();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The owned installer completion did not settle.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }
}
