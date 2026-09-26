using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.SoftwareIdentity;

internal sealed record NativeSoftwareIdentityCatalogAliasDefinition(
    NativeSoftwareIdentityAliasKind Kind,
    string? Key,
    ulong NumericValue,
    NativeSoftwareIdentityAliasFlags Flags = NativeSoftwareIdentityAliasFlags.None);

internal sealed record NativeSoftwareIdentityCatalogEntryDefinition(
    NativeSoftwareIdentityEntryPayload Payload,
    string AttributionId,
    string DisplayName,
    string Kind,
    uint Source,
    string PrimaryName,
    IReadOnlyList<NativeSoftwareIdentityCatalogAliasDefinition> Aliases,
    IReadOnlyList<string> RootPaths);

internal sealed record NativeSoftwareIdentityCatalogProjection(
    NativeSoftwareIdentityPayloadCatalog Payloads,
    NativeSoftwareIdentityCatalogEntryInput[] Entries,
    NativeSoftwareIdentityCatalogAliasInput[] Aliases,
    NativeSoftwareIdentityCatalogRootInput[] Roots,
    byte[] KeyBytes,
    uint ProhibitedAliasCount,
    uint LauncherTokenCount,
    uint ManagedChildRuleCount)
{
    internal void Load(NativeSoftwareIdentityCatalogSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var input = new NativeSoftwareIdentityCatalogReplaceInput
        {
            AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentityCatalogReplaceInput>()),
            ConfigurationGeneration = session.ConfigurationGeneration,
            CatalogGeneration = 1,
            OperationEpoch = 1,
            EntryCount = checked((uint)Entries.Length),
            AliasCount = checked((uint)Aliases.Length),
            RootCount = checked((uint)Roots.Length),
            KeyByteCount = checked((uint)KeyBytes.Length),
            ProhibitedAliasCount = ProhibitedAliasCount,
            LauncherTokenCount = LauncherTokenCount,
            ManagedChildRuleCount = ManagedChildRuleCount,
            ReservedU32 = 0,
            ValidMask = (ulong)NativeSoftwareIdentityCatalogReplaceValidity.Required
        };
        var status = session.Replace(in input, Entries, Aliases, Roots, KeyBytes);
        if (status != NativeSoftwareIdentityStatus.Ok)
        {
            throw new InvalidDataException(
                $"Native software identity catalog rejected the projected catalog with {status}.");
        }
    }
}

internal static partial class NativeSoftwareIdentityCatalogProjector
{
    internal static NativeSoftwareIdentityCatalogProjection ProjectBundled(
        SoftwareIdentityCatalogDocument document,
        CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateVersion(document.Version);
        var payloads = new NativeSoftwareIdentityPayloadCatalog();
        var definitions = (document.Entries ?? [])
            .Select(entry => CreateBundledDefinition(entry, payloads))
            .ToArray();
        return Project(definitions, payloads, plan);
    }

    internal static NativeSoftwareIdentityCatalogProjection ProjectRuntimeKnown(
        IReadOnlyList<SoftwareRecord> records,
        CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(records);
        var payloads = new NativeSoftwareIdentityPayloadCatalog();
        var definitions = records
            .Select(record =>
            {
                var attribution = new RuntimeSoftwareAttribution(
                    record.Id,
                    record.Name,
                    record.Kind,
                    record.DisplayKind,
                    record.RootPaths
                        .Select(CanonicalPath)
                        .Where(static value => value is not null)
                        .Select(static value => value!)
                        .ToArray());
                return new NativeSoftwareIdentityCatalogEntryDefinition(
                    NativeSoftwareIdentityEntryPayload.FromRuntime(attribution),
                    attribution.Id,
                    attribution.Name,
                    attribution.Kind,
                    NativeSoftwareIdentitySources.RuntimeKnown,
                    NormalizeTextKey(attribution.Name),
                    [
                        new NativeSoftwareIdentityCatalogAliasDefinition(
                            NativeSoftwareIdentityAliasKind.InstalledName,
                            CanonicalTextKey($"runtime-known:{attribution.Id}"),
                            0)
                    ],
                    attribution.RootPaths);
            })
            .ToArray();
        return Project(definitions, payloads, plan);
    }

