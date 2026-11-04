using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class JsonHostManagerRollbackStateStore
{
    // Only startup migration reads retired formats. The current wire and store have no text readers.
    private static bool IsLegacyStateImage(byte[] image)
    {
        using var document = JsonDocument.Parse(image, new JsonDocumentOptions
        {
            MaxDepth = HostManagerRollbackStateEnvelopeCodec.MaximumJsonDepth
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        var state = root;
        if (root.TryGetProperty("state", out var nested))
        {
            if (!root.TryGetProperty("magic", out var magic) || magic.ValueKind != JsonValueKind.String
                || magic.GetString() != HostManagerRollbackStateEnvelopeCodec.Magic
                || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetUInt32(out var schemaVersion) || schemaVersion != HostManagerRollbackStateEnvelopeCodec.SchemaVersion)
                return false;
            state = nested;
        }
        return state.ValueKind == JsonValueKind.Object
            && state.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number)
            && number is 1 or 2;
    }

    private static HostManagerRollbackStateDocument MigrateDisplayTextState(byte[] image, string sourcePath, out ulong revision, out bool converted)
    {
        revision = 0;
        converted = false;
        try
        {
            var root = JsonNode.Parse(image, documentOptions: new() { MaxDepth = HostManagerRollbackStateEnvelopeCodec.MaximumJsonDepth })!.AsObject();
            var state = (root["state"] ?? root).AsObject();
            var version = state["version"]!.GetValue<int>();
            if (version is not (1 or 2)) throw new InvalidDataException("Unsupported rollback migration version.");
            if (root["state"] is not null)
            {
                // Validate the old envelope against its exact compact payload, before removing any field.
                using var original = JsonDocument.Parse(image);
                var payload = Encoding.UTF8.GetBytes(original.RootElement.GetProperty("state").GetRawText());
                if (root["magic"]?.GetValue<string>() != HostManagerRollbackStateEnvelopeCodec.Magic
                    || root["schemaVersion"]?.GetValue<uint>() != HostManagerRollbackStateEnvelopeCodec.SchemaVersion
                    || root["payloadLength"]?.GetValue<int>() != payload.Length
                    || root["revision"]?.GetValue<ulong>() is not > 0)
                    throw new InvalidDataException("Invalid legacy rollback envelope.");
                Span<byte> header = stackalloc byte[16];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, HostManagerRollbackStateEnvelopeCodec.SchemaVersion);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header[4..], root["revision"]!.GetValue<ulong>());
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[12..], payload.Length);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(header);
                hash.AppendData(payload);
                revision = root["revision"]!.GetValue<ulong>();
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(root["checksumSha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid legacy rollback checksum.");
            }
            foreach (var retired in new[] { "pendingChanges", "appliedTargets", "nativeActionAttempts" })
            {
                if (state[retired] is JsonArray { Count: > 0 })
                    throw new InvalidDataException("Retired rollback action authority cannot be converted.");
                state.Remove(retired);
            }
            state.Remove("message");
            state["version"] = HostManagerRollbackStateDocument.CurrentVersion;
            state["coordinator"] = null; // Old prose is never parsed into facts.
            foreach (var node in state["appliedPlacements"]!.AsArray())
            {
                var placement = node!.AsObject();
                placement.Remove("displayName");
                // Exact process identity and name come from recovery metadata, never the display string.
                var target = placement["targetId"]!.GetValue<string>().Split(':');
                if (target.Length == 3 && target[0] == "process"
                    && int.TryParse(target[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    && ulong.TryParse(target[2], NumberStyles.None, CultureInfo.InvariantCulture, out var start) && pid > 0 && start > 0)
                {
                    placement["processId"] = pid;
                    placement["processStartKey"] = start.ToString(CultureInfo.InvariantCulture);
                }
                foreach (var record in placement["records"]!.AsArray())
                {
                    if (record?["metadata"]?["processName"] is JsonValue name)
                    {
                        placement["processName"] = name.GetValue<string>();
                        break;
                    }
                }
            }
            var migrated = ReadStoredDocument(state.Deserialize<HostManagerRollbackStateDocument>(JsonOptions));
            converted = true;
            return migrated;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or FormatException or OverflowException or NullReferenceException or ArgumentException)
        {
            // Explicit one-time migration outcome, with the exact original preserved by the caller.
            System.Diagnostics.Trace.TraceWarning("Rollback migration {0} starts empty: {1}", sourcePath, error.Message);
            revision = 0;
            return HostManagerRollbackStateDocument.Empty;
        }
    }

    private static string? PreserveExactFile(string path)
    {
        if (!File.Exists(path)) return null;
        return OptimizationDisplayTextBackup.Preserve(path,
            ResourceManager.App.Infrastructure.NativeCore.WindowsNativeAtomicFileCommitter.ReadCommittedBytes(path));
    }
}
