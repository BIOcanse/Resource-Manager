using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Collections.Immutable;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Application.ProcessAttribution;

public readonly record struct RuntimeProcessAttributionSourceVersion(
    int ItemCount,
    ulong FirstHash,
    ulong SecondHash);

public readonly record struct RuntimeProcessAttributionCatalogVersion(
    RuntimeProcessAttributionSourceVersion Software,
    RuntimeProcessAttributionSourceVersion Adapted,
    RuntimeProcessAttributionSourceVersion Controlled,
    string CatalogConfigurationSha256,
    string ResolutionConfigurationSha256,
    RuntimeProcessAttributionSourceVersion WindowsGames = default);

public readonly record struct RuntimeProcessAttributionCatalogRevision(
    long Generation, RuntimeProcessAttributionCatalogVersion Version, string BundledCatalogVersion);

public sealed class RuntimeProcessAttributionCatalog : IDisposable
{
    private readonly IRuntimePackageIdentityResolver packageIdentityResolver;
    private readonly IRuntimeServiceIdentityResolver serviceIdentityResolver;
    private readonly IRuntimeRootIdentityResolver rootIdentityResolver;
    private readonly IRuntimeSystemProcessClassifier systemClassifier;
    private readonly ISoftwareIdentityCatalog softwareIdentityCatalog;
    private readonly HostManagerSoftwareIdentityOwner softwareIdentityOwner;
    private readonly string bundledCatalogVersion;
    private bool disposed;
    private readonly ImmutableArray<(string Id, string Kind)> software;
    private readonly object baseScoreGate = new();
    private CompiledBaseScorePlan? baseScorePlan;
    private ImmutableArray<SoftwareBaseScore> softwareBaseScores = [];

