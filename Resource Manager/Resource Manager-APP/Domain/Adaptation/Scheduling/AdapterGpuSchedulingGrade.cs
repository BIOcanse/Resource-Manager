using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Adaptation.Scheduling;

[JsonConverter(typeof(AdapterGpuSchedulingGradeJsonConverter))]
public enum AdapterGpuSchedulingGrade : byte
{
    Freeze = 0,
    Optimize = 1,
    Normal = 2,
    Extreme = 3
}

public sealed class AdapterGpuSchedulingGradeJsonConverter : JsonConverter<AdapterGpuSchedulingGrade>
{
    public override AdapterGpuSchedulingGrade Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && AdapterGpuSchedulingGrades.TryParse(reader.GetString(), out var grade))
        {
            return grade;
        }

        throw new JsonException("GPU scheduling grade must be freeze, optimize, normal, or extreme.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        AdapterGpuSchedulingGrade value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(AdapterGpuSchedulingGrades.ToToken(value));
    }
}

public static class AdapterGpuSchedulingGrades
{
    public static IReadOnlyList<AdapterGpuSchedulingGrade> All { get; } =
    [
        AdapterGpuSchedulingGrade.Freeze,
        AdapterGpuSchedulingGrade.Optimize,
        AdapterGpuSchedulingGrade.Normal,
        AdapterGpuSchedulingGrade.Extreme
    ];

    public static string ToToken(AdapterGpuSchedulingGrade grade)
    {
        return grade switch
        {
            AdapterGpuSchedulingGrade.Freeze => "freeze",
            AdapterGpuSchedulingGrade.Optimize => "optimize",
            AdapterGpuSchedulingGrade.Normal => "normal",
            AdapterGpuSchedulingGrade.Extreme => "extreme",
            _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "Unknown GPU scheduling grade.")
        };
    }

    public static bool TryParse(string? value, out AdapterGpuSchedulingGrade grade)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "freeze":
                grade = AdapterGpuSchedulingGrade.Freeze;
                return true;
            case "optimize":
                grade = AdapterGpuSchedulingGrade.Optimize;
                return true;
            case "normal":
                grade = AdapterGpuSchedulingGrade.Normal;
                return true;
            case "extreme":
                grade = AdapterGpuSchedulingGrade.Extreme;
                return true;
            default:
                grade = AdapterGpuSchedulingGrade.Normal;
                return false;
        }
    }
}
