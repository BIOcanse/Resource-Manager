using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class StrictHostManagerProfileLoader(HostManagerProfileSource source)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    internal LoadedHostManagerProfile Load()
    {
        ValidateSource(source);
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream(source.ManifestResourceName)
            ?? throw new InvalidOperationException(
                $"Host Manager profile resource '{source.ManifestResourceName}' was not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidDataException(
                $"Host Manager profile '{source.DisplayName}' is empty.");
        }

        return LoadBytes(ExpandMetricDefaults(bytes), source.DisplayName);
    }

    // Explicit default expansion happens before strict full-profile loading and before caller overrides.
    internal static byte[] ReadExpandedDefaultBytes()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly.GetManifestResourceStream(
            "ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        return ExpandMetricDefaults(buffer.ToArray());
    }

    internal static byte[] ExpandMetricDefaults(ReadOnlySpan<byte> bytes)
    {
        ValidateNoDuplicateProperties(bytes);
        var root = JsonNode.Parse(bytes)?.AsObject()
            ?? throw new InvalidDataException("Host Manager default profile must be an object.");
        const string reference = "$metric_snapshot_defaults";
        var build = root["build_specialize"]?.AsObject();
        var recreate = root["host_recreate"]?.AsObject();
        var hot = root["hot_publish"]?.AsObject();
        var limits = build?["capacity_limits"]?.AsObject();
        if (limits?["metric_snapshot"] is not JsonObject metricBuild || !metricBuild.ContainsKey(reference))
            return bytes.ToArray(); // Fully explicit embedded profiles already satisfy the original contract.
        var defaultsBytes = WindowsMetricConfiguration.ReadDefaultProfileBytes();
        _ = WindowsMetricConfiguration.LoadBytes(defaultsBytes); // Validate the same full metric/PDH contract consumed by independent defaults.
        var defaults = JsonNode.Parse(defaultsBytes)!.AsObject();
        if (root["profile_revision"]!.GetValue<int>() != defaults["profile_revision"]!.GetValue<int>())
            throw new InvalidDataException("Host and metric default revisions must match before explicit expansion.");
        static JsonNode Clone(JsonNode? value) => value?.DeepClone() ?? throw new InvalidDataException("A metric default value is missing.");
        static void RequireReference(JsonNode? node, string expected, bool allowsPersistence = false)
        {
            if (node is not JsonObject obj || obj["$metric_snapshot_defaults"]?.GetValue<string>() != expected
                || obj.Count != (allowsPersistence ? 2 : 1)
                || (allowsPersistence && obj["persistence_relative_path"] is null))
                throw new InvalidDataException($"Metric default reference '{expected}' is invalid or has unknown properties.");
        }
        RequireReference(metricBuild, "build_capacity");
        RequireReference(recreate?["metric_snapshot"], "recreate", true);
        RequireReference(hot?["metric_snapshot"], "hot_publish");
        RequireReference(build?["pdh_collector_abi_version"], "pdh.abi_version");
        RequireReference(recreate?["pdh_collector"], "pdh.recreate");
        RequireReference(hot?["pdh_collector"], "pdh.hot_publish");
        var path = Clone(recreate!["metric_snapshot"]!["persistence_relative_path"]);
        limits!["metric_snapshot"] = Clone(defaults["build_capacity"]);
        var expandedRecreate = Clone(defaults["recreate"]).AsObject();
        // Preserve the original visible recreate property order, including Host-only persistence placement.
        var hostRecreate = new JsonObject { ["capacity"] = Clone(expandedRecreate["capacity"]), ["persistence_relative_path"] = path };
        foreach (var property in expandedRecreate.Where(static property => property.Key != "capacity"))
            hostRecreate.Add(property.Key, Clone(property.Value));
        recreate["metric_snapshot"] = hostRecreate;
        hot!["metric_snapshot"] = Clone(defaults["hot_publish"]);
        var pdh = defaults["pdh"]!.AsObject();
        build!["pdh_collector_abi_version"] = Clone(pdh["abi_version"]);
        recreate["pdh_collector"] = new JsonObject { ["baseline_reset_interval_ms"] = Clone(pdh["baseline_reset_interval_ms"]) };
        hot["pdh_collector"] = new JsonObject { ["frame_reuse_window_ms"] = Clone(pdh["frame_reuse_window_ms"]),
            ["last_good_lifetime_ms"] = Clone(pdh["last_good_lifetime_ms"]) };
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    internal static LoadedHostManagerProfile LoadBytes(
        ReadOnlySpan<byte> bytes,
        string sourceDisplayName)
    {
        if (string.IsNullOrWhiteSpace(sourceDisplayName))
        {
            throw new InvalidDataException(
                "The Host Manager profile display name must be explicit.");
        }

        if (bytes.IsEmpty)
        {
            throw new InvalidDataException(
                $"Host Manager profile '{sourceDisplayName}' is empty.");
        }

        try
        {
            ValidateNoDuplicateProperties(bytes);
            var profile = JsonSerializer.Deserialize<HostManagerConfigurationProfile>(
                    bytes,
                    SerializerOptions)
                ?? throw new JsonException("The profile root cannot be null.");
            return new LoadedHostManagerProfile(
                profile,
                sourceDisplayName,
                Convert.ToHexString(SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(profile, SerializerOptions))));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Host Manager profile '{sourceDisplayName}' is invalid: {exception.Message}",
                exception);
        }
    }

    private static void ValidateSource(HostManagerProfileSource value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.ManifestResourceName))
        {
            throw new InvalidOperationException(
                "The Host Manager manifest resource name must be explicit.");
        }

        if (string.IsNullOrWhiteSpace(value.DisplayName))
        {
            throw new InvalidOperationException(
                "The Host Manager profile display name must be explicit.");
        }
    }

    internal static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
        => WindowsMetricConfiguration.ValidateNoDuplicateProperties(bytes);

}
