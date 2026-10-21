using System.Diagnostics;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class WindowsProcessFieldOwner
{
    public const string ProcessDetailsCollectionItemId = "process.details";
    private InformationReadWriteBackend? processDetailsBackend;
    private WindowsProcessDetailsOptions? processDetailsOptions;
    private IWindowsProcessDetailsCaptureSource? processDetailsCaptureSource;
    private IWindowsProcessDetailsInformationReader? processDetailsReader;

    /// <summary>Configure the original owner to use its existing Windows inventory and native detail reader.</summary>
    public void ConfigureProcessDetails(WindowsProcessDetailsOptions options)
        => ConfigureProcessDetailsCore(options, null);

    /// <summary>Configure the original owner with an explicit inventory and typed detail source.</summary>
    public void ConfigureProcessDetails(WindowsProcessDetailsOptions options,
        IWindowsProcessDetailsCaptureSource captureSource)
    {
        ArgumentNullException.ThrowIfNull(captureSource);
        ConfigureProcessDetailsCore(options, captureSource);
    }

    private void ConfigureProcessDetailsCore(WindowsProcessDetailsOptions options,
        IWindowsProcessDetailsCaptureSource? captureSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        CaptureGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            lock (Current.Gate)
            {
                if (processDetailsOptions is not null || processDetailsBackend is not null
                    || Current.ReadProcessDetails().Devices.Count != 0)
                    throw new InvalidOperationException("Configure process details once, before creating its backend or capturing.");
                processDetailsOptions = options;
                processDetailsCaptureSource = captureSource;
            }
        }
        finally { CaptureGate.Release(); TryDisposeRetiredGate(); }
    }

    /// <summary>Typed details over the original process current. Its collection callback is explicit.</summary>
    public InformationReadWriteBackend ProcessDetailsBackend
    {
        get
        {
            lock (Current.Gate)
            {
                if (processDetailsOptions is null)
                    throw new InvalidOperationException("Call ConfigureProcessDetails before exposing the process-details backend.");
                return processDetailsBackend ??= new InformationReadWriteBackend(
                    Current.ReadProcessDetails,
                    CaptureAllProcessDetailsAsync,
                    static _ => OperatingSystem.IsWindows()
                        ? new OperationSupport(true, null, [])
                        : OperationSupport.Unsupported("windows-only-source"),
                    CaptureSelectedProcessDetailsAsync);
            }
        }
    }

    public DeviceInformationSnapshot ReadProcessDetails() => Current.ReadProcessDetails();

    public IReadWriteCollectionBorrow BorrowProcessDetailsSubscriptionCapture()
        => new InformationCollectionBorrow(ProcessDetailsBackend, Publications, ProcessDetailsCollectionItemId,
            async (selection, permit, token) =>
            {
                var requested = SelectProcessDetails(selection);
                var (snapshot, committed) = await CaptureProcessDetailsCoreAsync(requested, false, token, permit);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, true));
            }, FieldCollectionSourceKind.SourceQuery);

    private async ValueTask<DeviceInformationSnapshot> CaptureAllProcessDetailsAsync(CancellationToken cancellationToken)
        => (await CaptureProcessDetailsCoreAsync(null, true, cancellationToken, null)).Snapshot;

    private async ValueTask<DeviceInformationSnapshot> CaptureSelectedProcessDetailsAsync(
        ImmutableArray<InformationFieldSelection> selection, CancellationToken cancellationToken)
    {
        var requested = SelectProcessDetails(selection);
        return (await CaptureProcessDetailsCoreAsync(requested, false, cancellationToken, null)).Snapshot;
    }

    private async ValueTask<(DeviceInformationSnapshot Snapshot, bool Committed)> CaptureProcessDetailsCoreAsync(
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields>? requested, bool allProcesses,
        CancellationToken cancellationToken, IMetricPublicationPermit? permit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!allProcesses && (requested is null || requested.Count == 0))
            return (Current.ReadProcessDetails(), false);

        await CaptureGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var options = processDetailsOptions
                ?? throw new InvalidOperationException("Configure process details before capture.");
            var inventory = ReadProcessDetailsInventory(options, cancellationToken);
            var identityListShapeValid = !inventory.ProcessIdentities.IsDefault
                && inventory.ProcessIdentities.Length <= options.MaximumProcesses;
            var candidateKeys = identityListShapeValid ? inventory.ProcessIdentities.ToArray() : [];
            var ambiguousPids = candidateKeys.Where(static key => key.ProcessId > 0).GroupBy(static key => key.ProcessId)
                .Where(static group => group.Count() > 1 || group.Any(static key => key.StartKey <= 0))
                .Select(static group => group.Key).ToHashSet();
            var currentKeys = candidateKeys.Where(key => key.ProcessId > 0 && key.StartKey > 0
                && !ambiguousPids.Contains(key.ProcessId)).ToArray();
            var currentKeySet = currentKeys.ToHashSet();
            var identitiesComplete = identityListShapeValid && currentKeys.Length == candidateKeys.Length
                && ambiguousPids.Count == 0;
            var inventoryComplete = inventory.Succeeded && identitiesComplete && inventory.CapturedAt != default;
            var observedAt = inventory.CapturedAt != default ? inventory.CapturedAt : CaptureClock();

            IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selected;
            if (allProcesses)
                selected = currentKeys.ToDictionary(static key => key, static _ => WindowsProcessDetailsFields.All);
            else selected = requested!;

            var readSelection = selected.Where(pair => currentKeySet.Contains(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> results
                = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult>();
            if (readSelection.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var raw = processDetailsCaptureSource is { } source
                        ? source.ReadDetails(readSelection, cancellationToken)
                        : ProcessDetailsReader.Read(readSelection, cancellationToken);
                    results = ValidateProcessDetailsResults(readSelection, raw, options);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (IsProcessDetailsSourceFailure(error)) { }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var previous = Current.ReadProcessDetails();
            var next = WindowsProcessDetailsInformationProjection.Apply(previous, inventoryComplete,
                currentKeys, allProcesses, selected, results, observedAt);
            cancellationToken.ThrowIfCancellationRequested();
            var committed = false;
            void Publish()
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (Current.Gate)
                {
                    if (Volatile.Read(ref disposed) != 0) return;
                    Current.SetProcessDetails(next);
                    committed = true;
                }
            }
            if (permit is null) Publish(); else permit.TryPublish(Publish);
            if (committed) Publications.Publish([ProcessDetailsCollectionItemId]);
            return (Current.ReadProcessDetails(), committed);
        }
        finally
        {
            CaptureGate.Release();
            TryDisposeRetiredGate();
        }
    }

    private WindowsProcessDetailsInventory ReadProcessDetailsInventory(WindowsProcessDetailsOptions options,
        CancellationToken cancellationToken)
    {
        if (processDetailsCaptureSource is { } source)
        {
            try { return source.ReadInventory(cancellationToken) ?? new(false, [], CaptureClock()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (IsProcessDetailsSourceFailure(error))
            { return new(false, [], CaptureClock()); }
        }
        return CaptureNativeProcessDetailsInventory(options, cancellationToken);
    }

    private WindowsProcessDetailsInventory CaptureNativeProcessDetailsInventory(WindowsProcessDetailsOptions options,
        CancellationToken cancellationToken)
    {
        bool Capture(Process process, out ProcessDetailsInventoryRow row, out TimeSpan cpu, out ProcessInstanceKey? identity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cpu = default;
            try
            {
                var pid = process.Id;
                var creation = ReadCreation(process);
                identity = creation is > 0 ? new ProcessInstanceKey(pid, creation.Value) : null;
                row = new(pid, identity);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException
                or ExternalException or ArgumentException)
            {
                row = new(0, null);
                identity = null;
                return false;
            }
        }

        var batch = CaptureBatch(false, DirectCpu, (TryCaptureProcess<ProcessDetailsInventoryRow>)Capture,
            static _ => false, maximumProcessCount: options.MaximumProcesses);
        return MapProcessDetailsInventory(batch, options.MaximumProcesses);
    }

    internal static WindowsProcessDetailsInventory MapProcessDetailsInventory(
        ProcessCaptureBatch<ProcessDetailsInventoryRow> batch, int maximumProcessCount)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (maximumProcessCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumProcessCount));
        var rows = batch.Samples;
        var identities = rows.Where(static row => row.Key is not null).Select(static row => row.Key!.Value).ToImmutableArray();
        var succeeded = batch.Status == SamplingObservationStatus.Current
            && rows.Count == batch.EnumeratedCount && batch.ExcludedCount == 0 && batch.SkippedCount == 0
            && rows.All(static row => row.Key is { ProcessId: > 0, StartKey: > 0 })
            && rows.Count <= maximumProcessCount
            && identities.Select(static key => key.ProcessId).Distinct().Count() == identities.Length;
        return new(succeeded, identities, batch.CapturedAt);
    }

    private IWindowsProcessDetailsInformationReader ProcessDetailsReader
        => processDetailsReader ??= new WindowsProcessDetailsInformationReader(
            processDetailsOptions ?? throw new InvalidOperationException("Configure process details before capture."));

    private static IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> ValidateProcessDetailsResults(
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> results,
        WindowsProcessDetailsOptions options)
    {
        var validated = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult>(selection.Count);
        foreach (var pair in selection)
        {
            if (results is null || !results.TryGetValue(pair.Key, out var result) || result is null)
            {
                validated[pair.Key] = WindowsProcessDetailsReadResult.Unavailable;
                continue;
            }

            var signatureSelected = (pair.Value & WindowsProcessDetailsFields.ImageSignature) != 0;
            var signatureSucceeded = signatureSelected && result.SignatureSucceeded && result.SignatureStatus is not null;
            var moduleSelected = (pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0;
            var modulesSucceeded = moduleSelected && result.ModulesSucceeded && !result.Modules.IsDefault
                && result.Modules.Length <= options.MaximumModulesPerProcess
                && result.Modules.All(module => module is not null && module.BaseAddress != 0
                    && !string.IsNullOrWhiteSpace(module.Path) && module.Path.Length <= options.MaximumImagePathCharacters)
                && result.Modules.Select(static module => module.BaseAddress).Distinct().Count() == result.Modules.Length;
            var threadSelected = (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0;
            var threadsSucceeded = threadSelected && result.ThreadsSucceeded && !result.Threads.IsDefault
                && result.Threads.Length <= options.MaximumThreadsPerProcess
                && result.Threads.All(static thread => thread is not null && thread.ThreadId != 0)
                && result.Threads.Select(static thread => thread.ThreadId).Distinct().Count() == result.Threads.Length;

            validated[pair.Key] = new(
                signatureSucceeded, signatureSucceeded ? result.SignatureStatus : null,
                modulesSucceeded, modulesSucceeded ? result.Modules : [],
                threadsSucceeded, threadsSucceeded ? result.Threads : []);
        }
        return validated;
    }

    private static bool IsProcessDetailsSourceFailure(Exception error) => error is Win32Exception
        or InvalidOperationException or ExternalException or DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException or NotSupportedException or UnauthorizedAccessException or ArgumentException
        or IOException or TimeoutException;

    private static IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> SelectProcessDetails(
        ImmutableArray<InformationFieldSelection> selection)
    {
        var result = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>();
        foreach (var item in selection)
        {
            var definition = item.Definition;
            var expectedKind = definition == DeviceInformationFieldRegistry.ProcessAllImageSignatureReported ? ""
                : definition == DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual ? "module"
                : definition == DeviceInformationFieldRegistry.ProcessAllThreadStateActual ? "thread"
                : throw new ArgumentException("Unknown process detail field definition.", nameof(selection));
            if (!WindowsProcessDetailsIdentity.TryParse(item.Field.DeviceIdentity, out var key, out var actualKind)
                || actualKind != expectedKind)
                throw new ArgumentException("Process detail field identity does not match its canonical object type.", nameof(selection));
            var field = definition == DeviceInformationFieldRegistry.ProcessAllImageSignatureReported ? WindowsProcessDetailsFields.ImageSignature
                : definition == DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual ? WindowsProcessDetailsFields.LoadedModules
                : WindowsProcessDetailsFields.ThreadState;
            result[key] = result.GetValueOrDefault(key) | field;
        }
        return result;
    }

    internal sealed record ProcessDetailsInventoryRow(int ProcessId, ProcessInstanceKey? Key);
}
