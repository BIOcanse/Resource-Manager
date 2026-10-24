using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace ResourceManager.App.Application.ProcessAttribution;

public sealed class RuntimeProcessAttributionPipeline : IDisposable
{
    private readonly NativeRuntimeProcessAttributionResolver resolver;

    public RuntimeProcessAttributionPipeline(
        IReadOnlyList<SoftwareRecord> softwareRecords,
        IReadOnlyList<AdapterSoftwareRegistration> adapterRegistrations,
        IReadOnlyList<ControlledSoftwareRegistration> controlledRegistrations,
        IRuntimePackageIdentityResolver packageIdentityResolver,
        IRuntimeServiceIdentityResolver serviceIdentityResolver,
        IRuntimeRootIdentityResolver rootIdentityResolver,
        IRuntimeSystemProcessClassifier systemClassifier,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        HostManagerSoftwareIdentityOwner softwareIdentityOwner,
        IReadOnlySet<string>? windowsGamePaths = null)
    {
        resolver = new NativeRuntimeProcessAttributionResolver(
            softwareRecords,
            adapterRegistrations,
            controlledRegistrations,
            packageIdentityResolver,
            serviceIdentityResolver,
            rootIdentityResolver,
            systemClassifier,
            softwareIdentityCatalog,
            softwareIdentityOwner,
            windowsGamePaths);
    }

    public RuntimeSoftwareAttribution Match(RuntimeProcessIdentity process)
        => resolver.Resolve(process);

    public RuntimeProcessAttributionBatch MatchBatch(IReadOnlyList<RuntimeProcessIdentity> processes)
        => MatchBatch(processes, _ => this);

    internal static RuntimeProcessAttributionBatch MatchBatch(IReadOnlyList<RuntimeProcessIdentity> processes,
        Func<RuntimeProcessIdentity, RuntimeProcessAttributionPipeline> selectPipeline,
        CancellationToken cancellationToken = default)
    {
        var resolved = new Dictionary<int, RuntimeSoftwareAttribution>();
        var sources = new Dictionary<int, uint>();
        var failures = new Dictionary<int, string>();
        var byId = processes.ToDictionary(process => process.ProcessId);
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                resolved[process.ProcessId] = selectPipeline(process).resolver.Resolve(process, out var source);
                sources[process.ProcessId] = source;
            }
            catch (Exception error) when (RuntimeProcessAttributionFailure.IsCandidateFailure(error)
                || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var kind = RuntimeProcessAttributionFailure.Kind(error);
                failures[process.ProcessId] = kind;
            }
        }
        var owners = new Dictionary<int, int>();
        foreach (var process in processes.OrderBy(process => process.StartKey))
        {
            if (failures.ContainsKey(process.ProcessId)) continue;
            if (sources[process.ProcessId] is NativeRuntimeAttributionSources.Adapted
                or NativeRuntimeAttributionSources.Controlled
                or NativeRuntimeAttributionSources.Service) continue;
            var current = process;
            var visited = new HashSet<int> { current.ProcessId };
            while (visited.Count <= byId.Count && IsHelper(current)
                && current.StartKey is > 0 && current.ParentProcessId is { } parentId
                && byId.TryGetValue(parentId, out var parent) && parent.StartKey is > 0
                && parent.StartKey <= current.StartKey && visited.Add(parentId))
            {
                if (failures.ContainsKey(parentId)) break;
                current = parent;
                if (IsHelper(parent) && sources[parentId] is not (NativeRuntimeAttributionSources.Adapted
                    or NativeRuntimeAttributionSources.Controlled
                    or NativeRuntimeAttributionSources.Service)) continue;
                if (WebViewHostName(process.CommandLine) is { } hostName
                    && !Path.GetFileName(parent.ExecutablePath ?? parent.Name)
                        .Equals(hostName, StringComparison.OrdinalIgnoreCase)) break;
                var owner = resolved[parentId];
                if (owner.Kind == SoftwareKinds.Unattributed) break;
                if (sources[process.ProcessId] == NativeRuntimeAttributionSources.ResourceManagerSelf
                    && owner.Id != RuntimeAttributionIds.ResourceManagerSelf) break;
                resolved[process.ProcessId] = owner;
                owners[process.ProcessId] = parentId;
                break;
            }
        }
        // The exact catalog entry and a launcher-derived identity share the observed game root.
        foreach (var pair in resolved.Where(pair => pair.Value.Id.StartsWith("runtime-game:", StringComparison.Ordinal)).ToArray())
        {
            var catalogGame = resolved.Values.FirstOrDefault(value => value.Id.StartsWith("catalog:", StringComparison.Ordinal)
                && value.Kind == SoftwareKinds.Game && value.RootPaths.SequenceEqual(pair.Value.RootPaths, StringComparer.OrdinalIgnoreCase));
            if (catalogGame is not null) resolved[pair.Key] = catalogGame;
        }
        // Category belongs to the software, and is merged only within this batch.
        foreach (var group in resolved.GroupBy(pair => pair.Value.Id, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var category = group.Select(pair => pair.Value)
                .OrderByDescending(value => value.Kind == SoftwareKinds.Game)
                .FirstOrDefault(value => value.CategorySource is not null);
            if (category is null) continue;
            foreach (var pair in group) resolved[pair.Key] = pair.Value with
            { Kind = category.Kind, DisplayKind = category.DisplayKind, CategorySource = category.CategorySource };
        }
        return new(resolved, owners) { Failures = failures };
    }

    private static bool IsHelper(RuntimeProcessIdentity process)
        => Path.GetFileNameWithoutExtension(process.ExecutablePath ?? process.Name)
            .Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
            || process.CommandLine is { } command && System.Text.RegularExpressions.Regex.IsMatch(command,
                @"(?:^|\s)--type=(?:renderer|gpu-process|utility|zygote|crashpad-handler)(?:\s|$)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? WebViewHostName(string? commandLine)
    {
        if (commandLine is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(commandLine,
            "(?:^|\\s)--webview-exe-name=(?:\"([^\"]+)\"|([^\\s]+))",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value : null;
    }

    public void Dispose() => resolver.Dispose();
}

public sealed record RuntimeProcessAttributionBatch(
    IReadOnlyDictionary<int, RuntimeSoftwareAttribution> SoftwareByProcessId,
    IReadOnlyDictionary<int, int> HelperOwnerByProcessId)
{
    public IReadOnlyDictionary<int, string> Failures { get; init; } = new Dictionary<int, string>();
}
