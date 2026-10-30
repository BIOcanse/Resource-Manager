using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Settings;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;
using BackendBlocks = ResourceManager.App.StateItems.BlockUids;
using BackendRegistry = ResourceManager.App.StateItems.StateRegistry;

namespace Resource_Manager_APP.Tests;

public sealed class NativeControlAbiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly BusinessBlockReason Reason = new(
        BusinessBlockRequestKind.User, "native-control-abi-test", "explicit", 1);

    public static IEnumerable<object[]> DefinedResultMappings =>
    [
        [1, typeof(ArgumentException)],
        [2, typeof(InvalidOperationException)],
        [3, typeof(InvalidOperationException)],
        [4, typeof(InvalidOperationException)],
        [5, typeof(ArgumentException)],
        [6, typeof(InvalidOperationException)],
        [7, typeof(OutOfMemoryException)],
        [8, typeof(InvalidOperationException)],
        [20, typeof(InvalidOperationException)],
        [21, typeof(KeyNotFoundException)],
        [22, typeof(InvalidOperationException)],
        [24, typeof(ArgumentException)],
        [25, typeof(ArgumentOutOfRangeException)],
        [26, typeof(InvalidOperationException)],
        [27, typeof(InvalidOperationException)],
        [28, typeof(InvalidOperationException)],
        [29, typeof(InvalidOperationException)],
        [30, typeof(InvalidOperationException)],
        [31, typeof(InvalidOperationException)],
        [34, typeof(ArgumentException)],
        [35, typeof(InvalidOperationException)],
        [36, typeof(InvalidOperationException)],
        [37, typeof(KeyNotFoundException)],
        [38, typeof(ArgumentException)]
    ];

    [Fact]
    public void CheckAcceptsSuccessCode()
    {
        NativeControl.Check(0);
    }

    [Fact]
    public unsafe void VersionTwoAbiHasNoDemandAllocationFieldsAndReadFunctionUsesFixedWidths()
    {
        Assert.Equal(0x0002_0000u, NativeControl.AbiVersion());
        Assert.Equal(56, sizeof(NativeControl.Config));
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.OffsetOf<NativeControl.Config>(nameof(NativeControl.Config.HostBlocks)).ToInt32());
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.OffsetOf<NativeControl.Config>(nameof(NativeControl.Config.HostBlockCount)).ToInt32());
        Assert.Equal(8, sizeof(NativeControl.FunctionRead));
        Assert.Equal(16, sizeof(NativeControl.ReadFunction));
    }

    [Fact]
    public async Task NativeReadFunctionReadsCurrentValueOutsideControllerLockAndUnbindSuppressesCallback()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var key = new StateItemKey(BackendItems.PresentNativeSessionAvailable);
        var current = false;
        var fail = false;
        var calls = 0;
        var function = fixture.Authority.ClaimReadFunction<bool>(key, () =>
        {
            // Reentry would deadlock if the reader ran while the controller held its lock.
            _ = fixture.Control.ReadState(BackendBlocks.PresentFrameEtwSource);
            Interlocked.Increment(ref calls);
            if (fail) throw new InvalidOperationException("owner read failed");
            return StateRead<bool>.FromValue(current);
        });
        Assert.False(await Task.Run(() => fixture.Authority.Read<bool>(key).Value).WaitAsync(Timeout));
        current = true;
        Assert.True(fixture.Authority.Read<bool>(key).Value);
        await function.NotifyChangedAsync(Reason).WaitAsync(Timeout);
        await function.UnbindAsync(Reason).WaitAsync(Timeout);
        var unboundCalls = Volatile.Read(ref calls);
        Assert.Equal(NoValueReason.OwnerStopped, fixture.Authority.Read<bool>(key).NoValueReason);
        Assert.Equal(unboundCalls, Volatile.Read(ref calls));
        fail = true;
        await function.BindAsync(Reason).WaitAsync(Timeout);
        Assert.Equal(NoValueReason.ReadFailed, fixture.Authority.Read<bool>(key).NoValueReason);
    }

    [Theory]
    [MemberData(nameof(DefinedResultMappings))]
    public void CheckMapsEveryDefinedFailureToItsExactExceptionType(int result, Type expectedType)
    {
        var error = Record.Exception(() => NativeControl.Check(result));
        Assert.NotNull(error);
        Assert.Equal(expectedType, error.GetType());
    }

    [Fact]
    public async Task DirectAbiSnapshotReportsNeededLengthAndHostItemsExposePresenceOnly()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var authority = fixture.Authority;

        var requestItem = BackendRegistry.Items.Single(item => item.Uid == BackendItems.CpuThreadDiagnosticRequests);
        var appSettingsItem = BackendRegistry.Items.Single(item => item.Uid == BackendItems.AppSettingsCurrent);
        var startupSettingsItem = BackendRegistry.Items.Single(item => item.Uid == BackendItems.StartupModuleSettingsCurrent);
        Assert.Equal(66, requestItem.Uid);
        Assert.Equal(typeof(HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>), requestItem.ValueType);
        Assert.Equal(StateItemStorage.Own, requestItem.Storage);
        Assert.Equal(68, appSettingsItem.Uid);
        Assert.Equal(typeof(AppSettings), appSettingsItem.ValueType);
        Assert.Equal(StateItemStorage.Reference, appSettingsItem.Storage);
        Assert.Equal(69, startupSettingsItem.Uid);
        Assert.Equal(typeof(StartupModuleSettingsDocument), startupSettingsItem.ValueType);
        Assert.Equal(StateItemStorage.Reference, startupSettingsItem.Storage);

        var requestKey = new StateItemKey(BackendItems.CpuThreadDiagnosticRequests);
        var appSettingsKey = new StateItemKey(BackendItems.AppSettingsCurrent);
        var startupSettingsKey = new StateItemKey(BackendItems.StartupModuleSettingsCurrent);
        Assert.Equal(1u, ReadPresence(fixture.Control, requestKey));
        Assert.Equal(1u, ReadPresence(fixture.Control, appSettingsKey));
        Assert.Equal(1u, ReadPresence(fixture.Control, startupSettingsKey));

        var requests = new HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>();
        var writer = authority.ClaimWrite<HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>>(requestKey);
        await writer.PublishAsync(requests, Reason).WaitAsync(Timeout);
        Assert.Same(requests, authority.Read<HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>>(requestKey).Value);
        Assert.Equal(0u, ReadPresence(fixture.Control, requestKey));
        await writer.PublishNoValueAsync(NoValueReason.ReadFailed, Reason).WaitAsync(Timeout);
        Assert.Equal(NoValueReason.ReadFailed,
            authority.Read<HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>>(requestKey).NoValueReason);
        Assert.Equal(3u, ReadPresence(fixture.Control, requestKey));
        await writer.PublishAsync(requests, Reason).WaitAsync(Timeout);

        var settings = AppSettingsDefaults.Create() with { Version = "csharp-only-app-settings" };
        var settingsCell = authority.ClaimCell<AppSettings>(appSettingsKey);
        await settingsCell.PublishAsync(settings, Reason).WaitAsync(Timeout);
        Assert.Same(settings, authority.Read<AppSettings>(appSettingsKey).Value);
        Assert.Equal(0u, ReadPresence(fixture.Control, appSettingsKey));
        await settingsCell.UnbindAsync(Reason).WaitAsync(Timeout);
        Assert.Equal(NoValueReason.OwnerStopped, authority.Read<AppSettings>(appSettingsKey).NoValueReason);
        Assert.Equal(2u, ReadPresence(fixture.Control, appSettingsKey));

        var updatedSettings = AppSettingsDefaults.Create() with { Version = "csharp-only-app-settings-after-unbind" };
        await settingsCell.PublishAsync(updatedSettings, Reason).WaitAsync(Timeout);
        Assert.Equal(NoValueReason.OwnerStopped, authority.Read<AppSettings>(appSettingsKey).NoValueReason);
        Assert.Equal(2u, ReadPresence(fixture.Control, appSettingsKey));
        await settingsCell.BindAsync(Reason).WaitAsync(Timeout);
        Assert.Same(updatedSettings, authority.Read<AppSettings>(appSettingsKey).Value);
        Assert.Equal(0u, ReadPresence(fixture.Control, appSettingsKey));

        var startupSettings = new StartupModuleSettingsDocument(1,
            [new StartupModuleSelection("csharp-only-software", ["csharp-only-module"])]);
        var startupCell = authority.ClaimCell<StartupModuleSettingsDocument>(startupSettingsKey);
        await startupCell.PublishAsync(startupSettings, Reason).WaitAsync(Timeout);
        Assert.Same(startupSettings, authority.Read<StartupModuleSettingsDocument>(startupSettingsKey).Value);
        Assert.Equal(0u, ReadPresence(fixture.Control, startupSettingsKey));

        var handle = fixture.Control.Handle;
        var shortBufferResult = SnapshotDirect(handle, null, out var needed);
        Assert.Equal(5, shortBufferResult);
        Assert.True(needed > 0);

        var snapshot = fixture.Control.SnapshotJson();
        Assert.Equal(BackendRegistry.Version, snapshot.GetProperty("registryVersion").GetString());
        var hostRows = snapshot.GetProperty("items").EnumerateArray()
            .Where(row => row.GetProperty("uid").GetInt32() is 66 or 68 or 69)
            .ToDictionary(row => row.GetProperty("uid").GetInt32());
        Assert.Equal(3, hostRows.Count);
        foreach (var row in hostRows.Values)
        {
            Assert.True(row.GetProperty("hasValue").GetBoolean());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("noValueReason").ValueKind);
            Assert.False(row.TryGetProperty("value", out _));
        }
        var json = snapshot.GetRawText();
        Assert.DoesNotContain("csharp-only-app-settings", json, StringComparison.Ordinal);
        Assert.DoesNotContain("csharp-only-software", json, StringComparison.Ordinal);
        Assert.DoesNotContain("csharp-only-module", json, StringComparison.Ordinal);
        var evidenceBlock = BackendBlocks.TrustedAccessInvocationHostedService;
        await fixture.Authority.ClaimCell<bool>(new(BackendItems.HostStartPermitted, evidenceBlock)).PublishAsync(true, Reason).WaitAsync(Timeout);
        await Until(() => fixture.Control.ReadState(evidenceBlock) == BusinessBlockState.Running);
        var runningSnapshot = fixture.Control.SnapshotJson();
        var programRow = runningSnapshot.GetProperty("blocks").EnumerateArray()
            .Single(row => row.GetProperty("uid").GetInt32() == BackendBlocks.Program);
