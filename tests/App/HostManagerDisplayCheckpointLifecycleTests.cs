using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDisplayCheckpointLifecycleTests
{
    [Fact]
    public void RetiredWorkspaceCloseFailureStaysOwnedAndBlocksAnotherReplacementUntilRetrySucceeds()
    {
        using var fixture = new Fixture(mutable: false);
        fixture.Apply("a.bin");
        fixture.Contexts[0].Session.CloseFailuresRemaining = 2;
        fixture.Apply("b.bin"); // Publication succeeds; old Close remains owned.
        Assert.Equal(0, fixture.Contexts[0].Session.DisposeCount);
        Assert.Throws<IOException>(() => fixture.Apply("c.bin"));
        Assert.Equal(2, fixture.Contexts.Count);
        fixture.Apply("c.bin");
        Assert.Equal(1, fixture.Contexts[0].Session.DisposeCount);
        Assert.Equal(3, fixture.Contexts.Count);
    }


    [Fact]
    public void NormalNeverCreatesCheckpointStoreOrDirectory()
    {
        using var fixture = new Fixture(mutable: false);
        fixture.Apply("a.bin");
        fixture.Owner.Refresh();
        fixture.Apply("b.bin");
        Assert.Empty(fixture.Stores);
        Assert.All(fixture.Contexts, context => Assert.Null(context.Callbacks));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void SameRootSharesOneWriterAndRevokesBindingEvenWhenGenerationIsUnchanged()
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin", revision: 64);
        using var old = fixture.Owner.Acquire();
        var oldContext = fixture.Contexts[0];
        var store = Assert.Single(fixture.Stores);
        fixture.Apply("a.bin", revision: 64, maximumNodes: 511);
        Assert.Single(fixture.Stores);
        Assert.Equal(2, store.LoadCount);
        Assert.Equal(0, store.DisposeCount);
        Assert.Equal(oldContext.Configuration.Generation, fixture.Contexts[1].Configuration.Generation);
        old.Workspace.Refresh([]);
        Assert.Equal(0, store.SaveCount);
        fixture.Owner.Refresh();
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(0, oldContext.Session.DisposeCount);
        old.Dispose();
        Assert.Equal(1, oldContext.Session.DisposeCount);
    }

    [Fact]
    public async Task OldRefreshStoppedBeforeSaveCannotReachStoreAfterPublication()
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin");
        using var old = fixture.Owner.Acquire();
        using var export = new Barrier();
        fixture.Contexts[0].Session.BeforeExport = export.Wait;
        var refresh = Task.Run(() => old.Workspace.Refresh([]));
        try
        {
            await export.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Apply("b.bin");
            Assert.Equal(1, fixture.Stores[0].DisposeCount);
            Assert.Equal(0, fixture.Contexts[0].Session.DisposeCount);
        }
        finally { export.Release.Set(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Stores[0].SaveCount);
        Assert.Equal(0, fixture.Stores[0].AccessAfterDispose);
        fixture.Owner.Refresh();
        Assert.Equal(1, fixture.Stores[1].SaveCount);
    }

    [Fact]
    public async Task PublicationDrainsEnteredSaveBeforeClosingOldWriter()
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin");
        using var old = fixture.Owner.Acquire();
        using var save = new Barrier();
        fixture.Stores[0].DuringSave = save.Wait;
        var refresh = Task.Run(() => old.Workspace.Refresh([]));
        Task? publish = null;
        try
        {
            await save.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var enteredApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            publish = Task.Run(() => { enteredApply.SetResult(); fixture.Apply("b.bin"); });
            await enteredApply.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, fixture.Stores[0].DisposeCount);
            Assert.False(publish.IsCompleted);
        }
        finally { save.Release.Set(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        await publish!.WaitAsync(TimeSpan.FromSeconds(10));
        var trace = fixture.Trace.ToArray();
        Assert.True(Array.IndexOf(trace, "save-end:0") < Array.IndexOf(trace, "close:0"));
        Assert.Equal(1, fixture.Stores[0].SaveCount);
        Assert.Equal(1, fixture.Stores[0].DisposeCount);
        Assert.Equal(0, fixture.Contexts[0].Session.DisposeCount);
    }

    [Fact]
    public void CompletedSaveAndOutstandingNativeLeaseDoNotKeepOldRootOpenAcrossAToBToA()
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin");
        using var a = fixture.Owner.Acquire();
        a.Workspace.Refresh([]);
        fixture.Apply("b.bin");
        using var b = fixture.Owner.Acquire();
        b.Workspace.Refresh([]);
        fixture.Apply("a.bin");
        Assert.Equal(3, fixture.Stores.Count);
        Assert.Equal(fixture.Stores[0].Path, fixture.Stores[2].Path);
        Assert.Equal(1, fixture.Stores[0].DisposeCount);
        Assert.Equal(1, fixture.Stores[1].DisposeCount);
        Assert.Equal(0, fixture.Stores[2].DisposeCount);
        Assert.Equal(0, fixture.Contexts[0].Session.DisposeCount);
        Assert.Equal(0, fixture.Contexts[1].Session.DisposeCount);
        a.Workspace.Refresh([]);
        b.Workspace.Refresh([]);
        Assert.Equal(1, fixture.Stores[0].SaveCount);
        Assert.Equal(1, fixture.Stores[1].SaveCount);
        Assert.All(fixture.Stores, store => Assert.Equal(0, store.AccessAfterDispose));
        fixture.Owner.Refresh();
        Assert.Equal(1, fixture.Stores[2].SaveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorLoadFailureKeepsCurrentPermissionAndClosesOnlyNewRoot(bool sameRoot)
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin");
        using var old = fixture.Owner.Acquire();
        if (sameRoot) fixture.Stores[0].FailNextLoad = true;
        else fixture.FailNextStoreLoad = true;
        Assert.Throws<InvalidDataException>(() => fixture.Apply(sameRoot ? "a.bin" : "b.bin"));
        using var current = fixture.Owner.Acquire();
        Assert.Same(old.Workspace, current.Workspace);
        Assert.Equal(0, fixture.Stores[0].DisposeCount);
        if (!sameRoot) Assert.Equal(1, fixture.Stores[1].DisposeCount);
        Assert.Equal(1, fixture.Contexts[1].Session.DisposeCount);
        old.Workspace.Refresh([]);
        Assert.Equal(1, fixture.Stores[0].SaveCount);
    }

    [Fact]
    public void StaleDeploymentSettlementKeepsOldCurrentPermissionAndClosesCandidate()
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin");
        using var old = fixture.Owner.Acquire();
        fixture.AfterWorkspaceCreated = () => fixture.Publish("c.bin");
        Assert.Throws<InvalidOperationException>(() => fixture.Apply("b.bin"));
        using var current = fixture.Owner.Acquire();
        Assert.Same(old.Workspace, current.Workspace);
        Assert.Equal(0, fixture.Stores[0].DisposeCount);
        Assert.Equal(1, fixture.Stores[1].DisposeCount);
        Assert.Equal(1, fixture.Contexts[1].Session.DisposeCount);
        old.Workspace.Refresh([]);
        Assert.Equal(1, fixture.Stores[0].SaveCount);
    }

    [Fact]
    public async Task DisposeDrainsOnlyEnteredSaveAndDoesNotWaitForOutstandingNativeLease()
    {
        var fixture = new Fixture();
        fixture.Apply("a.bin");
        using var old = fixture.Owner.Acquire();
        using var save = new Barrier();
        fixture.Stores[0].DuringSave = save.Wait;
        var refresh = Task.Run(() => old.Workspace.Refresh([]));
        Task? disposal = null;
        try
        {
            await save.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var enteredDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = Task.Run(() => { enteredDispose.SetResult(); fixture.Owner.Dispose(); });
            await enteredDispose.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, fixture.Stores[0].DisposeCount);
            Assert.False(disposal.IsCompleted);
        }
        finally { save.Release.Set(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        await disposal!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fixture.Stores[0].DisposeCount);
        Assert.Equal(0, fixture.Contexts[0].Session.DisposeCount);
        old.Workspace.Refresh([]);
        Assert.Equal(1, fixture.Stores[0].SaveCount);
        Assert.Equal(0, fixture.Stores[0].AccessAfterDispose);
        Assert.Throws<ObjectDisposedException>(() => fixture.Owner.Acquire());
        old.Dispose();
        Assert.Equal(1, fixture.Contexts[0].Session.DisposeCount);
        fixture.Dispose();
    }

    [Fact]
    public void CallerMayHoldLeaseWhileDisposingAndFailedCloseCanRetryWithoutHostLock()
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin");
        using var old = fixture.Owner.Acquire();
        fixture.Stores[0].CloseFailuresRemaining = 1;
        fixture.Owner.Dispose();
        Assert.Equal(1, fixture.Stores[0].DisposeAttempts);
        Assert.Equal(0, fixture.Stores[0].DisposeCount);
        Assert.Equal(0, fixture.Contexts[0].Session.DisposeCount);
        old.Workspace.Refresh([]);
        Assert.Equal(0, fixture.Stores[0].SaveCount);
        fixture.Owner.Dispose();
        Assert.Equal(2, fixture.Stores[0].DisposeAttempts);
        Assert.Equal(1, fixture.Stores[0].DisposeCount);
        fixture.Owner.Dispose();
        Assert.Equal(2, fixture.Stores[0].DisposeAttempts);
    }

    [Fact]
    public void FailedOldRootCloseMustFinishBeforeReopeningThatRoot()
    {
        using var fixture = new Fixture();
        fixture.Apply("a.bin");
        fixture.Stores[0].CloseFailuresRemaining = 2;
        fixture.Apply("b.bin");
        Assert.Equal(0, fixture.Stores[0].DisposeCount);
        Assert.Throws<IOException>(() => fixture.Apply("a.bin"));
        Assert.Equal(2, fixture.Stores.Count);
        fixture.Owner.Refresh();
        Assert.Equal(1, fixture.Stores[1].SaveCount);
        fixture.Apply("a.bin");
        Assert.Equal(1, fixture.Stores[0].DisposeCount);
        Assert.Equal(3, fixture.Stores.Count);
        Assert.Equal(1, fixture.Stores[1].DisposeCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RuntimePlanProvider provider;
        private ulong version;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "rm-display-checkpoint-recorded-" + Guid.NewGuid().ToString("N"));
        internal HostManagerDisplayCoordinatorOwner Owner { get; }
        internal List<Store> Stores { get; } = [];
        internal List<Context> Contexts { get; } = [];
        internal ConcurrentQueue<string> Trace { get; } = new();
        internal bool FailNextStoreLoad;
        internal Action? AfterWorkspaceCreated;

        internal Fixture(bool mutable = true)
        {
            var state = new HostManagerDeploymentState();
            provider = new RuntimePlanProvider(state);
            Owner = new(provider, new HostManagerDisplayCoordinatorRuntime(provider, state),
                new RuntimePersistenceCapabilityPolicy(mutable), new EnvironmentInfo(Root),
                NullLogger<HostManagerDisplayCoordinatorOwner>.Instance, CreateWorkspace, CreateStore);
        }

        internal CompiledHostManagerPlan Publish(string path, int? revision = null, uint maximumNodes = 512)
        {
            var nextVersion = ++version;
            var plan = HostManagerTestPlanFactory.CreatePlan(root =>
            {
                root["profile_revision"] = revision ?? checked(64 + (int)nextVersion);
                var display = root["host_recreate"]!["display_coordinator"]!;
                display["persistence_relative_path"] = "display-coordinator/" + path;
                display["capacity"]!["maximum_node_count"] = maximumNodes;
            }, planEpoch: nextVersion);
            provider.Publish(CompiledRuntimePlan.Default with
            {
                Version = checked((long)nextVersion), CompiledAt = DateTimeOffset.UtcNow,
                Reason = "recorded-display-checkpoint", HostManager = plan
            });
            return plan;
        }

        internal void Apply(string path, int? revision = null, uint maximumNodes = 512)
            => Owner.Apply(Publish(path, revision, maximumNodes), throwOnFailure: true);

        private IDisplayCoordinatorCheckpointStore CreateStore(string path)
        {
            Assert.DoesNotContain(Stores, store => string.Equals(path, store.Path, StringComparison.OrdinalIgnoreCase)
                && store.DisposeCount == 0);
            var next = new Store(path, Stores.Count, Trace) { FailNextLoad = FailNextStoreLoad };
            FailNextStoreLoad = false;
            Stores.Add(next);
            return next;
        }

        private NativeDisplayCoordinatorWorkspace CreateWorkspace(NativeDisplayCoordinatorConfiguration configuration,
            NativeDisplayCoordinatorPersistenceCallbacks? callbacks)
        {
            var session = new Session(configuration.Generation);
            Contexts.Add(new(configuration, callbacks, session));
            var workspace = new NativeDisplayCoordinatorWorkspace(configuration, session,
                static () => new DeviceTopologyDisplayPathSnapshot([], null), callbacks);
            var action = AfterWorkspaceCreated;
            AfterWorkspaceCreated = null;
            action?.Invoke();
            return workspace;
        }

        public void Dispose() => Owner.Dispose();
    }

    private sealed record Context(NativeDisplayCoordinatorConfiguration Configuration,
        NativeDisplayCoordinatorPersistenceCallbacks? Callbacks, Session Session);

    private sealed class Store(string path, int index, ConcurrentQueue<string> trace) : IDisplayCoordinatorCheckpointStore
    {
        internal string Path { get; } = path;
        internal int SaveCount, LoadCount, DisposeCount, DisposeAttempts, AccessAfterDispose;
        internal bool FailNextLoad;
        internal int CloseFailuresRemaining;
        internal Action? DuringSave;
        public NativeDisplayCoordinatorPersistenceImage? Load(ulong generation)
        {
            CheckOpen(); LoadCount++; trace.Enqueue("load:" + index);
            if (FailNextLoad) { FailNextLoad = false; throw new InvalidDataException("recorded Load failure"); }
            return null;
        }
        public void Save(ulong generation, NativeDisplayCoordinatorPersistenceImage image)
        {
            CheckOpen(); trace.Enqueue("save-start:" + index);
            DuringSave?.Invoke(); SaveCount++; trace.Enqueue("save-end:" + index);
        }
        public void Dispose()
        {
            DisposeAttempts++;
            if (CloseFailuresRemaining > 0) { CloseFailuresRemaining--; throw new IOException("recorded Close InUse"); }
            Assert.Equal(0, DisposeCount);
            DisposeCount++; trace.Enqueue("close:" + index);
        }
        private void CheckOpen()
        {
            if (DisposeCount == 0) return;
            AccessAfterDispose++;
            throw new ObjectDisposedException(nameof(Store));
        }
    }

    private sealed class Barrier : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal void Wait()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Recorded checkpoint barrier was not released.");
        }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }

    private sealed class Session(ulong configurationGeneration) : INativeDisplayCoordinatorSession
    {
        private ulong generation;
        private long captured;
        internal int DisposeCount, CloseFailuresRemaining;
        internal Action? BeforeExport;
        public NativeDisplayCoordinatorCapacity Capacity => new()
        { MaximumObservationCount = 8, MaximumTextBindingCount = 32, MaximumTextByteCount = 4096 };
        public NativeDisplayCoordinatorStatus BeginRefresh(in NativeDisplayRefreshInput input)
        { generation++; captured = input.CapturedUtcMilliseconds; return NativeDisplayCoordinatorStatus.Ok; }
        public NativeDisplayCoordinatorStatus SubmitSource(in NativeDisplaySourceBatchHeader header,
            ReadOnlySpan<NativeDisplayFact> facts, ReadOnlySpan<NativeDisplayTextInput> texts, ReadOnlySpan<byte> bytes)
            => NativeDisplayCoordinatorStatus.Ok;
        public NativeDisplayCoordinatorStatus FinalizeRefresh(in NativeDisplayFinalizeInput input) => NativeDisplayCoordinatorStatus.Ok;
        public NativeDisplayCoordinatorStatus AbortRefresh(in NativeDisplayAbortInput input) => NativeDisplayCoordinatorStatus.Ok;
        public NativeDisplayCoordinatorStatus Snapshot(out NativeDisplaySnapshotOutput output)
        {
            output = new()
            {
                AbiVersion = NativeDisplayCoordinatorAbi.Version,
                StructSize = (uint)Unsafe.SizeOf<NativeDisplaySnapshotOutput>(), ConfigurationGeneration = configurationGeneration,
                ContentGeneration = generation, StateRevision = generation, Phase = (uint)NativeDisplayCoordinatorPhase.Ready,
                LastSuccessUtcMilliseconds = captured, CurrentSourceMask = 1
            };
            return NativeDisplayCoordinatorStatus.Ok;
        }
        public NativeDisplayCoordinatorStatus ReadNodes(in NativeDisplayReadInput input, Span<NativeDisplayNodeOutput> output)
            => NativeDisplayCoordinatorStatus.Ok;
        public NativeDisplayCoordinatorStatus ExportPersistence(in NativeDisplayPersistenceInput input,
            out NativeDisplayPersistenceHeader header, Span<NativeDisplayFact> facts, Span<NativeDisplayTextInput> texts, Span<byte> bytes)
        {
            BeforeExport?.Invoke();
            header = new() { OperationEpoch = input.OperationEpoch };
            return NativeDisplayCoordinatorStatus.Ok;
        }
        public NativeDisplayCoordinatorStatus ImportPersistence(in NativeDisplayPersistenceInput input,
            in NativeDisplayPersistenceHeader header, ReadOnlySpan<NativeDisplayFact> facts,
            ReadOnlySpan<NativeDisplayTextInput> texts, ReadOnlySpan<byte> bytes) => NativeDisplayCoordinatorStatus.Ok;
        public void Dispose() { if (CloseFailuresRemaining > 0) { CloseFailuresRemaining--; throw new IOException("recorded native Close failure"); } Assert.Equal(0, DisposeCount); DisposeCount++; }
    }

    private sealed class EnvironmentInfo(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
