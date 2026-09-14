using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Text.Json.Nodes;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task PublishedStateProjectsOneCommittedImageAndAdvancesRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Owner.GetPublishedState();

        var submitted = await fixture.SubmitComponentDownloadAsync();
        var after = fixture.Owner.GetPublishedState();

        Assert.True(after.PublicationRevision > before.PublicationRevision);
        Assert.True(after.CapturedAt >= before.CapturedAt);
        Assert.Equal(
            after.ConfigurationGeneration,
            after.Health.ConfigurationGeneration);
        Assert.Equal(
            after.ConfigurationGeneration,
            Assert.Single(after.Operations, item => item.Id == submitted.Id)
                .ConfigurationGeneration);
        var repeated = fixture.Owner.GetPublishedState();
        Assert.Equal(after.PublicationRevision, repeated.PublicationRevision);
        Assert.Equal(after.CapturedAt, repeated.CapturedAt);
        Assert.Equal(after.ConfigurationGeneration, repeated.ConfigurationGeneration);
        _ = await fixture.WaitForTerminalAsync(submitted.Id);
    }

    [Fact]
    public void RunningOwnerRejectsProcessTopologyChanges()
    {
        var current = CreateSmallPlan().OperationCoordinator;
        var changedWorkers = current with
        {
            HotPublish = current.HotPublish with
            {
                MaximumGlobalRunningCount =
                    current.HotPublish.MaximumGlobalRunningCount + 1
            }
        };
        var changedChannel = current with
        {
            Recreate = current.Recreate with
            {
                Capacity = current.Recreate.Capacity with
                {
                    MaximumActionCount =
                        current.Recreate.Capacity.MaximumActionCount + 1
                }
            }
        };

        Assert.False(
            HostManagerOperationCoordinatorOwner
                .RequiresProcessTopologyRestart(current, current));
        Assert.True(
            HostManagerOperationCoordinatorOwner
                .RequiresProcessTopologyRestart(current, changedWorkers));
        Assert.True(
            HostManagerOperationCoordinatorOwner
                .RequiresProcessTopologyRestart(current, changedChannel));
    }

    [Fact]
    public async Task StartEffectRunsOnlyAfterExactReceiptIsDurable()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.ComponentExecutor.OnExecute = async (ticket, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = fixture.ReadEnvelope();
            var receipt = envelope.Receipts.Require(ticket.ReceiptId);
            Assert.Equal(HostManagerOperationEffectReceiptState.Started, receipt.State);
            Assert.Equal(HostManagerOperationEffectReceiptOutcome.None, receipt.Outcome);
            Assert.Equal(ticket.Action.OperationId, receipt.OperationId);
            Assert.Equal(ticket.Action.AttemptToken, receipt.AttemptToken);
            Assert.Equal(ticket.Action.ActionId, receipt.ActionId);
            Assert.Equal(ticket.Action.PlanEpoch, receipt.PlanEpoch);
            Assert.Equal(ticket.Action.ConfigurationGeneration, receipt.ConfigurationGeneration);
            var evidence = envelope.Payloads.Require(receipt.ExpectedBeforeHandle);
            Assert.Equal(
                HostManagerOperationPayloadRole.EffectExpectedBefore,
                evidence.Role);
            Assert.Equal(
                ticket.Request.Handle,
                HostManagerOperationRequestCodec.DecodeEffectExpectedBefore(
                    evidence.Payload));
            await Task.Yield();
            return new(
                HostManagerOperationEffectOutcome.Succeeded,
                "component downloaded");
        };

        var submitted = await fixture.SubmitComponentDownloadAsync();
        var terminal = await fixture.WaitForTerminalAsync(submitted.Id);

        Assert.Equal(HostManagerOperationStates.Succeeded, terminal.State);
        Assert.Equal("component downloaded", terminal.Result);
        Assert.Equal(1, fixture.ComponentExecutor.ExecuteCount);
        var committed = fixture.ReadEnvelope();
        var receipt = Assert.Single(
            committed.Receipts.ExportCanonical(),
            value => value.OperationId == ParseHandle(submitted.Id));
        Assert.Equal(HostManagerOperationEffectReceiptState.Settled, receipt.State);
        Assert.Equal(HostManagerOperationEffectReceiptOutcome.Succeeded, receipt.Outcome);
        var observation = committed.Payloads.Require(receipt.ObservationHandle);
        Assert.Equal(HostManagerOperationPayloadRole.EffectObservation, observation.Role);
        Assert.Equal(
            "component downloaded",
            HostManagerOperationRequestCodec.DecodeText(observation.Payload));
    }

    [Fact]
    public async Task PersistedCancelAfterStartedPreservesUncertainEffect()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ComponentExecutor.OnExecute = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new(
                    HostManagerOperationEffectOutcome.Uncertain,
                    "blocking effect returned without cancellation");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return new(
                    HostManagerOperationEffectOutcome.Canceled,
                    "component download canceled");
            }
        };

        var submitted = await fixture.SubmitComponentDownloadAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var requested = await fixture.Owner.CancelAsync(
            submitted.Id,
            CancellationToken.None);
        Assert.NotNull(requested);
        Assert.True(requested.CancelRequested);

        var terminal = await fixture.WaitForTerminalAsync(submitted.Id);
        Assert.Equal(HostManagerOperationStates.StateUncertain, terminal.State);
        Assert.Equal(1, fixture.ComponentExecutor.ExecuteCount);
        Assert.Single(
            fixture.Owner.GetRecent(),
            operation => operation.Id == submitted.Id);
    }

    [Fact]
    public async Task RecreatePublishedDuringAnActiveAttemptAppliesWhenQuiescent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ComponentExecutor.OnExecute = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new(
                HostManagerOperationEffectOutcome.Succeeded,
                "component downloaded");
        };
        var changed = CreateSmallPlan(root =>
        {
            root["profile_revision"] =
                root["profile_revision"]!.GetValue<int>() + 1;
            var capacity = root["host_recreate"]!["operation_coordinator"]!["capacity"]!;
            capacity["maximum_operation_count"] = 33;
            capacity["maximum_read_count"] = 33;
            capacity["operation_index_capacity"] = 128;
        });

        var submitted = await fixture.SubmitComponentDownloadAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "operation-owner-quiescent-recreate-test",
            HostManager = changed
        });
        await Task.Delay(100);

        Assert.NotEqual(
            changed.OperationCoordinator.HotPublish.ConfigurationGeneration,
            fixture.Owner.GetHealth().ConfigurationGeneration);

        release.TrySetResult();
        _ = await fixture.WaitForTerminalAsync(submitted.Id);
        await fixture.WaitForConfigurationGenerationAsync(
            changed.OperationCoordinator.HotPublish.ConfigurationGeneration);
        var published = fixture.Owner.GetPublishedState();
        Assert.Equal(
            changed.OperationCoordinator.HotPublish.ConfigurationGeneration,
            published.ConfigurationGeneration);
        Assert.Equal(
            published.ConfigurationGeneration,
            published.Health.ConfigurationGeneration);
        Assert.All(
            published.Operations,
            operation => Assert.Equal(
                published.ConfigurationGeneration,
                operation.ConfigurationGeneration));
    }

    [Fact]
    public async Task RestartPreservesSessionChangesClockAndPublishesRecoveredImage()
    {
        var root = CreateTemporaryRoot();
        var plan = CreateSmallPlan();
        string operationId;
        HostManagerOperationCanonicalEnvelope beforeRestart;
        try
        {
            await using (var first = await Fixture.CreateAsync(root, plan))
            {
                var submitted = await first.SubmitComponentDownloadAsync();
                operationId = submitted.Id;
                _ = await first.WaitForTerminalAsync(operationId);
            }
            beforeRestart = LoadEnvelope(root, plan);

            await using (var second = await Fixture.CreateAsync(root, plan))
            {
                var recovered = second.Owner.Get(operationId);
                Assert.NotNull(recovered);
                Assert.Equal(HostManagerOperationStates.Succeeded, recovered.State);
                Assert.True(second.Owner.GetHealth().Ready);
            }
            var afterRestart = LoadEnvelope(root, plan);

            Assert.Equal(
                beforeRestart.SessionInstanceId,
                afterRestart.SessionInstanceId);
            Assert.NotEqual(
                beforeRestart.SourceClockInstanceId,
                afterRestart.SourceClockInstanceId);
            Assert.True(
                afterRestart.PublicationRevision
                > beforeRestart.PublicationRevision);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CanonicalStoreRejectsSecondOwnerAndStaleOverwrite()
    {
        var root = CreateTemporaryRoot();
        var plan = CreateSmallPlan();
        try
        {
            await using (var fixture = await Fixture.CreateAsync(root, plan))
            {
            }

            var path = EnvelopePath(root, plan);
            using var first = new HostManagerOperationCanonicalStore(
                path,
                plan.OperationCoordinator.Recreate.Capacity,
                plan.OperationCoordinator.HotPublish.ConfigurationGeneration);
            first.AcquireOwnerLease();
            using var second = new HostManagerOperationCanonicalStore(
                path,
                plan.OperationCoordinator.Recreate.Capacity,
                plan.OperationCoordinator.HotPublish.ConfigurationGeneration);
            Assert.Throws<InvalidOperationException>(second.AcquireOwnerLease);

            var initial = Assert.IsType<HostManagerOperationCanonicalEnvelope>(
                first.LoadValidated());
            var next = initial with
            {
                PublicationRevision = initial.PublicationRevision + 1,
                PriorEnvelopeSha256 = initial.FullEnvelopeSha256.ToArray(),
                FullEnvelopeSha256 =
                    HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest()
            };
            var committed = first.Commit(
                next,
                initial.FullEnvelopeSha256,
                plan.OperationCoordinator.Recreate.Capacity,
                plan.OperationCoordinator.HotPublish.ConfigurationGeneration);
            Assert.True(
                committed.PublicationRevision > initial.PublicationRevision);
            var stale = next with
            {
                PublicationRevision = next.PublicationRevision + 1,
                FullEnvelopeSha256 =
                    HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest()
            };
            Assert.Throws<IOException>(
                () => first.Commit(
                    stale,
                    initial.FullEnvelopeSha256,
                    plan.OperationCoordinator.Recreate.Capacity,
                    plan.OperationCoordinator.HotPublish.ConfigurationGeneration));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task PersistenceFailureLatchesOwnerAndRequiresRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        File.WriteAllBytes(
            fixture.EnvelopePath,
            Enumerable.Repeat(
                    (byte)0xA5,
                    System.Security.Cryptography.SHA256.HashSizeInBytes)
                .ToArray());

        await Assert.ThrowsAnyAsync<Exception>(
            fixture.SubmitComponentDownloadAsync);
        var health = fixture.Owner.GetHealth();
        Assert.False(health.Ready);
        Assert.True(health.PersistenceFaulted);
        Assert.Equal("submit-publication", health.FaultStage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            fixture.SubmitComponentDownloadAsync);
        Assert.Equal(0, fixture.ComponentExecutor.ExecuteCount);
    }

    private static CompiledHostManagerPlan CreateSmallPlan(
        Action<JsonObject>? mutate = null)
        => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var build = root["build_specialize"]!["capacity_limits"]![
                "operation_coordinator"]!;
            SetCapacity(build, 64, 64, 64, 128, 128, 64, 1_048_576,
                33_554_432, 256, 2_097_152, 128, 4_194_304);
            var recreate = root["host_recreate"]!["operation_coordinator"]![
                "capacity"]!;
            SetCapacity(recreate, 32, 32, 32, 64, 64, 32, 524_288,
                16_777_216, 128, 1_048_576, 64, 2_097_152);
            var hot = root["hot_publish"]!["operation_coordinator"]!;
            hot["maximum_global_running_count"] = 1;
            hot["maximum_recent_terminal_count"] = 16;
            hot["maximum_start_actions_per_plan"] = 4;
            hot["maximum_cancel_actions_per_plan"] = 4;
            hot["maximum_recover_actions_per_plan"] = 4;
            foreach (var kind in hot["kinds"]!.AsArray())
            {
                kind!["retry_delay_ms"] = 10;
                kind["execution_timeout_ms"] = 30_000;
                kind["cancel_grace_ms"] = 5_000;
                kind["terminal_retention_ms"] = 60_000;
            }
            mutate?.Invoke(root);
        });

    private static void SetCapacity(
        JsonNode target,
        int maximumOperationCount,
        int maximumDomainCount,
        int maximumActionCount,
        int operationIndexCapacity,
        int domainIndexCapacity,
        int maximumReadCount,
        int maximumPersistenceByteCount,
        int residentByteBudget,
        int maximumPayloadCount,
        int maximumPayloadByteCount,
        int maximumEffectReceiptCount,
        int maximumEnvelopeByteCount)
    {
        target["maximum_operation_count"] = maximumOperationCount;
        target["maximum_domain_count"] = maximumDomainCount;
        target["maximum_action_count"] = maximumActionCount;
        target["operation_index_capacity"] = operationIndexCapacity;
        target["domain_index_capacity"] = domainIndexCapacity;
        target["maximum_read_count"] = maximumReadCount;
        target["maximum_persistence_byte_count"] = maximumPersistenceByteCount;
        target["resident_byte_budget"] = residentByteBudget;
        target["maximum_payload_count"] = maximumPayloadCount;
        target["maximum_payload_byte_count"] = maximumPayloadByteCount;
        target["maximum_effect_receipt_count"] = maximumEffectReceiptCount;
        target["maximum_envelope_byte_count"] = maximumEnvelopeByteCount;
    }

    private static HostManagerOperationCanonicalEnvelope LoadEnvelope(
        string root,
        CompiledHostManagerPlan plan)
    {
        using var store = new HostManagerOperationCanonicalStore(
            EnvelopePath(root, plan),
            plan.OperationCoordinator.Recreate.Capacity,
            plan.OperationCoordinator.HotPublish.ConfigurationGeneration);
        store.AcquireOwnerLease();
        return Assert.IsType<HostManagerOperationCanonicalEnvelope>(
            store.LoadValidated());
    }

    private static string EnvelopePath(
        string root,
        CompiledHostManagerPlan plan)
        => Path.Combine(
            root,
            "UserData",
            "HostManager",
            plan.OperationCoordinator.Recreate.CanonicalEnvelopeRelativePath
                .Replace('/', Path.DirectorySeparatorChar));

    private static NativeOperationHandle128 ParseHandle(string value)
        => new(
            Convert.ToUInt64(value[..16], 16),
            Convert.ToUInt64(value[16..], 16));

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "resource-manager-operation-owner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Resource Manager-APP"));
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly bool ownsRoot;

        private Fixture(
            string root,
            CompiledHostManagerPlan plan,
            HostManagerOperationCoordinatorOwner owner,
            RuntimePlanProvider provider,
            RecordingEffectExecutor componentExecutor,
            bool ownsRoot)
        {
            Root = root;
            Plan = plan;
            Owner = owner;
            Provider = provider;
            ComponentExecutor = componentExecutor;
            this.ownsRoot = ownsRoot;
        }

        internal string Root { get; }

        internal CompiledHostManagerPlan Plan { get; }

        internal HostManagerOperationCoordinatorOwner Owner { get; }

        internal RuntimePlanProvider Provider { get; }

        internal RecordingEffectExecutor ComponentExecutor { get; }

        internal string EnvelopePath
            => HostManagerOperationCoordinatorOwnerTests.EnvelopePath(Root, Plan);

        internal static Task<Fixture> CreateAsync()
            => CreateAsync(CreateTemporaryRoot(), CreateSmallPlan(), ownsRoot: true);

        internal static Task<Fixture> CreateAsync(
            string root,
            CompiledHostManagerPlan plan)
            => CreateAsync(root, plan, ownsRoot: false);

        private static async Task<Fixture> CreateAsync(
            string root,
            CompiledHostManagerPlan plan,
            bool ownsRoot)
        {
            var deployment = new HostManagerDeploymentState();
            var provider = new RuntimePlanProvider(deployment);
            provider.Publish(CompiledRuntimePlan.Default with
            {
                Version = 1,
                CompiledAt = DateTimeOffset.UtcNow,
                Reason = "operation-owner-test",
                HostManager = plan
            });
            var component = new RecordingEffectExecutor(
                HostManagerOperationKinds.ComponentDownload);
            var executors = HostManagerOperationKinds.All
                .Select(kind => string.Equals(
                    kind,
                    HostManagerOperationKinds.ComponentDownload,
                    StringComparison.Ordinal)
                    ? component
                    : new RecordingEffectExecutor(kind))
                .Cast<IHostManagerOperationEffectExecutor>()
                .ToArray();
            var owner = new HostManagerOperationCoordinatorOwner(
                provider,
                new HostManagerOperationCoordinatorRuntime(provider, deployment),
                new HostManagerOperationEffectRouter(executors),
                new TestHostEnvironment(
                    Path.Combine(root, "Resource Manager-APP")),
                NullLogger<HostManagerOperationCoordinatorOwner>.Instance);
            await owner.StartAsync(CancellationToken.None);
            return new Fixture(root, plan, owner, provider, component, ownsRoot);
        }

        internal Task<HostManagerOperationSnapshot> SubmitComponentDownloadAsync()
            => Owner.SubmitAsync(
                HostManagerOperationRequestCodec.ComponentDownload(
                    "fixture-component",
                    new ComponentActionRequest(true)),
                CancellationToken.None);

        internal async Task<HostManagerOperationSnapshot> WaitForTerminalAsync(
            string operationId)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = Owner.Get(operationId);
                if (snapshot is not null
                    && HostManagerOperationStates.IsTerminal(snapshot.State))
                {
                    return snapshot;
                }
                await Task.Delay(20);
            }
            var last = Owner.Get(operationId);
            var health = Owner.GetHealth();
            var receiptSummary = string.Join(
                ";",
                ReadEnvelope().Receipts.ExportCanonical()
                    .Where(receipt => receipt.OperationId == ParseHandle(operationId))
                    .Select(receipt =>
                        $"{receipt.ActionKind}:{receipt.State}:"
                        + $"{receipt.AttemptToken.High:x16}{receipt.AttemptToken.Low:x16}:"
                        + $"{receipt.ActionId}"));
            throw new TimeoutException(
                $"Operation {operationId} did not reach a terminal state; "
                + $"state={last?.State ?? "<missing>"}, "
                + $"cancel={last?.CancelRequested}, "
                + $"faulted={health.PersistenceFaulted}, "
                + $"stage={health.FaultStage}, "
                + $"message={health.FaultMessage}, "
                + $"effects={ComponentExecutor.ExecuteCount}, "
                + $"receipts={receiptSummary}.");
        }

        internal async Task WaitForConfigurationGenerationAsync(
            ulong expectedGeneration)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (Owner.GetHealth().ConfigurationGeneration == expectedGeneration)
                {
                    return;
                }
                await Task.Delay(20);
            }
            var health = Owner.GetHealth();
            throw new TimeoutException(
                "The operation coordinator did not apply the pending quiescent "
                + $"configuration generation {expectedGeneration}; "
                + $"current={health.ConfigurationGeneration}, "
                + $"pending={Owner.HasPendingQuiescentRecreate}, "
                + $"ready={health.Ready}, faulted={health.PersistenceFaulted}, "
                + $"stage={health.FaultStage}, message={health.FaultMessage}, "
                + $"planError={Owner.LastPlanApplicationError}.");
        }

        internal HostManagerOperationCanonicalEnvelope ReadEnvelope()
        {
            const int maximumAttempts = 100;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var image = File.ReadAllBytes(EnvelopePath);
                    return HostManagerOperationCanonicalEnvelopeCodec.Decode(
                        image,
                        Plan.OperationCoordinator.Recreate.Capacity,
                        Plan.OperationCoordinator.HotPublish.ConfigurationGeneration);
                }
                catch (IOException error) when (
                    attempt < maximumAttempts
                    && IsTransientFileLock(error))
                {
                    Thread.Sleep(2);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Owner.StopAsync(CancellationToken.None);
            }
            finally
            {
                Owner.Dispose();
                if (ownsRoot)
                {
                    DeleteTemporaryRoot(Root);
                }
            }
        }
    }

    private static bool IsTransientFileLock(IOException error)
    {
        var windowsError = error.HResult & 0xffff;
        return windowsError is 32 or 33;
    }

    private sealed class RecordingEffectExecutor(string kind)
        : IHostManagerOperationEffectExecutor
    {
        private int executeCount;

        internal Func<
            HostManagerOperationActionTicket,
            CancellationToken,
            ValueTask<HostManagerOperationEffectCompletion>>?
            OnExecute
        { get; set; }

        public string Kind { get; } = kind;

        internal int ExecuteCount => Volatile.Read(ref executeCount);

        public async ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
            HostManagerOperationActionTicket ticket,
            IHostManagerOperationProgressSink progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            Interlocked.Increment(ref executeCount);
            return OnExecute is null
                ? new(
                    HostManagerOperationEffectOutcome.Succeeded,
                    "completed")
                : await OnExecute(ticket, cancellationToken);
        }

        public ValueTask<HostManagerOperationEffectCompletion> ReadbackAsync(
            HostManagerOperationActionTicket ticket,
            HostManagerOperationEffectReceipt receipt,
            CancellationToken cancellationToken)
        {
            _ = ticket;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                receipt.State == HostManagerOperationEffectReceiptState.Prepared
                    ? new HostManagerOperationEffectCompletion(
                        HostManagerOperationEffectOutcome.RetryableFailure,
                        "effect was not observed")
                    : new HostManagerOperationEffectCompletion(
                        HostManagerOperationEffectOutcome.Uncertain,
                        "effect state is uncertain"));
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ResourceManager.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; }
            = new NullFileProvider();
    }
}
