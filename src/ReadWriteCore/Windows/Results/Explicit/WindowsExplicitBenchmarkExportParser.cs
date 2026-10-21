using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

/// <summary>
/// Explicit binding supplied by the caller. These identities and the source version are
/// never inferred from a display name or from the currently installed executable.
/// </summary>
public sealed record WindowsExplicitResultImportContext(
    string DeviceIdentity,
    string ScopeIdentity,
    string SessionIdPrefix,
    string SourceVersion);

/// <summary>
/// Read-only adapters for two exact locally evidenced benchmark result shapes. They only
/// create typed submissions; the existing explicit-result owner remains the sole current.
/// </summary>
public static class WindowsExplicitBenchmarkExportParser
{
    private const string FurMarkSourceId = "geeks3d.furmark._scores.csv";
    private const string FurMarkMethod = "Geeks3D FurMark saved score CSV row";
    private const string ThreeDMarkSourceId = "ul.3dmark.cpu-profile.result-xml";
    private const string ThreeDMarkMethod = "UL 3DMark CPU Profile Result.xml passIndex=-1";

    private static readonly string[] FurMarkHeaders =
    [
        "date", "demo", "platform", "vendor", "renderer", "api_version",
        "width", "height", "fullscreen", "antialiasing", "duration",
        "max_gpu_temp", "score", "avg_fps", "min_fps", "max_fps"
    ];

    private static readonly (string Element, string Workload)[] CpuProfileScores =
    [
        ("CpuProfileSingleThreadScore", "3DMark CPU Profile, 1 thread"),
        ("CpuProfile2ThreadScore", "3DMark CPU Profile, 2 threads"),
        ("CpuProfile4ThreadScore", "3DMark CPU Profile, 4 threads"),
        ("CpuProfile8ThreadScore", "3DMark CPU Profile, 8 threads"),
        ("CpuProfile16ThreadScore", "3DMark CPU Profile, 16 threads"),
        ("CpuProfileUnlimitedThreadsScore", "3DMark CPU Profile, unlimited threads")
    ];

    /// <summary>
    /// Reads only the exact 16-column `_scores.csv` signature observed in the local
    /// FurMark 2 folder. The row has no source version or timezone, so the caller must
    /// provide both. `duration` is retained raw and is not mapped to milliseconds.
    /// </summary>
    public static ImmutableArray<WindowsExplicitResultSubmission> ParseFurMarkScoresCsv(
        Stream input, WindowsExplicitResultImportContext context, TimeSpan recordedAtOffset,
        WindowsExplicitBenchmarkExportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        ValidateContext(context);
        ValidateOffset(recordedAtOffset);

        var bytes = ReadBounded(input, limits.MaximumCsvBytes, "FurMark score CSV");
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The FurMark score CSV is not valid UTF-8.", exception);
        }

        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var records = ParseCsv(text, limits);
        if (records.Count < 2) throw new InvalidDataException("The FurMark score CSV has no result rows.");

        var header = records[0];
        if (header.Count != FurMarkHeaders.Length
            || !header.SequenceEqual(FurMarkHeaders, StringComparer.Ordinal))
            throw new InvalidDataException("The FurMark score CSV header is not the supported exact signature.");

        var results = ImmutableArray.CreateBuilder<WindowsExplicitResultSubmission>(records.Count - 1);
        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            var row = records[recordIndex];
            if (row.Count != FurMarkHeaders.Length)
                throw new InvalidDataException($"FurMark score row {recordIndex} has an unsupported column count.");

            var fields = new Dictionary<string, string>(FurMarkHeaders.Length, StringComparer.Ordinal);
            for (var column = 0; column < FurMarkHeaders.Length; column++)
                fields.Add(FurMarkHeaders[column], row[column]);

