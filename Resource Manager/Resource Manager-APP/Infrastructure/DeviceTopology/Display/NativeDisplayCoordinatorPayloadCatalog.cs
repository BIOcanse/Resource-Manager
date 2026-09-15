using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal enum NativeDisplayPayloadKind : uint
{
    DisplayPath = 1,
    OemConnectorProfile = 2,
    Evidence = 3
}

internal sealed record NativeDisplayPayloadEntry(
    NativeDisplayHandle128 Handle,
    NativeDisplayPayloadKind Kind,
    byte[] Payload);

internal sealed class NativeDisplayCoordinatorPayloadCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<NativeDisplayHandle128, NativeDisplayPayloadEntry> entries = [];

    internal NativeDisplayHandle128 AddDisplayPath(DeviceTopologyDisplayPath value)
        => Add(NativeDisplayPayloadKind.DisplayPath, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    internal NativeDisplayHandle128 AddOemProfile(DeviceTopologyOemDisplayConnectorProfile value)
        => Add(
            NativeDisplayPayloadKind.OemConnectorProfile,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    internal NativeDisplayHandle128 AddEvidence(ReadOnlySpan<byte> value)
        => Add(NativeDisplayPayloadKind.Evidence, value.ToArray());

    internal DeviceTopologyDisplayPath? ResolveDisplayPath(NativeDisplayHandle128 handle)
        => Resolve<DeviceTopologyDisplayPath>(handle, NativeDisplayPayloadKind.DisplayPath);

    internal DeviceTopologyOemDisplayConnectorProfile? ResolveOemProfile(
        NativeDisplayHandle128 handle)
        => Resolve<DeviceTopologyOemDisplayConnectorProfile>(
            handle,
            NativeDisplayPayloadKind.OemConnectorProfile);

    internal IReadOnlyList<NativeDisplayPayloadEntry> Export()
        => entries.Values
            .OrderBy(static entry => entry.Handle.High)
            .ThenBy(static entry => entry.Handle.Low)
            .Select(static entry => entry with { Payload = entry.Payload.ToArray() })
            .ToArray();

    internal NativeDisplayCoordinatorPayloadCatalog Clone()
    {
        var clone = new NativeDisplayCoordinatorPayloadCatalog();
        foreach (var entry in entries)
        {
            clone.entries.Add(entry.Key, entry.Value);
        }
        return clone;
    }

    internal void Import(IEnumerable<NativeDisplayPayloadEntry> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var entry in source)
        {
            if (entry.Handle.IsZero
                || !Enum.IsDefined(entry.Kind)
                || entry.Payload.Length == 0
                || !CreateHandle(entry.Kind, entry.Payload).Equals(entry.Handle))
            {
                throw new InvalidDataException(
                    "The display-coordinator payload catalog contains an invalid entry.");
            }
            // 上一版写下的载荷形状可能已经变了（例如某个字段从字符串改成消息码）。
            // 这类条目在这里丢掉，而不是等到解析时把宿主拖崩。
            if (!CanDeserialize(entry))
            {
                continue;
            }
            if (entries.TryGetValue(entry.Handle, out var existing)
                && (existing.Kind != entry.Kind
                    || !existing.Payload.AsSpan().SequenceEqual(entry.Payload)))
            {
                throw new InvalidDataException(
                    "The display-coordinator payload catalog contains a handle collision.");
            }
            entries[entry.Handle] = entry with { Payload = entry.Payload.ToArray() };
        }
    }

    private static bool CanDeserialize(NativeDisplayPayloadEntry entry)
    {
        try
        {
            return entry.Kind switch
            {
                NativeDisplayPayloadKind.DisplayPath =>
                    JsonSerializer.Deserialize<DeviceTopologyDisplayPath>(entry.Payload, JsonOptions) is not null,
                NativeDisplayPayloadKind.OemConnectorProfile =>
                    JsonSerializer.Deserialize<DeviceTopologyOemDisplayConnectorProfile>(
                        entry.Payload,
                        JsonOptions) is not null,
                _ => true
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private NativeDisplayHandle128 Add(NativeDisplayPayloadKind kind, byte[] payload)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("Display payloads must not be empty.", nameof(payload));
        }
        var handle = CreateHandle(kind, payload);
        if (entries.TryGetValue(handle, out var existing))
        {
            if (existing.Kind != kind || !existing.Payload.AsSpan().SequenceEqual(payload))
            {
                throw new InvalidOperationException(
                    "A display payload handle collision was detected.");
            }
            return handle;
        }

        entries.Add(handle, new NativeDisplayPayloadEntry(handle, kind, payload));
        return handle;
    }

    private T? Resolve<T>(NativeDisplayHandle128 handle, NativeDisplayPayloadKind kind)
    {
        if (handle.IsZero
            || !entries.TryGetValue(handle, out var entry)
            || entry.Kind != kind)
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(entry.Payload, JsonOptions);
    }

    private static NativeDisplayHandle128 CreateHandle(
        NativeDisplayPayloadKind kind,
        ReadOnlySpan<byte> payload)
    {
        var source = new byte[checked(sizeof(uint) + payload.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(source, (uint)kind);
        payload.CopyTo(source.AsSpan(sizeof(uint)));
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(source, digest);
        var result = new NativeDisplayHandle128
        {
            High = BinaryPrimitives.ReadUInt64LittleEndian(digest),
            Low = BinaryPrimitives.ReadUInt64LittleEndian(digest[sizeof(ulong)..])
        };
        return result.IsZero
            ? new NativeDisplayHandle128 { Low = 1 }
            : result;
    }
}
