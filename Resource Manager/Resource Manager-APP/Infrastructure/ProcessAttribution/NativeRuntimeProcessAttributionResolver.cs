using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

internal sealed class NativeRuntimeProcessAttributionResolver : IDisposable
{
    private static readonly string? WindowsRoot = CanonicalPath(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string? ProgramFilesRoot = CanonicalPath(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private static readonly string? ProgramFilesX86Root = CanonicalPath(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    private static readonly RuntimeSoftwareAttribution SelfAttribution = new(
        RuntimeAttributionIds.ResourceManagerSelf,
        ResourceManagerSelfDescriptor.DisplayName,
        SoftwareKinds.Adapted,
        SoftwareText.Adapted,
        ResourceManagerSelfDescriptor.ResolveRootPaths()
            .Select(CanonicalPath)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToArray());

    private readonly object gate = new();
    private readonly HostManagerSoftwareIdentityOwner.SessionLease lease;
    private readonly NativeRuntimeSourceCatalog adaptedCatalog;
    private readonly NativeRuntimeSourceCatalog controlledCatalog;
    private readonly NativeRuntimeSourceCatalog selfCatalog;
    private readonly NativeRuntimeSourceCatalog knownCatalog;
    private readonly IReadOnlyDictionary<string, RuntimeSoftwareAttribution> registeredSoftware;
    private readonly IReadOnlyList<DirectProcessBinding> adaptedBindings;
    private readonly IReadOnlyList<DirectProcessBinding> controlledBindings;
    private readonly IRuntimePackageIdentityResolver packageResolver;
    private readonly IRuntimeServiceIdentityResolver serviceResolver;
    private readonly IRuntimeRootIdentityResolver rootResolver;
    private readonly IRuntimeSystemProcessClassifier systemClassifier;
    private readonly INativeSoftwareIdentityProcessObserver bundledCatalog;
    private NativeSoftwareIdentityPayloadCatalog resolutionPayloads = new();
    private bool disposed;

    internal NativeRuntimeProcessAttributionResolver(
        IReadOnlyList<SoftwareRecord> softwareRecords,
        IReadOnlyList<AdapterSoftwareRegistration> adapterRegistrations,
        IReadOnlyList<ControlledSoftwareRegistration> controlledRegistrations,
        IRuntimePackageIdentityResolver packageResolver,
        IRuntimeServiceIdentityResolver serviceResolver,
        IRuntimeRootIdentityResolver rootResolver,
        IRuntimeSystemProcessClassifier systemClassifier,
        ISoftwareIdentityCatalog bundledCatalog,
        HostManagerSoftwareIdentityOwner owner)
    {
        ArgumentNullException.ThrowIfNull(softwareRecords);
        ArgumentNullException.ThrowIfNull(adapterRegistrations);
        ArgumentNullException.ThrowIfNull(controlledRegistrations);
        this.packageResolver = packageResolver;
        this.serviceResolver = serviceResolver;
        this.rootResolver = rootResolver;
        this.systemClassifier = systemClassifier;
        this.bundledCatalog = bundledCatalog as INativeSoftwareIdentityProcessObserver
            ?? throw new InvalidOperationException(
                "The bundled software identity catalog is not backed by the native catalog session.");
        var acquiredLease = owner.Acquire();
        NativeRuntimeSourceCatalog? acquiredAdaptedCatalog = null;
        NativeRuntimeSourceCatalog? acquiredControlledCatalog = null;
        NativeRuntimeSourceCatalog? acquiredSelfCatalog = null;
        NativeRuntimeSourceCatalog? acquiredKnownCatalog = null;
        try
        {
            var software = softwareRecords
                .Select(static record => new RuntimeSoftwareAttribution(
                    record.Id,
                    record.Name,
                    record.Kind,
                    record.DisplayKind,
                    record.RootPaths
                        .Select(CanonicalPath)
                        .Where(static value => value is not null)
                        .Select(static value => value!)
                        .ToArray()))
                .ToArray();

            registeredSoftware = software.ToDictionary(static item => item.Id, StringComparer.Ordinal);
            var adapted = BuildAdaptedDefinitions(
                adapterRegistrations,
                software,
                out adaptedBindings);
            var controlled = BuildControlledDefinitions(
                controlledRegistrations,
                software,
                out controlledBindings);
            var self = BuildSelfDefinitions();

            acquiredAdaptedCatalog = NativeRuntimeSourceCatalog.Create(
                adapted,
                acquiredLease.CatalogPlan);
            acquiredControlledCatalog = NativeRuntimeSourceCatalog.Create(
                controlled,
                acquiredLease.CatalogPlan);
            acquiredSelfCatalog = NativeRuntimeSourceCatalog.Create(
                self,
                acquiredLease.CatalogPlan);
            var knownProjection =
                NativeSoftwareIdentityCatalogProjector.ProjectRuntimeKnown(
                    softwareRecords,
                    acquiredLease.CatalogPlan);
            acquiredKnownCatalog = new NativeRuntimeSourceCatalog(
                knownProjection,
                acquiredLease.CatalogPlan);
            lease = acquiredLease;
            adaptedCatalog = acquiredAdaptedCatalog;
            controlledCatalog = acquiredControlledCatalog;
            selfCatalog = acquiredSelfCatalog;
            knownCatalog = acquiredKnownCatalog;
        }
        catch
        {
            acquiredKnownCatalog?.Dispose();
            acquiredSelfCatalog?.Dispose();
            acquiredControlledCatalog?.Dispose();
            acquiredAdaptedCatalog?.Dispose();
            acquiredLease.Dispose();
            throw;
        }
    }

    internal RuntimeSoftwareAttribution Resolve(RuntimeProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            resolutionPayloads = new NativeSoftwareIdentityPayloadCatalog();
            var frame = lease.NextFrame(DateTimeOffset.UtcNow);
            var observations = new List<NativeSoftwareIdentityObservationInput>(16);

            AddCatalogSource(
                observations,
                NativeRuntimeAttributionSources.Adapted,
                adaptedCatalog.Observe(process),
                adaptedBindings
                    .Where(binding => MatchesDirectProcessBinding(binding, process))
                    .Select(static binding => binding.Attribution),
                frame.FrameEpoch);
            AddCatalogSource(
                observations,
                NativeRuntimeAttributionSources.Controlled,
                controlledCatalog.Observe(process),
                controlledBindings
                    .Where(binding => MatchesDirectProcessBinding(binding, process))
                    .Select(static binding => binding.Attribution),
                frame.FrameEpoch);
            AddCatalogSource(
                observations,
                NativeRuntimeAttributionSources.ResourceManagerSelf,
                selfCatalog.Observe(process),
                process.IsSelfDescendant ? [SelfAttribution] : [],
                frame.FrameEpoch);
            AddSource(
                observations,
                NativeRuntimeAttributionSources.Package,
                packageResolver.Observe(process),
                frame.FrameEpoch);
            AddSource(
                observations,
                NativeRuntimeAttributionSources.Service,
                serviceResolver.Observe(process),
                frame.FrameEpoch);
            AddCatalogSource(
                observations,
                NativeRuntimeAttributionSources.KnownSoftware,
                knownCatalog.ObserveKnown(process),
                [],
                frame.FrameEpoch);
            AddBundledSource(observations, process, frame.FrameEpoch);
            AddSource(
                observations,
                NativeRuntimeAttributionSources.RuntimeProduct,
                ObserveRuntimeProduct(process),
                frame.FrameEpoch);
            AddSource(
                observations,
                NativeRuntimeAttributionSources.RuntimeRoot,
                rootResolver.Observe(process),
                frame.FrameEpoch);
            AddSource(
                observations,
                NativeRuntimeAttributionSources.System,
                systemClassifier.Observe(process),
                frame.FrameEpoch);

            observations.Sort(static (left, right) =>
            {
                var source = left.SourceId.CompareTo(right.SourceId);
                if (source != 0)
                {
                    return source;
                }
                var identity = left.IdentityHandle.CompareTo(right.IdentityHandle);
                return identity != 0
                    ? identity
                    : left.EvidenceMask.CompareTo(right.EvidenceMask);
            });
            var input = new NativeSoftwareIdentityResolveInput
            {
                AbiVersion = NativeSoftwareIdentityResolutionAbi.Version,
                StructSize = SizeOf<NativeSoftwareIdentityResolveInput>(),
                ConfigurationGeneration = lease.ResolutionPlan.ConfigurationGeneration,
                PolicyGeneration = lease.PolicyGeneration,
                FrameEpoch = frame.FrameEpoch,
                CommandUtcMilliseconds = frame.CommandUtcMilliseconds,
                ObservedAtUtcMilliseconds = frame.CommandUtcMilliseconds,
                ObservationCount = checked((uint)observations.Count),
                ReservedU32 = 0,
                ValidMask = (ulong)NativeSoftwareIdentityResolveValidity.Required,
                Flags = 0
            };
            var status = lease.ResolutionSession.Resolve(
                in input,
                observations.ToArray(),
                out var output);
            if (status != NativeSoftwareIdentityStatus.Ok)
            {
                throw new InvalidOperationException(
                    $"Native software identity resolution failed with {status}.");
            }
            return ResolveOutput(output);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        knownCatalog.Dispose();
        selfCatalog.Dispose();
        controlledCatalog.Dispose();
        adaptedCatalog.Dispose();
        lease.Dispose();
    }

    private RuntimeSoftwareAttribution ResolveOutput(
        NativeSoftwareIdentityResolutionOutput output)
    {
        if (output.Status != (uint)NativeSoftwareIdentityResolutionStatus.Matched)
        {
            return output.Status is
                (uint)NativeSoftwareIdentityResolutionStatus.Unattributed or
                (uint)NativeSoftwareIdentityResolutionStatus.Conflict or
                (uint)NativeSoftwareIdentityResolutionStatus.Unavailable
                ? RuntimeSoftwareAttribution.Unattributed
                : throw new InvalidOperationException(
                    $"Native software identity resolution returned unknown status {output.Status}.");
        }

        var attribution = resolutionPayloads.RequireAttribution(output.IdentityHandle);
        var expected = resolutionPayloads.GetOrAddAttribution(attribution);
        if (expected.DisplayNameHandle != output.DisplayNameHandle
            || expected.SoftwareKind != output.SoftwareKind
            || expected.RootHandle != output.RootHandle)
        {
            throw new InvalidOperationException(
                "Native software identity resolution returned a payload that does not match its numeric identity.");
        }
        return attribution;
    }

    private void AddBundledSource(
        ICollection<NativeSoftwareIdentityObservationInput> target,
        RuntimeProcessIdentity process,
        ulong generation)
    {
        if (CanonicalPath(process.ExecutablePath) is { } path
            && IsWindowsOwnedPath(path))
        {
            AddTerminal(
                target,
                NativeRuntimeAttributionSources.BundledCatalog,
                NativeSoftwareIdentityObservationStatus.NoMatch,
                generation);
            return;
        }

        var observation = bundledCatalog.ObserveProcess(process);
        switch (observation.Status)
        {
            case NativeCatalogProcessObservationStatus.NoMatch:
                AddTerminal(
                    target,
                    NativeRuntimeAttributionSources.BundledCatalog,
                    NativeSoftwareIdentityObservationStatus.NoMatch,
                    generation);
                return;
            case NativeCatalogProcessObservationStatus.Conflict:
                AddConflict(
                    target,
                    NativeRuntimeAttributionSources.BundledCatalog,
                    generation);
                return;
            case NativeCatalogProcessObservationStatus.Matched:
                var entry = observation.Entry
                    ?? throw new InvalidOperationException(
                        "Native bundled catalog matched without an entry payload.");
                var root = CanonicalPath(process.ExecutablePath) is { } executablePath
                    ? CanonicalPath(Path.GetDirectoryName(executablePath))
                    : null;
                AddMatched(
                    target,
                    NativeRuntimeAttributionSources.BundledCatalog,
                    registeredSoftware.TryGetValue($"catalog:{entry.Id}", out var registered)
                        ? registered
                        : new RuntimeSoftwareAttribution(
                        $"catalog:{entry.Id}",
                        entry.DisplayName,
                        entry.Kind,
                        SoftwareText.DisplayKind(entry.Kind),
                        root is null ? [] : [root]),
                    observation.EvidenceMask,
                    generation);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown native catalog observation status {observation.Status}.");
        }
    }

    private void AddCatalogSource(
        ICollection<NativeSoftwareIdentityObservationInput> target,
        uint source,
        NativeSourceCatalogObservation catalogObservation,
        IEnumerable<RuntimeSoftwareAttribution> directAttributions,
        ulong generation)
    {
        if (catalogObservation.Conflict)
        {
            AddConflict(target, source, generation);
            return;
        }

        var candidates = new Dictionary<string, AttributionCandidate>(
            StringComparer.Ordinal);
        foreach (var direct in directAttributions)
        {
            AddCandidate(
                candidates,
                direct,
                SourceEvidence(source));
        }
        foreach (var candidate in catalogObservation.Candidates)
        {
            AddCandidate(candidates, candidate.Attribution, candidate.EvidenceMask);
        }

        if (candidates.Count == 0)
        {
            AddTerminal(
                target,
                source,
                NativeSoftwareIdentityObservationStatus.NoMatch,
                generation);
            return;
        }
        foreach (var candidate in candidates.Values)
        {
            AddMatched(
                target,
                source,
                candidate.Attribution,
                candidate.EvidenceMask,
                generation);
        }
    }

    private static void AddCandidate(
        IDictionary<string, AttributionCandidate> candidates,
        RuntimeSoftwareAttribution attribution,
        ulong evidenceMask)
    {
        if (candidates.TryGetValue(attribution.Id, out var existing))
        {
            if (!AttributionEquals(existing.Attribution, attribution))
            {
                throw new InvalidDataException(
                    $"Runtime attribution id '{attribution.Id}' has conflicting source payloads.");
            }
            candidates[attribution.Id] = existing with
            {
                EvidenceMask = existing.EvidenceMask | evidenceMask
            };
            return;
        }
        candidates.Add(
            attribution.Id,
            new AttributionCandidate(
                attribution,
                evidenceMask == 0 ? 1UL : evidenceMask));
    }

    private void AddSource(
        ICollection<NativeSoftwareIdentityObservationInput> target,
        uint source,
        RuntimeAttributionObservation observation,
        ulong generation)
    {
        switch (observation.Status)
        {
            case RuntimeAttributionObservationStatus.NoMatch:
                AddTerminal(
                    target,
                    source,
                    NativeSoftwareIdentityObservationStatus.NoMatch,
                    generation);
                break;
            case RuntimeAttributionObservationStatus.Unavailable:
                AddTerminal(
                    target,
                    source,
                    NativeSoftwareIdentityObservationStatus.Unavailable,
                    generation);
                break;
            case RuntimeAttributionObservationStatus.Matched:
                AddMatched(
                    target,
                    source,
                    observation.Attribution
                        ?? throw new InvalidOperationException(
                            "Matched runtime attribution observation has no payload."),
                    SourceEvidence(source),
                    generation);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown runtime attribution observation status {observation.Status}.");
        }
    }

    private void AddMatched(
        ICollection<NativeSoftwareIdentityObservationInput> target,
        uint source,
        RuntimeSoftwareAttribution attribution,
        ulong evidenceMask,
        ulong generation)
    {
        var payload = resolutionPayloads.GetOrAddAttribution(attribution);
        target.Add(new NativeSoftwareIdentityObservationInput
        {
            StructSize = SizeOf<NativeSoftwareIdentityObservationInput>(),
            SourceId = source,
            Status = (uint)NativeSoftwareIdentityObservationStatus.Matched,
            Flags = 0,
            IdentityHandle = payload.IdentityHandle,
            DisplayNameHandle = payload.DisplayNameHandle,
            SoftwareKind = payload.SoftwareKind,
            ReservedU32 = 0,
            RootHandle = payload.RootHandle,
            EvidenceMask = evidenceMask == 0 ? SourceEvidence(source) : evidenceMask,
            ObservationGeneration = generation
        });
    }

    private void AddConflict(
        ICollection<NativeSoftwareIdentityObservationInput> target,
        uint source,
        ulong generation)
    {
        AddMatched(
            target,
            source,
            new RuntimeSoftwareAttribution(
                $"native-source-conflict:{source}:a",
                "Identity conflict",
                SoftwareKinds.Other,
                SoftwareText.General,
                []),
            SourceEvidence(source),
            generation);
        AddMatched(
            target,
            source,
            new RuntimeSoftwareAttribution(
                $"native-source-conflict:{source}:b",
                "Identity conflict",
                SoftwareKinds.Other,
                SoftwareText.General,
                []),
            SourceEvidence(source),
            generation);
    }

    private static void AddTerminal(
        ICollection<NativeSoftwareIdentityObservationInput> target,
        uint source,
        NativeSoftwareIdentityObservationStatus status,
        ulong generation)
    {
        target.Add(new NativeSoftwareIdentityObservationInput
        {
            StructSize = SizeOf<NativeSoftwareIdentityObservationInput>(),
            SourceId = source,
            Status = (uint)status,
            Flags = 0,
            ObservationGeneration = generation
        });
    }

    private static RuntimeAttributionObservation ObserveRuntimeProduct(
        RuntimeProcessIdentity process)
    {
        var processPath = CanonicalPath(process.ExecutablePath);
        if (processPath is null || IsWindowsOwnedPath(processPath))
        {
            return RuntimeAttributionObservation.NoMatch;
        }

        var displayName = CleanMetadata(process.ProductName)
            ?? CleanMetadata(process.FileDescription)
            ?? CleanMetadata(process.CompanyName);
        if (displayName is null || IsGenericProcessMetadata(displayName, process.Name))
        {
            return RuntimeAttributionObservation.NoMatch;
        }
        var identityText = string.Join(
            "|",
            new[]
            {
                CleanMetadata(process.CompanyName),
                displayName
            }.Where(static value => value is not null));
        var root = CanonicalPath(Path.GetDirectoryName(processPath));
        return RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
            $"runtime-product:{NormalizeId(identityText)}",
            displayName,
            SoftwareKinds.RuntimeProduct,
            SoftwareText.General,
            root is null ? [] : [root]));
    }

    private static IReadOnlyList<NativeSoftwareIdentityCatalogEntryDefinition>
        BuildAdaptedDefinitions(
            IReadOnlyList<AdapterSoftwareRegistration> registrations,
            IReadOnlyList<RuntimeSoftwareAttribution> software,
            out IReadOnlyList<DirectProcessBinding> bindings)
    {
        var direct = new List<DirectProcessBinding>();
        var definitions = new List<NativeSoftwareIdentityCatalogEntryDefinition>(
            registrations.Count);
        foreach (var registration in registrations)
        {
            var roots = registration.ProgramRootPaths
                .Select(RequireCanonicalPath)
                .Concat(registration.Processes
                    .Select(static process => process.ExecutablePath)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(RequireExecutableDirectory))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var attribution = software.FirstOrDefault(value =>
                    value.Id.Equals(registration.Id, StringComparison.OrdinalIgnoreCase))
                ?? new RuntimeSoftwareAttribution(
                    registration.Id,
                    registration.DisplayName,
                    SoftwareKinds.Adapted,
                    SoftwareText.Adapted,
                    roots);
            foreach (var process in registration.Processes)
            {
                if (process.ProcessId is { } processId
                    && TryReadProcessStartKey(processId) is { } startKey)
                {
                    direct.Add(new DirectProcessBinding(
                        processId,
                        startKey,
                        attribution));
                }
            }
            definitions.Add(CreateDefinition(
                attribution,
                NativeSoftwareIdentitySources.Adapted,
                registration.Processes.Select(static process =>
                    (process.Name, process.ExecutablePath)),
                roots));
        }
        bindings = direct;
        return definitions;
    }

    private static IReadOnlyList<NativeSoftwareIdentityCatalogEntryDefinition>
        BuildControlledDefinitions(
            IReadOnlyList<ControlledSoftwareRegistration> registrations,
            IReadOnlyList<RuntimeSoftwareAttribution> software,
            out IReadOnlyList<DirectProcessBinding> bindings)
    {
        var direct = new List<DirectProcessBinding>();
        var definitions = new List<NativeSoftwareIdentityCatalogEntryDefinition>(
            registrations.Count);
        foreach (var registration in registrations)
        {
            var id = $"controlled-registration:{registration.Id}";
            var roots = registration.ProgramRootPaths
                .Select(RequireCanonicalPath)
                .Concat(registration.Processes
                    .Select(static process => process.ExecutablePath)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(RequireExecutableDirectory))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var attribution = software.FirstOrDefault(value =>
                    value.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? new RuntimeSoftwareAttribution(
                    id,
                    registration.Software.Count > 0
                        ? string.Join(", ", registration.Software)
                        : registration.Command,
                    SoftwareKinds.Controlled,
                    SoftwareText.Controlled,
                    roots);
            foreach (var process in registration.Processes)
            {
                if (process.ProcessId is { } processId
                    && TryReadProcessStartKey(processId) is { } startKey)
                {
                    direct.Add(new DirectProcessBinding(
                        processId,
                        startKey,
                        attribution));
                }
            }
            definitions.Add(CreateDefinition(
                attribution,
                NativeSoftwareIdentitySources.Controlled,
                registration.Processes.Select(static process =>
                    (process.Name, process.ExecutablePath)),
                roots));
        }
        bindings = direct;
        return definitions;
    }

    private static IReadOnlyList<NativeSoftwareIdentityCatalogEntryDefinition>
        BuildSelfDefinitions()
        =>
        [
            CreateDefinition(
                SelfAttribution,
                NativeSoftwareIdentitySources.ResourceManagerSelf,
                ResourceManagerSelfDescriptor.ProcessNames.Select(static name =>
                    (name, (string?)null)),
                SelfAttribution.RootPaths)
        ];

    private static NativeSoftwareIdentityCatalogEntryDefinition CreateDefinition(
        RuntimeSoftwareAttribution attribution,
        uint source,
        IEnumerable<(string Name, string? ExecutablePath)> processes,
        IReadOnlyList<string> roots)
    {
        var aliases = processes
            .SelectMany(static process => new[]
            {
                NativeSoftwareIdentityCatalogProjector.CanonicalExecutableName(process.Name),
                NativeSoftwareIdentityCatalogProjector.CanonicalExecutableName(
                    process.ExecutablePath)
            })
            .Where(static value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .Select(static value => new NativeSoftwareIdentityCatalogAliasDefinition(
                NativeSoftwareIdentityAliasKind.ExecutableName,
                value,
                0))
            .ToList();
        if (aliases.Count == 0)
        {
            aliases.Add(new NativeSoftwareIdentityCatalogAliasDefinition(
                NativeSoftwareIdentityAliasKind.InstalledName,
                NativeSoftwareIdentityCatalogProjector.CanonicalTextKey(
                    $"runtime-entry:{attribution.Id}")
                    ?? throw new InvalidDataException(
                        $"Runtime attribution '{attribution.Id}' has no usable catalog alias."),
                0));
        }

        var primaryName =
            NativeSoftwareIdentityCatalogProjector.NormalizeTextKey(attribution.Name);
        if (primaryName.Length == 0)
        {
            throw new InvalidDataException(
                $"Runtime attribution '{attribution.Id}' has no normalized primary name.");
        }
        return new NativeSoftwareIdentityCatalogEntryDefinition(
            NativeSoftwareIdentityEntryPayload.FromRuntime(attribution),
            attribution.Id,
            attribution.Name,
            attribution.Kind,
            source,
            primaryName,
            aliases,
            roots);
    }

    private static string RequireCanonicalPath(string value)
        => CanonicalPath(value)
            ?? throw new InvalidDataException(
                $"Runtime attribution contains invalid path '{value}'.");

    private static string RequireExecutableDirectory(string? value)
    {
        var executable = RequireCanonicalPath(value!);
        return CanonicalPath(Path.GetDirectoryName(executable))
            ?? throw new InvalidDataException(
                $"Runtime attribution executable path '{value}' has no directory.");
    }

    private static string? CanonicalPath(string? value)
        => NativeSoftwareIdentityCatalogProjector.CanonicalPath(value);

    private static bool IsWindowsOwnedPath(string path)
        => IsUnder(path, WindowsRoot)
            || IsUnder(
                path,
                CanonicalPath(Path.Combine(
                    ProgramFilesRoot ?? string.Empty,
                    "WindowsApps")))
            || IsUnder(
                path,
                CanonicalPath(Path.Combine(
                    ProgramFilesX86Root ?? string.Empty,
                    "WindowsApps")));

    private static bool IsUnder(string candidate, string? root)
        => root is not null
            && (candidate.Equals(root, StringComparison.Ordinal)
                || candidate.StartsWith(root + "/", StringComparison.Ordinal));

    private static string? CleanMetadata(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static bool IsGenericProcessMetadata(string value, string processName)
        => value.Equals(processName, StringComparison.OrdinalIgnoreCase)
            || value.Equals(
                Path.GetFileNameWithoutExtension(processName),
                StringComparison.OrdinalIgnoreCase)
            || value.Equals("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
            || value.Equals(
                "Microsoft Windows Operating System",
                StringComparison.OrdinalIgnoreCase);

    private static string NormalizeId(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static character =>
                char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var result = string.Join(
            '-',
            new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? "unknown" : result;
    }

    private static bool AttributionEquals(
        RuntimeSoftwareAttribution left,
        RuntimeSoftwareAttribution right)
        => left.Id.Equals(right.Id, StringComparison.Ordinal)
            && left.Name.Equals(right.Name, StringComparison.Ordinal)
            && left.Kind.Equals(right.Kind, StringComparison.Ordinal)
            && left.DisplayKind.Equals(right.DisplayKind, StringComparison.Ordinal)
            && left.RootPaths.SequenceEqual(
                right.RootPaths,
                StringComparer.OrdinalIgnoreCase);

    private static ulong SourceEvidence(uint source)
        => 1UL << checked((int)source - 1);

    private static bool MatchesDirectProcessBinding(
        DirectProcessBinding binding,
        RuntimeProcessIdentity process)
        => MatchesDirectProcessInstance(
            binding.ProcessId,
            binding.StartKey,
            process);

    internal static bool MatchesDirectProcessInstance(
        int bindingProcessId,
        long bindingStartKey,
        RuntimeProcessIdentity process)
        => process.StartKey is { } startKey
            && bindingProcessId == process.ProcessId
            && bindingStartKey == startKey;

    private static long? TryReadProcessStartKey(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            return process.StartTime.ToFileTimeUtc();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private readonly record struct DirectProcessBinding(
        int ProcessId,
        long StartKey,
        RuntimeSoftwareAttribution Attribution);

    private readonly record struct AttributionCandidate(
        RuntimeSoftwareAttribution Attribution,
        ulong EvidenceMask);
}

internal sealed class NativeRuntimeSourceCatalog : IDisposable
{
    private readonly NativeSoftwareIdentityCatalogProjection projection;
    private readonly NativeSoftwareIdentityCatalogWorkspace workspace;
    private long queryEpoch;

    internal NativeRuntimeSourceCatalog(
        NativeSoftwareIdentityCatalogProjection projection,
        ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        this.projection = projection;
        workspace = new NativeSoftwareIdentityCatalogWorkspace(plan);
        try
        {
            projection.Load(workspace.Session);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    internal static NativeRuntimeSourceCatalog Create(
        IReadOnlyList<NativeSoftwareIdentityCatalogEntryDefinition> definitions,
        ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        var payloads = new NativeSoftwareIdentityPayloadCatalog();
        return new NativeRuntimeSourceCatalog(
            NativeSoftwareIdentityCatalogProjector.Project(
                definitions,
                payloads,
                plan),
            plan);
    }

    internal NativeSourceCatalogObservation Observe(RuntimeProcessIdentity process)
    {
        var exact = MatchExact(process);
        var known = MatchKnown(process);
        if (exact.Conflict || known.Conflict)
        {
            return NativeSourceCatalogObservation.Conflicted;
        }
        return new NativeSourceCatalogObservation(
            false,
            exact.Candidates
                .Concat(known.Candidates)
                .ToArray());
    }

    internal NativeSourceCatalogObservation ObserveKnown(RuntimeProcessIdentity process)
        => MatchKnown(process);

    public void Dispose() => workspace.Dispose();

    private NativeSourceCatalogObservation MatchExact(RuntimeProcessIdentity process)
    {
        var keys = new List<(NativeSoftwareIdentityAliasKind Kind, string Key)>(3);
        AddExecutableKey(keys, process.Name);
        AddExecutableKey(keys, process.ExecutablePath);
        var product = NativeSoftwareIdentityCatalogProjector.CanonicalTextKey(
            process.ProductName);
        if (product is not null)
        {
            keys.Add((NativeSoftwareIdentityAliasKind.ProductName, product));
        }
        keys = keys
            .Distinct()
            .OrderBy(static value => value.Kind)
            .ThenBy(static value => value.Key, StringComparer.Ordinal)
            .ToList();

        var keyBytes = new List<byte>();
        var facts = keys.Select(value =>
        {
            var key = AppendKey(keyBytes, value.Key);
            return new NativeSoftwareIdentityCatalogFactInput
            {
                StructSize = SizeOf<NativeSoftwareIdentityCatalogFactInput>(),
                Kind = (uint)value.Kind,
                KeyOffset = key.Offset,
                KeyLength = key.Length,
                NumericValue = 0,
                Flags = 0,
                ReservedU32 = 0
            };
        }).ToArray();
        var input = new NativeSoftwareIdentityCatalogQueryInput
        {
            AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
            StructSize = SizeOf<NativeSoftwareIdentityCatalogQueryInput>(),
            ConfigurationGeneration = workspace.Session.ConfigurationGeneration,
            CatalogGeneration = 1,
            QueryEpoch = NextQueryEpoch(),
            Mode = (uint)NativeSoftwareIdentityMatchMode.Process,
            FactCount = checked((uint)facts.Length),
            KeyByteCount = checked((uint)keyBytes.Count),
            Flags = 0,
            ValidMask = (ulong)NativeSoftwareIdentityCatalogQueryValidity.Required
        };
        var status = workspace.Session.Match(
            in input,
            facts,
            keyBytes.ToArray(),
            out var output);
        if (status != NativeSoftwareIdentityStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native runtime source exact query failed with {status}.");
        }
        return output.Status switch
        {
            (uint)NativeSoftwareIdentityMatchStatus.NoMatch =>
                NativeSourceCatalogObservation.NoMatch,
            (uint)NativeSoftwareIdentityMatchStatus.Conflict =>
                NativeSourceCatalogObservation.Conflicted,
            (uint)NativeSoftwareIdentityMatchStatus.Matched =>
                NativeSourceCatalogObservation.Matched(
                    RequireRuntimeAttribution(output.EntryHandle),
                    output.EvidenceMask),
            _ => throw new InvalidOperationException(
                $"Native runtime source catalog returned unknown status {output.Status}.")
        };
    }

    private NativeSourceCatalogObservation MatchKnown(RuntimeProcessIdentity process)
    {
        var path = NativeSoftwareIdentityCatalogProjector.CanonicalPath(
            process.ExecutablePath);
        var keyBytes = new List<byte>();
        var pathKey = path is null ? default : AppendKey(keyBytes, path);
        var signalValues = new[]
        {
            (NativeSoftwareIdentitySignalKind.ProcessName, process.Name),
            (NativeSoftwareIdentitySignalKind.ProductName, process.ProductName),
            (NativeSoftwareIdentitySignalKind.FileDescription, process.FileDescription),
            (NativeSoftwareIdentitySignalKind.WindowApplicationUserModelId,
                process.WindowApplicationUserModelId),
            (NativeSoftwareIdentitySignalKind.ApplicationUserModelId,
                process.ApplicationUserModelId),
            (NativeSoftwareIdentitySignalKind.WindowTitle, process.WindowTitle)
        };
        var signals = new List<NativeSoftwareIdentityKnownSignalInput>(6);
        foreach (var (kind, value) in signalValues)
        {
            var key = NativeSoftwareIdentityCatalogProjector.NormalizeTextKey(value);
            if (key.Length == 0)
            {
                continue;
            }
            var slice = AppendKey(keyBytes, key);
            signals.Add(new NativeSoftwareIdentityKnownSignalInput
            {
                StructSize = SizeOf<NativeSoftwareIdentityKnownSignalInput>(),
                Kind = (uint)kind,
                KeyOffset = slice.Offset,
                KeyLength = slice.Length,
                Flags = 0,
                ReservedU32 = 0
            });
        }
        var input = new NativeSoftwareIdentityKnownQueryInput
        {
            AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
            StructSize = SizeOf<NativeSoftwareIdentityKnownQueryInput>(),
            ConfigurationGeneration = workspace.Session.ConfigurationGeneration,
            CatalogGeneration = 1,
            QueryEpoch = NextQueryEpoch(),
            SignalCount = checked((uint)signals.Count),
            KeyByteCount = checked((uint)keyBytes.Count),
            ExecutablePathOffset = pathKey.Offset,
            ExecutablePathLength = pathKey.Length,
            ValidMask = (ulong)NativeSoftwareIdentityKnownQueryValidity.Required
                | (path is null
                    ? 0
                    : (ulong)NativeSoftwareIdentityKnownQueryValidity.ExecutablePath),
            Flags = 0
        };
        var status = workspace.Session.MatchKnown(
            in input,
            signals.ToArray(),
            keyBytes.ToArray(),
            out var output);
        if (status != NativeSoftwareIdentityStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native runtime source known query failed with {status}.");
        }
        return output.Status switch
        {
            (uint)NativeSoftwareIdentityMatchStatus.NoMatch =>
                NativeSourceCatalogObservation.NoMatch,
            (uint)NativeSoftwareIdentityMatchStatus.Conflict =>
                NativeSourceCatalogObservation.Conflicted,
            (uint)NativeSoftwareIdentityMatchStatus.Matched =>
                NativeSourceCatalogObservation.Matched(
                    output.Mode
                        == (uint)NativeSoftwareIdentityKnownMatchMode.LauncherManagedChild
                        ? CreateManagedChildAttribution(
                            process,
                            path,
                            keyBytes,
                            output)
                        : RequireRuntimeAttribution(output.EntryHandle),
                    output.EvidenceMask == 0 ? 1UL : output.EvidenceMask),
            _ => throw new InvalidOperationException(
                $"Native runtime known catalog returned unknown status {output.Status}.")
        };
    }

    private RuntimeSoftwareAttribution CreateManagedChildAttribution(
        RuntimeProcessIdentity process,
        string? path,
        IReadOnlyList<byte> keyBytes,
        NativeSoftwareIdentityKnownMatchOutput output)
    {
        if (path is null
            || output.DerivedRootByteLength == 0
            || output.DerivedRootByteLength > keyBytes.Count)
        {
            throw new InvalidOperationException(
                "Native runtime known catalog returned an invalid managed-child root.");
        }
        var bytes = keyBytes.Take(checked((int)output.DerivedRootByteLength)).ToArray();
        var root = Encoding.UTF8.GetString(bytes);
        var display = Path.GetFileName(root);
        return new RuntimeSoftwareAttribution(
            $"runtime-game:{output.DerivedIdentityFingerprintLow:x16}{output.DerivedIdentityFingerprintHigh:x16}",
            display,
            SoftwareKinds.Game,
            SoftwareText.Game,
            [root]);
    }

    private RuntimeSoftwareAttribution RequireRuntimeAttribution(ulong entryHandle)
        => projection.Payloads.RequireEntry(entryHandle).RuntimeAttribution
            ?? throw new InvalidOperationException(
                "Native runtime source catalog returned a non-runtime payload.");

    private ulong NextQueryEpoch()
    {
        var epoch = Interlocked.Increment(ref queryEpoch);
        return epoch > 0
            ? checked((ulong)epoch)
            : throw new InvalidOperationException(
                "Native runtime source catalog query epoch is exhausted.");
    }

    private static void AddExecutableKey(
        ICollection<(NativeSoftwareIdentityAliasKind Kind, string Key)> target,
        string? value)
    {
        var key = NativeSoftwareIdentityCatalogProjector.CanonicalExecutableName(value);
        if (key is not null)
        {
            target.Add((NativeSoftwareIdentityAliasKind.ExecutableName, key));
        }
    }

    private static (uint Offset, uint Length) AppendKey(
        ICollection<byte> target,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var offset = checked((uint)target.Count);
        foreach (var item in bytes)
        {
            target.Add(item);
        }
        return (offset, checked((uint)bytes.Length));
    }

    private static string? CleanMetadata(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static bool IsGenericProcessMetadata(string value, string processName)
        => value.Equals(processName, StringComparison.OrdinalIgnoreCase)
            || value.Equals(
                Path.GetFileNameWithoutExtension(processName),
                StringComparison.OrdinalIgnoreCase)
            || value.Equals("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
            || value.Equals(
                "Microsoft Windows Operating System",
                StringComparison.OrdinalIgnoreCase);

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());
}

internal readonly record struct NativeSourceCatalogObservation(
    bool Conflict,
    IReadOnlyList<NativeSourceCatalogCandidate> Candidates)
{
    internal static NativeSourceCatalogObservation NoMatch { get; } =
        new(false, []);

    internal static NativeSourceCatalogObservation Conflicted { get; } =
        new(true, []);

    internal static NativeSourceCatalogObservation Matched(
        RuntimeSoftwareAttribution attribution,
        ulong evidenceMask)
        => new(
            false,
            [new NativeSourceCatalogCandidate(attribution, evidenceMask)]);
}

internal readonly record struct NativeSourceCatalogCandidate(
    RuntimeSoftwareAttribution Attribution,
    ulong EvidenceMask);
