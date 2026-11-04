namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>A structural branch with no resources of its own.</summary>
internal sealed class BackendDomainBlock : IBusinessBlock
{
    public async Task RunAsync(BusinessBlockRunContext context)
    {
        context.ReportRunning();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
    }
}
