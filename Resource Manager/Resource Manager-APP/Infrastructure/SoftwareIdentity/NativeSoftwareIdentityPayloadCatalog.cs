using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;

namespace ResourceManager.App.Infrastructure.SoftwareIdentity;

internal static class NativeSoftwareIdentitySources
{
    internal const uint BundledCatalog = 1;
    internal const uint RuntimeKnown = 2;
    internal const uint Adapted = 3;
    internal const uint Controlled = 4;
    internal const uint ResourceManagerSelf = 5;
}

internal static class NativeRuntimeAttributionSources
{
    internal const uint Adapted = 1;
    internal const uint Controlled = 2;
    internal const uint ResourceManagerSelf = 3;
    internal const uint Package = 4;
    internal const uint Service = 5;
    internal const uint KnownSoftware = 6;
    internal const uint BundledCatalog = 7;
    internal const uint RuntimeProduct = 8;
    internal const uint RuntimeRoot = 9;
    internal const uint System = 10;
}

internal sealed record NativeSoftwareIdentityEntryPayload(
    SoftwareIdentityCatalogEntry? CatalogEntry,
    RuntimeSoftwareAttribution? RuntimeAttribution)
{
    internal static NativeSoftwareIdentityEntryPayload FromCatalog(
        SoftwareIdentityCatalogEntry entry)
        => new(entry, null);

    internal static NativeSoftwareIdentityEntryPayload FromRuntime(
        RuntimeSoftwareAttribution attribution)
        => new(null, attribution);
}

internal sealed class NativeSoftwareIdentityPayloadCatalog
{
    private readonly Dictionary<PayloadKey, ulong> handles = [];
    private readonly Dictionary<ulong, string> strings = [];
    private readonly Dictionary<ulong, NativeSoftwareIdentityEntryPayload> entries = [];
    private readonly Dictionary<ulong, RuntimeSoftwareAttribution> attributions = [];
    private ulong nextHandle = 1;

    internal ulong AddEntry(NativeSoftwareIdentityEntryPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var handle = Allocate();
        entries.Add(handle, payload);
        return handle;
    }

    internal ulong GetOrAddString(string scope, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(value);
        var key = new PayloadKey(scope, value);
        if (handles.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var handle = Allocate();
        handles.Add(key, handle);
        strings.Add(handle, value);
        return handle;
    }

    internal ulong AddUniqueString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var handle = Allocate();
        strings.Add(handle, value);
        return handle;
    }

    internal NativeSoftwareIdentityObservationPayload GetOrAddAttribution(
        RuntimeSoftwareAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        var identityHandle = GetOrAddString("attribution-id", attribution.Id);
        if (attributions.TryGetValue(identityHandle, out var existing)
            && !AttributionEquals(existing, attribution))
        {
            throw new InvalidDataException(
                $"Runtime attribution id '{attribution.Id}' maps to conflicting payloads.");
        }
        attributions[identityHandle] = attribution;

        var displayNameHandle = GetOrAddString("attribution-display-name", attribution.Name);
        var rootHandle = attribution.RootPaths.Count == 0
            ? 0
            : GetOrAddString("attribution-root", attribution.RootPaths[0]);
        return new NativeSoftwareIdentityObservationPayload(
            identityHandle,
            displayNameHandle,
            NativeSoftwareKindMap.ToNative(attribution.Kind),
            rootHandle);
    }

    internal NativeSoftwareIdentityEntryPayload RequireEntry(ulong handle)
        => entries.TryGetValue(handle, out var payload)
            ? payload
            : throw new InvalidOperationException(
                $"Native software identity returned unknown entry handle {handle}.");

    internal string RequireString(ulong handle)
        => strings.TryGetValue(handle, out var value)
            ? value
            : throw new InvalidOperationException(
                $"Native software identity returned unknown string handle {handle}.");

    internal RuntimeSoftwareAttribution RequireAttribution(ulong handle)
        => attributions.TryGetValue(handle, out var attribution)
            ? attribution
            : throw new InvalidOperationException(
                $"Native software identity returned unknown attribution handle {handle}.");

    private ulong Allocate()
    {
        if (nextHandle == 0)
        {
            throw new InvalidOperationException("Native software identity payload handle space is exhausted.");
        }
        return nextHandle++;
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

    private readonly record struct PayloadKey(string Scope, string Value);
}

internal readonly record struct NativeSoftwareIdentityObservationPayload(
    ulong IdentityHandle,
    ulong DisplayNameHandle,
    uint SoftwareKind,
    ulong RootHandle);

internal static class NativeSoftwareKindMap
{
    internal static uint ToNative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Equals(SoftwareKinds.Adapted, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (value.Equals(SoftwareKinds.Controlled, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        if (value.Equals(SoftwareKinds.Managed, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }
        if (value.Equals(SoftwareKinds.Game, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }
        if (value.Equals(SoftwareKinds.HighPerformance, StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }
        if (value.Equals(SoftwareKinds.Other, StringComparison.OrdinalIgnoreCase))
        {
            return 7;
        }
        if (value.Equals(SoftwareKinds.WindowsSystem, StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }
        if (value.Equals(SoftwareKinds.WindowsComponent, StringComparison.OrdinalIgnoreCase))
        {
            return 9;
        }
        if (value.Equals(SoftwareKinds.WindowsService, StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }
        if (value.Equals(SoftwareKinds.RuntimePackage, StringComparison.OrdinalIgnoreCase))
        {
            return 11;
        }
        if (value.Equals(SoftwareKinds.RuntimeProduct, StringComparison.OrdinalIgnoreCase))
        {
            return 12;
        }
        if (value.Equals(SoftwareKinds.RuntimeRoot, StringComparison.OrdinalIgnoreCase))
        {
            return 13;
        }
        if (value.Equals(SoftwareKinds.Unattributed, StringComparison.OrdinalIgnoreCase))
        {
            return 14;
        }
        throw new InvalidDataException($"Unsupported software kind '{value}'.");
    }

    internal static string ToManaged(uint value)
        => value switch
        {
            2 => SoftwareKinds.Adapted,
            3 => SoftwareKinds.Controlled,
            4 => SoftwareKinds.Managed,
            5 => SoftwareKinds.Game,
            6 => SoftwareKinds.HighPerformance,
            7 => SoftwareKinds.Other,
            8 => SoftwareKinds.WindowsSystem,
            9 => SoftwareKinds.WindowsComponent,
            10 => SoftwareKinds.WindowsService,
            11 => SoftwareKinds.RuntimePackage,
            12 => SoftwareKinds.RuntimeProduct,
            13 => SoftwareKinds.RuntimeRoot,
            14 => SoftwareKinds.Unattributed,
            _ => throw new InvalidOperationException(
                $"Native software identity returned unknown software kind {value}.")
        };
}
