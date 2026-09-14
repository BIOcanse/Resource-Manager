using System.Text.Json;
using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;

public static class GpuTelemetryWorkerProtocol
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string SerializeRequest(GpuTelemetryWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(request, SerializerOptions);
    }

    public static GpuTelemetryWorkerRequest DeserializeRequest(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<GpuTelemetryWorkerRequest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("GPU telemetry worker request payload is empty.");
    }

    public static string SerializeSnapshot(GpuTelemetryWorkerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    public static GpuTelemetryWorkerSnapshot DeserializeSnapshot(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<GpuTelemetryWorkerSnapshot>(json, SerializerOptions)
            ?? throw new InvalidOperationException("GPU telemetry worker snapshot payload is empty.");
    }
}
