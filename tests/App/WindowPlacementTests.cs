using System.Drawing;
using System.Windows.Forms;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class WindowPlacementTests
{
    private static readonly Size MinimumSize = new(760, 520);

    [Theory]
    [InlineData(0, 0, 760, 520)]
    [InlineData(500, 900, 760, 520)]
    [InlineData(1400, 300, 832, 520)]
    [InlineData(500, 300, 760, 520)]
    [InlineData(0, 900, 760, 520)]
    [InlineData(1400, 0, 832, 520)]
    [InlineData(1600, 1000, 1312, 820)]
    [InlineData(3840, 2160, 1600, 1000)]
    public void PreferredSizeRespectsMinimumAndPreservesNormalSizing(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        var workArea = new Rectangle(-1200, 300, width, height);

        Assert.Equal(new Size(expectedWidth, expectedHeight),
            WindowPlacement.GetPreferredPrimaryScreenSize(MinimumSize, workArea));
    }

    [Theory]
    [InlineData(0, 0, 760, 520, -1200, 300)]
    [InlineData(500, 900, 760, 800, -1200, 350)]
    [InlineData(1400, 300, 1200, 520, -1100, 300)]
    [InlineData(500, 300, 760, 520, -1200, 300)]
    [InlineData(0, 900, 760, 800, -1200, 350)]
    [InlineData(1400, 0, 1200, 520, -1100, 300)]
    [InlineData(1600, 1000, 1200, 800, -1000, 400)]
    public Task OpenRespectsMinimumAndCentersOnlyWhereSpaceAllows(
        int width, int height, int expectedWidth, int expectedHeight, int left, int top) => OnStaAsync(() =>
    {
        using var form = CreateForm();

        WindowPlacement.PlaceForOpenOnPrimaryScreen(form, new Rectangle(-1200, 300, width, height));

        Assert.Equal(new Rectangle(left, top, expectedWidth, expectedHeight), form.Bounds);
        Assert.Equal(FormStartPosition.Manual, form.StartPosition);
        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.False(form.IsHandleCreated);
    });

    [Theory]
    [InlineData(0, 0, 760, 520)]
    [InlineData(500, 900, 760, 900)]
    [InlineData(1400, 300, 1400, 520)]
    [InlineData(500, 300, 760, 520)]
    [InlineData(1600, 1000, 1600, 1000)]
    public Task PassivePlacementRespectsMinimumAtWorkAreaOrigin(
        int width, int height, int expectedWidth, int expectedHeight) => OnStaAsync(() =>
    {
        using var form = CreateForm();

        Assert.True(WindowPlacement.TryPlaceForPassiveValidationOnSecondaryScreen(
            form, new Rectangle(-1200, 300, width, height)));

        Assert.Equal(new Rectangle(-1200, 300, expectedWidth, expectedHeight), form.Bounds);
        Assert.False(form.IsHandleCreated);
    });

    [Fact]
    public Task MissingSecondaryScreenKeepsExistingPlacement() => OnStaAsync(() =>
    {
        using var form = CreateForm();
        var originalBounds = form.Bounds;
        var originalStartPosition = form.StartPosition;

        Assert.False(WindowPlacement.TryPlaceForPassiveValidationOnSecondaryScreen(form, null));

        Assert.Equal(originalBounds, form.Bounds);
        Assert.Equal(originalStartPosition, form.StartPosition);
        Assert.False(form.IsHandleCreated);
    });

    [Theory]
    [InlineData(0, 0, 760, 520)]
    [InlineData(40, 30, 760, 520)]
    [InlineData(96, 96, 760, 520)]
    [InlineData(500, 900, 760, 800)]
    [InlineData(1400, 300, 1200, 520)]
    [InlineData(500, 300, 760, 520)]
    [InlineData(0, 900, 760, 800)]
    [InlineData(1400, 0, 1200, 520)]
    public Task CaptionDragRestoreRespectsMinimumAndAvailableVisibleMargin(
        int width, int height, int expectedWidth, int expectedHeight) => OnStaAsync(() =>
    {
        var workArea = new Rectangle(-1200, 300, width, height);
        Point[] cursors = [new(-10000, -10000), new(10000, 10000), new(-1180, 310)];
        foreach (var cursor in cursors)
        {
            using var form = CreateForm();

            WindowPlacement.RestoreMaximizedForCaptionDrag(form, cursor, workArea);

            Assert.Equal(new Size(expectedWidth, expectedHeight), form.Size);
            if (width == 0) Assert.Equal(workArea.Left, form.Left);
            if (height == 0) Assert.Equal(workArea.Top, form.Top);
            var visible = Rectangle.Intersect(form.Bounds, workArea);
            Assert.True(visible.Width >= Math.Min(96, width), $"Horizontal visibility: {form.Bounds}");
            Assert.True(visible.Height >= Math.Min(96, height), $"Vertical visibility: {form.Bounds}");
            Assert.True(form.Top >= workArea.Top);
            Assert.Equal(FormWindowState.Normal, form.WindowState);
            Assert.False(form.IsHandleCreated);
        }
    });

    [Theory]
    [InlineData(700, 220, 100, 200)]
    [InlineData(-10000, -10000, -2204, -300)]
    [InlineData(10000, 10000, 404, 604)]
    public Task CaptionDragRestoreKeepsNormalPlacement(
        int cursorX, int cursorY, int left, int top) => OnStaAsync(() =>
    {
        using var form = CreateForm();

        WindowPlacement.RestoreMaximizedForCaptionDrag(form,
            new Point(cursorX, cursorY), new Rectangle(-1100, -300, 1600, 1000));

        Assert.Equal(new Rectangle(left, top, 1200, 800), form.Bounds);
        Assert.False(form.IsHandleCreated);
    });

    [Fact]
    public Task CaptionDragRestoreAllowsWindowSmallerThanCaptionOffsets() => OnStaAsync(() =>
    {
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            MinimumSize = new Size(1, 1),
            Bounds = new Rectangle(100, 200, 8, 8)
        };

        WindowPlacement.RestoreMaximizedForCaptionDrag(form,
            new Point(10000, 10000), new Rectangle(-1200, 300, 8, 8));

        Assert.Equal(new Rectangle(-1200, 300, 8, 8), form.Bounds);
        Assert.False(form.IsHandleCreated);
    });

    private static Form CreateForm() => new()
    {
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        MinimumSize = MinimumSize,
        Bounds = new Rectangle(100, 200, 1200, 800)
    };

    private static async Task OnStaAsync(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.TrySetResult(); }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
