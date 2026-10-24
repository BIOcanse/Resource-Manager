using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
namespace ResourceManager.ReadWrite.Windows.Metrics.Configuration;

public sealed record WindowsMetricConfiguration(NativeMetricSnapshotConfiguration Configuration,
    NativeMetricSnapshotCatalogConfiguration CatalogConfiguration, NativePdhSessionOptions PdhOptions)
{
    public const string DefaultResourceName = "ResourceManager.ReadWrite.Windows.Metrics.Configuration.Default";
    public static WindowsMetricConfiguration Default => CreateDefault();
    internal static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static byte[] ReadDefaultProfileBytes()
    {
        using var stream = typeof(WindowsMetricConfiguration).Assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"Metric defaults resource '{DefaultResourceName}' was not embedded.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    public static WindowsMetricConfiguration CreateDefault() => LoadBytes(ReadDefaultProfileBytes());
    public static WindowsMetricConfiguration LoadBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) throw new InvalidDataException("Metric configuration cannot be empty.");
        try {
            ValidateNoDuplicateProperties(bytes);
            var profile = JsonSerializer.Deserialize<MetricSnapshotConfigurationProfile>(bytes, CreateSerializerOptions())
                ?? throw new JsonException("Metric configuration root cannot be null.");
            return Compile(profile);
        }
        catch (JsonException exception) { throw new InvalidDataException($"Metric configuration is invalid: {exception.Message}", exception); }
    }
    public static WindowsMetricConfiguration Compile(MetricSnapshotConfigurationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ProfileRevision <= 0) throw new InvalidDataException("Metric profile_revision must be positive.");
        if (profile.AbiVersion != NativeMetricSnapshotAbi.Version) throw new InvalidDataException("Metric ABI version must exactly match the native ABI.");
        var build = WindowsMetricConfigurationCompiler.CompileMetricSnapshotCapacity(profile.BuildCapacity, "build_specialize.capacity_limits.metric_snapshot");
        var recreate = WindowsMetricConfigurationCompiler.CompileRecreate(profile.Recreate, build);
        var hot = WindowsMetricConfigurationCompiler.CompileMetricSnapshotHotPublish(profile.HotPublish, recreate, profile.ProfileRevision);
        var pdh = profile.Pdh ?? throw new InvalidDataException("Metric PDH configuration is required.");
        if (pdh.AbiVersion != NativeCoreAbi.Version || pdh.BaselineResetIntervalMilliseconds < 1000
            || pdh.FrameReuseWindowMilliseconds < 0 || pdh.LastGoodLifetimeMilliseconds < 0)
            throw new InvalidDataException("Metric PDH configuration contains an invalid ABI or duration.");
        return new(WindowsMetricConfigurationCompiler.MapConfiguration(profile.AbiVersion, recreate, hot),
            new(hot.CatalogGenerationBase, hot.CatalogManifestSha256, recreate.Capacity.MaximumGpuAdapterCount,
                recreate.Capacity.MaximumRuleCount, recreate.Capacity.MaximumMetricCount, hot.SourcePolicies, hot.RuleTemplates),
            new(TimeSpan.FromMilliseconds(pdh.FrameReuseWindowMilliseconds), TimeSpan.FromMilliseconds(pdh.LastGoodLifetimeMilliseconds), pdh.BaselineResetIntervalMilliseconds));
    }

    public static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException("Unexpected object terminator.");
                    }

                    objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException("A property exists outside an object.");
                    }

                    var propertyName = reader.GetString()
                        ?? throw new JsonException("A property name cannot be null.");
                    if (!objectProperties.Peek().Add(propertyName))
                    {
                        throw new JsonException(
                            $"Duplicate property '{propertyName}' is not allowed.");
                    }

                    break;
            }
        }

        if (objectProperties.Count != 0)
        {
            throw new JsonException("The profile contains an unterminated object.");
        }
    }
}
