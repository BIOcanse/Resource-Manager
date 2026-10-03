using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ResourceManager.NativeUi.Overlay;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayDesktopTests
{
    [EnvironmentVariableFact("RM_OVERLAY_DESKTOP_GENERATOR")]
    [Trait("Category", "Desktop")]
    public void LayeredWindowCoversGeneratorClientAndFollowsResize()
    {
        OnSta(() =>
        {
            using var generator = StartGenerator(includeDll: false);
            try
            {
                var id = ProcessIdentity(generator);
                var before = WaitForWindow(id);
                using var overlay = new ExternalOverlayWindow();
                var target = Target(id, "external");
                var image = Assert.IsType<OverlayImage>(OverlayBitmapRenderer.Render(
                    target, before.ClientBounds.Size));
                overlay.Update(image, before.ClientBounds);
                Assert.Equal(before.ClientBounds, WindowBounds(overlay.Handle));
                Assert.Equal(0x080800A8L, GetWindowLongPtr(overlay.Handle, -20).ToInt64() & 0x080800A8L);

                var monitor = Screen.AllScreens.FirstOrDefault(static screen => !screen.Primary)
                    ?? Screen.PrimaryScreen!;
                var area = monitor.WorkingArea;
                Assert.True(SetWindowPos(before.Handle, 0, area.X + 40, area.Y + 40,
                    900, 520, 0x0010 | 0x0004));
                OverlayWindowState after = default;
                WaitUntil(() =>
                {
                    after = OverlayTargetWindow.Read(id, before.Handle);
                    return after.ClientBounds.Width != before.ClientBounds.Width;
                });
                var resized = Assert.IsType<OverlayImage>(OverlayBitmapRenderer.Render(
                    target, after.ClientBounds.Size));
                overlay.Update(resized, after.ClientBounds);
                Assert.Equal(after.ClientBounds, WindowBounds(overlay.Handle));
            }
            finally { StopGenerator(generator); }
        });
    }

    [EnvironmentVariableFact("RM_OVERLAY_DESKTOP_GENERATOR")]
    [Trait("Category", "Desktop")]
    public void InjectedDllConsumesBitmapFromNativeUiProducer()
    {
        OnSta(() =>
        {
            using var generator = StartGenerator(includeDll: true);
            try
            {
                var id = ProcessIdentity(generator);
                var window = WaitForWindow(id);
                using var producer = new InjectedOverlaySection(id, window.Handle);
                WaitUntil(() => producer.SwapChainSize.Width > 0
                    && producer.SwapChainSize.Height > 0);
                var image = Assert.IsType<OverlayImage>(OverlayBitmapRenderer.Render(
                    Target(id, "injected"), producer.SwapChainSize));
                producer.Publish(image);
                var generation = producer.Generation;
                WaitUntil(() => producer.ConsumedGeneration >= generation
                    && producer.DrawSuccessCounter > 0);
                Assert.Equal(0, generation & 1);
                Assert.True(producer.DrawSuccessCounter > 0);
            }
            finally { StopGenerator(generator); }
        });
    }

    private static Process StartGenerator(bool includeDll)
    {
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_OVERLAY_DESKTOP_GENERATOR")!);
        Assert.True(File.Exists(path));
        var process = new Process { StartInfo = new ProcessStartInfo(path) { UseShellExecute = false } };
        process.StartInfo.ArgumentList.Add("dxgi");
        process.StartInfo.ArgumentList.Add("60");
        process.StartInfo.ArgumentList.Add("10");
        if (includeDll)
        {
            var dll = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_OVERLAY_DESKTOP_DLL")!);
            Assert.True(File.Exists(dll));
            process.StartInfo.ArgumentList.Add(dll);
            process.StartInfo.ArgumentList.Add("external-producer");
        }
        Assert.True(process.Start());
        return process;
    }

    private static OverlayTargetId ProcessIdentity(Process process) =>
        new(process.Id, checked((ulong)process.StartTime.ToFileTimeUtc()));

    private static OverlayWindowState WaitForWindow(OverlayTargetId id)
    {
        OverlayWindowState window = default;
        WaitUntil(() =>
        {
            window = OverlayTargetWindow.Read(id, 0);
            return window.Handle != 0 && window.Visible;
        });
        return window;
    }

    private static OverlayTarget Target(OverlayTargetId id, string mode)
    {
        var settings = new OverlaySettings("test", true, mode, ["target.fps"],
            "windowRatio", 14, 0.25, 0.2, "bottomRight", 12);
        return new OverlayTarget("test", "Generator", id.ProcessId, id.ProcessStartKey,
            settings, [PerformanceOverlayNativeUiTests.Metric()]);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(6))
        {
            if (condition()) return;
            Thread.Sleep(25);
        }
        Assert.Fail("The desktop overlay condition did not become true.");
    }

    private static void StopGenerator(Process process)
    {
        if (process.HasExited) return;
        process.Kill();
        process.WaitForExit();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var previous = SetThreadDpiAwarenessContext(new nint(-4));
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Desktop test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Rectangle WindowBounds(nint window)
    {
        Assert.True(GetWindowRect(window, out var rect));
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