            var demo = RequireField(fields, "demo", recordIndex);
            var renderer = RequireField(fields, "renderer", recordIndex);
            var width = RequireField(fields, "width", recordIndex);
            var height = RequireField(fields, "height", recordIndex);
            var fullscreen = RequireField(fields, "fullscreen", recordIndex);
            var antialiasing = RequireField(fields, "antialiasing", recordIndex);
            var score = ParseNonNegativeNumber(RequireField(fields, "score", recordIndex), "score", recordIndex);
            var averageFps = ParseNonNegativeNumber(RequireField(fields, "avg_fps", recordIndex), "avg_fps", recordIndex);
            var localTime = ParseLocalTime(RequireField(fields, "date", recordIndex), recordIndex);
            var recordedAt = new DateTimeOffset(localTime, recordedAtOffset);
            var workload = $"{demo}; {width}x{height}; fullscreen={fullscreen}; antialiasing={antialiasing}";

            var sourceMetadata = CreateSourceMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["formatSignature"] = string.Join(',', FurMarkHeaders),
                ["rawRow"] = fields,
                ["recordedAtLocalText"] = fields["date"],
                ["recordedAtOffsetMinutes"] = checked((int)recordedAtOffset.TotalMinutes),
                ["rendererText"] = renderer,
                ["sourceVersionOrigin"] = "caller-supplied; the CSV row has no source-version column"
            }, limits.MaximumSourceMetadataBytes);

            var metadata = new WindowsExplicitResultMetadata(
                context.DeviceIdentity,
                WindowsExplicitResultScope.Gpu,
                context.ScopeIdentity,
                $"{context.SessionIdPrefix}:furmark-row-{recordIndex}",
                FurMarkSourceId,
                context.SourceVersion,
                recordedAt,
                FurMarkMethod,
                workload,
                sourceMetadata);
            var values = new WindowsExplicitResultValues
            {
                GpuAllBenchmarkScore = new(score, "score"),
                GpuAllBenchmarkFps = new(averageFps, "fps")
                // The CSV's bare `duration` value is preserved in source metadata.
            };
            results.Add(new(WindowsExplicitResultOwner.SupportedSchemaVersion,
                WindowsExplicitResultKind.GpuBenchmark, metadata, values));
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// Reads the exact locally observed CPU Profile Result.xml member from a `.3dmark-result`
    /// ZIP archive. Each of the six reported thread-count scores becomes an independent
    /// explicit workload submission; source version, recorded time, and selected CPU identity
    /// are caller-supplied because this member does not provide those facts.
    /// </summary>
    public static ImmutableArray<WindowsExplicitResultSubmission> ParseThreeDMarkCpuProfile(
        Stream input,
        string resultFileName,
        WindowsExplicitResultImportContext context,
        DateTimeOffset recordedAt,
        WindowsExplicitBenchmarkExportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(resultFileName);
        ValidateContext(context);
        if (recordedAt == default)
            throw new ArgumentException("A recorded timestamp must be supplied explicitly.", nameof(recordedAt));

        var fileName = Path.GetFileName(resultFileName);
        if (!string.Equals(Path.GetExtension(fileName), ".3dmark-result", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The input filename must use the official .3dmark-result extension.");

        var archiveBytes = ReadBounded(input, limits.MaximumResultArchiveBytes, "3DMark result archive");
        byte[] resultXmlBytes;
        try
        {
            using var archiveStream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > limits.MaximumResultArchiveEntries)
                throw new InvalidDataException("The 3DMark result archive exceeds the entry-count bound.");

            var resultEntries = archive.Entries
                .Where(static entry => string.Equals(entry.FullName, "Result.xml", StringComparison.Ordinal))
                .ToArray();
            if (resultEntries.Length != 1)
                throw new InvalidDataException("The 3DMark archive must contain exactly one top-level Result.xml member.");
            if (resultEntries[0].Length <= 0 || resultEntries[0].Length > limits.MaximumResultXmlBytes)
                throw new InvalidDataException("The 3DMark Result.xml member is empty or exceeds its size bound.");

            using var memberStream = resultEntries[0].Open();
            resultXmlBytes = ReadBounded(memberStream, limits.MaximumResultXmlBytes, "3DMark Result.xml");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The 3DMark result archive is invalid or truncated.", exception);
        }

        var archiveSha256 = Convert.ToHexString(SHA256.HashData(archiveBytes));
        var resultXmlSha256 = Convert.ToHexString(SHA256.HashData(resultXmlBytes));
        XDocument document;
        try
        {
            using var member = new MemoryStream(resultXmlBytes, writable: false);
            using var reader = XmlReader.Create(member, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = limits.MaximumResultXmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The 3DMark Result.xml member is invalid XML.", exception);
        }

        var root = document.Root;
        if (root?.Name != XName.Get("benchmark"))
            throw new InvalidDataException("The 3DMark Result.xml root is not the observed un-namespaced benchmark element.");
        var resultsContainers = root.Elements(XName.Get("results")).ToArray();
        if (resultsContainers.Length != 1)
            throw new InvalidDataException("The 3DMark Result.xml must contain exactly one results element.");
        var summaryGroups = resultsContainers[0].Elements()
            .Where(static group => string.Equals((string?)group.Element(XName.Get("passIndex")), "-1", StringComparison.Ordinal))
            .ToArray();
        if (summaryGroups.Length != 1)
            throw new InvalidDataException("The 3DMark CPU Profile result must contain one observed passIndex=-1 score group.");

        var resultGroup = summaryGroups[0];
        var rawScoreGroupXml = resultGroup.ToString(SaveOptions.DisableFormatting);
        var results = ImmutableArray.CreateBuilder<WindowsExplicitResultSubmission>(CpuProfileScores.Length);
        foreach (var (elementName, workload) in CpuProfileScores)
        {
            var scoreNodes = resultGroup.Elements(XName.Get(elementName)).ToArray();
            if (scoreNodes.Length != 1)
                throw new InvalidDataException($"The 3DMark CPU Profile score group must contain exactly one {elementName} element.");
            var rawScore = scoreNodes[0].Value;
            var score = ParseNonNegativeNumber(rawScore, elementName, results.Count);
            var sourceMetadata = CreateSourceMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["formatSignature"] = "3dmark-result ZIP / top-level Result.xml / results passIndex=-1",
                ["resultFileName"] = fileName,
                ["resultArchiveSha256"] = archiveSha256,
                ["resultMember"] = "Result.xml",
                ["resultMemberSha256"] = resultXmlSha256,
                ["rawScoreGroupXml"] = rawScoreGroupXml,
                ["scoreElement"] = elementName,
                ["rawScoreText"] = rawScore,
                ["recordedAtOrigin"] = "caller-supplied; the selected score group has no timestamp",
                ["sourceVersionOrigin"] = "caller-supplied; the selected score group has no version"
            }, limits.MaximumSourceMetadataBytes);
            var metadata = new WindowsExplicitResultMetadata(
                context.DeviceIdentity,
                WindowsExplicitResultScope.Cpu,
                context.ScopeIdentity,
                $"{context.SessionIdPrefix}:cpu-profile:{elementName}",
                ThreeDMarkSourceId,
                context.SourceVersion,
                recordedAt,
                ThreeDMarkMethod,
                workload,
                sourceMetadata);
            var values = new WindowsExplicitResultValues
            {
                CpuAllBenchmarkScore = new(score, "score")
            };
            results.Add(new(WindowsExplicitResultOwner.SupportedSchemaVersion,
                WindowsExplicitResultKind.CpuBenchmark, metadata, values));
        }

        return results.ToImmutable();
    }

    private static JsonElement CreateSourceMetadata(Dictionary<string, object?> values, int maximumSourceMetadataBytes)
    {
        var metadata = JsonSerializer.SerializeToElement(values);
        if (Encoding.UTF8.GetByteCount(metadata.GetRawText()) > maximumSourceMetadataBytes)
            throw new InvalidDataException("The parsed result provenance exceeds the existing explicit-result source-metadata bound.");
        return metadata;
    }

    private static void ValidateContext(WindowsExplicitResultImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireText(context.DeviceIdentity, nameof(context.DeviceIdentity), 256);
        RequireText(context.ScopeIdentity, nameof(context.ScopeIdentity), 256);
        RequireText(context.SessionIdPrefix, nameof(context.SessionIdPrefix), 128);
        RequireText(context.SourceVersion, nameof(context.SourceVersion), 128);
    }

    private static void RequireText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException($"{parameterName} must be bounded single-line text.", parameterName);
    }

    private static void ValidateOffset(TimeSpan offset)
    {
        if (offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14)
            || offset.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "The caller-supplied local-time offset must be a whole-minute UTC offset within 14 hours.");
    }

    private static byte[] ReadBounded(Stream input, int maximumBytes, string description)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"The {description} exceeds its {maximumBytes}-byte bound.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static List<List<string>> ParseCsv(string text, WindowsExplicitBenchmarkExportLimits limits)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var closedQuote = false;
        var fieldStarted = false;

        void Append(char value)
        {
            if (field.Length >= limits.MaximumCsvFieldChars)
                throw new InvalidDataException("A FurMark score CSV field exceeds its character bound.");
            field.Append(value);
        }

        void FinishField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
            closedQuote = false;
        }

        void FinishRecord()
        {
            var isBlank = row.Count == 0 && field.Length == 0 && !fieldStarted && !closedQuote;
            if (!isBlank)
            {
                FinishField();
                records.Add(row.ToList());
                if (records.Count > limits.MaximumCsvRecords)
                    throw new InvalidDataException("The FurMark score CSV exceeds the record-count bound.");
                row.Clear();
            }
            else
            {
                row.Clear();
                field.Clear();
            }
            fieldStarted = false;
            closedQuote = false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                        closedQuote = true;
                    }
                }
                else
                {
                    Append(current);
                }
                continue;
            }

            if (closedQuote)
            {
                if (current == ',')
                {
                    FinishField();
                    continue;
                }
                if (current is '\r' or '\n')
                {
                    if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    FinishRecord();
                    continue;
                }
                throw new InvalidDataException("The FurMark score CSV has characters after a quoted field.");
            }

            if (current == ',')
            {
                FinishField();
            }
            else if (current is '\r' or '\n')
            {
                if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                FinishRecord();
            }
            else if (current == '"')
            {
                if (fieldStarted || field.Length != 0)
                    throw new InvalidDataException("The FurMark score CSV contains a quote inside an unquoted field.");
                inQuotes = true;
                fieldStarted = true;
            }
            else
            {
                if (fieldStarted && field.Length == 0 && !closedQuote)
                    fieldStarted = false;
                Append(current);
                fieldStarted = true;
            }
        }

        if (inQuotes) throw new InvalidDataException("The FurMark score CSV has an unterminated quoted field.");
        if (row.Count > 0 || field.Length > 0 || fieldStarted || closedQuote)
            FinishRecord();
        return records;
    }

    private static string RequireField(IReadOnlyDictionary<string, string> fields, string name, int rowIndex)
    {
        var value = fields[name];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"FurMark score row {rowIndex} is missing required column '{name}'.");
        return value;
    }

    private static DateTime ParseLocalTime(string text, int rowIndex)
    {
        if (!DateTime.TryParseExact(text, "yyyy.MM.dd@HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var value))
            throw new InvalidDataException($"FurMark score row {rowIndex} has an unsupported local timestamp.");
        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    private static double ParseNonNegativeNumber(string text, string fieldName, int rowIndex)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0)
            throw new InvalidDataException($"Result row {rowIndex} has an invalid non-negative number in '{fieldName}'.");
        return value;
    }
}
