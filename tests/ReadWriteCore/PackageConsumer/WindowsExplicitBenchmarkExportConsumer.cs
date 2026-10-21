using System.IO.Compression;
using System.Text;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Results.Explicit;

internal static class WindowsExplicitBenchmarkExportConsumer
{
    internal static Task RunAsync()
    {
        var limits = new WindowsExplicitBenchmarkExportLimits();
        var cpuContext = new WindowsExplicitResultImportContext(
            "recorded-host-device",
            "cpu:caller-selected-scope",
            "cpu-profile-import-test",
            "test-only-caller-declared-version");
        var recordedAt = new DateTimeOffset(2026, 3, 13, 21, 19, 49, TimeSpan.FromHours(-5));
        using var cpuInput = new MemoryStream(ReadVerifiedFixture(
            "3DMark_CPUProfile_10505_20260313211949.3dmark-result",
            "52ED6DE17BC1AA3BFB4E45229EA5DAFE3E50B9804C2B51B2C52C015A4F88ACAF"));
        var cpuSubmissions = WindowsExplicitBenchmarkExportParser.ParseThreeDMarkCpuProfile(
            cpuInput, "3DMark_CPUProfile_10505_20260313211949.3dmark-result", cpuContext, recordedAt, limits);
        Require(cpuSubmissions.Length == 6,
            "CPU Profile produces six individual score submissions rather than one combined score");

        using (var cpuOwner = new WindowsExplicitResultOwner(cpuContext.DeviceIdentity))
        {
            var expectedScores = new[] { 993d, 1964d, 3775d, 6443d, 9816d, 10505d };
            var expectedWorkloads = new[]
            {
                "3DMark CPU Profile, 1 thread",
                "3DMark CPU Profile, 2 threads",
                "3DMark CPU Profile, 4 threads",
                "3DMark CPU Profile, 8 threads",
                "3DMark CPU Profile, 16 threads",
                "3DMark CPU Profile, unlimited threads"
            };
            var backend = cpuOwner.CreateBackend();
            for (var index = 0; index < cpuSubmissions.Length; index++)
            {
                var submission = cpuSubmissions[index];
                Require(submission.Kind == WindowsExplicitResultKind.CpuBenchmark
                    && submission.Metadata.Scope == WindowsExplicitResultScope.Cpu
                    && submission.Metadata.ScopeIdentity == cpuContext.ScopeIdentity
                    && submission.Metadata.SourceVersion == cpuContext.SourceVersion
                    && submission.Metadata.RecordedAt == recordedAt
                    && submission.Metadata.Workload == expectedWorkloads[index]
                    && submission.Values.CpuAllBenchmarkScore?.Value == expectedScores[index]
                    && submission.Values.CpuAllBenchmarkDurationMs is null,
                    "each CPU Profile score retains its workload and does not invent a duration");

                var current = cpuOwner.Submit(submission);
                var projected = backend.ReadCached(new(cpuOwner.TestResultDeviceIdentity, "cpu_all_benchmark_score"));
                Require(ReferenceEquals(current, cpuOwner.ReadCurrent())
                    && current.Revision == (ulong)index + 1
                    && current.Values.CpuAllBenchmarkScore?.Value == expectedScores[index]
                    && current.Values.CpuAllBenchmarkWorkloadDeclared?.Value == expectedWorkloads[index]
                    && projected.Status == FieldObservationStatus.Current
                    && projected.Value?.Number == expectedScores[index],
                    "the public ReadWriteCore owner projects each selected recorded CPU score through its current");
            }

            var finalMetadata = cpuOwner.ReadCurrent().Metadata!;
            Require(finalMetadata.SourceMetadata.GetProperty("resultMember")
                    .GetString() == "Result.xml"
                && finalMetadata.SourceMetadata.GetProperty("resultMemberSha256").GetString()?.Length == 64
                && finalMetadata.SourceMetadata.GetProperty("rawScoreGroupXml").GetString()
                    ?.Contains("CpuProfileUnlimitedThreadsScore", StringComparison.Ordinal) == true
                && finalMetadata.SourceMetadata.GetProperty("scoreElement").GetString()
                    == "CpuProfileUnlimitedThreadsScore",
                "the imported CPU result retains original member/hash, raw score group, and score element provenance");
        }

        var gpuContext = new WindowsExplicitResultImportContext(
            "recorded-host-device",
            "gpu:caller-selected-scope",
            "furmark-import-test",
            "test-only-caller-declared-version");
        using var furMarkInput = new MemoryStream(ReadVerifiedFixture(
            "FurMark2_10_2_scores.csv",
            "729AEC929F80CEAFFC38FABF7C46CCAAD7BA6BC53F7DADEE5ECC3B8854964DF3"));
        var furMarkSubmissions = WindowsExplicitBenchmarkExportParser.ParseFurMarkScoresCsv(
            furMarkInput, gpuContext, TimeSpan.FromHours(-5), limits);
        Require(furMarkSubmissions.Length == 2,
            "the exact observed FurMark score CSV fixture has two recorded rows");

        using (var gpuOwner = new WindowsExplicitResultOwner(gpuContext.DeviceIdentity))
        {
            var expected = new[] { (11811d, 196d, "2025-10-04T12:15:26.0000000-05:00"),
                (11587d, 193d, "2025-10-15T17:39:30.0000000-05:00") };
            var backend = gpuOwner.CreateBackend();
            for (var index = 0; index < furMarkSubmissions.Length; index++)
            {
                var submission = furMarkSubmissions[index];
                Require(submission.Kind == WindowsExplicitResultKind.GpuBenchmark
                    && submission.Metadata.Scope == WindowsExplicitResultScope.Gpu
                    && submission.Metadata.ScopeIdentity == gpuContext.ScopeIdentity
                    && submission.Metadata.SourceVersion == gpuContext.SourceVersion
                    && submission.Metadata.RecordedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                        == expected[index].Item3
                    && submission.Values.GpuAllBenchmarkScore?.Value == expected[index].Item1
                    && submission.Values.GpuAllBenchmarkFps?.Value == expected[index].Item2
                    && submission.Values.GpuAllBenchmarkDurationMs is null,
                    "FurMark maps only explicit score/FPS and retains unknown duration units");
                Require(submission.Metadata.SourceMetadata.GetProperty("rawRow").GetProperty("duration").GetString()
                    == "60000"
                    && submission.Metadata.SourceMetadata.GetProperty("rawRow").GetProperty("renderer").GetString()
                        ?.Contains("GeForce RTX 5070", StringComparison.Ordinal) == true,
                    "the original FurMark duration text and renderer remain raw metadata, not inferred identity/unit");

                var current = gpuOwner.Submit(submission);
                var score = backend.ReadCached(new(gpuOwner.TestResultDeviceIdentity, "gpu_all_benchmark_score"));
                var fps = backend.ReadCached(new(gpuOwner.TestResultDeviceIdentity, "gpu_all_benchmark_fps"));
                Require(ReferenceEquals(current, gpuOwner.ReadCurrent())
                    && current.Values.GpuAllBenchmarkScore?.Value == expected[index].Item1
                    && score.Status == FieldObservationStatus.Current
                    && score.Value?.Number == expected[index].Item1
                    && fps.Value?.Number == expected[index].Item2,
                    "the public ReadWriteCore owner projects the recorded GPU score and average FPS");
            }
        }

        var unsupportedContext = gpuContext with { SourceVersion = string.Empty };
        Expect<ArgumentException>(() => WindowsExplicitBenchmarkExportParser.ParseFurMarkScoresCsv(
            new MemoryStream(ReadVerifiedFixture("FurMark2_10_2_scores.csv",
                "729AEC929F80CEAFFC38FABF7C46CCAAD7BA6BC53F7DADEE5ECC3B8854964DF3")),
            unsupportedContext, TimeSpan.FromHours(-5), limits));

        var changedHeader = Encoding.UTF8.GetString(ReadVerifiedFixture("FurMark2_10_2_scores.csv",
                "729AEC929F80CEAFFC38FABF7C46CCAAD7BA6BC53F7DADEE5ECC3B8854964DF3"))
            .Replace("date,demo,platform", "date,demo,unsupported-column", StringComparison.Ordinal);
        Expect<InvalidDataException>(() => WindowsExplicitBenchmarkExportParser.ParseFurMarkScoresCsv(
            new MemoryStream(Encoding.UTF8.GetBytes(changedHeader)), gpuContext, TimeSpan.FromHours(-5), limits));

        using var missingMemberStream = new MemoryStream();
        using (var invalidArchive = new ZipArchive(missingMemberStream, ZipArchiveMode.Create, leaveOpen: true))
        using (var invalidEntry = new StreamWriter(invalidArchive.CreateEntry("Other.xml").Open()))
            invalidEntry.Write("<benchmark />");
        missingMemberStream.Position = 0;
        Expect<InvalidDataException>(() => WindowsExplicitBenchmarkExportParser.ParseThreeDMarkCpuProfile(
            missingMemberStream, "unsupported.3dmark-result", cpuContext, recordedAt, limits));

        var recordedCsv = ReadVerifiedFixture("FurMark2_10_2_scores.csv",
            "729AEC929F80CEAFFC38FABF7C46CCAAD7BA6BC53F7DADEE5ECC3B8854964DF3");
        Expect<InvalidDataException>(() => WindowsExplicitBenchmarkExportParser.ParseFurMarkScoresCsv(
            new MemoryStream(recordedCsv), gpuContext, TimeSpan.FromHours(-5),
            limits with { MaximumCsvBytes = recordedCsv.Length - 1 }));
        var explicitlySized = WindowsExplicitBenchmarkExportParser.ParseFurMarkScoresCsv(
            new MemoryStream(recordedCsv), gpuContext, TimeSpan.FromHours(-5),
            limits with { MaximumCsvBytes = recordedCsv.Length, MaximumCsvRecords = 3 });
        Require(explicitlySized.Length == 2, "caller-selected bounds accept the exact recorded input");

        Console.WriteLine("Explicit benchmark parser consumer passed: exact FurMark CSV and 3DMark CPU Profile recorded inputs use the existing latest-result owner; no benchmark process is launched.");
        return Task.CompletedTask;
    }

    private static string FindFixture(string fileName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"The offline recorded fixture '{fileName}' was not found.", candidate);
        return candidate;
    }

    private static byte[] ReadVerifiedFixture(string fileName, string expectedSha256)
    {
        var bytes = File.ReadAllBytes(FindFixture(fileName));
        var actualSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        Require(string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal),
            $"The offline recorded fixture '{fileName}' has an unexpected SHA-256.");
        return bytes;
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected {typeof(TException).Name} was not raised.");
        }
        catch (TException)
        {
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
