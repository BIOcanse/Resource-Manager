using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.SoftwareIdentity;

internal interface INativeSoftwareIdentityProcessObserver
{
    NativeCatalogProcessObservation ObserveProcess(RuntimeProcessIdentity process);
}

internal enum NativeCatalogProcessObservationStatus : byte
{
    NoMatch = 1,
    Matched = 2,
    Conflict = 3
}

internal readonly record struct NativeCatalogProcessObservation(
    NativeCatalogProcessObservationStatus Status,
    SoftwareIdentityCatalogEntry? Entry,
    ulong EvidenceMask);

public sealed partial class SoftwareIdentityCatalogCompiler :
    ISoftwareIdentityCatalog,
    INativeSoftwareIdentityProcessObserver,
    IDisposable
{
    private readonly NativeSoftwareIdentityCatalogWorkspace workspace;
    private readonly NativeSoftwareIdentityCatalogProjection projection;
    private long queryEpoch;
    private bool disposed;

    public SoftwareIdentityCatalogCompiler(
        SoftwareIdentityCatalogDocument document,
        CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plan);
        Version = ValidateVersion(document.Version);
        Entries = Array.AsReadOnly((document.Entries ?? []).ToArray());
        projection = NativeSoftwareIdentityCatalogProjector.ProjectBundled(document, plan);
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

    ~SoftwareIdentityCatalogCompiler() => Dispose(false);

    public string Version { get; }

    public IReadOnlyList<SoftwareIdentityCatalogEntry> Entries { get; }

    internal string ConfigurationSha256 { get; init; } = string.Empty;

    public SoftwareIdentityCatalogEntry? MatchInstalledSoftware(InstalledSoftwareEntry software)
    {
        ArgumentNullException.ThrowIfNull(software);
        var facts = new List<QueryFact>(6);
        AddTextFact(facts, NativeSoftwareIdentityAliasKind.InstalledName, software.Name);
        AddPackageFacts(facts, software.Id);
        AddPackageFacts(facts, software.RegistryPath);
        foreach (Match match in SteamAppIdRegex().Matches($"{software.Id}\n{software.RegistryPath}"))
        {
            if (ulong.TryParse(match.Groups[1].Value, out var appId) && appId != 0)
            {
                facts.Add(new QueryFact(NativeSoftwareIdentityAliasKind.SteamAppId, null, appId));
            }
        }
        return MatchEntry(NativeSoftwareIdentityMatchMode.InstalledSoftware, facts)?.CatalogEntry;
    }

    public SoftwareIdentityCatalogEntry? MatchProcess(RuntimeProcessIdentity process)
    {
        var observation = ObserveProcess(process);
        return observation.Status == NativeCatalogProcessObservationStatus.Matched
            ? observation.Entry
            : null;
    }

    NativeCatalogProcessObservation INativeSoftwareIdentityProcessObserver.ObserveProcess(
        RuntimeProcessIdentity process)
        => ObserveProcess(process);

    internal NativeCatalogProcessObservation ObserveProcess(RuntimeProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var result = Match(
            NativeSoftwareIdentityMatchMode.Process,
            CreateProcessFacts(process));
        return result.Output.Status switch
        {
            (uint)NativeSoftwareIdentityMatchStatus.NoMatch =>
                new NativeCatalogProcessObservation(
                    NativeCatalogProcessObservationStatus.NoMatch,
                    null,
                    0),
            (uint)NativeSoftwareIdentityMatchStatus.Conflict =>
                new NativeCatalogProcessObservation(
                    NativeCatalogProcessObservationStatus.Conflict,
                    null,
                    result.Output.EvidenceMask),
            (uint)NativeSoftwareIdentityMatchStatus.Matched =>
                new NativeCatalogProcessObservation(
                    NativeCatalogProcessObservationStatus.Matched,
                    projection.Payloads.RequireEntry(result.Output.EntryHandle).CatalogEntry
                        ?? throw new InvalidOperationException(
                            "Native bundled catalog returned a non-catalog payload."),
                    result.Output.EvidenceMask),
            _ => throw new InvalidOperationException(
                $"Native bundled catalog returned unknown status {result.Output.Status}.")
        };
    }

    public PortableSoftwareIdentityMatch? MatchPortableProcess(RuntimeProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var result = Match(
            NativeSoftwareIdentityMatchMode.PortableProcess,
            CreateProcessFacts(process));
        if (result.Output.Status != (uint)NativeSoftwareIdentityMatchStatus.Matched)
        {
            return null;
        }

        var entry = projection.Payloads.RequireEntry(result.Output.EntryHandle).CatalogEntry
            ?? throw new InvalidOperationException(
                "Native bundled catalog returned a non-catalog payload.");
        var confidence = result.Output.Confidence switch
        {
            (uint)NativeSoftwareIdentityMatchConfidence.Candidate =>
                PortableSoftwareIdentityConfidence.Candidate,
            (uint)NativeSoftwareIdentityMatchConfidence.Confirmed =>
                PortableSoftwareIdentityConfidence.Confirmed,
            _ => throw new InvalidOperationException(
                $"Native bundled catalog returned unknown confidence {result.Output.Confidence}.")
        };
        var evidence = new List<string>(2);
        if ((result.Output.EvidenceMask
            & (ulong)NativeSoftwareIdentityEvidence.ExecutableName) != 0)
        {
            evidence.Add("ExecutableName");
        }
        if ((result.Output.EvidenceMask
            & (ulong)NativeSoftwareIdentityEvidence.ProductName) != 0)
        {
            evidence.Add("ProductName");
        }
        return new PortableSoftwareIdentityMatch(entry, confidence, evidence);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private NativeSoftwareIdentityEntryPayload? MatchEntry(
        NativeSoftwareIdentityMatchMode mode,
        IReadOnlyList<QueryFact> facts)
    {
        var result = Match(mode, facts);
        return result.Output.Status switch
        {
            (uint)NativeSoftwareIdentityMatchStatus.NoMatch => null,
            (uint)NativeSoftwareIdentityMatchStatus.Conflict => null,
            (uint)NativeSoftwareIdentityMatchStatus.Matched =>
                projection.Payloads.RequireEntry(result.Output.EntryHandle),
            _ => throw new InvalidOperationException(
                $"Native bundled catalog returned unknown status {result.Output.Status}.")
        };
    }

    private NativeMatchResult Match(
        NativeSoftwareIdentityMatchMode mode,
        IReadOnlyList<QueryFact> facts)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var orderedFacts = facts
            .Distinct()
            .OrderBy(static fact => fact.Kind)
            .ThenBy(static fact => fact.NumericValue)
            .ThenBy(static fact => fact.Key, StringComparer.Ordinal)
            .ToArray();
        var factRows = new NativeSoftwareIdentityCatalogFactInput[orderedFacts.Length];
        var keyBytes = new List<byte>();
        for (var index = 0; index < orderedFacts.Length; index++)
        {
            var fact = orderedFacts[index];
            var key = fact.Key is null ? default : AppendKey(keyBytes, fact.Key);
            factRows[index] = new NativeSoftwareIdentityCatalogFactInput
            {
                StructSize = SizeOf<NativeSoftwareIdentityCatalogFactInput>(),
                Kind = (uint)fact.Kind,
                KeyOffset = key.Offset,
                KeyLength = key.Length,
                NumericValue = fact.NumericValue,
                Flags = 0,
                ReservedU32 = 0
            };
        }

        var input = new NativeSoftwareIdentityCatalogQueryInput
        {
            AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
            StructSize = SizeOf<NativeSoftwareIdentityCatalogQueryInput>(),
            ConfigurationGeneration = workspace.Session.ConfigurationGeneration,
            CatalogGeneration = 1,
            QueryEpoch = NextQueryEpoch(),
            Mode = (uint)mode,
            FactCount = checked((uint)factRows.Length),
            KeyByteCount = checked((uint)keyBytes.Count),
            Flags = 0,
            ValidMask = (ulong)NativeSoftwareIdentityCatalogQueryValidity.Required
        };
        var status = workspace.Session.Match(
            in input,
            factRows,
            keyBytes.ToArray(),
            out var output);
        if (status != NativeSoftwareIdentityStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native bundled software identity query failed with {status}.");
        }
        return new NativeMatchResult(output);
    }

    private static IReadOnlyList<QueryFact> CreateProcessFacts(RuntimeProcessIdentity process)
    {
        var facts = new List<QueryFact>(3);
        AddExecutableFact(facts, process.Name);
        AddExecutableFact(facts, process.ExecutablePath);
        AddTextFact(facts, NativeSoftwareIdentityAliasKind.ProductName, process.ProductName);
        return facts
            .Distinct()
            .ToArray();
    }

    private static void AddPackageFacts(ICollection<QueryFact> facts, string? value)
    {
        var direct = NativeSoftwareIdentityCatalogProjector.CanonicalTextKey(value);
        if (direct is not null)
        {
            facts.Add(new QueryFact(
                NativeSoftwareIdentityAliasKind.PackageIdentifier,
                direct,
                0));
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var leaf = value.Trim().TrimEnd('\\', '/');
        var separatorIndex = Math.Max(leaf.LastIndexOf('\\'), leaf.LastIndexOf('/'));
        if (separatorIndex >= 0)
        {
            leaf = leaf[(separatorIndex + 1)..];
        }
        var colonIndex = leaf.LastIndexOf(':');
        if (colonIndex >= 0)
        {
            leaf = leaf[(colonIndex + 1)..];
        }
        var canonical = NativeSoftwareIdentityCatalogProjector.CanonicalTextKey(leaf);
        if (canonical is not null && canonical != direct)
        {
            facts.Add(new QueryFact(
                NativeSoftwareIdentityAliasKind.PackageIdentifier,
                canonical,
                0));
        }
    }

    private static void AddExecutableFact(ICollection<QueryFact> facts, string? value)
    {
        var key = NativeSoftwareIdentityCatalogProjector.CanonicalExecutableName(value);
        if (key is not null)
        {
            facts.Add(new QueryFact(NativeSoftwareIdentityAliasKind.ExecutableName, key, 0));
        }
    }

    private static void AddTextFact(
        ICollection<QueryFact> facts,
        NativeSoftwareIdentityAliasKind kind,
        string? value)
    {
        var key = NativeSoftwareIdentityCatalogProjector.CanonicalTextKey(value);
        if (key is not null)
        {
            facts.Add(new QueryFact(kind, key, 0));
        }
    }

    private ulong NextQueryEpoch()
    {
        var value = Interlocked.Increment(ref queryEpoch);
        return value > 0
            ? checked((ulong)value)
            : throw new InvalidOperationException(
                "Native bundled software identity query epoch is exhausted.");
    }

    private void Dispose(bool disposing)
    {
        _ = disposing;
        if (disposed)
        {
            return;
        }
        disposed = true;
        workspace.Dispose();
    }

    private static string ValidateVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || !System.Version.TryParse(version, out var parsed)
            || parsed.Major < 1
            || parsed.Build < 0
            || parsed.Revision >= 0)
        {
            throw new InvalidDataException(
                "Software identity catalog version must use the '1.0.0' format.");
        }
        return version.Trim();
    }

    private static (uint Offset, uint Length) AppendKey(List<byte> target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var offset = checked((uint)target.Count);
        target.AddRange(bytes);
        return (offset, checked((uint)bytes.Length));
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private readonly record struct QueryFact(
        NativeSoftwareIdentityAliasKind Kind,
        string? Key,
        ulong NumericValue);

    private readonly record struct NativeMatchResult(
        NativeSoftwareIdentityCatalogMatchOutput Output);

    [GeneratedRegex(
        @"steam(?:[-_\\/ ]+)(?:app|appid)(?:[-_\\/ ]+)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamAppIdRegex();
}