    internal static NativeSoftwareIdentityCatalogProjection Project(
        IReadOnlyList<NativeSoftwareIdentityCatalogEntryDefinition> definitions,
        NativeSoftwareIdentityPayloadCatalog payloads,
        CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Software identity catalog plan is not published.");
        }

        var stagedEntries = new List<StagedEntry>(definitions.Count);
        var stagedAliases = new List<StagedAlias>();
        var stagedRoots = new List<StagedRoot>();
        foreach (var definition in definitions)
        {
            ValidateDefinition(definition);
            var entryHandle = payloads.AddEntry(definition.Payload);
            var attributionIdHandle = payloads.GetOrAddString("catalog-attribution-id", definition.AttributionId);
            var displayNameHandle = payloads.GetOrAddString("catalog-display-name", definition.DisplayName);
            var primaryName = RequireNormalizedTextKey(definition.PrimaryName, "catalog primary name");
            stagedEntries.Add(new StagedEntry(
                entryHandle,
                attributionIdHandle,
                displayNameHandle,
                definition.Source,
                NativeSoftwareKindMap.ToNative(definition.Kind),
                primaryName));
            foreach (var alias in definition.Aliases)
            {
                stagedAliases.Add(CreateAlias(entryHandle, alias));
            }
            var canonicalRoots = definition.RootPaths
                .Select(rootPath => CanonicalPath(rootPath)
                    ?? throw new InvalidDataException(
                        $"Software identity '{definition.AttributionId}' contains an invalid root path."))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var canonical in canonicalRoots)
            {
                stagedRoots.Add(new StagedRoot(
                    payloads.AddUniqueString(canonical),
                    entryHandle,
                    canonical));
            }
        }

        foreach (var value in plan.Recreate.ProhibitedExecutableAliases)
        {
            stagedAliases.Add(new StagedAlias(
                NativeSoftwareIdentityAliasKind.ExecutableName,
                0,
                RequireCanonicalTextKey(value, "prohibited executable alias"),
                0,
                NativeSoftwareIdentityAliasFlags.Prohibited));
        }
        foreach (var value in plan.Recreate.LauncherTokens)
        {
            stagedAliases.Add(new StagedAlias(
                NativeSoftwareIdentityAliasKind.LauncherToken,
                0,
                RequireNormalizedTextKey(value, "launcher token"),
                0,
                NativeSoftwareIdentityAliasFlags.None));
        }
        foreach (var value in plan.Recreate.ManagedChildSegments)
        {
            stagedAliases.Add(new StagedAlias(
                NativeSoftwareIdentityAliasKind.ManagedChildSegment,
                0,
                RequireNormalizedTextKey(value, "managed child segment"),
                0,
                NativeSoftwareIdentityAliasFlags.None));
        }

        var orderedEntries = stagedEntries.OrderBy(static value => value.EntryHandle).ToArray();
        var orderedRoots = stagedRoots.OrderBy(static value => value.RootHandle).ToArray();
        var orderedAliases = stagedAliases
            .OrderBy(static value => value.Kind)
            .ThenBy(static value => value.NumericValue)
            .ThenBy(static value => value.Key, StringComparer.Ordinal)
            .ThenBy(static value => value.EntryHandle)
            .ToArray();

        var keyBytes = new List<byte>();
        var entries = orderedEntries
            .Select(value =>
            {
                var key = AppendKey(keyBytes, value.PrimaryName);
                return new NativeSoftwareIdentityCatalogEntryInput
                {
                    StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentityCatalogEntryInput>()),
                    Flags = 0,
                    EntryHandle = value.EntryHandle,
                    AttributionIdHandle = value.AttributionIdHandle,
                    DisplayNameHandle = value.DisplayNameHandle,
                    Source = value.Source,
                    SoftwareKind = value.SoftwareKind,
                    PrimaryNameOffset = key.Offset,
                    PrimaryNameLength = key.Length
                };
            })
            .ToArray();
        var roots = orderedRoots
            .Select(value =>
            {
                var key = AppendKey(keyBytes, value.Path);
                return new NativeSoftwareIdentityCatalogRootInput
                {
                    StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentityCatalogRootInput>()),
                    Flags = 0,
                    RootHandle = value.RootHandle,
                    EntryHandle = value.EntryHandle,
                    KeyOffset = key.Offset,
                    KeyLength = key.Length
                };
            })
            .ToArray();
        var aliases = orderedAliases
            .Select(value =>
            {
                var key = value.Kind == NativeSoftwareIdentityAliasKind.SteamAppId
                    ? default
                    : AppendKey(keyBytes, value.Key!);
                return new NativeSoftwareIdentityCatalogAliasInput
                {
                    StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentityCatalogAliasInput>()),
                    Kind = (uint)value.Kind,
                    EntryHandle = value.EntryHandle,
                    KeyOffset = key.Offset,
                    KeyLength = key.Length,
                    NumericValue = value.NumericValue,
                    Flags = (uint)value.Flags,
                    ReservedU32 = 0
                };
            })
            .ToArray();

        var capacity = plan.Recreate.Capacity;
        if (entries.Length > capacity.MaximumEntryCount
            || aliases.Length > capacity.MaximumAliasCount
            || roots.Length > capacity.MaximumRootCount
            || keyBytes.Count > capacity.MaximumCatalogKeyByteCount)
        {
            throw new InvalidDataException(
                "Projected software identity catalog exceeds the explicit native capacity.");
        }

        return new NativeSoftwareIdentityCatalogProjection(
            payloads,
            entries,
            aliases,
            roots,
            keyBytes.ToArray(),
            checked((uint)plan.Recreate.ProhibitedExecutableAliases.Length),
            checked((uint)plan.Recreate.LauncherTokens.Length),
            checked((uint)plan.Recreate.ManagedChildSegments.Length));
    }

    internal static string? CanonicalExecutableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var fileName = Path.GetFileName(value.Trim().Trim('"'));
        return CanonicalTextKey(Path.GetFileNameWithoutExtension(fileName));
    }

    internal static string? CanonicalTextKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return WhitespaceRegex()
            .Replace(value.Trim(), " ")
            .ToLowerInvariant();
    }

    internal static string NormalizeTextKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }
        return builder.ToString();
    }

    internal static string? CanonicalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .Replace('\\', '/')
                .ToLowerInvariant();
            var root = Path.GetPathRoot(path.Replace('/', Path.DirectorySeparatorChar));
            var canonicalRoot = root?.Replace('\\', '/').ToLowerInvariant();
            return canonicalRoot is not null && path.Equals(canonicalRoot, StringComparison.Ordinal)
                ? path
                : path.TrimEnd('/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static NativeSoftwareIdentityCatalogEntryDefinition CreateBundledDefinition(
        SoftwareIdentityCatalogEntry entry,
        NativeSoftwareIdentityPayloadCatalog payloads)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Id)
            || string.IsNullOrWhiteSpace(entry.DisplayName)
            || string.IsNullOrWhiteSpace(entry.Source))
        {
            throw new InvalidDataException("Software identity catalog entry metadata is incomplete.");
        }
        _ = NativeSoftwareKindMap.ToNative(entry.Kind);
        var aliases = new List<NativeSoftwareIdentityCatalogAliasDefinition>();
        aliases.AddRange((entry.SteamAppIds ?? [])
            .Select(static value => new NativeSoftwareIdentityCatalogAliasDefinition(
                NativeSoftwareIdentityAliasKind.SteamAppId,
                null,
                checked((ulong)value))));
        aliases.AddRange((entry.PackageIdentifiers ?? [])
            .Select(static value => TextAlias(NativeSoftwareIdentityAliasKind.PackageIdentifier, value)));
        aliases.AddRange((entry.InstalledNames ?? [])
            .Select(static value => TextAlias(NativeSoftwareIdentityAliasKind.InstalledName, value)));
        aliases.AddRange((entry.ExecutableNames ?? [])
            .Select(value => new NativeSoftwareIdentityCatalogAliasDefinition(
                NativeSoftwareIdentityAliasKind.ExecutableName,
                CanonicalExecutableName(value)
                    ?? throw new InvalidDataException(
                        $"Software identity '{entry.Id}' has an invalid executable alias."),
                0)));
        aliases.AddRange((entry.ProductNames ?? [])
            .Select(static value => TextAlias(NativeSoftwareIdentityAliasKind.ProductName, value)));
        if (aliases.Count == 0)
        {
            throw new InvalidDataException(
                $"Software identity '{entry.Id}' has no exact identity evidence.");
        }
        return new NativeSoftwareIdentityCatalogEntryDefinition(
            NativeSoftwareIdentityEntryPayload.FromCatalog(entry),
            entry.Id.Trim(),
            entry.DisplayName.Trim(),
            entry.Kind,
            NativeSoftwareIdentitySources.BundledCatalog,
            NormalizeTextKey(entry.DisplayName),
            aliases,
            []);
    }

    private static NativeSoftwareIdentityCatalogAliasDefinition TextAlias(
        NativeSoftwareIdentityAliasKind kind,
        string value)
        => new(
            kind,
            CanonicalTextKey(value)
                ?? throw new InvalidDataException("Software identity catalog contains an empty text alias."),
            0);

    private static StagedAlias CreateAlias(
        ulong entryHandle,
        NativeSoftwareIdentityCatalogAliasDefinition alias)
    {
        if (alias.Kind == NativeSoftwareIdentityAliasKind.SteamAppId)
        {
            if (alias.NumericValue == 0 || alias.Key is not null || alias.Flags != 0)
            {
                throw new InvalidDataException("Software identity Steam AppID alias is invalid.");
            }
            return new StagedAlias(alias.Kind, entryHandle, null, alias.NumericValue, alias.Flags);
        }
        if (alias.NumericValue != 0)
        {
            throw new InvalidDataException("Software identity text alias has a numeric payload.");
        }
        return new StagedAlias(
            alias.Kind,
            entryHandle,
            alias.Kind is NativeSoftwareIdentityAliasKind.LauncherToken
                or NativeSoftwareIdentityAliasKind.ManagedChildSegment
                ? RequireNormalizedTextKey(alias.Key, "catalog rule")
                : RequireCanonicalTextKey(alias.Key, "catalog alias"),
            0,
            alias.Flags);
    }

    private static void ValidateDefinition(NativeSoftwareIdentityCatalogEntryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Payload);
        if (string.IsNullOrWhiteSpace(definition.AttributionId)
            || string.IsNullOrWhiteSpace(definition.DisplayName)
            || definition.Source == 0
            || definition.Aliases.Count == 0)
        {
            throw new InvalidDataException("Software identity catalog definition is incomplete.");
        }
    }

    private static string RequireCanonicalTextKey(string? value, string field)
    {
        if (string.IsNullOrEmpty(value)
            || value[0] == ' '
            || value[^1] == ' '
            || value.Contains('\0')
            || value.Any(static character => character is >= 'A' and <= 'Z'))
        {
            throw new InvalidDataException($"{field} is not a canonical lowercase UTF-8 key.");
        }
        return value;
    }

    private static string RequireNormalizedTextKey(string? value, string field)
    {
        var result = RequireCanonicalTextKey(value, field);
        if (result.Any(static character =>
            character < 0x80 && !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException($"{field} is not a normalized alphanumeric key.");
        }
        return result;
    }

    private static (uint Offset, uint Length) AppendKey(List<byte> target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var offset = checked((uint)target.Count);
        target.AddRange(bytes);
        return (offset, checked((uint)bytes.Length));
    }

    private static void ValidateVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Version.TryParse(value, out var parsed)
            || parsed.Major < 1
            || parsed.Build < 0
            || parsed.Revision >= 0)
        {
            throw new InvalidDataException(
                "Software identity catalog version must use the '1.0.0' format.");
        }
    }

    private sealed record StagedEntry(
        ulong EntryHandle,
        ulong AttributionIdHandle,
        ulong DisplayNameHandle,
        uint Source,
        uint SoftwareKind,
        string PrimaryName);

    private sealed record StagedAlias(
        NativeSoftwareIdentityAliasKind Kind,
        ulong EntryHandle,
        string? Key,
        ulong NumericValue,
        NativeSoftwareIdentityAliasFlags Flags);

    private sealed record StagedRoot(
        ulong RootHandle,
        ulong EntryHandle,
        string Path);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
