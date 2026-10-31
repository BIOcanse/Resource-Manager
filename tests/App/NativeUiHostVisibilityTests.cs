using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

// Coverage is the resident's visibility fact (desktop/occlusion.zig computes it); the window only applies it.
public sealed class NativeUiHostVisibilityTests
{
    [Fact]
    public void ShownWindowThatIsNotCoveredIsVisible()
    {
        Assert.True(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: false, fullyCovered: false));
    }

    [Fact]
    public void ResidentCoverageFactHidesTheWindow()
    {
        Assert.False(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: false, fullyCovered: true));
    }

    [Fact]
    public void MinimizedAndHiddenWindowsPauseAndRestoringResumes()
    {
        Assert.False(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: true, fullyCovered: false));
        Assert.False(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: false, minimized: false, fullyCovered: false));
        Assert.False(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: false, fullyCovered: false, forceHidden: true));
        Assert.True(NativeUiHostVisibilityPolicy.IsVisible(
            windowVisible: true, minimized: false, fullyCovered: false));
    }
}
