using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

public interface IGpuVfCurveDescriptorProvider
{
    Task<GpuVfCurveDescriptor> GetAsync(string objectId, CancellationToken cancellationToken);
}

/// <summary>Called under the mutation gate. Implementations must read snapshots/raw stores, never reacquire it.</summary>
public interface IGpuVfCurveReferences
{
    Task<IReadOnlyList<GpuVfCurveReference>> GetAsync(string curveId, CancellationToken cancellationToken);
}
public interface IGpuVfCurvePresetStore
{
    Task<GpuVfCurveCatalog> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(GpuVfCurveCatalog catalog, CancellationToken cancellationToken);
}
public interface IGpuVfCurvePresets
{
    Task<GpuVfCurveLibraryView> ReadAsync(string? objectId, CancellationToken cancellationToken);
    Task<GpuVfCurvePreset> SaveAsync(string curveId, GpuVfCurveSaveRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(string curveId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GpuVfCurveReference>> ReferencesAsync(string curveId, CancellationToken cancellationToken);
    /// <summary>The caller holds the shared mutation gate through persistence of the accepted reference.</summary>
    Task<GpuVfCurveExecution> ResolveAsync(string objectId, string curveId, CancellationToken cancellationToken);
    /// <summary>One-time scalar migration; caller holds the shared mutation gate.</summary>
    Task<string> EnsureUniformAsync(string objectId, double offsetKhz, CancellationToken cancellationToken);
}
public interface IGpuVfCurveEditor
{
    Task<GpuVfCurveDraft?> ExecuteAsync(GpuVfCurveEditorCommand command, CancellationToken cancellationToken);
}

public sealed class GpuVfCurveRequestException(ControlApplyReason reason,
    IReadOnlyList<GpuVfCurveReference>? references = null) : ArgumentException(reason.Code)
{
    public ControlApplyReason Reason { get; } = reason;
    public IReadOnlyList<GpuVfCurveReference> References { get; } = references ?? [];
}
