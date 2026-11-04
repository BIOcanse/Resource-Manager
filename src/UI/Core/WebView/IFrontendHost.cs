namespace ResourceManager.NativeUi.WebView;

// All members are used on the window's STA. StopAsync retains that message loop
// until initialization, native control disposal and browser exit have completed.
internal interface IFrontendHost : IDisposable
{
    Control View { get; }
    Color BackgroundColor { get; }
    bool IsReady { get; }
    string ApplicationAddress { get; }
    event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived;
    event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted;
    Task InitializeAsync();
    Task StopAsync();
    void Navigate(string address);
    void PostStringMessage(string message);
    void PostJsonMessage(string json);
    void FocusContent();
}

internal static class FrontendHostShutdown
{
    internal static async Task DrainAsync(Task? initialization, Action closeSurface,
        Func<Task> waitForBrowserExit)
    {
        if (initialization is not null)
        {
            try { await initialization; }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        }
        closeSurface();
        await waitForBrowserExit();
    }
}
