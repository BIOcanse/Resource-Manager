using ResourceManager.Shared.BusinessBlocks.State;
using BlockUids = ResourceManager.NativeUi.StateItems.BlockUids;
using Microsoft.Extensions.Logging;
using ResourceManager.Shared.BusinessBlocks;
using SharedScheduler = ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler;
using ResourceManager.NativeUi.StateItems;

namespace ResourceManager.NativeUi.Blocks;

/// <summary>The native process's main and tray trees share state items, never control each other.</summary>
internal sealed class NativeUiBlockTrees : IAsyncDisposable
{
    private readonly NativeUiStateItems state = new();
    private readonly BusinessBlockController control;
    private readonly SharedScheduler scheduler;
    private long sessionGeneration;
    private int started;
    private int disposed;

    internal NativeUiBlockTrees(Func<int, IBusinessBlock> create, ILogger logger,
        ILogger<BusinessBlockController> controllerLogger, int historyLimit = 128)
    {
#if RM_STATE_NAMES
        Func<int, string?> nameLookup = StateNames.Name;
#else
        Func<int, string?> nameLookup = static _ => null;
#endif
        control = new(controllerLogger, historyLimit, nameLookup);
        var graph = new BusinessBlockGraph(BlockUids.Program, [
            Node(BlockUids.MainRoot, BlockUids.Program),
            Node(BlockUids.TrayRoot, BlockUids.Program),
            Node(BlockUids.SingleInstance, BlockUids.MainRoot),
            Node(BlockUids.BackendConnection, BlockUids.MainRoot),
            Node(BlockUids.MainWindow, BlockUids.MainRoot),
            Node(BlockUids.DesktopObservation, BlockUids.BackendConnection, BlockUids.BackendConnection),
            Node(BlockUids.Overlay, BlockUids.BackendConnection, BlockUids.BackendConnection),
            Node(BlockUids.LoopbackProxy, BlockUids.MainWindow, BlockUids.BackendConnection),
            Node(BlockUids.WebViewHost, BlockUids.LoopbackProxy, BlockUids.LoopbackProxy),
            Node(BlockUids.TrayNotifications, BlockUids.TrayRoot, BlockUids.BackendConnection)
        ]);
        scheduler = new(graph, create, historyLimit, logger, StateRegistry.Version, nameLookup, state.Authority, null, StateItemUids.ProgramShuttingDown, control, [
            // Whether a whole tree runs is decided here, never by the tree itself; program exit is the scheduler's shutdown item.
            new(BlockUids.Program, [StateItemUids.ProgramStarted], input => input.Read<bool>(StateItemUids.ProgramStarted).GetValueOrDefault(false)
                ? [BlockUids.MainRoot, BlockUids.TrayRoot] : []),
            new(BlockUids.MainRoot, [StateItemUids.WindowWanted], input => input.Read<bool>(StateItemUids.WindowWanted).GetValueOrDefault(false)
                ? [BlockUids.SingleInstance, BlockUids.BackendConnection, BlockUids.MainWindow]
                : [BlockUids.SingleInstance, BlockUids.BackendConnection]),
            new(BlockUids.BackendConnection, [StateItemUids.BackendSessionReady], input => input.Read<bool>(StateItemUids.BackendSessionReady).GetValueOrDefault(false)
                ? [BlockUids.DesktopObservation, BlockUids.Overlay] : []),
            new(BlockUids.MainWindow, [StateItemUids.BackendSessionReady], input =>
                input.Read<bool>(StateItemUids.BackendSessionReady).GetValueOrDefault(false)
                    || input.Children[BlockUids.LoopbackProxy] != BusinessBlockState.Stopped
                        ? [BlockUids.LoopbackProxy] : []),
            new(BlockUids.LoopbackProxy, [], _ => [BlockUids.WebViewHost]),
            new(BlockUids.TrayRoot, [StateItemUids.BackendSessionReady], input => input.Read<bool>(StateItemUids.BackendSessionReady).GetValueOrDefault(false)
                ? [BlockUids.TrayNotifications] : [])
        ], () => Volatile.Read(ref sessionGeneration));
    }

    internal async Task StartAsync(bool windowWanted)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        await SetWindowWantedAsync(windowWanted, "startup").ConfigureAwait(false);
        await state.ProgramStarted.PublishAsync(true, Reason("program", "process-started")).ConfigureAwait(false);
    }

    internal Task SetWindowWantedAsync(bool wanted, string owner)
        => state.WindowWanted.PublishAsync(wanted, Reason(owner, wanted ? "show-window" : "close-window"));

    internal Task SetBackendSessionReadyAsync(bool ready, long generation)
    {
        Interlocked.Exchange(ref sessionGeneration, generation);
        return state.BackendSessionReady.PublishAsync(ready,
            new(BusinessBlockRequestKind.Dependency, BlockUids.BackendConnection.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ready ? "session-ready" : "session-lost", generation));
    }

    internal Task StartBackendSessionPublicationAsync()
        => state.BackendSessionReady.BindAsync(Reason(BlockUids.BackendConnection.ToString(System.Globalization.CultureInfo.InvariantCulture), "owner-started"));

    // Whole owner-flow shutdown only; generation transitions keep the existing publication points.
    internal Task StopBackendSessionPublicationAsync()
        => state.BackendSessionReady.UnbindAsync(Reason(BlockUids.BackendConnection.ToString(System.Globalization.CultureInfo.InvariantCulture), "owner-stopped"));

    internal async Task ShutdownAsync(BusinessBlockReason reason)
    {
        await scheduler.ShutdownAsync(reason).ConfigureAwait(false);
        await Task.WhenAll(
            WaitForStateAsync(BlockUids.MainRoot, BusinessBlockState.Stopped),
            WaitForStateAsync(BlockUids.TrayRoot, BusinessBlockState.Stopped)).ConfigureAwait(false);
        if (scheduler.Read().Blocks.Any(block => block.State != BusinessBlockState.Stopped))
            throw new InvalidOperationException("Native UI lifetime roots returned before their branches stopped.");
    }

    internal BusinessBlocksSnapshot Read() => scheduler.Read();
    internal BusinessBlockState ReadState(int id) => scheduler.ReadState(id);

    /// <summary>Waits for authoritative controller state; notifications carry no cached state.</summary>
    internal async Task WaitForStateAsync(int id, BusinessBlockState state, CancellationToken cancellation = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(int changedId, BusinessBlockState _)
        {
            if (changedId == id && control.ReadState(id) == state) completion.TrySetResult();
        }
        control.StateChanged += Changed;
        try
        {
            if (scheduler.ReadState(id) == state) completion.TrySetResult();
            await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        finally { control.StateChanged -= Changed; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await ShutdownAsync(Reason("program", "dispose")).ConfigureAwait(false);
        await scheduler.DisposeAsync().ConfigureAwait(false);
    }

    private BusinessBlockReason Reason(string owner, string cause)
        => new(BusinessBlockRequestKind.User, owner, cause, Volatile.Read(ref sessionGeneration));
    private static BusinessBlockNode Node(int id, int parent, params int[] inputs)
        => new(id, parent, inputs);
}
