using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal enum HostManagerOperationPayloadRole : uint
{
    Domain = 1,
    Kind = 2,
    Title = 3,
    Request = 4,
    Result = 5,
    Error = 6,
    ProgressStage = 7,
    ProgressMessage = 8,
    Checkpoint = 9,
    EffectExpectedBefore = 10,
    EffectExpectedAfter = 11,
    EffectObservation = 12
}

internal sealed record HostManagerOperationPayloadEntry(
    uint SchemaId,
    uint SchemaVersion,
    HostManagerOperationPayloadRole Role,
    NativeOperationHandle128 Handle,
    NativeOperationHandle128 SessionInstanceId,
    NativeOperationHandle128 OperationId,
    NativeOperationHandle128 AttemptToken,
    byte[] Payload,
    byte[] PayloadSha256);

internal sealed class HostManagerOperationPayloadCatalog
{
    private readonly Dictionary<NativeOperationHandle128, HostManagerOperationPayloadEntry>
        entries = [];
    private ulong payloadByteCount;

    internal int Count => entries.Count;

    internal ulong PayloadByteCount => payloadByteCount;

    internal HostManagerOperationPayloadEntry Add(
        uint schemaId,
        uint schemaVersion,
        HostManagerOperationPayloadRole role,
        NativeOperationHandle128 sessionInstanceId,
        NativeOperationHandle128 operationId,
        NativeOperationHandle128 attemptToken,
        ReadOnlySpan<byte> payload)
    {
        ValidateBinding(
            schemaId,
            schemaVersion,
            role,
            sessionInstanceId,
            operationId,
            attemptToken,
            payload.Length);
        var digest = ComputeDigest(
            schemaId,
            schemaVersion,
            role,
            sessionInstanceId,
            operationId,
            attemptToken,
            payload);
        var handle = ReadHandle(digest.AsSpan(0, 16));
        if (handle.IsZero)
        {
            handle = ReadHandle(digest.AsSpan(16, 16));
            if (handle.IsZero)
            {
                throw new CryptographicException(
                    "The canonical operation payload produced a zero handle.");
            }
        }

        var candidate = new HostManagerOperationPayloadEntry(
            schemaId,
            schemaVersion,
            role,
            handle,
            sessionInstanceId,
            operationId,
            attemptToken,
            payload.ToArray(),
            digest);
        if (entries.TryGetValue(handle, out var existing))
        {
            if (!Equivalent(existing, candidate))
            {
                throw new CryptographicException(
                    "A 128-bit operation payload handle collision was detected.");
            }
            return existing;
        }

        entries.Add(handle, candidate);
        payloadByteCount = checked(payloadByteCount + (ulong)payload.Length);
        return candidate;
    }

    internal HostManagerOperationPayloadEntry Require(NativeOperationHandle128 handle)
        => entries.TryGetValue(handle, out var entry)
            ? entry
            : throw new InvalidDataException(
                "A referenced operation payload is absent from the canonical catalog.");

    internal bool TryGet(
        NativeOperationHandle128 handle,
        out HostManagerOperationPayloadEntry entry)
        => entries.TryGetValue(handle, out entry!);

    internal HostManagerOperationPayloadEntry[] ExportCanonical()
        => entries.Values
            .OrderBy(static entry => entry.Handle.High)
            .ThenBy(static entry => entry.Handle.Low)
            .ToArray();

    internal HostManagerOperationPayloadCatalog Clone()
    {
        var result = new HostManagerOperationPayloadCatalog();
        foreach (var entry in entries.Values)
        {
            result.Import(entry);
        }
        return result;
    }

    internal void Import(HostManagerOperationPayloadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var imported = Add(
            entry.SchemaId,
            entry.SchemaVersion,
            entry.Role,
            entry.SessionInstanceId,
            entry.OperationId,
            entry.AttemptToken,
            entry.Payload);
        if (imported.Handle != entry.Handle
            || !CryptographicOperations.FixedTimeEquals(
                imported.PayloadSha256,
                entry.PayloadSha256))
        {
            throw new InvalidDataException(
                "The imported operation payload identity is non-canonical.");
        }
    }

