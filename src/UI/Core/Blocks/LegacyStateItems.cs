using ResourceManager.Shared.BusinessBlocks.State;
using Desktop = ResourceManager.NativeUi.DesktopStateItems;
using Window = ResourceManager.NativeUi.StateItems;

namespace ResourceManager.NativeUi.LegacyStateItems;

// Registry metadata for the Zig resident's host-owned immutable session value.
// This declaration does not retain or mirror the resident's current value.
internal sealed record DesktopBackendSession(bool Ready, long Generation, string? Reason);

// UID bridge used by the no-parent-pipe composition (see Program.cs).
internal static class BlockUids
{
    public const int Program = Desktop.BlockUids.Program;
    public const int MainRoot = Desktop.BlockUids.MainFrontend;
    public const int TrayRoot = Desktop.BlockUids.Tray;
    public const int SingleInstance = Desktop.BlockUids.SingleInstance;
    public const int BackendConnection = Desktop.BlockUids.BackendConnection;
    public const int MainWindow = Window.BlockUids.MainWindow;
    public const int DesktopObservation = Desktop.BlockUids.DesktopObservation;
    public const int Overlay = Desktop.BlockUids.Overlay;
    public const int LoopbackProxy = Window.BlockUids.LoopbackProxy;
    public const int WebViewHost = Window.BlockUids.WebViewHost;
    public const int TrayNotifications = Desktop.BlockUids.TrayNotifications;
}

internal static class StateItemUids
{
    public const int ProgramShuttingDown = Desktop.StateItemUids.ProgramShuttingDown;
    public const int ProgramStarted = Desktop.StateItemUids.ProgramStarted;
    public const int WindowWanted = Desktop.StateItemUids.WindowWanted;
    public const int BackendSessionReady = Desktop.StateItemUids.BackendSessionReady;
}

internal static class StateRegistry
{
    public const string Version = Window.StateRegistry.Version;
    public static IReadOnlyList<int> Blocks { get; } = Array.AsReadOnly(new[] {
        BlockUids.Program, BlockUids.MainRoot, BlockUids.TrayRoot, BlockUids.SingleInstance,
        BlockUids.BackendConnection, BlockUids.MainWindow, BlockUids.DesktopObservation,
        BlockUids.Overlay, BlockUids.LoopbackProxy, BlockUids.WebViewHost, BlockUids.TrayNotifications });
    public static IReadOnlyList<StateItemDeclaration> Items { get; } = Array.AsReadOnly(
        Desktop.StateRegistry.Items.Where(item => item.Uid == StateItemUids.ProgramShuttingDown
            || item.Uid == StateItemUids.ProgramStarted || item.Uid == StateItemUids.WindowWanted
            || item.Uid == StateItemUids.BackendSessionReady).ToArray());
}

#if RM_STATE_NAMES
internal static class StateNames
{
    public static string? Name(int uid) => Desktop.StateNames.Name(uid) ?? Window.StateNames.Name(uid);
    public static string? Comment(int uid) => Desktop.StateNames.Comment(uid) ?? Window.StateNames.Comment(uid);
}
#endif