#if RM_STATE_NAMES
        Assert.Equal("Program", programRow.GetProperty("name").GetString());
        var runningLabel = $"Business block {evidenceBlock} (TrustedAccessInvocationHostedService): starting -> running";
#else
        Assert.False(programRow.TryGetProperty("name", out _));
        Assert.All(runningSnapshot.GetProperty("blocks").EnumerateArray(), row => Assert.False(row.TryGetProperty("name", out _)));
        Assert.All(runningSnapshot.GetProperty("items").EnumerateArray(), row => Assert.False(row.TryGetProperty("name", out _)));
        Assert.All(runningSnapshot.GetProperty("nodes").EnumerateArray(), node =>
            Assert.All(node.GetProperty("conditions").EnumerateArray(), row => Assert.False(row.TryGetProperty("name", out _))));
        var runningLabel = $"Business block {evidenceBlock}: starting -> running";
#endif
        await Until(() => fixture.NativeLog.Messages.Contains(runningLabel));
        await WriteNativeEvidenceAsync(fixture);
    }

    [Fact]
    public async Task NativeBooleanReadsAllocateNoManagedMemory()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var key = new StateItemKey(BackendItems.PresentNativeSessionAvailable);
        var cell = fixture.Authority.ClaimCell<bool>(key);
        await cell.PublishAsync(true, Reason).WaitAsync(Timeout);

        for (var index = 0; index < 256; index++) _ = fixture.Authority.Read<bool>(key).Value;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var observed = 0;
        for (var index = 0; index < 4096; index++)
        {
            if (fixture.Authority.Read<bool>(key).Value) observed++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(4096, observed);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task RuntimeReaderCanReadAndChangeSubscriptionsReentrantlyDuringCallback()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var key = new StateItemKey(BackendItems.PresentNativeSessionAvailable);
        var cell = fixture.Authority.ClaimCell<bool>(key);
        IDisposable? first = null;
        IDisposable? second = null;
        var firstCalls = 0;
        var secondCalls = 0;
        try
        {
            first = fixture.Authority.Subscribe(key, "native-reentrant-first", _ =>
            {
                Interlocked.Increment(ref firstCalls);
                Assert.True(fixture.Authority.Read<bool>(key).Value);
                second = fixture.Authority.Subscribe(key, "native-reentrant-second", _ =>
                {
                    Interlocked.Increment(ref secondCalls);
                    return Task.CompletedTask;
                });
                first.Dispose();
                first.Dispose();
                return Task.CompletedTask;
            });

            await cell.PublishAsync(true, Reason).WaitAsync(Timeout);
            Assert.Equal(1, firstCalls);
            Assert.Equal(0, secondCalls);
            await cell.PublishAsync(false, Reason).WaitAsync(Timeout);
            Assert.Equal(1, secondCalls);
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
        }
    }

    [Fact]
    public async Task NamedReadersAreUniqueUnregisterIsIdempotentAndPublishWaitsForReaderTask()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var key = new StateItemKey(BackendItems.PresentNativeSessionAvailable);
        var cell = fixture.Authority.ClaimCell<bool>(key);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Authority.Subscribe(key, "native-held-reader", _ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        IDisposable? replacement = null;
        var replacementCalls = 0;
        try
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Authority.Subscribe(key, "native-held-reader", _ => Task.CompletedTask));
            var publish = cell.PublishAsync(true, Reason);
            await entered.Task.WaitAsync(Timeout);
            Assert.False(publish.IsCompleted);

            first.Dispose();
            first.Dispose();
            replacement = fixture.Authority.Subscribe(key, "native-held-reader", _ =>
            {
                Interlocked.Increment(ref replacementCalls);
                return Task.CompletedTask;
            });
            first.Dispose();

            release.SetResult();
            await publish.WaitAsync(Timeout);
            await cell.PublishAsync(false, Reason).WaitAsync(Timeout);
            Assert.Equal(1, replacementCalls);
        }
        finally
        {
            release.TrySetResult();
            first.Dispose();
            replacement?.Dispose();
            replacement?.Dispose();
        }
    }

    [Fact]
    public async Task HostPublicationKeepsCommittedCurrentWhenReaderFailsAfterCommit()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var key = new StateItemKey(BackendItems.CpuThreadDiagnosticRequests);
        var requests = new HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>();
        var writer = fixture.Authority.ClaimWrite<HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>>(key);
        var nullReader = fixture.Authority.Subscribe(key, "native-null-task-reader", _ => null!);

        Task? publication = null;
        var synchronousFailure = Record.Exception(() => { publication = writer.PublishAsync(requests, Reason); });
        Assert.Null(synchronousFailure);
        Assert.NotNull(publication);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publication!.WaitAsync(Timeout));
        Assert.Same(requests, fixture.Authority.Read<HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>>(key).Value);
        Assert.Equal(0u, ReadPresence(fixture.Control, key));

        nullReader.Dispose();
        var readerFailure = new InvalidOperationException("Reader failed after native commit.");
        using var throwingReader = fixture.Authority.Subscribe(key, "native-throwing-reader", _ => throw readerFailure);
        publication = null;
        synchronousFailure = Record.Exception(() => { publication = writer.PublishNoValueAsync(NoValueReason.ReadFailed, Reason); });
        Assert.Null(synchronousFailure);
        Assert.NotNull(publication);
        Assert.Same(readerFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => publication!.WaitAsync(Timeout)));
        Assert.Equal(NoValueReason.ReadFailed,
            fixture.Authority.Read<HashSet<EtwCpuResidencyCaptureOwner.ThreadDiagnosticRequest>>(key).NoValueReason);
        Assert.Equal(3u, ReadPresence(fixture.Control, key));
    }

    [Fact]
    public void CreateRejectsHostBlockIdsThatDoNotMatchFullProfileTree()
    {
        var wrongHostIds = FullProfileHostUids().Skip(1).ToArray();
        Assert.Throws<ArgumentException>(() => new NativeControl(wrongHostIds, Options(), NullLogger<NativeControlAbiTests>.Instance));
    }

    [Fact]
    public async Task NativeDestroyRefusesWhileAHostRunIsActive()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var block = BackendBlocks.TrustedAccessInvocationHostedService;
        await fixture.Authority.ClaimCell<bool>(new(BackendItems.HostStartPermitted, block)).PublishAsync(true, Reason).WaitAsync(Timeout);
        await Until(() => fixture.Control.ReadState(block) == BusinessBlockState.Running);

        var result = NativeControl.Destroy(fixture.Control.Handle.DangerousGetHandle());
        Assert.Equal(36, result);
        var error = Record.Exception(() => NativeControl.Check(result));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(BusinessBlockState.Running, fixture.Control.ReadState(block));
    }

    [Fact]
    public async Task ConcurrentNativeDisposalCallsShareOneDrain()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await NativeFixture.CreateAsync(_ => new GatedHoldingRun(release.Task));
        var block = BackendBlocks.TrustedAccessInvocationHostedService;
        await fixture.Authority.ClaimCell<bool>(new(BackendItems.HostStartPermitted, block)).PublishAsync(true, Reason).WaitAsync(Timeout);
        await Until(() => fixture.Control.ReadState(block) == BusinessBlockState.Running);

        var first = fixture.Control.DisposeAsync().AsTask();
        var second = fixture.Control.DisposeAsync().AsTask();
        Assert.Same(first, second);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
    }

    private static uint ReadPresence(NativeControl control, StateItemKey key)
    {
        var nativeKey = new NativeControl.ItemKey
        {
            Item = checked((uint)key.ItemUid),
            Block = checked((uint)(key.BlockUid ?? 0))
        };
        NativeControl.Check(NativeControl.ReadPresence(control.Handle, in nativeKey, out var presence));
        return presence;
    }

    private static unsafe int SnapshotDirect(NativeControl.ControlHandle handle, byte[]? buffer, out nuint needed)
    {
        if (buffer is null) return NativeControl.Snapshot(handle, null, 0, out needed);
        fixed (byte* data = buffer) return NativeControl.Snapshot(handle, data, (nuint)buffer.Length, out needed);
    }

    private static int[] FullProfileHostUids() => BackendRegistry.Blocks
        .Where(uid => uid != BackendBlocks.Program && uid != BackendBlocks.AutomaticProductUpdateService)
        .ToArray();

    private static NativeControlOptions Options() => new()
    {
        Profile = 1,
        ShutdownItemUid = BackendItems.ProgramShuttingDown
    };

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }

    private static async Task WriteNativeEvidenceAsync(NativeFixture fixture)
    {
        if (Environment.GetEnvironmentVariable("RM_STATE_UID_EVIDENCE") is not { Length: > 0 } evidencePath)
            return;

        var directory = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(directory);
#if RM_STATE_NAMES
        const string mode = "names-on";
#else
        const string mode = "names-off";
#endif
        await File.WriteAllTextAsync(Path.Combine(directory, "native-abi-" + mode + "-snapshot.json"),
            fixture.Control.SnapshotJson().GetRawText());
        await File.WriteAllLinesAsync(Path.Combine(directory, "native-abi-" + mode + "-log.txt"), fixture.NativeLog.Messages);
    }

    private sealed class NativeFixture(NativeControl control, StateAuthority authority, CapturedNativeLogger logger) : IAsyncDisposable
    {
        internal NativeControl Control { get; } = control;
        internal StateAuthority Authority { get; } = authority;
        internal CapturedNativeLogger NativeLog { get; } = logger;

        internal static async Task<NativeFixture> CreateAsync(Func<int, IBusinessBlock>? runFactory = null)
        {
            var logger = new CapturedNativeLogger();
            var control = new NativeControl(FullProfileHostUids(), Options(), logger);
            try
            {
                var authority = control.CreateAuthority(BackendRegistry.Items, BackendRegistry.Blocks);
                foreach (var uid in control.Graph.BlockIds.Where(uid => uid != BackendBlocks.TrustedAccessInvocationHostedService))
                    await authority.ClaimCell<bool>(new(BackendItems.HostStartPermitted, uid)).PublishAsync(true, Reason);
                control.Activate(runFactory ?? (static _ => new HoldingRun()));
                return new(control, authority, logger);
            }
            catch
            {
                try { await control.DisposeAsync(); } catch { }
                throw;
            }
        }

        public ValueTask DisposeAsync() => Control.DisposeAsync();
    }

    private sealed class CapturedNativeLogger : ILogger<NativeControl>
    {
        private readonly ConcurrentQueue<string> messages = new();
        internal string[] Messages => messages.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            messages.Enqueue(exception is null ? message : message + Environment.NewLine + exception);
        }
    }

    private sealed class HoldingRun : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }

    private sealed class GatedHoldingRun(Task release) : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            await release.ConfigureAwait(false);
        }
    }
}
