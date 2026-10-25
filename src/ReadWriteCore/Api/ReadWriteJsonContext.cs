using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetricInteger))]
[JsonSerializable(typeof(FieldValue))]
[JsonSerializable(typeof(FieldReading))]
[JsonSerializable(typeof(FieldReading[]))]
[JsonSerializable(typeof(DeviceMetricFieldSnapshot))]
[JsonSerializable(typeof(ProcessMetricFieldSnapshot))]
public partial class ReadWriteJsonContext : JsonSerializerContext;
