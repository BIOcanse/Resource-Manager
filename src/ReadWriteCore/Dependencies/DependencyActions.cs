using System.Collections.Frozen;

namespace ResourceManager.ReadWrite.Dependencies;

public sealed record DependencyBinding(string Id, Func<DependencyStatus> Query,
    Func<DependencyPackage, CancellationToken, ValueTask<DependencyActionResult>>? Install);

/// <summary>Simple selected-source download and explicitly bound installation.
/// The caller owns HttpClient and chooses the source, destination and version.</summary>
public sealed class DependencyActions(HttpClient client, IEnumerable<DependencyBinding> bindings) : IReadWriteDependencies
{
    private readonly FrozenDictionary<string, DependencyBinding> bindings = bindings.ToFrozenDictionary(static item => item.Id, StringComparer.Ordinal);

    public DependencyStatus Query(string id) => Require(id).Query();

    public async ValueTask<DependencyPackage> FetchAsync(string id, Uri source, string destination, CancellationToken cancellationToken)
    {
        _ = Require(id);
        if (!source.IsAbsoluteUri || source.Scheme is not ("https" or "http")) throw new ArgumentException("Select an HTTP package source.", nameof(source));
        using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var output = new FileStream(destination, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous });
        await response.Content.CopyToAsync(output, cancellationToken);
        return new(id, Path.GetFullPath(destination));
    }

    public ValueTask<DependencyActionResult> InstallAsync(DependencyPackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = Require(package.DependencyId);
        return binding.Install is { } install ? install(package, cancellationToken)
            : ValueTask.FromResult(new DependencyActionResult(package.DependencyId, "unsupported", "dependency-installer-not-bound"));
    }

    private DependencyBinding Require(string id) => bindings.TryGetValue(id, out var binding) ? binding
        : throw new ArgumentException("Unknown dependency.", nameof(id));
}
