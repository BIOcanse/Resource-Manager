namespace ResourceManager.NativeUi;

/// <summary>
/// The page is visible when the window is shown, not minimized and not fully covered. Coverage is the resident's
/// visibility fact (the resident computes occlusion); the window only applies it.
/// </summary>
internal static class NativeUiHostVisibilityPolicy
{
    internal static bool IsVisible(bool windowVisible, bool minimized, bool fullyCovered, bool forceHidden = false) =>
        !forceHidden && windowVisible && !minimized && !fullyCovered;
}