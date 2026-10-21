namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>A caller-owned publication permission for a specific workspace and configuration.</summary>
/// <remarks>TryPublish must synchronously execute commit under the caller's original
/// publication lock, or return false without executing it. It must not defer the commit.</remarks>
public interface IMetricPublicationPermit
{
    ulong WorkspaceIncarnation { get; }
    ulong ConfigurationGeneration { get; }
    bool IsActive { get; }
    bool TryPublish(Action commit);
}
