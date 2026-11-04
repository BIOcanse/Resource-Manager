using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace ResourceManager.App.Infrastructure.CrashReports;

// Reads the Application log with a time-bounded XPath query; every loop stops at the caller's maximum.
[SupportedOSPlatform("windows")]
public sealed class WindowsCrashEventSource : ICrashEventSource
{
    internal const string ApplicationErrorProvider = "Application Error";
    internal const string DotNetRuntimeProvider = ".NET Runtime";
    internal const int ApplicationErrorEventId = 1000;
    internal const int DotNetRuntimeEventId = 1026;

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Crash report discovery or packaging")]
    public IReadOnlyList<CrashEvent> ReadCrashEvents(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> images, int max)
    {
        var query = $"*[System[((EventID={ApplicationErrorEventId} and Provider[@Name='{ApplicationErrorProvider}']) or " +
            $"(EventID={DotNetRuntimeEventId} and Provider[@Name='{DotNetRuntimeProvider}'])) and {TimeFilter(from, to)}]]";
        var result = new List<CrashEvent>();
        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query));
        var scanned = 0;
        while (scanned < max && reader.ReadEvent() is { } record)
        {
            scanned++;
            using (record)
            {
                var parsed = record.Id == ApplicationErrorEventId ? ParseApplicationError(record) : ParseDotNetRuntime(record);
                if (parsed is not null && images.Contains(parsed.Image, StringComparer.OrdinalIgnoreCase)) result.Add(parsed);
            }
        }
        return result;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Crash report packaging")]
    public IReadOnlyList<ServiceLogEntry> ReadServiceLog(string source, DateTimeOffset from, DateTimeOffset to, int max)
    {
        var result = new List<ServiceLogEntry>();
        if (max <= 0 || source.Contains('\'')) return result;
        var query = $"*[System[Provider[@Name='{source}'] and {TimeFilter(from, to)}]]";
        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query));
        while (result.Count < max && reader.ReadEvent() is { } record)
        {
            using (record)
                result.Add(new(Time(record), LevelName(record.Level), Text(record)));
        }
        return result;
    }

    private static string TimeFilter(DateTimeOffset from, DateTimeOffset to)
        => $"TimeCreated[@SystemTime>='{from.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}' and " +
            $"@SystemTime<'{to.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}']";

    private static DateTimeOffset Time(EventRecord record)
        => record.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue;

    // Application Error 1000: AppName, AppVersion, AppTimeStamp, ModuleName, ModuleVersion, ModuleTimeStamp,
    // ExceptionCode, FaultingOffset, ProcessId, ...
    private static CrashEvent? ParseApplicationError(EventRecord record)
    {
        var values = record.Properties;
        if (values.Count < 8 || values[0].Value is not string image) return null;
        return new(Time(record), ApplicationErrorEventId, image, values[3].Value as string,
            Hex(values[7].Value), Hex(values[6].Value), null, null, Text(record));
    }

    // .NET Runtime 1026: one string, "Application: X.exe", "Description: ...", then "Stack:" and the frames.
    private static CrashEvent? ParseDotNetRuntime(EventRecord record)
    {
        if (record.Properties.Count < 1 || record.Properties[0].Value is not string message) return null;
        var parsed = CrashEventText.ParseDotNetRuntime(message);
        return parsed.Image is null ? null
            : new(Time(record), DotNetRuntimeEventId, parsed.Image, null, null, null, parsed.Description, parsed.Stack, message);
    }

    private static string? Hex(object? value) => value switch
    {
        uint number => "0x" + number.ToString("x8", CultureInfo.InvariantCulture),
        int number => "0x" + unchecked((uint)number).ToString("x8", CultureInfo.InvariantCulture),
        ulong number => "0x" + number.ToString("x16", CultureInfo.InvariantCulture),
        long number => "0x" + unchecked((ulong)number).ToString("x16", CultureInfo.InvariantCulture),
        string text when !string.IsNullOrWhiteSpace(text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? text.Trim() : "0x" + text.Trim(),
        _ => null
    };

    private static string LevelName(byte? level) => level switch
    {
        1 => "Critical", 2 => "Error", 3 => "Warning", 4 => "Information", 5 => "Verbose", _ => "Information"
    };

    private static string Text(EventRecord record)
    {
        try
        {
            if (record.FormatDescription() is { Length: > 0 } formatted) return formatted;
        }
        catch (EventLogException) { }
        var text = new StringBuilder();
        foreach (var property in record.Properties) text.AppendLine(Convert.ToString(property.Value, CultureInfo.InvariantCulture));
        return text.ToString();
    }
}

// Pure text parsing of event messages, shared by the Windows source and tests.
public static class CrashEventText
{
    public static (string? Image, string? Description, string? Stack) ParseDotNetRuntime(string message)
    {
        string? image = null, description = null;
        var lines = message.Replace("\r\n", "\n").Split('\n');
        var stackStart = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (image is null && line.StartsWith("Application:", StringComparison.Ordinal))
                image = line["Application:".Length..].Trim();
            else if (description is null && line.StartsWith("Description:", StringComparison.Ordinal))
                description = line["Description:".Length..].Trim();
            else if (line == "Stack:")
            {
                stackStart = index + 1;
                break;
            }
        }
        var stack = stackStart < 0 ? null : string.Join('\n', lines[stackStart..].Select(static line => line.Trim())
            .Where(static line => line.Length > 0));
        return (string.IsNullOrEmpty(image) ? null : image, description, string.IsNullOrEmpty(stack) ? null : stack);
    }
}
