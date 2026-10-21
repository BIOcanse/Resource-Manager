using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Results.Explicit;

internal static class WindowsExplicitResultConsumer
{
    internal static async Task RunAsync()
    {
        using var owner = new WindowsExplicitResultOwner("recorded-host-device",
            new WindowsExplicitResultLimits(MaximumInputSamples: 64, MaximumDisplayFrames: 64));
        Require(owner.Limits.MaximumInputSamples == 64 && owner.Limits.MaximumDisplayFrames == 64,
            "the owner accepts explicit validated per-instance sample limits");
        var backend = owner.CreateBackend();
        var catalog = backend.Catalog;
        var definitions = catalog.Fields;
        Require(definitions.Length == 32, "the public catalog exposes exactly the reviewed 32 names");
        Require(definitions.Select(static item => item.Field.FieldName).ToHashSet(StringComparer.Ordinal)
                .SetEquals(WindowsExplicitResultFieldRegistry.Definitions.Select(static definition => definition.Name)),
            "the public catalog uses the exact canonical field set");
        var catalogIdentities = catalog.Devices.Select(static device => device.Identity).ToHashSet(StringComparer.Ordinal);
        Require(catalog.Devices.Length == 3 && catalogIdentities.Count == 3
            && catalogIdentities.SetEquals(new[] { owner.ManualTestDeviceIdentity, owner.TestResultDeviceIdentity, owner.ReferenceDeviceIdentity }),
            "the three Information object groups have distinct catalog device identities");
        var manualIdentity = catalog.Devices.Single(static device => device.Resource == "manual_test").Identity;
        var testResultIdentity = catalog.Devices.Single(static device => device.Resource == "test_result").Identity;
        var referenceIdentity = catalog.Devices.Single(static device => device.Resource == "reference").Identity;
        foreach (var device in catalog.Devices)
        {
            var expectedKind = device.Resource switch
            {
                "manual_test" => WindowsExplicitResultObjectKind.ManualTest,
                "test_result" => WindowsExplicitResultObjectKind.TestResult,
                "reference" => WindowsExplicitResultObjectKind.Reference,
                _ => throw new InvalidOperationException("The explicit-result catalog contains an unknown object group.")
            };
            Require(WindowsExplicitResultOwner.TryGetOwnerDeviceIdentity(device.Identity,
                    out var recoveredDevice, out var recoveredKind)
                && recoveredDevice == owner.DeviceIdentity && recoveredKind == expectedKind,
                "the typed Information identity helper reverses each object identity to its fixed owner and group");
        }

        var cpuScore = new FieldReference(testResultIdentity, "cpu_all_benchmark_score");
        Require(backend.ReadCached(cpuScore).Status == FieldObservationStatus.Unavailable
            && backend.ReadCached(cpuScore).Value is null && owner.ReadCurrent().Revision == 0,
            "cached reads of an unsubmitted result are passive and unavailable");

        var cpu = Submission(WindowsExplicitResultKind.CpuBenchmark, WindowsExplicitResultScope.Cpu,
            "cpu:recorded-0", "cpu-session-1", "recorded-benchmark-suite", "2.4", "fixed-score-method",
            "threads=4;workload=recorded");
        cpu = cpu with { Values = new() { CpuAllBenchmarkScore = Measure(0d, "score"),
            CpuAllBenchmarkDurationMs = Measure(120d, "ms") } };
        var committed = owner.Submit(cpu);
        Require(ReferenceEquals(committed, owner.ReadCurrent()), "Submit atomically publishes the owner's one current");
        Require(backend.ReadCached(cpuScore) is { Status: FieldObservationStatus.Current, Value.Number: 0d },
            "a valid zero score remains a current value");
        var collected = await backend.CollectAsync([cpuScore], CancellationToken.None);
        Require(collected.Single() == backend.ReadCached(cpuScore),
            "direct collection returns the same submitted current without launching a producer");

        var beforeFailedSubmit = owner.ReadCurrent();
        try
        {
            owner.Submit(cpu with { Values = cpu.Values with { CpuAllBenchmarkScore = Measure(1d, "fps") } });
            throw new InvalidOperationException("A unit-mismatched result was accepted.");
        }
        catch (ArgumentException) { }
        Require(ReferenceEquals(beforeFailedSubmit, owner.ReadCurrent()),
            "validation failure preserves the previous current and does not publish a partial result");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                owner.Submit(cpu, cancellation.Token);
                throw new InvalidOperationException("A canceled result submission was committed.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Require(ReferenceEquals(beforeFailedSubmit, owner.ReadCurrent()),
            "cancellation before commit preserves the previous immutable current");

        var inputMetadata = Metadata(WindowsExplicitResultScope.RawInputDevice, "rawinput:mouse-01",
            "input-session-1", "recorded-rawinput", "1", "resource-manager.rawinput-input-v1", "manual mouse report session",
            "{\"nativeSource\":\"RawInput-recorded\",\"formula\":\"resource-manager.rawinput-input-v1\"}");
        var input = new WindowsExplicitResultSubmission(1, WindowsExplicitResultKind.MouseInputManual,
            inputMetadata, new(), new("input-session-1", "rawinput:mouse-01", 0x44, 10_000_000, 100, 10_100,
                "resource-manager.rawinput-input-v1",
                [new(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports", 100, 10_100),
                 new(WindowsExplicitInputEvidenceKind.MouseTrajectoryTest, "trajectory", 100, 10_100),
                 new(WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest, "buttons", 100, 10_100)], 5000,
                [new(0x44, 100, WindowsExplicitInputSampleKind.MouseReport,
                        [WindowsExplicitInputEvidenceKind.MouseReportObservation], 0, 0, 0, null),
                 new(0x44, 5000, WindowsExplicitInputSampleKind.MouseMovement,
                        [WindowsExplicitInputEvidenceKind.MouseTrajectoryTest], 0, 0, 0, null),
                 new(0x44, 6000, WindowsExplicitInputSampleKind.MouseButtonTransition,
                        [WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest], 1, 0, 0, true),
                 new(0x44, 10_100, WindowsExplicitInputSampleKind.MouseReport,
                        [WindowsExplicitInputEvidenceKind.MouseReportObservation], 0, 0, 0, null)]));
        var inputCurrent = owner.Submit(input);
        Require(inputCurrent.Metadata?.SourceMetadata.GetRawText().Contains("RawInput-recorded", StringComparison.Ordinal) == true,
            "typed input submission retains the raw source metadata");
        Require(inputCurrent.Values.MouseAllReportRateMeasured?.Value == 1000d
            && inputCurrent.Values.MouseAllReportJitterMeasured?.Value == 0d
            && inputCurrent.Values.MouseAllTrajectoryManualTest?.Value == 0
            && inputCurrent.Values.MouseAllButtonBounceManualTest?.Value == 0
            && inputCurrent.Values.MouseAllButtonTransitionManualTest?.Value == 1,
            "mouse rate, interval jitter, movement count, and bounce count derive from the actual QPC event window");
        Require(backend.ReadCached(cpuScore).Status == FieldObservationStatus.Unavailable,
            "a new one-result current clears fields outside the submitted result");

        var reportsOnly = input with
        {
            Metadata = input.Metadata with { SessionId = "input-session-reports-only" },
            InputSession = input.InputSession! with
            {
                SessionId = "input-session-reports-only",
                Samples = [
                    new(0x44, 100, WindowsExplicitInputSampleKind.MouseReport,
                        [WindowsExplicitInputEvidenceKind.MouseReportObservation], 0, 0, 0, null),
                    new(0x44, 10_100, WindowsExplicitInputSampleKind.MouseReport,
                        [WindowsExplicitInputEvidenceKind.MouseReportObservation], 0, 0, 0, null)
                ]
            }
        };
        var reportsOnlyCurrent = owner.Submit(reportsOnly);
        Require(reportsOnlyCurrent.Values.MouseAllReportRateMeasured?.Value == 1000d
            && reportsOnlyCurrent.Values.MouseAllTrajectoryManualTest is null
            && reportsOnlyCurrent.Values.MouseAllButtonTransitionManualTest is null
            && reportsOnlyCurrent.Values.MouseAllButtonBounceManualTest is null,
            "completed action windows without their measurement samples leave those fields unknown instead of zero");
        var noInputSamples = reportsOnly with
        {
            Metadata = reportsOnly.Metadata with { SessionId = "input-session-no-samples" },
            InputSession = reportsOnly.InputSession! with
            { SessionId = "input-session-no-samples", Samples = [] }
        };
        var noInputCurrent = owner.Submit(noInputSamples);
        Require(noInputCurrent.Values.MouseAllReportRateMeasured is null
            && noInputCurrent.Values.MouseAllReportJitterMeasured is null
            && noInputCurrent.Values.MouseAllTrajectoryManualTest is null
            && noInputCurrent.Values.MouseAllButtonTransitionManualTest is null
            && noInputCurrent.Values.MouseAllButtonBounceManualTest is null,
            "an explicitly recorded input session with no event samples publishes unknowns, never numeric zeroes");

        var keyboard = new WindowsExplicitResultSubmission(1, WindowsExplicitResultKind.KeyboardInputManual,
            Metadata(WindowsExplicitResultScope.RawInputDevice, "rawinput:keyboard-01", "keyboard-session-1",
                "recorded-rawinput", "1", "resource-manager.rawinput-input-v1", "manual chord sequence", "{}"),
            new(), new("keyboard-session-1", "rawinput:keyboard-01", 0x55, 1_000_000, 10, 50,
                "resource-manager.rawinput-input-v1",
                [new(WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, "key-transitions", 10, 50),
                 new(WindowsExplicitInputEvidenceKind.KeyboardCombinationTest, "key-combination", 10, 50)], 0,
                [new(0x55, 10, WindowsExplicitInputSampleKind.KeyTransition,
                        [WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, WindowsExplicitInputEvidenceKind.KeyboardCombinationTest],
                        0x1D, 0, 0, true),
                 new(0x55, 20, WindowsExplicitInputSampleKind.KeyTransition,
                        [WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, WindowsExplicitInputEvidenceKind.KeyboardCombinationTest],
                        0x1E, 0, 0, true),
                 new(0x55, 30, WindowsExplicitInputSampleKind.KeyTransition,
                        [WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, WindowsExplicitInputEvidenceKind.KeyboardCombinationTest],
                        0x1E, 0, 0, false),
                 new(0x55, 40, WindowsExplicitInputSampleKind.KeyTransition,
                        [WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, WindowsExplicitInputEvidenceKind.KeyboardCombinationTest],
                        0x1D, 0, 0, false)]));
        var keyboardCurrent = owner.Submit(keyboard);
        Require(keyboardCurrent.Values.KeyboardAllComboManualTest?.Value == 1
            && keyboardCurrent.Values.KeyboardAllKeyTransitionManualTest?.Value == 4
            && keyboardCurrent.Values.KeyboardAllScanCodeManualTest?.Value.SequenceEqual(new uint[] { 0x1D, 0x1E, 0x1E, 0x1D }) == true,
            "keyboard combo, transitions, and scan-code sequence derive from the retained QPC event sequence");
        var scanCodes = backend.ReadCached(new(manualIdentity, "keyboard_all_scan_code_manual_test"));
        Require(scanCodes.Status == FieldObservationStatus.Current
            && scanCodes.Value?.Structure?.GetArrayLength() == 4,
            "the public Information projection retains a structured scan-code sequence");

        var display = new WindowsExplicitResultSubmission(1, WindowsExplicitResultKind.DisplayAnimationManual,
            Metadata(WindowsExplicitResultScope.DisplaySession, "display:recorded-0", "display-session-1",
                "recorded-browser-session", "1", "resource-manager.display-frame-intervals-v1", "visible animation", "{}"),
            new(), null, new("display-session-1", "display:recorded-0", true, 60,
                WindowsExplicitDisplayTimebase.BrowserPerformanceNow, 1000, 0, 200,
                "resource-manager.display-frame-intervals-v1", [0, 100, 200], null));
        var displayCurrent = owner.Submit(display);
        Require(displayCurrent.Values.DisplayAllAnimationFpsManualTest?.Value == 10d
            && displayCurrent.Values.DisplayAllFrameJitterManualTest?.Value == 0d
            && displayCurrent.DisplaySession?.FrameTicks.SequenceEqual(new long[] { 0, 100, 200 }) == true,
            "display FPS and jitter derive from retained visible-session timestamps and timebase");
        var noFrames = display with
        {
            Metadata = display.Metadata with { SessionId = "display-session-no-frames" },
            DisplaySession = display.DisplaySession! with
            { SessionId = "display-session-no-frames", FrameTicks = [] }
        };
        var noFramesCurrent = owner.Submit(noFrames);
        Require(noFramesCurrent.Values.DisplayAllAnimationFpsManualTest is null
            && noFramesCurrent.Values.DisplayAllFrameJitterManualTest is null,
            "a visible display session without retained frame timestamps leaves animation values unknown");

        var observation = new WindowsExplicitResultSubmission(1, WindowsExplicitResultKind.DisplayObservationManual,
            Metadata(WindowsExplicitResultScope.DisplaySession, "display:recorded-0", "pattern-session-1",
                "manual-pattern-observation", "1", "resource-manager.display-observation-v1", "white field", "{}"),
            new()
            {
                DisplayAllDeadPixelObservationManualTest = Measure("none observed", "text"),
                DisplayAllPatternObservationManualTest = Measure("uniform white-field pattern", "text")
            }, null, new("pattern-session-1", "display:recorded-0", true, 60,
                WindowsExplicitDisplayTimebase.BrowserPerformanceNow, 1000, 0, 100,
                "resource-manager.display-observation-v1", [], "white-field"));
        Require(owner.Submit(observation).Values.DisplayAllPatternObservationManualTest?.Value
            == "uniform white-field pattern", "display observation requires explicit visible pattern identity and submitted text");

        var memory = Submission(WindowsExplicitResultKind.MemoryTestManual, WindowsExplicitResultScope.Memory,
            "memory:system", "memory-session-1", "recorded-memory-tool", "5", "recorded-check-method",
            "range=all;pattern=recorded;version=5");
        memory = memory with { Values = new()
        {
            MemoryAllTestCoverageManualTest = Measure(UInt128.MaxValue, "B"),
            MemoryAllTestErrorCountManualTest = Measure(0UL, "count")
        } };
        owner.Submit(memory);
        var capacity = backend.ReadCached(new(manualIdentity, "memory_all_test_coverage_manual_test"));
        var errors = backend.ReadCached(new(manualIdentity, "memory_all_test_error_count_manual_test"));
        Require(capacity.Value?.Integer == UInt128.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && errors.Value?.Integer == "0", "byte capacity and zero error count remain exact unsigned values");

        var storagePerformance = Submission(WindowsExplicitResultKind.StoragePerformanceManual,
            WindowsExplicitResultScope.StorageDevice, "storage:recorded-disk-0", "storage-session-1",
            "recorded-storage-tool", "3", "recorded-throughput-iops-latency-v1", "read;queue=1;block=4KiB;duration=10s");
        storagePerformance = storagePerformance with { Values = new()
        {
            StorageAllLatencyManualTest = Measure(0d, "ms"),
            StorageAllRandomIopsManualTest = Measure(0d, "ops/s"),
            StorageAllSequentialThroughputManualTest = Measure(0d, "MB/s")
        } };
        var storageCurrent = owner.Submit(storagePerformance);
        Require(storageCurrent.Values.StorageAllLatencyManualTest?.Value == 0d
            && storageCurrent.Values.StorageAllRandomIopsManualTest?.Value == 0d
            && storageCurrent.Values.StorageAllSequentialThroughputManualTest?.Value == 0d,
            "explicit storage result preserves valid zero metrics with declared workload and source method");

        var gpu = Submission(WindowsExplicitResultKind.GpuBenchmark, WindowsExplicitResultScope.Gpu,
            "gpu:recorded-adapter-0", "gpu-session-1", "recorded-gpu-suite", "7", "recorded-render-score-v1",
            "renderer=recorded;resolution=1280x720;version=7");
        gpu = gpu with { Values = new()
        {
            GpuAllBenchmarkDurationMs = Measure(1d, "ms"),
            GpuAllBenchmarkFps = Measure(0d, "fps"),
            GpuAllBenchmarkScore = Measure(0d, "score")
        } };
        var gpuCurrent = owner.Submit(gpu);
        Require(gpuCurrent.Values.GpuAllBenchmarkFps?.Value == 0d
            && gpuCurrent.Values.GpuAllBenchmarkScore?.Value == 0d,
            "GPU result retains exact benchmark metadata and valid zero metrics");

        var selfTest = Submission(WindowsExplicitResultKind.CpuSelfTest, WindowsExplicitResultScope.Cpu,
            "cpu:recorded-0", "self-test-session-1", "recorded-self-test-tool", "2", "recorded-self-test-v1",
            "workers=4;test=recorded;version=2");
        var errorAt = new DateTimeOffset(2026, 10, 7, 18, 31, 0, TimeSpan.Zero);
        selfTest = selfTest with { Values = new()
        {
            CpuAllSelftestErrorReported = Measure("worker reported an error", "text"),
            CpuAllSelftestErrorTime = Measure(errorAt, "timestamp")
        } };
        var selfTestCurrent = owner.Submit(selfTest);
        Require(selfTestCurrent.Values.CpuAllSelftestWorkloadDeclared?.Value == selfTest.Metadata.Workload
            && backend.ReadCached(new(testResultIdentity, "cpu_all_selftest_error_time")).Value?.Text
                == errorAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "self-test error status, timestamp and workload remain source-backed typed results");

        var storageVerification = Submission(WindowsExplicitResultKind.StorageVerificationManual,
            WindowsExplicitResultScope.StorageDevice, "storage:recorded-disk-0", "storage-verify-session-1",
            "recorded-verification-tool", "1", "recorded-readback-check-v1", "range=declared;pattern=recorded");
        storageVerification = storageVerification with { Values = new()
        {
            StorageAllReadbackErrorCountManualTest = Measure(0UL, "count"),
            StorageAllVerifiedCapacityManualTest = Measure(UInt128.MaxValue, "B")
        } };
        Require(owner.Submit(storageVerification).Values.StorageAllVerifiedCapacityManualTest?.Value == UInt128.MaxValue,
            "storage verification preserves exact unsigned capacity and zero error count");

        var latency = Submission(WindowsExplicitResultKind.MemoryHierarchyLatencyManual,
            WindowsExplicitResultScope.Memory, "memory:system", "memory-latency-session-1",
            "recorded-latency-tool", "1", "recorded-pointer-chase-v1", "cores=0-3;threads=1;working-set=recorded");
        latency = latency with { Values = new()
        { MemoryAllHierarchyLatencyManualTest = Measure(0d, "ns") } };
        Require(owner.Submit(latency).Values.MemoryAllHierarchyLatencyManualTest?.Value == 0d,
            "memory hierarchy result accepts a declared zero latency without converting to another unit");

        const string rankingJson = """
            {"schemaVersion":1,"kind":"ReferenceRanking","metadata":{"deviceIdentity":"recorded-host-device","scope":"ReferenceSubject","scopeIdentity":"reference:cpu-model","sessionId":"ranking-session-1","sourceId":"user-import","sourceVersion":"1","recordedAt":"2026-10-07T18:30:00+00:00","method":"overall-score ascending","workload":"recorded cpu cohort","sourceMetadata":{"catalog":"recorded-reference","sourceRevision":"7"}},"values":{"reference_all_ranking_date":{"value":"2026-10-07","unit":"date"},"reference_all_ranking_source":{"value":"https://example.test/ranking","unit":"URL"}}}
            """;
        var ranking = owner.ImportJson(rankingJson);
        Require(ranking.Kind == WindowsExplicitResultKind.ReferenceRanking
            && backend.ReadCached(new(referenceIdentity, "reference_all_ranking_date")).Value?.Text == "2026-10-07"
            && backend.ReadCached(new(referenceIdentity, "reference_all_ranking_method_declared")).Value?.Text
                == "overall-score ascending"
            && backend.ReadCached(new(referenceIdentity, "reference_all_ranking_source")).Value?.Text
                == "https://example.test/ranking",
            "the owned bounded JSON import projects explicit source, date, and method");
        Require(ranking.Metadata?.SourceMetadata.GetRawText().Contains("sourceRevision", StringComparison.Ordinal) == true,
            "JSON import preserves the raw source metadata object");

        var beforeBadImport = owner.ReadCurrent();
        try
        {
            owner.ImportJson(rankingJson.Replace("\"values\":{", "\"values\":{\"future_field\":1,", StringComparison.Ordinal));
            throw new InvalidOperationException("An unknown result field was accepted.");
        }
        catch (InvalidDataException) { }
        Require(ReferenceEquals(beforeBadImport, owner.ReadCurrent()),
            "unknown JSON fields are rejected without replacing current");
        foreach (var malformedJson in new[]
        {
            rankingJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            new string(' ', owner.Limits.MaximumImportBytes + 1)
        })
        {
            try
            {
                owner.ImportJson(malformedJson);
                throw new InvalidOperationException("A duplicate-property or oversized import was accepted.");
            }
            catch (InvalidDataException) { }
        }
        Require(ReferenceEquals(beforeBadImport, owner.ReadCurrent()),
            "duplicate-property and oversized JSON imports preserve the prior current");
        using (var lowNodeOwner = new WindowsExplicitResultOwner("node-bound-owner",
            new WindowsExplicitResultLimits(MaximumJsonNodeCount: 4)))
        {
            try
            {
                lowNodeOwner.ImportJson(rankingJson);
                throw new InvalidOperationException("An import over the configured JSON node limit was accepted.");
            }
            catch (InvalidDataException) { }
            Require(lowNodeOwner.ReadCurrent().Revision == 0,
                "the configured iterative JSON node limit rejects before replacing current");
        }
        try
        {
            _ = new WindowsExplicitResultOwner("invalid-limits", new WindowsExplicitResultLimits(MaximumJsonDepth: 0));
            throw new InvalidOperationException("An invalid explicit-result budget was accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("ReadWriteCore explicit-result consumer passed: exact 32-field catalog, passive current, typed input provenance, valid zero/exact bytes, bounded import and failure/cancellation preservation.");
    }

    private static WindowsExplicitResultSubmission Submission(WindowsExplicitResultKind kind,
        WindowsExplicitResultScope scope, string scopeIdentity, string sessionId, string sourceId,
        string sourceVersion, string method, string workload)
        => new(1, kind, Metadata(scope, scopeIdentity, sessionId, sourceId, sourceVersion, method, workload, "{}"), new());

    private static WindowsExplicitResultMetadata Metadata(WindowsExplicitResultScope scope,
        string scopeIdentity, string sessionId, string sourceId, string sourceVersion,
        string method, string workload, string sourceMetadata)
    {
        using var document = JsonDocument.Parse(sourceMetadata);
        return new("recorded-host-device", scope, scopeIdentity, sessionId, sourceId, sourceVersion,
            new DateTimeOffset(2026, 10, 7, 18, 30, 0, TimeSpan.Zero), method, workload, document.RootElement.Clone());
    }

    private static WindowsExplicitMeasurement<T> Measure<T>(T value, string unit) => new(value, unit);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
