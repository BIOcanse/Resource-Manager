using ResourceManager.App.Domain.Metrics;
using SdkWmi = ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;
namespace ResourceManager.App.Infrastructure.Monitoring;
internal static class PlatformSensorWmiUtilities
{
    internal static QueryResult QueryObjects(string path, string query)
    {
        var result = SdkWmi.QueryObjects(path, query, TimeSpan.FromSeconds(2));
        return new(result.Rows, result.Error);
    }
    internal static bool ValueMatches(IReadOnlyDictionary<string, object?> row, string property, string? expected) => SdkWmi.ValueMatches(row, property, expected);
    internal static bool ValueContains(IReadOnlyDictionary<string, object?> row, string property, string? expected) => SdkWmi.ValueContains(row, property, expected);
    internal static string? ReadString(IReadOnlyDictionary<string, object?> row, string property) => SdkWmi.ReadString(row, property);
    internal static int? ParseInt(object? value) => SdkWmi.ParseInt(value);
    internal static ulong? ParseUlong(object? value) => SdkWmi.ParseUlong(value);
    internal static double? ParseTemperature(object? value) => SdkWmi.ParseTemperature(value);
    internal static double? ParseFanRpm(object? value) => SdkWmi.ParseFanRpm(value);
    internal static double? ParsePercent(object? value) => SdkWmi.ParsePercent(value);
    internal static double? ParseDouble(object? value) => SdkWmi.ParseDouble(value);
    internal static HardwareSensorProviderState MergeProviderStates(
        string provider,
        params HardwareSensorProviderState[] states)
    {
        if (states.Any(static state => state.State == HardwareSensorProviderStates.Active))
        {
            return new HardwareSensorProviderState(
                provider,
                HardwareSensorProviderStates.Active,
                string.Join("; ", states.Select(static state => $"{state.Provider}: {state.State}")));
        }

        return new HardwareSensorProviderState(
            provider,
            HardwareSensorProviderStates.Unavailable,
            string.Join("; ", states.Select(static state => $"{state.Provider}: {state.Message}")));
    }
}
internal sealed record QueryResult(IReadOnlyList<Dictionary<string, object?>> Rows, string? Error);