    internal void ReplaceWith(HostManagerOperationPayloadCatalog source)
    {
        ArgumentNullException.ThrowIfNull(source);
        entries.Clear();
        payloadByteCount = 0;
        foreach (var entry in source.entries.Values)
        {
            Import(entry);
        }
    }

    internal void PruneTo(IReadOnlySet<NativeOperationHandle128> liveHandles)
    {
        ArgumentNullException.ThrowIfNull(liveHandles);
        foreach (var handle in entries.Keys.ToArray())
        {
            if (liveHandles.Contains(handle))
            {
                continue;
            }
            payloadByteCount -= checked((ulong)entries[handle].Payload.Length);
            entries.Remove(handle);
        }
    }

    internal static HashSet<NativeOperationHandle128> CollectNativeRoots(
        IReadOnlyList<NativeOperationOutput> records)
    {
        var roots = new HashSet<NativeOperationHandle128>();
        foreach (var record in records)
        {
            AddIfNonzero(roots, record.DomainId);
            AddIfNonzero(roots, record.KindHandle);
            AddIfNonzero(roots, record.TitleHandle);
            AddIfNonzero(roots, record.RequestHandle);
            AddIfNonzero(roots, record.ResultHandle);
            AddIfNonzero(roots, record.ErrorHandle);
            AddIfNonzero(roots, record.StageHandle);
            AddIfNonzero(roots, record.MessageHandle);
            AddIfNonzero(roots, record.CheckpointHandle);
        }
        return roots;
    }

    private static void AddIfNonzero(
        HashSet<NativeOperationHandle128> roots,
        NativeOperationHandle128 handle)
    {
        if (!handle.IsZero)
        {
            roots.Add(handle);
        }
    }

    private static void ValidateBinding(
        uint schemaId,
        uint schemaVersion,
        HostManagerOperationPayloadRole role,
        NativeOperationHandle128 sessionInstanceId,
        NativeOperationHandle128 operationId,
        NativeOperationHandle128 attemptToken,
        int payloadLength)
    {
        if (schemaId == 0
            || schemaVersion == 0
            || !Enum.IsDefined(role)
            || sessionInstanceId.IsZero
            || payloadLength < 0)
        {
            throw new ArgumentException("The operation payload contract is invalid.");
        }

        var valid = role switch
        {
            HostManagerOperationPayloadRole.Domain =>
                operationId.IsZero && attemptToken.IsZero,
            HostManagerOperationPayloadRole.Kind
                or HostManagerOperationPayloadRole.Title
                or HostManagerOperationPayloadRole.Request =>
                !operationId.IsZero && attemptToken.IsZero,
            _ => !operationId.IsZero && !attemptToken.IsZero
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The operation payload binding does not match its role.");
        }
    }

    private static byte[] ComputeDigest(
        uint schemaId,
        uint schemaVersion,
        HostManagerOperationPayloadRole role,
        NativeOperationHandle128 sessionInstanceId,
        NativeOperationHandle128 operationId,
        NativeOperationHandle128 attemptToken,
        ReadOnlySpan<byte> payload)
    {
        Span<byte> prefix = stackalloc byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, schemaId);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[4..], schemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[8..], (uint)role);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[12..], 0);
        WriteHandle(prefix[16..], sessionInstanceId);
        WriteHandle(prefix[32..], operationId);
        WriteHandle(prefix[48..], attemptToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static NativeOperationHandle128 ReadHandle(ReadOnlySpan<byte> source)
        => new(
            BinaryPrimitives.ReadUInt64LittleEndian(source),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..]));

    internal static void WriteHandle(
        Span<byte> destination,
        NativeOperationHandle128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value.High);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], value.Low);
    }

    private static bool Equivalent(
        HostManagerOperationPayloadEntry left,
        HostManagerOperationPayloadEntry right)
        => left.SchemaId == right.SchemaId
            && left.SchemaVersion == right.SchemaVersion
            && left.Role == right.Role
            && left.Handle == right.Handle
            && left.SessionInstanceId == right.SessionInstanceId
            && left.OperationId == right.OperationId
            && left.AttemptToken == right.AttemptToken
            && left.Payload.AsSpan().SequenceEqual(right.Payload)
            && CryptographicOperations.FixedTimeEquals(
                left.PayloadSha256,
                right.PayloadSha256);
}
