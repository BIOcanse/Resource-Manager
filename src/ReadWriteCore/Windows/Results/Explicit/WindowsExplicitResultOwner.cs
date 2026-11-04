using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

/// <summary>
/// Owns one latest explicit result current. Submission/import is the only mutation;
/// discovery, direct reads, and subscription captures never launch a producer.
/// </summary>
public sealed class WindowsExplicitResultOwner : IDisposable
{
    public const int SupportedSchemaVersion = 1;

    private readonly SemaphoreSlim submissionGate = new(1, 1);
    private readonly InformationReadWriteBackend backend;
    private readonly JsonSerializerOptions importOptions;
    private readonly JsonDocumentOptions documentOptions;
    private WindowsExplicitResultCurrent current;
    private int disposed;

    public string DeviceIdentity { get; }
    public string ManualTestDeviceIdentity => GetInformationDeviceIdentity(DeviceIdentity, WindowsExplicitResultObjectKind.ManualTest);
    public string TestResultDeviceIdentity => GetInformationDeviceIdentity(DeviceIdentity, WindowsExplicitResultObjectKind.TestResult);
    public string ReferenceDeviceIdentity => GetInformationDeviceIdentity(DeviceIdentity, WindowsExplicitResultObjectKind.Reference);
    public SamplingPublicationSignal Publications { get; } = new();
    public WindowsExplicitResultLimits Limits { get; }

    public static string GetInformationDeviceIdentity(string deviceIdentity, WindowsExplicitResultObjectKind objectKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        return $"{deviceIdentity}::explicit-result::{ResourceFor(objectKind)}";
    }

    public static bool TryGetOwnerDeviceIdentity(string informationDeviceIdentity,
        out string deviceIdentity, out WindowsExplicitResultObjectKind objectKind)
    {
        deviceIdentity = string.Empty;
        objectKind = default;
        if (string.IsNullOrWhiteSpace(informationDeviceIdentity)) return false;

        foreach (var candidate in new[]
            { WindowsExplicitResultObjectKind.ManualTest, WindowsExplicitResultObjectKind.TestResult,
                WindowsExplicitResultObjectKind.Reference })
        {
            var suffix = $"::explicit-result::{ResourceFor(candidate)}";
            if (!informationDeviceIdentity.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var ownerIdentity = informationDeviceIdentity[..^suffix.Length];
            if (string.IsNullOrWhiteSpace(ownerIdentity)) return false;
            deviceIdentity = ownerIdentity;
            objectKind = candidate;
            return true;
        }

        return false;
    }

    private static string ResourceFor(WindowsExplicitResultObjectKind objectKind) => objectKind switch
    {
        WindowsExplicitResultObjectKind.ManualTest => "manual_test",
        WindowsExplicitResultObjectKind.TestResult => "test_result",
        WindowsExplicitResultObjectKind.Reference => "reference",
        _ => throw new ArgumentOutOfRangeException(nameof(objectKind))
    };

    public WindowsExplicitResultOwner(string deviceIdentity, WindowsExplicitResultLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        DeviceIdentity = deviceIdentity;
        Limits = (limits ?? new WindowsExplicitResultLimits()).Validate();
        importOptions = new()
        {
            MaxDepth = Limits.MaximumJsonDepth,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        documentOptions = new()
        {
            MaxDepth = Limits.MaximumJsonDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        };
        var emptyValues = new WindowsExplicitResultValues();
        current = new(0, null, null, emptyValues, null, null,
            WindowsExplicitResultFieldRegistry.Project(DeviceIdentity, null, emptyValues));
        backend = new(ReadInformation, ObserveAsync, static _ => new(true, null, []));
    }

    public InformationReadWriteBackend CreateBackend() => backend;

    /// <summary>Returns the same immutable current observed by Information and subscribers.</summary>
    public WindowsExplicitResultCurrent ReadCurrent() => Volatile.Read(ref current);

    public DeviceInformationSnapshot ReadInformation() => ReadCurrent().Information;

    /// <summary>Submit one already-recorded typed result; no measurement is started here.</summary>
    public WindowsExplicitResultCurrent Submit(WindowsExplicitResultSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var prepared = Prepare(submission);
        submissionGate.Wait(cancellationToken);
        WindowsExplicitResultCurrent committed;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var revision = checked(ReadCurrent().Revision + 1);
            committed = new(revision, prepared.Kind, prepared.Metadata, prepared.Values,
                prepared.InputSession, prepared.DisplaySession,
                WindowsExplicitResultFieldRegistry.Project(DeviceIdentity, prepared.Metadata, prepared.Values));
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref current, committed);
        }
        finally
        {
            submissionGate.Release();
        }

        Publications.Publish([WindowsExplicitResultFieldRegistry.CollectionItemId]);
        return committed;
    }

