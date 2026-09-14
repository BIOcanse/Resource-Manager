using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Adaptation.Scheduling;

[JsonConverter(typeof(ResourceManagerSelfGpuGradeJsonConverter))]
public enum ResourceManagerSelfGpuGrade : byte
{
    Normal = 0,
    Optimize = 1
}

public static class ResourceManagerSelfGpuGrades
{
    public static bool TryFromAdapter(
        AdapterGpuSchedulingGrade? grade,
        out ResourceManagerSelfGpuGrade selfGrade)
    {
        switch (grade)
        {
            case AdapterGpuSchedulingGrade.Normal:
                selfGrade = ResourceManagerSelfGpuGrade.Normal;
                return true;
            case AdapterGpuSchedulingGrade.Optimize:
                selfGrade = ResourceManagerSelfGpuGrade.Optimize;
                return true;
            default:
                selfGrade = ResourceManagerSelfGpuGrade.Normal;
                return false;
        }
    }

    public static AdapterGpuSchedulingGrade ToAdapter(ResourceManagerSelfGpuGrade grade)
    {
        return grade switch
        {
            ResourceManagerSelfGpuGrade.Normal => AdapterGpuSchedulingGrade.Normal,
            ResourceManagerSelfGpuGrade.Optimize => AdapterGpuSchedulingGrade.Optimize,
            _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "Unknown self GPU grade.")
        };
    }

    public static string ToToken(ResourceManagerSelfGpuGrade grade)
    {
        return grade switch
        {
            ResourceManagerSelfGpuGrade.Normal => "normal",
            ResourceManagerSelfGpuGrade.Optimize => "optimize",
            _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "Unknown self GPU grade.")
        };
    }
}

public sealed class ResourceManagerSelfGpuGradeJsonConverter : JsonConverter<ResourceManagerSelfGpuGrade>
{
    public override ResourceManagerSelfGpuGrade Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString()?.Trim().ToLowerInvariant() switch
            {
                "normal" => ResourceManagerSelfGpuGrade.Normal,
                "optimize" => ResourceManagerSelfGpuGrade.Optimize,
                _ => throw new JsonException("Self GPU grade must be normal or optimize.")
            };
        }

        throw new JsonException("Self GPU grade must be a string.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ResourceManagerSelfGpuGrade value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(ResourceManagerSelfGpuGrades.ToToken(value));
    }
}
