using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

internal sealed record NativePortableSoftwareObserveEnvelope(
    NativePortableSoftwareObserveInput Input,
    ReadOnlyMemory<byte> KeyBytes);

internal sealed record NativePortableSoftwareConfirmRootEnvelope(
    NativePortableSoftwareConfirmRootInput Input,
    ReadOnlyMemory<byte> KeyBytes);

internal sealed class NativePortableSoftwareRegistryProjection
{
    private const string CatalogPrefix = "catalog:";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly NativePortableSoftwareRegistryPayloadCatalog payloadCatalog;
    private readonly uint maximumExecutablePathByteCount;
    private readonly uint maximumRootPathByteCount;
    private readonly uint maximumFutureSkewMilliseconds;

    public NativePortableSoftwareRegistryProjection(
        NativePortableSoftwareRegistryPayloadCatalog payloadCatalog,
        uint maximumExecutablePathByteCount,
        uint maximumRootPathByteCount,
        uint maximumFutureSkewMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(payloadCatalog);
        if (maximumExecutablePathByteCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExecutablePathByteCount));
        }

        if (maximumRootPathByteCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRootPathByteCount));
        }

        this.payloadCatalog = payloadCatalog;
        this.maximumExecutablePathByteCount = maximumExecutablePathByteCount;
        this.maximumRootPathByteCount = maximumRootPathByteCount;
        this.maximumFutureSkewMilliseconds = maximumFutureSkewMilliseconds;
    }

    public NativePortableSoftwareObserveEnvelope ProjectObserve(
        PortableSoftwareObservation observation,
        ulong configurationGeneration,
        ulong operationEpoch,
        DateTimeOffset commandUtc,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateGenerationAndEpoch(configurationGeneration, operationEpoch);
        var commandUtcMilliseconds = ToNonNegativeUnixMilliseconds(commandUtc, nameof(commandUtc));
        var observedAtUtcMilliseconds = ToNonNegativeUnixMilliseconds(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtcMilliseconds > SaturatingAdd(commandUtcMilliseconds, maximumFutureSkewMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAtUtc),
                "Portable software observation exceeds the configured future-skew limit.");
        }

        var softwareId = CanonicalSoftwareId(observation.SoftwareId);
        var catalogEntryId = CanonicalRequiredText(observation.CatalogEntryId, nameof(observation.CatalogEntryId));
        if (!softwareId.Equals(CatalogPrefix + catalogEntryId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Portable software id must exactly identify its catalog entry after canonicalization.",
                nameof(observation));
        }

        var displayName = RequiredText(observation.Name, nameof(observation.Name));
        var softwareKind = CanonicalRequiredText(observation.Kind, nameof(observation.Kind));
        var executablePath = CanonicalPath(observation.ExecutablePath, nameof(observation.ExecutablePath));
        var rootPath = CanonicalPath(observation.SuggestedRootPath, nameof(observation.SuggestedRootPath));
        if (!IsSameOrUnder(executablePath, rootPath))
        {
            throw new ArgumentException(
                "Portable software executable path must be the root path or a descendant of it.",
                nameof(observation));
        }

        var executableBytes = EncodeBoundedPath(
            executablePath,
            maximumExecutablePathByteCount,
            nameof(observation.ExecutablePath));
        var rootBytes = EncodeBoundedPath(
            rootPath,
            maximumRootPathByteCount,
            nameof(observation.SuggestedRootPath));
        var keyBytes = Join(executableBytes, rootBytes);
        var handles = payloadCatalog.GetOrAddBatch(
        [
            new(NativePortableSoftwarePayloadKind.SoftwareId, softwareId),
            new(NativePortableSoftwarePayloadKind.CatalogEntryId, catalogEntryId),
            new(NativePortableSoftwarePayloadKind.DisplayName, displayName),
            new(NativePortableSoftwarePayloadKind.SoftwareKind, softwareKind),
            new(NativePortableSoftwarePayloadKind.ExecutablePath, executablePath),
            new(NativePortableSoftwarePayloadKind.RootPath, rootPath)
        ]);

        var input = new NativePortableSoftwareObserveInput
        {
            AbiVersion = NativePortableSoftwareRegistryAbi.Version,
            StructSize = (uint)Marshal.SizeOf<NativePortableSoftwareObserveInput>(),
            ConfigurationGeneration = configurationGeneration,
            OperationEpoch = operationEpoch,
            CommandUtcMilliseconds = commandUtcMilliseconds,
            ObservedAtUtcMilliseconds = observedAtUtcMilliseconds,
            SoftwareHandle = handles[0],
            CatalogEntryHandle = handles[1],
            DisplayNameHandle = handles[2],
            SoftwareKindHandle = handles[3],
            PathHandle = handles[4],
            RootHandle = handles[5],
            ExecutablePathOffset = 0,
            ExecutablePathLength = (uint)executableBytes.Length,
            RootPathOffset = (uint)executableBytes.Length,
            RootPathLength = (uint)rootBytes.Length,
            PathFlags = (uint)(
                (observation.IdentityConfirmed ? NativePortableSoftwarePathFlags.IdentityConfirmed : 0)
                | (observation.RootPathConfirmed ? NativePortableSoftwarePathFlags.RootConfirmed : 0)),
            ValidMask = (ulong)NativePortableSoftwareObserveValidity.Required
        };
        return new NativePortableSoftwareObserveEnvelope(input, keyBytes);
    }

    public NativePortableSoftwareConfirmRootEnvelope ProjectConfirmRoot(
        string softwareId,
        string rootPath,
        ulong configurationGeneration,
        ulong operationEpoch,
        DateTimeOffset commandUtc)
    {
        ValidateGenerationAndEpoch(configurationGeneration, operationEpoch);
        var canonicalSoftwareId = CanonicalSoftwareId(softwareId);
        var canonicalRootPath = CanonicalPath(rootPath, nameof(rootPath));
        var rootBytes = EncodeBoundedPath(
            canonicalRootPath,
            maximumRootPathByteCount,
            nameof(rootPath));
        var handles = payloadCatalog.GetOrAddBatch(
        [
            new(NativePortableSoftwarePayloadKind.SoftwareId, canonicalSoftwareId),
            new(NativePortableSoftwarePayloadKind.RootPath, canonicalRootPath)
        ]);
        var input = new NativePortableSoftwareConfirmRootInput
        {
            AbiVersion = NativePortableSoftwareRegistryAbi.Version,
            StructSize = (uint)Marshal.SizeOf<NativePortableSoftwareConfirmRootInput>(),
            ConfigurationGeneration = configurationGeneration,
            OperationEpoch = operationEpoch,
            CommandUtcMilliseconds = ToNonNegativeUnixMilliseconds(commandUtc, nameof(commandUtc)),
            SoftwareHandle = handles[0],
            RootHandle = handles[1],
            RootPathOffset = 0,
            RootPathLength = (uint)rootBytes.Length,
            ValidMask = (ulong)NativePortableSoftwareConfirmRootValidity.Required
        };
        return new NativePortableSoftwareConfirmRootEnvelope(input, rootBytes);
    }

    public NativePortableSoftwareMarkMissingInput ProjectMarkMissing(
        string softwareId,
        string executablePath,
        ulong configurationGeneration,
        ulong operationEpoch,
        DateTimeOffset commandUtc)
    {
        ValidateGenerationAndEpoch(configurationGeneration, operationEpoch);
        var canonicalSoftwareId = CanonicalSoftwareId(softwareId);
        var canonicalExecutablePath = CanonicalPath(executablePath, nameof(executablePath));
        _ = EncodeBoundedPath(
            canonicalExecutablePath,
            maximumExecutablePathByteCount,
            nameof(executablePath));
        var handles = payloadCatalog.GetOrAddBatch(
        [
            new(NativePortableSoftwarePayloadKind.SoftwareId, canonicalSoftwareId),
            new(NativePortableSoftwarePayloadKind.ExecutablePath, canonicalExecutablePath)
        ]);
        return new NativePortableSoftwareMarkMissingInput
        {
            AbiVersion = NativePortableSoftwareRegistryAbi.Version,
            StructSize = (uint)Marshal.SizeOf<NativePortableSoftwareMarkMissingInput>(),
            ConfigurationGeneration = configurationGeneration,
            OperationEpoch = operationEpoch,
            CommandUtcMilliseconds = ToNonNegativeUnixMilliseconds(commandUtc, nameof(commandUtc)),
            SoftwareHandle = handles[0],
            PathHandle = handles[1],
            ValidMask = (ulong)NativePortableSoftwareMarkMissingValidity.Required
        };
    }

    internal static string CanonicalPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Portable software path cannot be empty.", parameterName);
        }

        var candidate = path.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1];
        }
        else if (candidate[0] == '"' || candidate[^1] == '"')
        {
            throw new ArgumentException("Portable software path has unbalanced quotes.", parameterName);
        }

        if (candidate.AsSpan().Contains('\0')
            || candidate.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || candidate.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(candidate))
        {
            throw new ArgumentException("Portable software path must be an absolute Win32 drive or UNC path.", parameterName);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Portable software path is invalid.", parameterName, ex);
        }

        var canonical = fullPath.Replace('\\', '/').ToLowerInvariant();
        var isDriveRoot = canonical.Length == 3
            && canonical[0] is >= 'a' and <= 'z'
            && canonical[1] == ':'
            && canonical[2] == '/';
        if (!isDriveRoot)
        {
            canonical = canonical.TrimEnd('/');
        }

        if (!IsCanonicalDrivePath(canonical) && !IsCanonicalUncPath(canonical))
        {
            throw new ArgumentException("Portable software path cannot be represented by the native canonical path contract.", parameterName);
        }

        _ = StrictUtf8.GetBytes(canonical);
        return canonical;
    }

    private static string CanonicalSoftwareId(string value)
    {
        var canonical = CanonicalRequiredText(value, nameof(value));
        if (!canonical.StartsWith(CatalogPrefix, StringComparison.Ordinal)
            || canonical.Length == CatalogPrefix.Length)
        {
            throw new ArgumentException("Portable software id must use the catalog: namespace.", nameof(value));
        }

        return canonical;
    }

    private static string CanonicalRequiredText(string value, string parameterName)
        => RequiredText(value, parameterName).ToLowerInvariant();

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Portable software text cannot be empty.", parameterName);
        }

        var result = value.Trim();
        if (result.AsSpan().Contains('\0'))
        {
            throw new ArgumentException("Portable software text cannot contain NUL.", parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(result);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Portable software text must be valid Unicode.", parameterName, ex);
        }

        return result;
    }

    private static byte[] EncodeBoundedPath(string path, uint maximumByteCount, string parameterName)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(path);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Portable software path must be valid Unicode.", parameterName, ex);
        }

        if ((uint)bytes.Length > maximumByteCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Portable software path exceeds the configured UTF-8 byte limit.");
        }

        return bytes;
    }

    private static byte[] Join(byte[] first, byte[] second)
    {
        var joined = new byte[checked(first.Length + second.Length)];
        first.CopyTo(joined, 0);
        second.CopyTo(joined, first.Length);
        return joined;
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        if (candidate.Equals(root, StringComparison.Ordinal))
        {
            return true;
        }

        return root.EndsWith('/')
            ? candidate.StartsWith(root, StringComparison.Ordinal)
            : candidate.StartsWith(root + '/', StringComparison.Ordinal);
    }

    private static bool IsCanonicalDrivePath(string path)
    {
        if (path.Length < 3 || path[0] is < 'a' or > 'z' || path[1] != ':' || path[2] != '/')
        {
            return false;
        }

        return ValidSegments(path, 3, minimumSegmentCount: 0);
    }

    private static bool IsCanonicalUncPath(string path)
    {
        if (path.Length < 5 || path[0] != '/' || path[1] != '/' || path[2] == '/')
        {
            return false;
        }

        return ValidSegments(path, 2, minimumSegmentCount: 2);
    }

    private static bool ValidSegments(string path, int start, int minimumSegmentCount)
    {
        if (path.AsSpan().Contains('\\') || path.AsSpan().Contains('\0'))
        {
            return false;
        }

        var segmentCount = 0;
        var segmentStart = start;
        while (segmentStart < path.Length)
        {
            var separator = path.IndexOf('/', segmentStart);
            var segmentEnd = separator < 0 ? path.Length : separator;
            var segment = path.AsSpan(segmentStart, segmentEnd - segmentStart);
            if (segment.Length == 0
                || segment.SequenceEqual(".".AsSpan())
                || segment.SequenceEqual("..".AsSpan())
                || segment.Contains(':'))
            {
                return false;
            }

            segmentCount++;
            if (separator < 0)
            {
                break;
            }

            segmentStart = separator + 1;
        }

        return segmentCount >= minimumSegmentCount;
    }

    private static void ValidateGenerationAndEpoch(ulong configurationGeneration, ulong operationEpoch)
    {
        if (configurationGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configurationGeneration));
        }

        if (operationEpoch == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationEpoch));
        }
    }

    private static long ToNonNegativeUnixMilliseconds(DateTimeOffset value, string parameterName)
    {
        var milliseconds = value.ToUnixTimeMilliseconds();
        return milliseconds >= 0
            ? milliseconds
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static long SaturatingAdd(long value, uint addition)
        => value > long.MaxValue - addition ? long.MaxValue : value + addition;
}
