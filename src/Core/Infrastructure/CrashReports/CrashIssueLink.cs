using System.Globalization;
using System.Text;
using ResourceManager.App.Application.CrashReports;

namespace ResourceManager.App.Infrastructure.CrashReports;

// The prefilled GitHub issue page (docs/crash_dumps_2026-10-31.md 接口): the title names the program and the faulting
// module; the body gives version, time, fault summary and asks for the report package by file name. No local path
// is written into the public issue. The URL stays within issue_url_max_chars by quoting fewer stack lines, then none.
public static class CrashIssueLink
{
    public static string Build(CrashReport report, CrashReportOptions options)
    {
        var title = Title(report);
        var stack = (report.Fault?.ManagedStack ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = Math.Min(stack.Length, options.IssueStackMaxLines);
        for (; lines >= 0; lines--)
        {
            var url = Url(options, title, Body(report, stack, lines));
            if (url.Length <= options.IssueUrlMaxChars) return url;
        }
        // Even without the stack the body is too long (a very long description): drop the description too.
        var shortest = Url(options, title, Body(report with { Fault = report.Fault is null ? null : report.Fault with { Description = null } }, [], 0));
        if (shortest.Length <= options.IssueUrlMaxChars) return shortest;
        // Bound all external text, including a single very long module/description in the title. Keep the package
        // instructions whenever they fit; for a configured smaller limit retain the longest fitting prefix.
        var body = PackageLine(report);
        var compactTitle = $"Crash: {report.Program}";
        while (Url(options, compactTitle, body).Length > options.IssueUrlMaxChars && (body.Length > 0 || compactTitle.Length > 0))
        {
            if (body.Length > 0) body = Shorten(body);
            else compactTitle = Shorten(compactTitle);
        }
        var bounded = Url(options, compactTitle, body);
        return bounded.Length <= options.IssueUrlMaxChars ? bounded
            : throw new InvalidDataException("The configured crash issue URL leaves no room for query parameters.");
    }

    private static string Shorten(string text)
    {
        var length = text.Length - 1;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    internal static string Title(CrashReport report)
    {
        var fault = report.Fault?.Module ?? report.Fault?.ExceptionCode ?? report.Fault?.Description ?? "unknown fault";
        return $"Crash: {report.Program} ({report.Role}) in {fault}" + (report.Version is { } version ? $" [{version}]" : "");
    }

    private static string Url(CrashReportOptions options, string title, string body)
        => $"{options.IssueNewUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    private static string Body(CrashReport report, string[] stack, int lines)
    {
        var body = new StringBuilder();
        body.AppendLine("### Crash report").AppendLine()
            .Append("- Program: `").Append(report.Program).Append("` (").Append(report.Role).AppendLine(")")
            .Append("- Version: ").AppendLine(report.Version ?? "unknown")
            .Append("- Time (UTC): ").AppendLine(report.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append("- Dump: ").AppendLine(report.DumpStatus);
        if (report.Fault is { } fault)
        {
            if (fault.Module is not null) body.Append("- Faulting module: `").Append(fault.Module).AppendLine("`");
            if (fault.Offset is not null) body.Append("- Offset: `").Append(fault.Offset).AppendLine("`");
            if (fault.ExceptionCode is not null) body.Append("- Exception code: `").Append(fault.ExceptionCode).AppendLine("`");
            if (fault.Description is not null) body.Append("- Description: ").AppendLine(fault.Description);
        }
        if (lines > 0)
        {
            body.AppendLine().AppendLine("Managed stack:").AppendLine("```");
            foreach (var line in stack.Take(lines)) body.AppendLine(line);
            body.AppendLine("```");
        }
        body.AppendLine().Append(PackageLine(report));
        return body.ToString();
    }

    private static string PackageLine(CrashReport report)
        => $"**Report package:** please drag `{report.Id}.zip` here (Resource Manager selected it in Explorer).\n" +
            $"请把报告包 `{report.Id}.zip` 拖到这里（资源管理器已在文件资源管理器中选中它）。\n";
}
