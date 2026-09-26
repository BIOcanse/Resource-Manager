using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal readonly record struct GpuPlacementWindowState(
    int Left, int Top, int Width, int Height, bool Visible, bool Iconic, bool Maximized);

internal readonly record struct GpuPlacementWindowCall(bool Accepted, int? Error);

internal interface IGpuPlacementWindowOperations
{
    bool TryRead(IntPtr window, out GpuPlacementWindowState state);
    GpuPlacementWindowCall Redraw(IntPtr window);
    GpuPlacementWindowCall Resize(IntPtr window, int width, int height);
}

internal sealed record GpuPlacementWindowAction(
    GpuPlacementWindowState Before,
    bool? Changed,
    bool Restored,
    int? RestoreError)
{
    internal string? ChangeReadException { get; init; }
    internal string? RestoreException { get; init; }
}

internal static class GpuPlacementWindowActions
{
    internal static GpuPlacementWindowAction? RequestResize(
        IntPtr window, Func<bool> ownsWindow, IGpuPlacementWindowOperations operations)
    {
        if (!ownsWindow() || !operations.TryRead(window, out var before)
            || !before.Visible || before.Iconic || before.Width <= 0 || before.Height <= 0
            || before.Width == int.MaxValue)
            return null;

        var request = operations.Resize(window, before.Width + 1, before.Height);
        if (!request.Accepted) return null;
        bool? changed = null;
        string? changeReadException = null;
        try
        {
            if (operations.TryRead(window, out var afterRequest)) changed = afterRequest != before;
        }
        catch (Exception exception) { changeReadException = exception.ToString(); }

        // An observation failure must not skip the accepted request's one restoration attempt.
        var restore = new GpuPlacementWindowCall(false, null);
        var restored = false;
        string? restoreException = null;
        try
        {
            restore = ownsWindow()
                ? operations.Resize(window, before.Width, before.Height)
                : new GpuPlacementWindowCall(false, 1400);
            restored = restore.Accepted && operations.TryRead(window, out var afterRestore) && afterRestore == before;
        }
        catch (Exception exception) { restoreException = exception.ToString(); }
        return new(before, changed, restored, restore.Error)
        {
            ChangeReadException = changeReadException,
            RestoreException = restoreException
        };
    }
}

internal sealed class WindowsGpuPlacementWindowOperations : IGpuPlacementWindowOperations
{
    internal static readonly WindowsGpuPlacementWindowOperations Instance = new();

    public bool TryRead(IntPtr window, out GpuPlacementWindowState state)
    {
        if (!NativeMethods.GetWindowRect(window, out var rectangle)) { state = default; return false; }
        state = new(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height,
            NativeMethods.IsWindowVisible(window), NativeMethods.IsIconic(window), IsZoomed(window));
        return true;
    }

    public GpuPlacementWindowCall Redraw(IntPtr window)
        => Result(NativeMethods.RedrawWindow(window, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.RdwInvalidate | NativeMethods.RdwAllChildren));

    public GpuPlacementWindowCall Resize(IntPtr window, int width, int height)
        => Result(NativeMethods.SetWindowPos(window, IntPtr.Zero, 0, 0, width, height,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpNoActivate));

    private static GpuPlacementWindowCall Result(bool accepted)
        => new(accepted, accepted ? null : Marshal.GetLastWin32Error());

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);
}
