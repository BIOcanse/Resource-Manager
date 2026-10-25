namespace ResourceManager.App.Domain.Control;

/// <summary>A rejected business request, with facts suitable for any transport.</summary>
public sealed class ControlRequestException(ControlApplyReason reason) : InvalidOperationException(reason.Code)
{
    public ControlApplyReason Reason { get; } = reason;
}
