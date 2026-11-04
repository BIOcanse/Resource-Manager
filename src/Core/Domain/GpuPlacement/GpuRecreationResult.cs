namespace ResourceManager.App.Domain.GpuPlacement;

public enum GpuRecreationState { None, Armed, Signalled, Expired, Cancelled }

public sealed record GpuRecreationResult(GpuRecreationState State, ulong SourceAdapterKey, string Status)
{
    public bool Signalled => State == GpuRecreationState.Signalled;
}
