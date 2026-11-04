using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.CrashReports;

// The report package (docs/crash_dumps_2026-10-31.md 接口): the dump as it is (never truncated), report.json with the
// record, build version and system facts, events.txt with the crash's Application log events, service-log.txt with
// the service log around the crash. No machine or user name is added; the dump itself may contain both.
public static class CrashReportPackageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record PackageFacts(CrashReport Report, string BuildVersion, string OperatingSystem,
        string OperatingSystemVersion, string Architecture, int ProcessorCount);

    // Writes the zip next to its final path and replaces it atomically. Returns the package size in bytes.
    public static long Write(string path, CrashReport report, string buildVersion,
        IReadOnlyList<CrashEvent> crashEvents, IReadOnlyList<ServiceLogEntry> serviceLog)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    if (report.DumpStatus == CrashReportDumpStatuses.Saved && report.Dump is { } dump
                        && File.Exists(dump.Path) && File.GetLastWriteTimeUtc(dump.Path) == report.OccurredAt.UtcDateTime)
                        archive.CreateEntryFromFile(dump.Path, Path.GetFileName(dump.Path), CompressionLevel.Optimal);
                    var facts = new PackageFacts(report with { Package = null }, buildVersion, RuntimeInformation.OSDescription,
                        Environment.OSVersion.VersionString, RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount);
                    WriteText(archive, "report.json", JsonSerializer.Serialize(facts, JsonOptions));
                    var text = new StringBuilder();
                    foreach (var item in crashEvents)
                        text.Append(item.TimeCreated.ToString("o")).Append("  event ").Append(item.EventId).AppendLine()
                            .AppendLine(item.Text).AppendLine();
                    WriteText(archive, "events.txt", text.ToString());
                    text.Clear();
                    foreach (var entry in serviceLog)
                        text.Append(entry.TimeCreated.ToString("o")).Append("  ").Append(entry.Level).Append("  ")
                            .AppendLine(entry.Text.ReplaceLineEndings(Environment.NewLine + "    "));
                    WriteText(archive, "service-log.txt", text.ToString());
                }
                stream.Flush(flushToDisk: true);
            }
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
            return new FileInfo(path).Length;
        }
        finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath); }
    }

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
