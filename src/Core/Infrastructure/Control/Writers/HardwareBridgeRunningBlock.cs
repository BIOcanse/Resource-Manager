namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>The compiled HardwareControl rule alone determines this entry's lifetime.</summary>
internal sealed class HardwareBridgeRunningBlock(HardwareBridgeProcessClient bridge, HardwareCommandQueue commands) : IBusinessBlock
{
    // The resident queue keeps waiting commands across a normal entry boundary,
    // and returns them to their own callers when the owner becomes unavailable.
    // The entry only joins commands already taken by this run.
    public Task RunAsync(BusinessBlockRunContext context)
        => bridge.Owner.RunAsync(context.StopNotification, context.ReportRunning, commands.RunAsync);
}
