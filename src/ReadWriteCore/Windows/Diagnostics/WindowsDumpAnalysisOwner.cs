using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Diagnostics;

/// <summary>AnalyzeAsync is the only operation that opens a dump or starts an engine.
/// Backend and subscription captures observe the same current and never analyze.</summary>
public sealed class WindowsDumpAnalysisOwner : IDisposable
{
    /// <summary>Caller-assigned identity of this dump resource. Distinct resources require distinct identities;
    /// constructing the owner never derives identity by opening a file.</summary>
    public string DeviceIdentity { get; }
    public const string NativeHelperDependency = "resource-manager.native.dump-analysis-helper";
    public const string InstalledEngineDependency = "windows.dbgeng";
    public const string CollectionItemId = "information.crash-analysis";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock;
    private readonly InformationReadWriteBackend backend;
    private WindowsDumpAnalysisResult current;
    private int disposed;
    public SamplingPublicationSignal Publications { get; } = new();

    public WindowsDumpAnalysisOwner(string resourceIdentity, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceIdentity);
        DeviceIdentity = resourceIdentity;
        this.clock = clock ?? TimeProvider.System;
        current = Empty(DeviceIdentity, WindowsDumpAnalysisStatus.NotAnalyzed, null);
        backend = new(ReadInformation, ObserveAsync, static _ => new(true, null,
            [NativeHelperDependency, InstalledEngineDependency]));
    }
    public WindowsDumpAnalysisResult ReadCurrent() => Volatile.Read(ref current);
    public DeviceInformationSnapshot ReadInformation() => ReadCurrent().Information;
    public InformationReadWriteBackend CreateBackend() => backend;
    private ValueTask<DeviceInformationSnapshot> ObserveAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ReadInformation()); }

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new InformationCollectionBorrow(backend, Publications, CollectionItemId,
            (selection, permit, token) =>
            {
                token.ThrowIfCancellationRequested();
                var snapshot = DeviceInformationSnapshot.Empty;
                var observed = false;
                var permitted = permit.TryPublish(() =>
                {
                    if (Volatile.Read(ref disposed) == 0) { snapshot = ReadInformation(); observed = true; }
                });
                return ValueTask.FromResult(new FieldCollectionResult(InformationReadWriteBackend.Read(selection, snapshot), permitted && observed));
            });
    }
    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var subscription = Publications.Subscribe([CollectionItemId]);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        yield return ReadInformation();
        while (!cancellationToken.IsCancellationRequested)
        {
            await subscription.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref disposed) != 0) yield break;
            yield return ReadInformation();
        }
    }

    public async ValueTask<WindowsDumpAnalysisResult> AnalyzeAsync(WindowsDumpAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        WindowsDumpAnalysisReader.Validate(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var result = await WindowsDumpAnalysisReader.ReadAsync(DeviceIdentity, request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            result = result with { Information = Project(DeviceIdentity, result, clock.GetUtcNow()) };
            Volatile.Write(ref current, result);
            Publications.Publish([CollectionItemId]);
            return result;
        }
        finally { gate.Release(); }
    }
    internal static WindowsDumpAnalysisResult Empty(string resourceIdentity, WindowsDumpAnalysisStatus status, string? diagnostic)
    {
        var result = new WindowsDumpAnalysisResult(status, diagnostic, null, null, null, null, null,
            WindowsDumpFieldStatus.Unavailable, WindowsDumpFieldStatus.Unavailable,
            WindowsDumpFieldStatus.Unavailable, DeviceInformationSnapshot.Empty);
        return result with { Information = Project(resourceIdentity, result, null) };
    }
    private static DeviceInformationSnapshot Project(string resourceIdentity, WindowsDumpAnalysisResult value, DateTimeOffset? at)
    {
        DeviceInformationField Field(DeviceInformationDefinition definition, object? actual)
            => new(definition, actual is null ? null : JsonSerializer.SerializeToElement(actual));
        string U64(ulong actual) => actual.ToString(CultureInfo.InvariantCulture);
        var exception = value.ExceptionContextInterpreted;
        object? context = exception is null ? null : new {
            processorType = exception.ProcessorType, processId = exception.ProcessId, threadId = exception.ThreadId,
            exceptionCode = exception.ExceptionCode, exceptionFlags = exception.ExceptionFlags,
            exceptionAddress = U64(exception.ExceptionAddress), firstChance = exception.FirstChance,
            exceptionParameters = exception.ExceptionParameters.Select(U64).ToArray(),
            amd64ThreadContext = exception.Amd64ThreadContext is { } bytes ? Convert.ToBase64String(bytes.AsSpan()) : null,
            threadContextStatus = exception.ThreadContextStatus.ToString() };
        object? stack = value.SymbolizedStackInterpreted?.Select(frame => new {
            instructionOffset = U64(frame.InstructionOffset), returnOffset = U64(frame.ReturnOffset),
            frameOffset = U64(frame.FrameOffset), stackOffset = U64(frame.StackOffset),
            symbol = frame.Symbol, symbolDisplacement = U64(frame.SymbolDisplacement), module = frame.Module }).ToArray();
        DeviceInformationField[] fields = [
            Field(DeviceInformationFieldRegistry.CrashAllBugcheckCodeReported, value.BugcheckCodeReported?.ToString(CultureInfo.InvariantCulture)),
            Field(DeviceInformationFieldRegistry.CrashAllBugcheckParametersReported, value.BugcheckParametersReported?.Select(static n => n.ToString(CultureInfo.InvariantCulture)).ToArray()),
            Field(DeviceInformationFieldRegistry.CrashAllSuspectedModuleInterpreted, value.SuspectedModuleInterpreted),
            Field(DeviceInformationFieldRegistry.CrashAllExceptionContextInterpreted, context),
            Field(DeviceInformationFieldRegistry.CrashAllSymbolizedStackInterpreted, stack)];
        return new(fields.Any(static f => f.Value is not null) ? at : null, [new(resourceIdentity, "crash_record", fields)]);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Wait();
        gate.Release();
        Publications.Publish([CollectionItemId]); // Wake existing event observers to end their borrowed stream.
    }
}
