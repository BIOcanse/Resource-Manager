namespace ResourceManager.ReadWrite.Dependencies;

public enum DependencyAvailability { Ready, Missing, Unknown, Unsupported }
public sealed record DependencyStatus(string Id, DependencyAvailability Availability, string? Reason);
public sealed record DependencyPackage(string DependencyId, string LocalPath);
public sealed record DependencyActionResult(string DependencyId, string Status, string? Reason);

/// <summary>External code selects a source/package. These calls neither select a
/// product version nor install during a read, query or cache access.</summary>
public interface IReadWriteDependencies
{
    DependencyStatus Query(string id);
    ValueTask<DependencyPackage> FetchAsync(string id, Uri source, string destination, CancellationToken cancellationToken);
    ValueTask<DependencyActionResult> InstallAsync(DependencyPackage package, CancellationToken cancellationToken);
}
