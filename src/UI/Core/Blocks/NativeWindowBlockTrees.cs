using Microsoft.Extensions.Logging;
using ResourceManager.NativeUi.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.NativeUi.Blocks;

// Window control remains on the shared C# controller until the scheduled 5d C ABI migration.
internal sealed class NativeWindowBlockTrees : IAsyncDisposable
{
    private readonly BusinessBlockController control;
    private readonly BusinessBlockScheduler scheduler;
    private readonly StateWriter<bool> parentWantsRunning;
    private readonly StateWriter<bool> backendReady;
    private long generation;

    internal NativeWindowBlockTrees(Func<int, IBusinessBlock> create, ILogger logger,
        ILogger<BusinessBlockController> controllerLogger)
    {
        var authority = new StateAuthority(StateRegistry.Items, StateRegistry.Blocks);
        parentWantsRunning = authority.ClaimWrite<bool>(StateItemUids.ParentWantsMeRunning);
        backendReady = authority.ClaimWrite<bool>(StateItemUids.BackendSessionReady);
#if RM_STATE_NAMES
        Func<int, string?> names = StateNames.Name;
#else
        Func<int, string?> names = static _ => null;
#endif
        control = new(controllerLogger, 128, names);
        var graph = new BusinessBlockGraph(BlockUids.Program, [
            new(BlockUids.MainWindow, BlockUids.Program, []),
            new(BlockUids.LoopbackProxy, BlockUids.MainWindow, []),
            new(BlockUids.WebViewHost, BlockUids.LoopbackProxy, [])
        ]);
        scheduler = new(graph, create, 128, logger, StateRegistry.Version, names, authority,
            StateItemUids.ProgramShuttingDown, control, [
                new(BlockUids.Program, [StateItemUids.ParentWantsMeRunning], input =>
                    input.Read<bool>(StateItemUids.ParentWantsMeRunning).GetValueOrDefault(false)
                        ? [BlockUids.MainWindow] : []),
                new(BlockUids.MainWindow, [StateItemUids.BackendSessionReady], input =>
                    input.Read<bool>(StateItemUids.BackendSessionReady).GetValueOrDefault(false)
                    || input.Children[BlockUids.LoopbackProxy] != BusinessBlockState.Stopped
                        ? [BlockUids.LoopbackProxy] : []),
                new(BlockUids.LoopbackProxy, [], _ => [BlockUids.WebViewHost])
            ], () => Volatile.Read(ref generation));
    }

    internal Task SetParentWantsRunningAsync(bool running)
        => parentWantsRunning.PublishAsync(running, Reason("parent-wants-me-running"));
    internal Task SetBackendSessionAsync(bool ready, long currentGeneration)
    {
        Interlocked.Exchange(ref generation, currentGeneration);
        return backendReady.PublishAsync(ready, Reason(ready ? "session-ready" : "session-lost"));
    }
    internal BusinessBlocksSnapshot Read() => scheduler.Read();
    internal BusinessBlockState ReadState(int id) => scheduler.ReadState(id);
    internal async Task WaitForStateAsync(int id, BusinessBlockState expected, CancellationToken cancellation = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(int changed, BusinessBlockState _) { if (changed == id && control.ReadState(id) == expected) done.TrySetResult(); }
        control.StateChanged += Changed;
        try
        {
            if (control.ReadState(id) == expected) done.TrySetResult();
            await done.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        finally { control.StateChanged -= Changed; }
    }
    public async ValueTask DisposeAsync()
    {
        await scheduler.ShutdownAsync(Reason("parent-stopped")).ConfigureAwait(false);
        await WaitForStateAsync(BlockUids.MainWindow, BusinessBlockState.Stopped).ConfigureAwait(false);
        await scheduler.DisposeAsync().ConfigureAwait(false);
    }
    private BusinessBlockReason Reason(string cause)
        => new(BusinessBlockRequestKind.User, "resident-pipe", cause, Volatile.Read(ref generation));
}