    private RuntimeProcessAttributionCatalog(
        long generation,
        RuntimeProcessAttributionCatalogVersion version,
        IReadOnlyList<SoftwareRecord> softwareRecords,
        RuntimeProcessAttributionPipeline pipeline,
        IRuntimePackageIdentityResolver packageIdentityResolver,
        IRuntimeServiceIdentityResolver serviceIdentityResolver,
        IRuntimeRootIdentityResolver rootIdentityResolver,
        IRuntimeSystemProcessClassifier systemClassifier,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        HostManagerSoftwareIdentityOwner softwareIdentityOwner,
        string bundledCatalogVersion)
    {
        Generation = generation;
        Version = version;
        software = softwareRecords
            .OrderBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static record => (record.Id, record.Kind))
            .ToImmutableArray();
        Pipeline = pipeline;
        this.packageIdentityResolver = packageIdentityResolver;
        this.serviceIdentityResolver = serviceIdentityResolver;
        this.rootIdentityResolver = rootIdentityResolver;
        this.systemClassifier = systemClassifier;
        this.softwareIdentityCatalog = softwareIdentityCatalog;
        this.softwareIdentityOwner = softwareIdentityOwner;
        this.bundledCatalogVersion = bundledCatalogVersion;
    }

    public long Generation { get; }

    public RuntimeProcessAttributionCatalogVersion Version { get; }

    internal RuntimeProcessAttributionCatalogRevision Revision => new(Generation, Version, bundledCatalogVersion);

    internal string BundledCatalogVersion => bundledCatalogVersion;

    public RuntimeProcessAttributionPipeline Pipeline { get; }

    public ImmutableArray<SoftwareBaseScore> GetSoftwareBaseScores(CompiledBaseScorePlan plan)
    {
        lock (baseScoreGate)
        {
            if (!ReferenceEquals(baseScorePlan, plan))
            {
                softwareBaseScores = software.Select(item => new SoftwareBaseScore(
                    item.Id, plan.ResolveBaseScore(item.Id, item.Kind, processKey: null)))
                    .ToImmutableArray();
                baseScorePlan = plan;
            }
            return softwareBaseScores;
        }
    }

    public static RuntimeProcessAttributionCatalog CreateOrReuse(
        RuntimeProcessAttributionCatalog? current,
        IReadOnlyList<SoftwareRecord> softwareRecords,
        IReadOnlyList<AdapterSoftwareRegistration> adapterRegistrations,
        IReadOnlyList<ControlledSoftwareRegistration> controlledRegistrations,
        IRuntimePackageIdentityResolver packageIdentityResolver,
        IRuntimeServiceIdentityResolver serviceIdentityResolver,
        IRuntimeRootIdentityResolver rootIdentityResolver,
        IRuntimeSystemProcessClassifier systemClassifier,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        HostManagerSoftwareIdentityOwner softwareIdentityOwner,
        IReadOnlySet<string>? windowsGamePaths = null,
        RuntimeProcessAttributionCatalogRevision? previousRevision = null)
    {
        using var lease = softwareIdentityOwner.Acquire();
        var bundledCatalogVersion = softwareIdentityCatalog.Version;
        var version = RuntimeProcessAttributionCatalogVersionFactory.Create(
            softwareRecords,
            adapterRegistrations,
            controlledRegistrations,
            lease.CatalogPlan.ConfigurationSha256,
            lease.ResolutionPlan.ConfigurationSha256,
            windowsGamePaths);
        if (current is not null
            && current.Version == version
            && current.bundledCatalogVersion.Equals(bundledCatalogVersion, StringComparison.Ordinal)
            && current.UsesResolvers(
                packageIdentityResolver,
                serviceIdentityResolver,
                rootIdentityResolver,
                systemClassifier,
                softwareIdentityCatalog,
                softwareIdentityOwner))
        {
            return current;
        }

        var generation = current is not null ? checked(current.Generation + 1)
            : previousRevision is not { } revision ? 1
            : revision.Version == version && revision.BundledCatalogVersion == bundledCatalogVersion
                ? revision.Generation : checked(revision.Generation + 1);
        var pipeline = new RuntimeProcessAttributionPipeline(
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
        try
        {
            return new RuntimeProcessAttributionCatalog(
                generation,
                version,
                softwareRecords,
                pipeline,
                packageIdentityResolver,
                serviceIdentityResolver,
                rootIdentityResolver,
                systemClassifier,
                softwareIdentityCatalog,
                softwareIdentityOwner,
                bundledCatalogVersion);
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Pipeline.Dispose();
    }

    private bool UsesResolvers(
        IRuntimePackageIdentityResolver packageResolver,
        IRuntimeServiceIdentityResolver serviceResolver,
        IRuntimeRootIdentityResolver rootResolver,
        IRuntimeSystemProcessClassifier classifier,
        ISoftwareIdentityCatalog identityCatalog,
        HostManagerSoftwareIdentityOwner identityOwner)
    {
        return ReferenceEquals(packageIdentityResolver, packageResolver)
            && ReferenceEquals(serviceIdentityResolver, serviceResolver)
            && ReferenceEquals(rootIdentityResolver, rootResolver)
            && ReferenceEquals(systemClassifier, classifier)
            && ReferenceEquals(softwareIdentityCatalog, identityCatalog)
            && ReferenceEquals(softwareIdentityOwner, identityOwner);
    }
}

internal static class RuntimeProcessAttributionCatalogVersionFactory
{
    internal static RuntimeProcessAttributionCatalogVersion Create(
        IReadOnlyList<SoftwareRecord> softwareRecords,
        IReadOnlyList<AdapterSoftwareRegistration> adapterRegistrations,
        IReadOnlyList<ControlledSoftwareRegistration> controlledRegistrations,
        string catalogConfigurationSha256,
        string resolutionConfigurationSha256,
        IReadOnlySet<string>? windowsGamePaths = null)
    {
        return new RuntimeProcessAttributionCatalogVersion(
            CreateSoftwareVersion(softwareRecords),
            CreateAdaptedVersion(adapterRegistrations),
            CreateControlledVersion(controlledRegistrations),
            catalogConfigurationSha256,
            resolutionConfigurationSha256,
            CreateWindowsGamesVersion(windowsGamePaths));
    }

    private static RuntimeProcessAttributionSourceVersion CreateWindowsGamesVersion(IReadOnlySet<string>? paths)
    {
        var hash = new StableVersionHash(paths?.Count ?? 0);
        foreach (var path in (paths ?? new HashSet<string>()).Order(StringComparer.Ordinal)) hash.Add(path);
        return hash.ToVersion(paths?.Count ?? 0);
    }

    private static RuntimeProcessAttributionSourceVersion CreateSoftwareVersion(
        IReadOnlyList<SoftwareRecord> records)
    {
        var hash = new StableVersionHash(records.Count);
        foreach (var record in records)
        {
            hash.Add(record.Id);
            hash.Add(record.Name);
            hash.Add(record.Kind);
            hash.Add(record.HasExplicitKind ? 1 : 0);
            hash.Add(record.SourceSoftwareId);
            hash.Add(record.RootPaths.Count);
            foreach (var rootPath in record.RootPaths)
            {
                hash.Add(rootPath);
            }
        }

        return hash.ToVersion(records.Count);
    }

    internal static RuntimeProcessAttributionSourceVersion CreateAdaptedVersion(
        IReadOnlyList<AdapterSoftwareRegistration> registrations)
    {
        var hash = new StableVersionHash(registrations.Count);
        foreach (var registration in registrations)
        {
            hash.Add(registration.Id);
            hash.Add(registration.DisplayName);
            hash.Add(registration.ProgramRootPaths.Count);
            foreach (var rootPath in registration.ProgramRootPaths)
            {
                hash.Add(rootPath);
            }

            hash.Add(registration.Processes.Count);
            foreach (var process in registration.Processes)
            {
                hash.Add(process.Name);
                hash.Add(process.ProcessId);
                hash.Add(process.ExecutablePath);
            }
        }

        return hash.ToVersion(registrations.Count);
    }

    internal static RuntimeProcessAttributionSourceVersion CreateControlledVersion(
        IReadOnlyList<ControlledSoftwareRegistration> registrations)
    {
        var hash = new StableVersionHash(registrations.Count);
        foreach (var registration in registrations)
        {
            hash.Add(registration.Id);
            hash.Add(registration.Command);
            hash.Add(registration.Software.Count);
            foreach (var softwareName in registration.Software)
            {
                hash.Add(softwareName);
            }

            hash.Add(registration.ProgramRootPaths.Count);
            foreach (var rootPath in registration.ProgramRootPaths)
            {
                hash.Add(rootPath);
            }

            hash.Add(registration.Processes.Count);
            foreach (var process in registration.Processes)
            {
                hash.Add(process.Name);
                hash.Add(process.ProcessId);
                hash.Add(process.ExecutablePath);
            }
        }

        return hash.ToVersion(registrations.Count);
    }

    private struct StableVersionHash
    {
        private const ulong FirstOffset = 14695981039346656037UL;
        private const ulong FirstPrime = 1099511628211UL;
        private const ulong SecondOffset = 7809847782465536322UL;
        private const ulong SecondPrime = 14029467366897019727UL;

        private ulong first;
        private ulong second;

        internal StableVersionHash(int itemCount)
        {
            first = FirstOffset;
            second = SecondOffset;
            Add(itemCount);
        }

        internal void Add(string? value)
        {
            if (value is null)
            {
                Add(-1);
                return;
            }

            Add(value.Length);
            foreach (var character in value)
            {
                Mix(character);
            }
        }

        internal void Add(int? value)
        {
            Add(value.HasValue ? 1 : 0);
            if (value.HasValue)
            {
                Add(value.Value);
            }
        }

        internal void Add(int value)
        {
            Mix(unchecked((ulong)(long)value));
        }

        internal void Add(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes);
            foreach (var item in bytes)
            {
                Mix(item);
            }
        }

        internal RuntimeProcessAttributionSourceVersion ToVersion(int itemCount)
        {
            return new RuntimeProcessAttributionSourceVersion(itemCount, first, second);
        }

        private void Mix(ulong value)
        {
            first = unchecked((first ^ value) * FirstPrime);
            second = unchecked((second ^ RotateLeft(value + 0x9E3779B97F4A7C15UL, 23)) * SecondPrime);
        }

        private static ulong RotateLeft(ulong value, int offset)
        {
            return (value << offset) | (value >> (64 - offset));
        }
    }
}
