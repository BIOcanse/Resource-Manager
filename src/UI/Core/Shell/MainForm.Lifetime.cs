namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private readonly HashSet<Task> frontendRequests = [];
    private Task? frontendStopTask;
    private bool frontendStopping;
    private bool finalClose;

    internal Task<bool> InitializeFrontendForBlockAsync() => EnsureFrontendHostAsync();

    internal Task StopFrontendAsync()
    {
        if (frontendStopTask is not null) return frontendStopTask;
        frontendStopping = true;
        StopPolling();
        frontendHost.MessageReceived -= HandleFrontendMessageReceived;
        frontendHost.NavigationCompleted -= HandleFrontendNavigationCompleted;
        return frontendStopTask = StopFrontendCoreAsync();
    }

    private async Task StopFrontendCoreAsync()
    {
        // Stop the host's local readiness wait before joining callers awaiting
        // initialization. This does not cancel their accepted backend operations.
        var stoppingHost = frontendHost.StopAsync();
        // Requests are finite shell continuations, not owners of backend work.
        // Joining them leaves already accepted backend mutations untouched.
        var requests = Task.WhenAll(frontendRequests.ToArray());
        try { await requests; }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        finally { await stoppingHost; }
        if (frontendHostInitializationTask is not null)
            await frontendHostInitializationTask;
    }

    private void TrackFrontendRequest(Task request)
    {
        foreach (var completed in frontendRequests.Where(task => task.IsCompleted).ToArray())
        {
            if (completed.Exception is { } failure) System.Diagnostics.Trace.WriteLine(failure);
            frontendRequests.Remove(completed);
        }
        frontendRequests.Add(request);
    }
}
