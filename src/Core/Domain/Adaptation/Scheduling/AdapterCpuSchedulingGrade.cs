using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Adaptation.Scheduling;

[JsonConverter(typeof(AdapterCpuSchedulingGradeJsonConverter))]
public enum AdapterCpuSchedulingGrade : byte
{
    Freeze = 0,
    Optimize = 1,
    Normal = 2,
    Extreme = 3
}

public sealed class AdapterCpuSchedulingGradeJsonConverter : JsonConverter<AdapterCpuSchedulingGrade>
{
    public override AdapterCpuSchedulingGrade Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && AdapterCpuSchedulingGrades.TryParse(reader.GetString(), out var grade))
        {
            return grade;
        }

        throw new JsonException("CPU scheduling grade must be freeze, optimize, normal, or extreme.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        AdapterCpuSchedulingGrade value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(AdapterCpuSchedulingGrades.ToToken(value));
    }
}

public static class AdapterCpuSchedulingGrades
{
    public static IReadOnlyList<AdapterCpuSchedulingGrade> All { get; } =
    [
        AdapterCpuSchedulingGrade.Freeze,
        AdapterCpuSchedulingGrade.Optimize,
        AdapterCpuSchedulingGrade.Normal,
        AdapterCpuSchedulingGrade.Extreme
    ];

    public static string ToToken(AdapterCpuSchedulingGrade grade)
    {
        return grade switch
        {
            AdapterCpuSchedulingGrade.Freeze => "freeze",
            AdapterCpuSchedulingGrade.Optimize => "optimize",
            AdapterCpuSchedulingGrade.Normal => "normal",
            AdapterCpuSchedulingGrade.Extreme => "extreme",
            _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "Unknown CPU scheduling grade.")
        };
    }

    public static bool TryParse(string? value, out AdapterCpuSchedulingGrade grade)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "freeze":
                grade = AdapterCpuSchedulingGrade.Freeze;
                return true;
            case "optimize":
                grade = AdapterCpuSchedulingGrade.Optimize;
                return true;
            case "normal":
                grade = AdapterCpuSchedulingGrade.Normal;
                return true;
            case "extreme":
                grade = AdapterCpuSchedulingGrade.Extreme;
                return true;
            default:
                grade = AdapterCpuSchedulingGrade.Normal;
                return false;
        }
    }
}
