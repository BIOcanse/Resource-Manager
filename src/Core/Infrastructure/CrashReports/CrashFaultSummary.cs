using ResourceManager.App.Application.CrashReports;

namespace ResourceManager.App.Infrastructure.CrashReports;

// WER LocalDumps names a dump "<image>.<pid>.dmp".
public static class CrashDumpFileName
{
    public static string? ImageOf(string fileName)
    {
        if (!fileName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) return null;
        var stem = fileName[..^4];
        var dot = stem.LastIndexOf('.');
        if (dot <= 0 || dot == stem.Length - 1 || !stem[(dot + 1)..].All(char.IsAsciiDigit)) return null;
        var image = stem[..dot];
        return image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image : null;
    }
}

// Turns the Application log events of one crash into the record's fault summary.
public static class CrashFaultSummary
{
    // Per image and in time order, events within the merge window of the group's first event are one crash.
    public static IReadOnlyList<IReadOnlyList<CrashEvent>> Group(IReadOnlyList<CrashEvent> events, TimeSpan mergeWindow)
    {
        var result = new List<IReadOnlyList<CrashEvent>>();
        foreach (var image in events.GroupBy(static item => item.Image, StringComparer.OrdinalIgnoreCase))
        {
            List<CrashEvent>? current = null;
            foreach (var item in image.OrderBy(static item => item.TimeCreated))
            {
                if (current is null || item.TimeCreated - current[0].TimeCreated > mergeWindow)
                {
                    current = [];
                    result.Add(current);
                }
                current.Add(item);
            }
        }
        return result;
    }

    // Module, offset and exception code from the 1000 event; description and stack from the 1026 event.
    public static CrashReportFault? From(IEnumerable<CrashEvent> crash, CrashReportOptions options)
    {
        var items = crash.ToArray();
        if (items.Length == 0) return null;
        var native = items.FirstOrDefault(static item => item.EventId == WindowsCrashEventSource.ApplicationErrorEventId);
        var managed = items.FirstOrDefault(static item => item.EventId == WindowsCrashEventSource.DotNetRuntimeEventId);
        var stack = managed?.ManagedStack;
        if (stack is not null && stack.Length > options.ManagedStackMaxChars) stack = stack[..options.ManagedStackMaxChars];
        return new(native?.Module, native?.Offset, native?.ExceptionCode, stack, managed?.Description);
    }
}
