using Microsoft.Extensions.Logging;
using ResourceManager.Shared.BusinessBlocks;
using SharedScheduler = ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler;

namespace ResourceManager.NativeUi.Blocks;

internal static class NativeUiBlockIds
{
    public const string MainRoot = "native-ui:main";
    public const string TrayRoot = "native-ui:tray";
    public const string SingleInstance = "native-ui:single-instance";
    public const string BackendConnection = "native-ui:backend-connection";
    public const string MainWindow = "native-ui:main-window";
    public const string DesktopObservation = "native-ui:desktop-observation";
    public const string Overlay = "native-ui:overlay";
    public const string LoopbackProxy = "native-ui:loopback-proxy";
    public const string WebViewHost = "native-ui:webview-host";
    public const string TrayNotifications = "native-ui:tray-notifications";
}

/// <summary>The native process's main and tray trees share facts, never control each other.</summary>
internal sealed class NativeUiBlockTrees : IAsyncDisposable
{
    internal const string WindowWantedFact = "native-ui:window-wanted";
    internal const string BackendSessionReadyFact = "native-ui:backend-session-ready";
    private readonly BusinessBlockController control;
    private readonly SharedScheduler scheduler;
    private long sessionGeneration;
    private int started;
    private int disposed;

    internal NativeUiBlockTrees(Func<string, IBusinessBlock> create, ILogger logger,
        ILogger<BusinessBlockController> controllerLogger, int historyLimit = 128)
    {
        control = new(controllerLogger, historyLimit);
        var graph = new BusinessBlockGraph([
            Node(NativeUiBlockIds.MainRoot, BusinessBlockGraph.Program),
            Node(NativeUiBlockIds.TrayRoot, BusinessBlockGraph.Program),
            Node(NativeUiBlockIds.SingleInstance, NativeUiBlockIds.MainRoot),
            Node(NativeUiBlockIds.BackendConnection, NativeUiBlockIds.MainRoot),
            Node(NativeUiBlockIds.MainWindow, NativeUiBlockIds.MainRoot),
            Node(NativeUiBlockIds.DesktopObservation, NativeUiBlockIds.BackendConnection, NativeUiBlockIds.BackendConnection),
            Node(NativeUiBlockIds.Overlay, NativeUiBlockIds.BackendConnection, NativeUiBlockIds.BackendConnection),
            Node(NativeUiBlockIds.LoopbackProxy, NativeUiBlockIds.MainWindow, NativeUiBlockIds.BackendConnection),
            Node(NativeUiBlockIds.WebViewHost, NativeUiBlockIds.LoopbackProxy, NativeUiBlockIds.LoopbackProxy),
            Node(NativeUiBlockIds.TrayNotifications, NativeUiBlockIds.TrayRoot, NativeUiBlockIds.BackendConnection)
        ]);
        scheduler = new(graph, create, historyLimit, logger, control, [
            new(NativeUiBlockIds.MainRoot, [WindowWantedFact], input => input.Facts[WindowWantedFact].Value
                ? [NativeUiBlockIds.SingleInstance, NativeUiBlockIds.BackendConnection, NativeUiBlockIds.MainWindow]
                : [NativeUiBlockIds.SingleInstance, NativeUiBlockIds.BackendConnection]),
            new(NativeUiBlockIds.BackendConnection, [BackendSessionReadyFact], input => input.Facts[BackendSessionReadyFact].Value
                ? [NativeUiBlockIds.DesktopObservation, NativeUiBlockIds.Overlay] : []),
            new(NativeUiBlockIds.MainWindow, [], _ => [NativeUiBlockIds.LoopbackProxy]),
            new(NativeUiBlockIds.LoopbackProxy, [BackendSessionReadyFact], input =>
                input.Facts[BackendSessionReadyFact].Value
                    || input.Children[NativeUiBlockIds.WebViewHost] != BusinessBlockState.Stopped
                        ? [NativeUiBlockIds.WebViewHost] : []),
            new(NativeUiBlockIds.TrayRoot, [BackendSessionReadyFact], input => input.Facts[BackendSessionReadyFact].Value
                ? [NativeUiBlockIds.TrayNotifications] : [])
        ], () => Volatile.Read(ref sessionGeneration));
    }

    internal async Task StartAsync(bool windowWanted)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        await SetWindowWantedAsync(windowWanted, "startup").ConfigureAwait(false);
        var reason = Reason("program", "process-started");
        await Task.WhenAll(
            scheduler.SetDemandAsync("native-ui:main-lifetime", NativeUiBlockIds.MainRoot, reason),
            scheduler.SetDemandAsync("native-ui:tray-lifetime", NativeUiBlockIds.TrayRoot, reason)).ConfigureAwait(false);
    }

    internal Task SetWindowWantedAsync(bool wanted, string owner)
        => scheduler.SetFactAsync(WindowWantedFact, wanted, Reason(owner, wanted ? "show-window" : "close-window"));

    internal Task SetBackendSessionReadyAsync(bool ready, long generation)
    {
        Interlocked.Exchange(ref sessionGeneration, generation);
        return scheduler.SetFactAsync(BackendSessionReadyFact, ready,
            new(BusinessBlockRequestKind.Dependency, NativeUiBlockIds.BackendConnection,
                ready ? "session-ready" : "session-lost", generation));
    }

    internal async Task ShutdownAsync(BusinessBlockReason reason)
    {
        await scheduler.ShutdownAsync(reason).ConfigureAwait(false);
        await Task.WhenAll(
            WaitForStateAsync(NativeUiBlockIds.MainRoot, BusinessBlockState.Stopped),
            WaitForStateAsync(NativeUiBlockIds.TrayRoot, BusinessBlockState.Stopped)).ConfigureAwait(false);
        if (scheduler.Read().Blocks.Any(block => block.State != BusinessBlockState.Stopped))
            throw new InvalidOperationException("Native UI lifetime roots returned before their branches stopped.");
    }

    internal BusinessBlocksSnapshot Read() => scheduler.Read();
    internal BusinessBlockState ReadState(string id) => scheduler.ReadState(id);

    /// <summary>Waits for authoritative controller state; notifications carry no cached state.</summary>
    internal async Task WaitForStateAsync(string id, BusinessBlockState state, CancellationToken cancellation = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(string changedId, BusinessBlockState _)
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
    private static BusinessBlockNode Node(string id, string parent, params string[] inputs)
        => new(id, parent, inputs, [id + ":output"]);
}