    /// <summary>
    /// Import only the owned version-1 typed interchange document. This is not a parser
    /// for CPU-Z, FurMark, Prime95, or other vendor formats.
    /// </summary>
    public WindowsExplicitResultCurrent ImportJson(string json,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > Limits.MaximumImportBytes)
            throw new InvalidDataException("The explicit-result interchange document exceeds its size bound.");
        try
        {
            using var document = JsonDocument.Parse(json, documentOptions);
            RejectDuplicateProperties(document.RootElement, Limits.MaximumJsonNodeCount);
            var submission = JsonSerializer.Deserialize<WindowsExplicitResultSubmission>(json, importOptions)
                ?? throw new InvalidDataException("The explicit-result document has no submission.");
            return Submit(submission, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The explicit-result interchange document is invalid.", exception);
        }
    }

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new InformationCollectionBorrow(backend, Publications,
            WindowsExplicitResultFieldRegistry.CollectionItemId, (selection, permit, token) =>
            {
                token.ThrowIfCancellationRequested();
                var snapshot = DeviceInformationSnapshot.Empty;
                var observed = false;
                var committed = permit.TryPublish(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref disposed) == 0) { snapshot = ReadInformation(); observed = true; }
                });
                return ValueTask.FromResult(new FieldCollectionResult(
                    InformationReadWriteBackend.Read(selection, snapshot), committed && observed));
            });
    }

    private ValueTask<DeviceInformationSnapshot> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadInformation());
    }

    private PreparedSubmission Prepare(WindowsExplicitResultSubmission submission)
    {
        if (submission.SchemaVersion != SupportedSchemaVersion)
            throw new ArgumentException("The explicit-result schema version is unsupported.", nameof(submission));
        ArgumentNullException.ThrowIfNull(submission.Metadata);
        ArgumentNullException.ThrowIfNull(submission.Values);
        var metadata = submission.Metadata;
        Require(metadata.DeviceIdentity == DeviceIdentity, "The submitted device identity does not match this fixed owner.");
        RequireText(metadata.ScopeIdentity, "scopeIdentity");
        RequireText(metadata.SessionId, "sessionId");
        RequireText(metadata.SourceId, "sourceId");
        RequireText(metadata.SourceVersion, "sourceVersion");
        RequireText(metadata.Method, "method");
        RequireText(metadata.Workload, "workload");
        Require(metadata.RecordedAt != default, "recordedAt must be an actual recorded timestamp.");
        Require(metadata.Scope == ScopeFor(submission.Kind), "The kind and declared scope do not match.");
        var sourceMetadata = CloneBoundedSourceMetadata(metadata.SourceMetadata);
        metadata = metadata with { SourceMetadata = sourceMetadata };

        var values = AddMetadataFields(submission.Kind, metadata, submission.Values);
        ValidateEvidence(submission.Kind, metadata, values, submission.InputSession, submission.DisplaySession);
        values = AddSampleDerivedValues(submission.Kind, values, submission.InputSession, submission.DisplaySession);
        ValidateValues(submission.Kind, values);
        return new(submission.Kind, metadata, values, submission.InputSession, submission.DisplaySession);
    }

    private static WindowsExplicitResultValues AddMetadataFields(WindowsExplicitResultKind kind,
        WindowsExplicitResultMetadata metadata, WindowsExplicitResultValues values)
    {
        if (kind == WindowsExplicitResultKind.CpuBenchmark)
        {
            RequireMetadataTextMatches(values.CpuAllBenchmarkWorkloadDeclared, metadata.Workload,
                WindowsExplicitResultFieldRegistry.CpuAllBenchmarkWorkloadDeclared.Name);
            return values with
            {
                CpuAllBenchmarkWorkloadDeclared = new(metadata.Workload,
                    WindowsExplicitResultFieldRegistry.CpuAllBenchmarkWorkloadDeclared.Unit)
            };
        }
        if (kind == WindowsExplicitResultKind.CpuSelfTest)
        {
            RequireMetadataTextMatches(values.CpuAllSelftestWorkloadDeclared, metadata.Workload,
                WindowsExplicitResultFieldRegistry.CpuAllSelftestWorkloadDeclared.Name);
            return values with
            {
                CpuAllSelftestWorkloadDeclared = new(metadata.Workload,
                    WindowsExplicitResultFieldRegistry.CpuAllSelftestWorkloadDeclared.Unit)
            };
        }
        if (kind == WindowsExplicitResultKind.ReferenceRanking)
        {
            RequireMetadataTextMatches(values.ReferenceAllRankingMethodDeclared, metadata.Method,
                WindowsExplicitResultFieldRegistry.ReferenceAllRankingMethodDeclared.Name);
            return values with
            {
                ReferenceAllRankingMethodDeclared = new(metadata.Method,
                    WindowsExplicitResultFieldRegistry.ReferenceAllRankingMethodDeclared.Unit)
            };
        }
        return values;
    }

    private static WindowsExplicitResultValues AddSampleDerivedValues(WindowsExplicitResultKind kind,
        WindowsExplicitResultValues values, WindowsExplicitInputSession? input,
        WindowsExplicitDisplaySession? display)
        => kind switch
        {
            WindowsExplicitResultKind.MouseInputManual => DeriveMouseMeasurements(input!),
            WindowsExplicitResultKind.KeyboardInputManual => DeriveKeyboardMeasurements(input!),
            WindowsExplicitResultKind.DisplayAnimationManual => DeriveDisplayMeasurements(display!),
            _ => values
        };

    private static WindowsExplicitResultValues DeriveMouseMeasurements(WindowsExplicitInputSession session)
    {
        var evidence = session.Evidence.Select(static item => item.Kind).ToHashSet();
        var reports = evidence.Contains(WindowsExplicitInputEvidenceKind.MouseReportObservation)
            ? session.Samples.Where(static sample => sample.Kind == WindowsExplicitInputSampleKind.MouseReport
                    && sample.Purposes.Contains(WindowsExplicitInputEvidenceKind.MouseReportObservation))
                .Select(static sample => sample.Qpc).ToArray()
            : Array.Empty<long>();
        var (rate, jitter) = IntervalMetrics(reports, session.QpcFrequency);
        var hasTrajectory = evidence.Contains(WindowsExplicitInputEvidenceKind.MouseTrajectoryTest);
        var hasButtons = evidence.Contains(WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest);
        var movementSamples = hasTrajectory ? session.Samples.Where(static sample => sample.Kind == WindowsExplicitInputSampleKind.MouseMovement
            && sample.Purposes.Contains(WindowsExplicitInputEvidenceKind.MouseTrajectoryTest)).ToArray()
            : Array.Empty<WindowsExplicitInputSample>();
        var movementPoints = movementSamples.Length > 0
            ? checked((ulong)movementSamples.Count(static sample => sample.DeltaX != 0 || sample.DeltaY != 0)) : (ulong?)null;
        var buttonSamples = hasButtons ? session.Samples.Where(static sample => sample.Kind == WindowsExplicitInputSampleKind.MouseButtonTransition
            && sample.Purposes.Contains(WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest)).ToImmutableArray()
            : ImmutableArray<WindowsExplicitInputSample>.Empty;
        var buttonTransitions = buttonSamples.Length > 0 ? checked((ulong)buttonSamples.Length) : (ulong?)null;
        var bounces = buttonSamples.Length > 0 ? CountRapidButtonAlternations(buttonSamples, session.BounceWindowQpc) : (ulong?)null;
        return new()
        {
            MouseAllReportJitterMeasured = jitter is { } jitterMs ? new(jitterMs, "ms") : null,
            MouseAllReportRateMeasured = rate is { } rateHz ? new(rateHz, "Hz") : null,
            MouseAllTrajectoryManualTest = movementPoints is { } points ? new(points, "points") : null,
            MouseAllButtonBounceManualTest = bounces is { } bounceCount ? new(bounceCount, "count") : null,
            MouseAllButtonTransitionManualTest = buttonTransitions is { } buttonCount ? new(buttonCount, "event") : null
        };
    }

    private static WindowsExplicitResultValues DeriveKeyboardMeasurements(WindowsExplicitInputSession session)
    {
        var pressed = new HashSet<uint>();
        var observedChords = new HashSet<string>(StringComparer.Ordinal);
        var evidence = session.Evidence.Select(static item => item.Kind).ToHashSet();
        var transitionSamples = evidence.Contains(WindowsExplicitInputEvidenceKind.KeyboardTransitionTest)
            ? session.Samples.Where(static sample => sample.Kind == WindowsExplicitInputSampleKind.KeyTransition
                    && sample.Purposes.Contains(WindowsExplicitInputEvidenceKind.KeyboardTransitionTest))
                .ToArray()
            : Array.Empty<WindowsExplicitInputSample>();
        var comboSamples = evidence.Contains(WindowsExplicitInputEvidenceKind.KeyboardCombinationTest)
            ? session.Samples.Where(static sample => sample.Kind == WindowsExplicitInputSampleKind.KeyTransition
                    && sample.Purposes.Contains(WindowsExplicitInputEvidenceKind.KeyboardCombinationTest))
                .ToArray()
            : Array.Empty<WindowsExplicitInputSample>();
        foreach (var sample in comboSamples)
        {
            if (sample.IsDown == true)
            {
                pressed.Add(sample.Code);
                if (pressed.Count >= 2)
                    observedChords.Add(string.Join(",", pressed.Order()));
            }
            else
            {
                pressed.Remove(sample.Code);
            }
        }
        return new()
        {
            KeyboardAllComboManualTest = comboSamples.Length > 0
                ? new(checked((ulong)observedChords.Count), "event") : null,
            KeyboardAllKeyTransitionManualTest = transitionSamples.Length > 0
                ? new(checked((ulong)transitionSamples.Length), "event") : null,
            KeyboardAllScanCodeManualTest = transitionSamples.Length > 0
                ? new(transitionSamples.Select(static sample => sample.Code).ToImmutableArray(), "code") : null
        };
    }

    private static WindowsExplicitResultValues DeriveDisplayMeasurements(WindowsExplicitDisplaySession session)
    {
        var (rate, jitter) = IntervalMetrics(session.FrameTicks, session.TicksPerSecond);
        return new()
        {
            DisplayAllAnimationFpsManualTest = rate is { } fps ? new(fps, "fps") : null,
            DisplayAllFrameJitterManualTest = jitter is { } jitterMs ? new(jitterMs, "ms") : null
        };
    }

    private static (double? Rate, double? JitterMilliseconds) IntervalMetrics(
        IReadOnlyList<long> timestamps, long ticksPerSecond)
    {
        if (timestamps.Count < 2) return (null, null);
        var intervals = new double[timestamps.Count - 1];
        for (var index = 1; index < timestamps.Count; index++)
        {
            var delta = timestamps[index] - timestamps[index - 1];
            if (delta <= 0) return (null, null);
            intervals[index - 1] = delta * 1000d / ticksPerSecond;
        }
        var elapsed = timestamps[timestamps.Count - 1] - timestamps[0];
        if (elapsed <= 0) return (null, null);
        var mean = intervals.Average();
        var variance = intervals.Sum(interval => (interval - mean) * (interval - mean)) / intervals.Length;
        var rate = (timestamps.Count - 1d) * ticksPerSecond / elapsed;
        return (double.IsFinite(rate) ? rate : null, double.IsFinite(variance) ? Math.Sqrt(variance) : null);
    }

    private static ulong CountRapidButtonAlternations(ImmutableArray<WindowsExplicitInputSample> samples,
        long bounceWindowQpc)
    {
        ulong total = 0;
        foreach (var group in samples.Where(static sample => sample.Kind == WindowsExplicitInputSampleKind.MouseButtonTransition)
            .GroupBy(static sample => sample.Code))
        {
            var transitions = group.ToArray();
            if (transitions.Length < 3) continue;
            var burstCount = 1;
            for (var index = 1; index < transitions.Length; index++)
            {
                if (transitions[index].Qpc - transitions[index - 1].Qpc <= bounceWindowQpc)
                {
                    burstCount++;
                }
                else
                {
                    if (burstCount > 2) total = checked(total + (ulong)(burstCount - 2));
                    burstCount = 1;
                }
            }
            if (burstCount > 2) total = checked(total + (ulong)(burstCount - 2));
        }
        return total;
    }

    private static void ValidateValues(WindowsExplicitResultKind kind, WindowsExplicitResultValues values)
    {
        var allowed = AllowedFields(kind).ToHashSet(StringComparer.Ordinal);
        var entries = WindowsExplicitResultFieldRegistry.Entries(values);
        Require(entries.Length > 0 || kind is WindowsExplicitResultKind.MouseInputManual
                or WindowsExplicitResultKind.KeyboardInputManual or WindowsExplicitResultKind.DisplayAnimationManual,
            "A submission must contain a value or a bounded input/display session whose measurements remain unavailable.");
        foreach (var entry in entries)
        {
            Require(allowed.Contains(entry.Name), $"{entry.Name} does not belong to {kind}.");
            var definition = WindowsExplicitResultFieldRegistry.Definitions.Single(item => item.Name == entry.Name);
            Require(entry.Unit == definition.Unit, $"{entry.Name} must declare unit '{definition.Unit}'.");
            switch (entry.Value)
            {
                case double number:
                    Require(double.IsFinite(number) && number >= 0,
                        $"{entry.Name} must be a finite non-negative measurement.");
                    break;
                case string text:
                    RequireText(text, entry.Name);
                    break;
                case ImmutableArray<uint> codes:
                    Require(!codes.IsDefault, $"{entry.Name} must be an initialized scan-code sequence.");
                    break;
                case DateTimeOffset timestamp:
                    Require(timestamp != default, $"{entry.Name} must be an actual recorded timestamp.");
                    break;
                case DateOnly date:
                    Require(date != default, $"{entry.Name} must be an actual ranking date.");
                    break;
                case ulong:
                case UInt128:
                    break;
                default:
                    throw new ArgumentException($"{entry.Name} has an unsupported typed result.");
            }
        }

        var names = entries.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal);
        switch (kind)
        {
            case WindowsExplicitResultKind.CpuBenchmark:
                Require(names.Contains("cpu_all_benchmark_score") || names.Contains("cpu_all_benchmark_duration_ms"),
                    "A CPU benchmark result requires a reported score or duration.");
                break;
            case WindowsExplicitResultKind.GpuBenchmark:
                Require(names.Overlaps(new[] { "gpu_all_benchmark_duration_ms", "gpu_all_benchmark_fps", "gpu_all_benchmark_score" }),
                    "A GPU benchmark result requires a reported metric.");
                break;
            case WindowsExplicitResultKind.CpuSelfTest:
                Require(names.Contains("cpu_all_selftest_error_reported"),
                    "A self-test must explicitly report its error status (for example, 'none').");
                var errorText = values.CpuAllSelftestErrorReported!.Value;
                var noError = string.Equals(errorText, "none", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(errorText, "no error", StringComparison.OrdinalIgnoreCase);
                Require(noError != names.Contains("cpu_all_selftest_error_time"),
                    "A self-test error time is required only when an error was actually reported.");
                break;
            case WindowsExplicitResultKind.ReferenceRanking:
                Require(names.Contains("reference_all_ranking_date") && names.Contains("reference_all_ranking_method_declared")
                    && names.Contains("reference_all_ranking_source"),
                    "A reference ranking requires date, method, and source URL.");
                var url = values.ReferenceAllRankingSource?.Value;
                Require(Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                    && parsed.Scheme is "http" or "https", "The ranking source must be an absolute HTTP(S) URL.");
                break;
        }
    }

    private void ValidateEvidence(WindowsExplicitResultKind kind, WindowsExplicitResultMetadata metadata,
        WindowsExplicitResultValues values, WindowsExplicitInputSession? input,
        WindowsExplicitDisplaySession? display)
    {
        if (kind is WindowsExplicitResultKind.MouseInputManual or WindowsExplicitResultKind.KeyboardInputManual)
        {
            Require(metadata.Scope == WindowsExplicitResultScope.RawInputDevice,
                "Input result scope must identify the actual RawInput device.");
            Require(input is not null, "Input results require recorded native-device/QPC session provenance.");
            ValidateInputSession(metadata, input!, kind);
            Require(WindowsExplicitResultFieldRegistry.Entries(values).IsEmpty,
                "Mouse/keyboard measurements are derived from their recorded QPC event samples, not caller scalars.");
        }
        else
        {
            Require(input is null, "RawInput evidence is accepted only with mouse or keyboard results.");
        }

        if (kind is WindowsExplicitResultKind.DisplayAnimationManual or WindowsExplicitResultKind.DisplayObservationManual)
        {
            Require(metadata.Scope == WindowsExplicitResultScope.DisplaySession,
                "Display result scope must identify the actual display session.");
            Require(display is not null, "Display results require explicit session, visibility, refresh, and timebase context.");
            ValidateDisplaySession(metadata, display!, kind, values);
            if (kind == WindowsExplicitResultKind.DisplayAnimationManual)
                Require(WindowsExplicitResultFieldRegistry.Entries(values).IsEmpty,
                    "Display animation values are derived from the recorded frame timestamps, not caller scalars.");
        }
        else
        {
            Require(display is null, "Display evidence is accepted only with display result kinds.");
        }
    }

    private void ValidateInputSession(WindowsExplicitResultMetadata metadata,
        WindowsExplicitInputSession input, WindowsExplicitResultKind kind)
    {
        Require(input.SessionId == metadata.SessionId && input.ScopeIdentity == metadata.ScopeIdentity,
            "RawInput session identity does not match the submitted result scope/session.");
        Require(input.DeviceHandle != 0 && input.QpcFrequency > 0 && input.StartQpc >= 0
            && input.EndQpc > input.StartQpc && input.BounceWindowQpc >= 0,
            "RawInput device handle and QPC session bounds are required.");
        Require(input.FormulaVersion == "resource-manager.rawinput-input-v1"
            && metadata.Method == input.FormulaVersion,
            "Input measurements must declare the implemented raw-input/QPC formula version.");
        Require(!input.Evidence.IsDefaultOrEmpty && input.Evidence.Length <= 5,
            "Input results require bounded completed test windows; an empty sample source is not a measured zero.");
        var evidenceByKind = new Dictionary<WindowsExplicitInputEvidenceKind, WindowsExplicitInputEvidence>();
        var actionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in input.Evidence)
        {
            if (evidence is null) throw new ArgumentException("Input evidence entries cannot be null.");
            Require(AllowedInputEvidence(kind).Contains(evidence.Kind),
                "Input test evidence does not match the selected mouse/keyboard domain.");
            RequireText(evidence.ActionId, "input evidence actionId");
            Require(evidence.StartQpc >= input.StartQpc && evidence.EndQpc <= input.EndQpc
                && evidence.EndQpc > evidence.StartQpc, "Input test evidence must have an actual in-session QPC window.");
            Require(evidenceByKind.TryAdd(evidence.Kind, evidence) && actionIds.Add(evidence.ActionId),
                "Input evidence kinds and action IDs must be unique in one result.");
        }
        if (evidenceByKind.ContainsKey(WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest))
            Require(input.BounceWindowQpc > 0, "Mouse button-bounce observation requires its declared QPC threshold window.");
        Require(!input.Samples.IsDefault && input.Samples.Length <= Limits.MaximumInputSamples,
            "RawInput sample sequence must be initialized and within its size bound.");
        long previous = input.StartQpc;
        var buttonStates = new Dictionary<uint, bool>();
        var keyStates = new Dictionary<uint, bool>();
        foreach (var sample in input.Samples)
        {
            if (sample is null) throw new ArgumentException("RawInput samples cannot be null.");
            Require(Enum.IsDefined(sample.Kind), "RawInput sample kind must be one of the closed typed kinds.");
            Require(sample.DeviceHandle == input.DeviceHandle && sample.Qpc >= previous
                && sample.Qpc >= input.StartQpc && sample.Qpc <= input.EndQpc,
                "RawInput samples must preserve the selected device and ordered in-window QPC timestamps.");
            Require(!sample.Purposes.IsDefaultOrEmpty
                && sample.Purposes.Distinct().Count() == sample.Purposes.Length,
                "Each RawInput sample must have a nonempty unique set of declared test purposes.");
            var isMouse = sample.Kind is WindowsExplicitInputSampleKind.MouseReport
                or WindowsExplicitInputSampleKind.MouseMovement or WindowsExplicitInputSampleKind.MouseButtonTransition;
            Require(isMouse == (kind == WindowsExplicitResultKind.MouseInputManual),
                "RawInput event kind does not match the submitted mouse/keyboard session.");
            if (sample.Kind is WindowsExplicitInputSampleKind.KeyTransition
                or WindowsExplicitInputSampleKind.MouseButtonTransition)
                Require(sample.IsDown.HasValue, "Transition samples must retain their down/up state.");
            if (sample.Kind == WindowsExplicitInputSampleKind.KeyTransition)
            {
                Require(sample.Code != 0, "Keyboard transition samples must retain their scan code.");
                Require(!keyStates.TryGetValue(sample.Code, out var lastKeyState) || lastKeyState != sample.IsDown!.Value,
                    "Keyboard samples must contain state transitions, not repeated key-down/up reports.");
                keyStates[sample.Code] = sample.IsDown!.Value;
            }
            if (sample.Kind == WindowsExplicitInputSampleKind.MouseButtonTransition)
            {
                Require(sample.Code != 0, "Mouse button transitions must retain their button code.");
                Require(!buttonStates.TryGetValue(sample.Code, out var lastButtonState) || lastButtonState != sample.IsDown!.Value,
                    "Mouse button samples must contain state transitions, not repeated down/up reports.");
                buttonStates[sample.Code] = sample.IsDown!.Value;
            }
            foreach (var purpose in sample.Purposes)
            {
                Require(evidenceByKind.TryGetValue(purpose, out var item)
                    && sample.Qpc >= item.StartQpc && sample.Qpc <= item.EndQpc,
                    "Every RawInput sample purpose must have a matching completed action window.");
                Require((sample.Kind, purpose) switch
                {
                    (WindowsExplicitInputSampleKind.MouseReport, WindowsExplicitInputEvidenceKind.MouseReportObservation) => true,
                    (WindowsExplicitInputSampleKind.MouseMovement, WindowsExplicitInputEvidenceKind.MouseTrajectoryTest) => true,
                    (WindowsExplicitInputSampleKind.MouseButtonTransition, WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest) => true,
                    (WindowsExplicitInputSampleKind.KeyTransition, WindowsExplicitInputEvidenceKind.KeyboardTransitionTest) => true,
                    (WindowsExplicitInputSampleKind.KeyTransition, WindowsExplicitInputEvidenceKind.KeyboardCombinationTest) => true,
                    _ => false
                }, "RawInput sample kind and declared test purpose do not match.");
            }
            previous = sample.Qpc;
        }

        // Default preserves the original recorded-input contract: older typed submissions
        // have derived samples but do not claim that their raw packets were retained.
        Require(input.RawEvents.IsDefault || input.RawEvents.Length <= Limits.MaximumInputSamples,
            "RawInput packet sequence must be initialized or absent and within its size bound.");
        if (input.RawEvents.IsDefault)
        {
            Require(input.MaximumRawInputBytes == 0,
                "An absent RawInput packet sequence cannot declare a packet-byte bound.");
        }
        else
        {
            Require(input.MaximumRawInputBytes is >= 40 and <= 1_048_576,
                "Retained RawInput packets require a finite configured byte bound.");
            long previousRawQpc = input.StartQpc;
            foreach (var raw in input.RawEvents)
            {
                if (raw is null) throw new ArgumentException("RawInput packet entries cannot be null.");
                Require(Enum.IsDefined(raw.Kind), "RawInput packet kind must be one of the closed typed kinds.");
                var isMousePacket = raw.Kind == WindowsExplicitRawInputPacketKind.Mouse;
                Require(isMousePacket == (kind == WindowsExplicitResultKind.MouseInputManual),
                    "RawInput packet kind does not match the submitted mouse/keyboard session.");
                var minimumPacketBytes = isMousePacket ? 40u : 32u;
                Require(raw.DeviceHandle == input.DeviceHandle && raw.QpcAtDispatch >= previousRawQpc
                    && raw.QpcAtDispatch >= input.StartQpc && raw.QpcAtDispatch <= input.EndQpc,
                    "RawInput packets must preserve the selected device and ordered in-session QPC timestamps.");
                Require(raw.InputSizeBytes >= minimumPacketBytes
                    && raw.InputSizeBytes <= input.MaximumRawInputBytes && raw.MessageInputCode <= 1,
                    "RawInput packet size/input code is outside the retained session bounds.");
                Require(!raw.Purposes.IsDefault
                    && raw.Purposes.Distinct().Count() == raw.Purposes.Length,
                    "RawInput packet purposes must be an initialized unique sequence.");

                if (isMousePacket)
                {
                    Require(raw.KeyboardMakeCode == 0 && raw.KeyboardFlags == 0 && raw.KeyboardReserved == 0
                        && raw.VirtualKey == 0 && raw.KeyboardMessage == 0 && raw.KeyboardExtraInformation == 0,
                        "RAWMOUSE packets cannot carry RAWKEYBOARD fields.");
                }
                else
                {
                    Require(raw.MouseFlags == 0 && raw.MouseButtonsUnion == 0 && raw.MouseButtonFlags == 0
                        && raw.MouseButtonData == 0 && raw.MouseRawButtons == 0 && raw.MouseX == 0
                        && raw.MouseY == 0 && raw.MouseExtraInformation == 0,
                        "RAWKEYBOARD packets cannot carry RAWMOUSE fields.");
                    Require(raw.KeyboardReserved == 0 && raw.KeyboardMakeCode <= byte.MaxValue
                        && raw.KeyboardMakeCode != 0x00FF && (raw.KeyboardFlags & ~0x0007) == 0
                        && (raw.KeyboardFlags & 0x0006) != 0x0006,
                        "RAWKEYBOARD reserved/scan-code/flag fields are outside the implemented format.");
                }

                foreach (var purpose in raw.Purposes)
                {
                    Require(Enum.IsDefined(purpose) && AllowedInputEvidence(kind).Contains(purpose)
                        && evidenceByKind.TryGetValue(purpose, out var action)
                        && raw.QpcAtDispatch >= action.StartQpc && raw.QpcAtDispatch <= action.EndQpc,
                        "Each retained RawInput purpose must match a completed in-session action window.");
                }
                previousRawQpc = raw.QpcAtDispatch;
            }
        }
    }

    private static IEnumerable<WindowsExplicitInputEvidenceKind> AllowedInputEvidence(WindowsExplicitResultKind kind)
        => kind == WindowsExplicitResultKind.MouseInputManual
            ? [WindowsExplicitInputEvidenceKind.MouseReportObservation, WindowsExplicitInputEvidenceKind.MouseTrajectoryTest,
                WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest]
            : [WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, WindowsExplicitInputEvidenceKind.KeyboardCombinationTest];

    private void ValidateDisplaySession(WindowsExplicitResultMetadata metadata,
        WindowsExplicitDisplaySession display, WindowsExplicitResultKind kind,
        WindowsExplicitResultValues values)
    {
        Require(display.SessionId == metadata.SessionId && display.ScopeIdentity == metadata.ScopeIdentity,
            "Display session identity does not match the submitted result scope/session.");
        Require(Enum.IsDefined(display.Timebase), "Display session timebase must be a closed typed timebase.");
        Require(display.Visible && double.IsFinite(display.RefreshRateHz) && display.RefreshRateHz > 0
            && display.TicksPerSecond > 0 && display.StartTicks >= 0 && display.EndTicks > display.StartTicks,
            "Display result requires a visible session with actual refresh and monotonic timebase bounds.");
        var expectedFormula = kind == WindowsExplicitResultKind.DisplayAnimationManual
            ? "resource-manager.display-frame-intervals-v1" : "resource-manager.display-observation-v1";
        Require(display.FormulaVersion == expectedFormula && metadata.Method == expectedFormula,
            "Display results must declare the implemented frame-interval or observation method.");
        Require(!display.FrameTicks.IsDefault && display.FrameTicks.Length <= Limits.MaximumDisplayFrames,
            "Display frame timestamp sequence must be initialized and within its size bound.");
        long? previous = null;
        foreach (var tick in display.FrameTicks)
        {
            Require((previous is null || tick > previous.Value) && tick >= display.StartTicks && tick <= display.EndTicks,
                "Display frame timestamps must be ordered inside the declared timebase window.");
            previous = tick;
        }
        var entries = WindowsExplicitResultFieldRegistry.Entries(values);
        if (kind == WindowsExplicitResultKind.DisplayObservationManual)
        {
            RequireText(display.PatternId, "patternId");
            Require(entries.Any(static entry => entry.Name is "display_all_pattern_observation_manual_test"
                or "display_all_dead_pixel_observation_manual_test"), "Pattern observation text is required.");
        }
    }

    private static IEnumerable<string> AllowedFields(WindowsExplicitResultKind kind) => kind switch
    {
        WindowsExplicitResultKind.MouseInputManual => [
            "mouse_all_report_jitter_measured", "mouse_all_report_rate_measured", "mouse_all_trajectory_manual_test",
            "mouse_all_button_bounce_manual_test", "mouse_all_button_transition_manual_test"],
        WindowsExplicitResultKind.KeyboardInputManual => [
            "keyboard_all_combo_manual_test", "keyboard_all_key_transition_manual_test", "keyboard_all_scan_code_manual_test"],
        WindowsExplicitResultKind.DisplayAnimationManual => [
            "display_all_animation_fps_manual_test", "display_all_frame_jitter_manual_test"],
        WindowsExplicitResultKind.DisplayObservationManual => [
            "display_all_dead_pixel_observation_manual_test", "display_all_pattern_observation_manual_test"],
        WindowsExplicitResultKind.StoragePerformanceManual => [
            "storage_all_latency_manual_test", "storage_all_random_iops_manual_test", "storage_all_sequential_throughput_manual_test"],
        WindowsExplicitResultKind.CpuBenchmark => [
            "cpu_all_benchmark_duration_ms", "cpu_all_benchmark_score", "cpu_all_benchmark_workload_declared"],
        WindowsExplicitResultKind.GpuBenchmark => [
            "gpu_all_benchmark_duration_ms", "gpu_all_benchmark_fps", "gpu_all_benchmark_score"],
        WindowsExplicitResultKind.CpuSelfTest => [
            "cpu_all_selftest_error_reported", "cpu_all_selftest_error_time", "cpu_all_selftest_workload_declared"],
        WindowsExplicitResultKind.MemoryTestManual => [
            "memory_all_test_coverage_manual_test", "memory_all_test_error_count_manual_test"],
        WindowsExplicitResultKind.StorageVerificationManual => [
            "storage_all_readback_error_count_manual_test", "storage_all_verified_capacity_manual_test"],
        WindowsExplicitResultKind.MemoryHierarchyLatencyManual => [
            "memory_all_hierarchy_latency_manual_test"],
        WindowsExplicitResultKind.ReferenceRanking => [
            "reference_all_ranking_date", "reference_all_ranking_method_declared", "reference_all_ranking_source"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static WindowsExplicitResultScope ScopeFor(WindowsExplicitResultKind kind) => kind switch
    {
        WindowsExplicitResultKind.MouseInputManual or WindowsExplicitResultKind.KeyboardInputManual
            => WindowsExplicitResultScope.RawInputDevice,
        WindowsExplicitResultKind.DisplayAnimationManual or WindowsExplicitResultKind.DisplayObservationManual
            => WindowsExplicitResultScope.DisplaySession,
        WindowsExplicitResultKind.StoragePerformanceManual or WindowsExplicitResultKind.StorageVerificationManual
            => WindowsExplicitResultScope.StorageDevice,
        WindowsExplicitResultKind.CpuBenchmark or WindowsExplicitResultKind.CpuSelfTest
            => WindowsExplicitResultScope.Cpu,
        WindowsExplicitResultKind.GpuBenchmark => WindowsExplicitResultScope.Gpu,
        WindowsExplicitResultKind.MemoryTestManual or WindowsExplicitResultKind.MemoryHierarchyLatencyManual
            => WindowsExplicitResultScope.Memory,
        WindowsExplicitResultKind.ReferenceRanking => WindowsExplicitResultScope.ReferenceSubject,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private JsonElement CloneBoundedSourceMetadata(JsonElement source)
    {
        Require(source.ValueKind == JsonValueKind.Object, "sourceMetadata must be a JSON object.");
        var raw = source.GetRawText();
        Require(Encoding.UTF8.GetByteCount(raw) <= Limits.MaximumSourceMetadataBytes,
            "sourceMetadata exceeds its size bound.");
        using var parsed = JsonDocument.Parse(raw, documentOptions);
        RejectDuplicateProperties(parsed.RootElement, Limits.MaximumJsonNodeCount);
        return parsed.RootElement.Clone();
    }

    private static void RejectDuplicateProperties(JsonElement element, int maximumNodeCount)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(element);
        var nodeCount = 1;
        while (pending.TryPop(out var current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in current.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException($"Duplicate JSON property '{property.Name}' is not accepted.");
                    if (++nodeCount > maximumNodeCount)
                        throw new InvalidDataException("The explicit-result interchange document exceeds its JSON node bound.");
                    pending.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    if (++nodeCount > maximumNodeCount)
                        throw new InvalidDataException("The explicit-result interchange document exceeds its JSON node bound.");
                    pending.Push(item);
                }
            }
        }
    }

    private static void RequireMetadataTextMatches<T>(WindowsExplicitMeasurement<T>? measurement,
        string expected, string fieldName)
    {
        if (measurement is null) return;
        Require(measurement.Value is string actual && actual == expected,
            $"{fieldName} must match the typed workload/method metadata.");
    }

    private static void RequireText(string? value, string name)
        => Require(!string.IsNullOrWhiteSpace(value), $"{name} must be explicit non-empty text.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }

    public void Dispose()
    {
        submissionGate.Wait();
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        }
        finally { submissionGate.Release(); }
        Publications.Publish([WindowsExplicitResultFieldRegistry.CollectionItemId]);
    }

    private sealed record PreparedSubmission(WindowsExplicitResultKind Kind,
        WindowsExplicitResultMetadata Metadata, WindowsExplicitResultValues Values,
        WindowsExplicitInputSession? InputSession, WindowsExplicitDisplaySession? DisplaySession);
}
