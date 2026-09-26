using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Adaptation.Scheduling;

[JsonConverter(typeof(ResourceManagerSelfCpuGradeJsonConverter))]
public enum ResourceManagerSelfCpuGrade : byte
{
    Normal = 0,
    Optimize = 1
}

public static class ResourceManagerSelfCpuGrades
{
    public static bool TryFromAdapter(
        AdapterCpuSchedulingGrade? grade,
        out ResourceManagerSelfCpuGrade selfGrade)
    {
        switch (grade)
        {
            case AdapterCpuSchedulingGrade.Normal:
                selfGrade = ResourceManagerSelfCpuGrade.Normal;
                return true;
            case AdapterCpuSchedulingGrade.Optimize:
                selfGrade = ResourceManagerSelfCpuGrade.Optimize;
                return true;
            default:
                selfGrade = ResourceManagerSelfCpuGrade.Normal;
                return false;
        }
    }

    public static AdapterCpuSchedulingGrade ToAdapter(ResourceManagerSelfCpuGrade grade)
    {
        return grade switch
        {
            ResourceManagerSelfCpuGrade.Normal => AdapterCpuSchedulingGrade.Normal,
            ResourceManagerSelfCpuGrade.Optimize => AdapterCpuSchedulingGrade.Optimize,
            _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "Unknown self CPU grade.")
        };
    }

    public static string ToToken(ResourceManagerSelfCpuGrade grade)
    {
        return grade switch
        {
            ResourceManagerSelfCpuGrade.Normal => "normal",
            ResourceManagerSelfCpuGrade.Optimize => "optimize",
            _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "Unknown self CPU grade.")
        };
    }
}

public sealed class ResourceManagerSelfCpuGradeJsonConverter : JsonConverter<ResourceManagerSelfCpuGrade>
{
    public override ResourceManagerSelfCpuGrade Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString()?.Trim().ToLowerInvariant() switch
            {
                "normal" => ResourceManagerSelfCpuGrade.Normal,
                "optimize" => ResourceManagerSelfCpuGrade.Optimize,
                _ => throw new JsonException("Self CPU grade must be normal or optimize.")
            };
        }

        throw new JsonException("Self CPU grade must be a string.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ResourceManagerSelfCpuGrade value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(ResourceManagerSelfCpuGrades.ToToken(value));
    }
}
