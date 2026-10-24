using System.Drawing;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiHostVisibilityTests
{
    private static readonly Rectangle Window = new(0, 0, 100, 100);

    [Fact]
    public void VisibleWindowRemainsActiveWhenOnlyPartlyCovered()
    {
        var partialCover = new Rectangle(0, 0, 99, 100);

        Assert.False(NativeWindowOcclusion.IsFullyCovered(Window, [partialCover]));
        Assert.True(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: false, fullyCovered: false));
    }

    [Fact]
    public void WindowsAboveCanCoverTheWholeWindowTogether()
    {
        Rectangle[] covers =
        [
            new(0, 0, 50, 100),
            new(50, 0, 50, 100)
        ];

        Assert.True(NativeWindowOcclusion.IsFullyCovered(Window, covers));
        Assert.False(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: false, fullyCovered: true));
    }

    [Fact]
    public void MinimizedAndTrayWindowsPauseAndRestoringResumes()
    {
        Assert.False(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: true, fullyCovered: false));
        Assert.False(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: false, minimized: false, fullyCovered: false));
        Assert.True(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: false, fullyCovered: false));
    }
}
