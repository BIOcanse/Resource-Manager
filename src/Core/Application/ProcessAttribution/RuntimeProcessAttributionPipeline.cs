using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace ResourceManager.App.Application.ProcessAttribution;

public sealed partial class RuntimeProcessAttributionPipeline : IDisposable
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
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<int, RuntimeProcessAttributionDirectMatch>? cachedDirectMatches = null,
        bool captureDirectMatches = false,
        IReadOnlyDictionary<int, RuntimeProcessAttributionFixedInputs>? cachedFixedInputs = null,
        IReadOnlyDictionary<int, RuntimeProcessHelperFacts>? cachedHelperFacts = null)
    {
        var versions = new Dictionary<NativeRuntimeProcessAttributionResolver, RuntimeAttributionInputVersions>();
        var resolved = new Dictionary<int, RuntimeSoftwareAttribution>();
        var sources = new Dictionary<int, uint>();
        Dictionary<int, RuntimeProcessAttributionDirectMatch>? directMatches =
            captureDirectMatches ? new Dictionary<int, RuntimeProcessAttributionDirectMatch>() : null;
        var failures = new Dictionary<int, string>();
        var byId = processes.ToDictionary(process => process.ProcessId);
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resolver = selectPipeline(process).resolver;
                if (!versions.TryGetValue(resolver, out var inputs))
                    versions.Add(resolver, inputs = resolver.CaptureExternalInputVersions());
                if (cachedDirectMatches?.TryGetValue(process.ProcessId, out var cachedMatch) == true
                    && NativeRuntimeProcessAttributionResolver.AreExternalInputsCurrent(inputs, cachedMatch.ExternalInputs))
                {
                    if (directMatches is not null) directMatches[process.ProcessId] = cachedMatch;
                    resolved[process.ProcessId] = cachedMatch.Attribution;
                    sources[process.ProcessId] = cachedMatch.Source;
                    continue;
                }
                var attribution = resolver.Resolve(process, out var source, out var externalInputs, inputs, cachedFixedInputs?.GetValueOrDefault(process.ProcessId));
                var directMatch = new RuntimeProcessAttributionDirectMatch(attribution, source, externalInputs);
                if (directMatches is not null && NativeRuntimeProcessAttributionResolver.HasStablePathInput(process))
                    directMatches[process.ProcessId] = directMatch;
                resolved[process.ProcessId] = attribution;
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
        // Helper facts read only the instance's fixed name, path and command line. The per-instance entry hands them
        // back, so a known instance does not match its command line again.
        var helperFacts = new Dictionary<int, RuntimeProcessHelperFacts>();
        RuntimeProcessHelperFacts Facts(RuntimeProcessIdentity process)
        {
            if (helperFacts.TryGetValue(process.ProcessId, out var known)) return known;
            RuntimeProcessHelperFacts? facts = null;
            cachedHelperFacts?.TryGetValue(process.ProcessId, out facts);
            if (facts is null || !facts.Describes(process)) facts = ReadHelperFacts(process);
            helperFacts[process.ProcessId] = facts;
            return facts;
        }
        foreach (var process in processes.OrderBy(process => process.StartKey))
        {
            if (failures.ContainsKey(process.ProcessId)) continue;
            if (sources[process.ProcessId] is NativeRuntimeAttributionSources.Adapted
                or NativeRuntimeAttributionSources.Controlled
                or NativeRuntimeAttributionSources.Service) continue;
            var current = process;
            var visited = new HashSet<int> { current.ProcessId };
            while (visited.Count <= byId.Count && Facts(current).IsHelper
                && current.StartKey is > 0 && current.ParentProcessId is { } parentId
                && byId.TryGetValue(parentId, out var parent) && parent.StartKey is > 0
                && parent.StartKey <= current.StartKey && visited.Add(parentId))
            {
                if (failures.ContainsKey(parentId)) break;
                current = parent;
                if (Facts(parent).IsHelper && sources[parentId] is not (NativeRuntimeAttributionSources.Adapted
                    or NativeRuntimeAttributionSources.Controlled
                    or NativeRuntimeAttributionSources.Service)) continue;
                if (Facts(process).WebViewHostName is { } hostName
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
        return new(resolved, owners)
        {
            Failures = failures,
            DirectMatchesByProcessId = directMatches is null
                ? ImmutableDictionary<int, RuntimeProcessAttributionDirectMatch>.Empty
                : directMatches,
            HelperFactsByProcessId = helperFacts
        };
    }

    // The host name is read only for a helper: the chain consults it only for a process that passed IsHelper.
    internal static RuntimeProcessHelperFacts ReadHelperFacts(RuntimeProcessIdentity process)
    {
        var isHelper = IsHelper(process);
        return new(process.ProcessId, process.StartKey, process.Name, process.ExecutablePath, process.CommandLine,
            isHelper, isHelper ? WebViewHostName(process.CommandLine) : null);
    }

    private static bool IsHelper(RuntimeProcessIdentity process)
        => Path.GetFileNameWithoutExtension(process.ExecutablePath ?? process.Name)
            .Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
            || process.CommandLine is { } command && HelperTypePattern().IsMatch(command);

    private static string? WebViewHostName(string? commandLine)
    {
        if (commandLine is null) return null;
        var match = WebViewHostNamePattern().Match(commandLine);
        return match.Success ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value : null;
    }

    [GeneratedRegex(@"(?:^|\s)--type=(?:renderer|gpu-process|utility|zygote|crashpad-handler)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex HelperTypePattern();

    [GeneratedRegex("(?:^|\\s)--webview-exe-name=(?:\"([^\"]+)\"|([^\\s]+))", RegexOptions.CultureInvariant)]
    private static partial Regex WebViewHostNamePattern();

    public void Dispose() => resolver.Dispose();
}

public sealed record RuntimeProcessAttributionBatch(
    IReadOnlyDictionary<int, RuntimeSoftwareAttribution> SoftwareByProcessId,
    IReadOnlyDictionary<int, int> HelperOwnerByProcessId)
{
    public IReadOnlyDictionary<int, string> Failures { get; init; } = new Dictionary<int, string>();

    internal IReadOnlyDictionary<int, RuntimeProcessAttributionDirectMatch> DirectMatchesByProcessId { get; init; }
        = ImmutableDictionary<int, RuntimeProcessAttributionDirectMatch>.Empty;

    // The helper facts this batch consulted, reused or read, for the caller's per-instance entry.
    internal IReadOnlyDictionary<int, RuntimeProcessHelperFacts> HelperFactsByProcessId { get; init; }
        = ImmutableDictionary<int, RuntimeProcessHelperFacts>.Empty;
}

internal readonly record struct RuntimeProcessAttributionDirectMatch(
    RuntimeSoftwareAttribution Attribution,
    uint Source,
    RuntimeProcessAttributionExternalInputs? ExternalInputs = null);

// These are the observations actually consumed by one resolution, never a
// stamp sampled later when storing its result.
internal sealed class RuntimeProcessAttributionExternalInputs(
    RuntimeAttributionObservation package,
    RuntimeAttributionObservation service,
    RuntimeAttributionObservation root,
    RuntimeAttributionObservation system,
    RuntimeAttributionObservation? productRoot = null)
{
    internal RuntimeAttributionInputVersions? Versions { get; init; }
    internal RuntimeProcessAttributionFixedInputs? FixedInputs { get; init; }
    internal RuntimeAttributionObservation Package { get; } = package;
    internal RuntimeAttributionObservation Service { get; } = service;
    internal RuntimeAttributionObservation Root { get; } = root;
    internal RuntimeAttributionObservation System { get; } = system;
    internal RuntimeAttributionObservation? ProductRoot { get; } = productRoot;
}
