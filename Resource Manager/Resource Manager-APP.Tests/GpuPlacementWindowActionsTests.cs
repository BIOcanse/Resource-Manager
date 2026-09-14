using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementWindowActionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResizeUsesOriginalBoundsAndPreservesShowState(bool maximized)
    {
        var windows = new Windows { State = new(30, 40, 800, 600, true, false, maximized) };
        var original = windows.State;
        var result = GpuPlacementWindowActions.RequestResize(1, () => true, windows);
        Assert.NotNull(result);
        Assert.True(result.Changed);
        Assert.True(result.Restored);
        Assert.Equal(original, result.Before);
        Assert.Equal(original, windows.State);
        Assert.Equal(new[] { (801, 600), (800, 600) }, windows.Sizes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MissingIneligibleOrUnownedWindowHasNoRequest(bool hidden, bool unowned)
    {
        var windows = new Windows { Readable = hidden || unowned,
            State = new(0, 0, 100, 100, !hidden, false, false) };
        Assert.Null(GpuPlacementWindowActions.RequestResize(1, () => !unowned, windows));
        Assert.Empty(windows.Sizes);
    }

    [Fact]
    public void RejectedResizeDoesNotClaimARecreationOpportunity()
    {
        var windows = new Windows { FailCall = 1 };
        Assert.Null(GpuPlacementWindowActions.RequestResize(1, () => true, windows));
        Assert.Single(windows.Sizes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRestoreRetainsAcceptedRequestAndOriginalSize(bool mismatchedReadBack)
    {
        var windows = new Windows { FailCall = mismatchedReadBack ? 0 : 2, IgnoreRestore = mismatchedReadBack };
        var result = GpuPlacementWindowActions.RequestResize(1, () => true, windows);
        Assert.NotNull(result);
        Assert.True(result.Changed);
        Assert.False(result.Restored);
        Assert.Equal(800, result.Before.Width);
        Assert.Equal(801, windows.State.Width);
        Assert.Equal(mismatchedReadBack ? null : 5, result.RestoreError);
    }

    [Fact]
    public void ReusedWindowIsNotRestoredThroughLostOwnership()
    {
        var windows = new Windows();
        var checks = 0;
        var result = GpuPlacementWindowActions.RequestResize(1, () => ++checks == 1, windows);
        Assert.NotNull(result);
        Assert.False(result.Restored);
        Assert.Single(windows.Sizes);
    }

    [Theory]
    [InlineData("change-read")]
    [InlineData("restore-owner")]
    [InlineData("restore-call")]
    [InlineData("restore-read")]
    public void AcceptedResizeSurvivesLaterExceptionAndStillSettlesRestore(string failure)
    {
        var windows = new Windows
        {
            ThrowReadCall = failure == "change-read" ? 2 : failure == "restore-read" ? 3 : 0,
            ThrowResizeCall = failure == "restore-call" ? 2 : 0
        };
        var original = windows.State;
        var checks = 0;
        bool OwnsWindow()
        {
            if (++checks == 2 && failure == "restore-owner")
                throw new InvalidOperationException("injected-restore-owner");
            return true;
        }
        GpuPlacementWindowAction? result = null;
        var escaped = Record.Exception(() => result = GpuPlacementWindowActions.RequestResize(1, OwnsWindow, windows));
        Assert.Null(escaped);
        Assert.NotNull(result);
        Assert.Equal(original, result.Before);
        Assert.Equal(failure == "change-read", result.Restored);
        Assert.Equal(failure == "restore-owner" ? 1 : 2, windows.Sizes.Count);
        if (failure is "change-read" or "restore-read") Assert.Equal(original, windows.State);
        if (failure == "change-read")
        {
            Assert.Null(result.Changed);
            Assert.Contains("injected-read-2", result.ChangeReadException);
            Assert.Null(result.RestoreException);
        }
        else
        {
            Assert.True(result.Changed);
            Assert.Null(result.ChangeReadException);
            Assert.Contains("injected-", result.RestoreException);
        }
        Assert.Null(result.RestoreError);
    }

    [Fact]
    public void IndependentChangeReadAndRestoreErrorsAreBothRetained()
    {
        var windows = new Windows { ThrowReadCall = 2, ThrowResizeCall = 2 };
        var result = GpuPlacementWindowActions.RequestResize(1, () => true, windows);
        Assert.NotNull(result);
        Assert.Null(result.Changed);
        Assert.False(result.Restored);
        Assert.Null(result.RestoreError);
        Assert.Contains("injected-read-2", result.ChangeReadException);
        Assert.Contains("injected-restore-call", result.RestoreException);
        Assert.Equal(2, windows.Sizes.Count);
    }

    [Fact]
    public void UnreadableChangeIsUnknownAndStillRestoresOnce()
    {
        var windows = new Windows { UnreadableCall = 2 };
        var result = GpuPlacementWindowActions.RequestResize(1, () => true, windows);
        Assert.NotNull(result);
        Assert.Null(result.Changed);
        Assert.True(result.Restored);
        Assert.Null(result.ChangeReadException);
        Assert.Equal(2, windows.Sizes.Count);
    }

    [Fact]
    public void NativeCallsResizeOnlyOwnHiddenWindowAndReportDestroyedWindowFailure()
    {
        var window = CreateWindowEx(0, "STATIC", "GPU action test", 0x80000000, 0, 0, 80, 60, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        var operations = WindowsGpuPlacementWindowOperations.Instance;
        try
        {
            Assert.True(operations.TryRead(window, out var initial));
            Assert.False(initial.Visible);
            Assert.Null(GpuPlacementWindowActions.RequestResize(window, () => true, operations));
            Assert.True(operations.Resize(window, 81, 60).Accepted);
            Assert.True(operations.TryRead(window, out var resized));
            Assert.Equal(81, resized.Width);
            Assert.True(operations.Resize(window, 80, 60).Accepted);
            Assert.True(operations.TryRead(window, out var restored));
            Assert.Equal(initial, restored);
        }
        finally { Assert.True(DestroyWindow(window)); }
        Assert.False(operations.Redraw(window).Accepted);
        Assert.False(operations.Resize(window, 80, 60).Accepted);
        Assert.False(operations.TryRead(window, out _));
    }

    private sealed class Windows : IGpuPlacementWindowOperations
    {
        internal GpuPlacementWindowState State = new(0, 0, 800, 600, true, false, false);
        internal bool Readable = true;
        internal int FailCall;
        internal bool IgnoreRestore;
        internal int ThrowReadCall;
        internal int ThrowResizeCall;
        internal int UnreadableCall;
        private int readCount;
        internal List<(int, int)> Sizes { get; } = [];
        public bool TryRead(IntPtr window, out GpuPlacementWindowState state)
        {
            if (++readCount == ThrowReadCall) throw new InvalidOperationException($"injected-read-{readCount}");
            state = State;
            return Readable && readCount != UnreadableCall;
        }
        public GpuPlacementWindowCall Redraw(IntPtr window) => new(true, null);
        public GpuPlacementWindowCall Resize(IntPtr window, int width, int height)
        {
            Sizes.Add((width, height));
            if (Sizes.Count == ThrowResizeCall) throw new InvalidOperationException("injected-restore-call");
            if (Sizes.Count == FailCall) return new(false, 5);
            if (!IgnoreRestore || Sizes.Count != 2) State = State with { Width = width, Height = height };
            return new(true, null);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
