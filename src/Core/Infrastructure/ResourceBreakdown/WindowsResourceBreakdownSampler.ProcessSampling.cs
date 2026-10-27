using ResourceManager.ReadWrite.Windows.Processes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private ProcessSampleBatch CaptureProcessSamples(
        bool includeCpu,
        ProcessSampleDetailLevel detailLevel,
        ProcessCpuDeltaTracker cpuDeltaTracker,
        ProcessMetricReadMask basicMask = ProcessMetricReadMask.Memory | ProcessMetricReadMask.PrivateWorkingSet,
        bool preserveProductIdentity = true)
    {
        var now = DateTimeOffset.UtcNow;
        processFieldOwner.ReadAttributionText = ReadAttributionText;
        var textSharing = processFieldOwner.CreateTextSharing();
        var windowIdentities = preserveProductIdentity && ShouldReadWindowIdentities(detailLevel)
            ? shellIdentityReader.ReadWindowIdentities() : new Dictionary<int, ShellProcessIdentity>();
        IReadOnlyDictionary<WindowsProcessInventoryEntry, WindowsProcessFieldOwner.CapturedProcessBasic>? prepared = null;
        bool Capture(WindowsProcessInventoryEntry process, ProcessCreationRead creation, out ProcessResourceSample sample, out TimeSpan cpu, out ProcessInstanceKey? key)
            => TryCreateProcessSample(process, includeCpu, detailLevel,
                includeCpu ? basicMask | ProcessMetricReadMask.CpuTime : basicMask, preserveProductIdentity,
                windowIdentities, creation.Creation, out sample, out cpu, out key,
                prepared is null ? null : prepared.GetValueOrDefault(process)
                    ?? new(creation.Creation, ProcessBasicMetricObservation.Unavailable), textSharing);
        List<ProcessResourceSample>? projected = null;
        var batch = processFieldOwner.CaptureBatch(includeCpu, cpuDeltaTracker,
            (TryCaptureProcess<ProcessResourceSample>)Capture, static row => row.HasProcessorTime,
            (rows, live) =>
            {
                projected = MarkSelfDescendants(rows);
                processIdentityCache.Prune(live);
                processAttributionCache.Prune(live);
                var livePaths = rows.Select(static row => row.ExecutablePath)
                    .Concat(live.Select(key => TryGetCachedProcessIdentity(key)?.ExecutablePath))
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in fileMetadataCache.Keys)
                    if (!livePaths.Contains(path)) fileMetadataCache.TryRemove(path, out _);
            }, now, (basicMask & (ProcessMetricReadMask.ThreadCount | ProcessMetricReadMask.HandleCount)) != 0
                ? processes => prepared = processFieldOwner.ReadBasicBatch(processes, (process, creation) =>
                {
                    var mask = includeCpu ? basicMask | ProcessMetricReadMask.CpuTime : basicMask;
                    var cached = creation is { } birth ? TryGetCachedProcessIdentity(new(process.Id, birth)) : null;
                    if (preserveProductIdentity && cached is null) mask |= ProcessMetricReadMask.ImagePath;
                    if (preserveProductIdentity) mask |= ProcessMetricReadMask.UserSid;
                    return mask;
                }, textSharing) : null);
        if (batch.Status != SamplingObservationStatus.Current)
            return new(batch.Status, batch.EnumeratedCount, batch.ExcludedCount, batch.SkippedCount, []);
        var samples = projected ?? batch.Samples.ToList();
        var cpuInterval = batch.Cpu;
        var cpuPercentages = cpuInterval.Percentages;
        if (includeCpu && cpuPercentages.Count > 0)
        {
            samples = samples
                .Select(sample => sample.StartKey is { } startKey
                    && cpuPercentages.TryGetValue(
                        new ProcessInstanceKey(sample.ProcessId, startKey),
                        out var percent)
                        ? sample with { CpuPercent = percent, HasCpuPercent = true }
                        : sample)
                .ToList();
        }

        return new ProcessSampleBatch(
            SamplingObservationStatus.Current,
            batch.EnumeratedCount,
            batch.ExcludedCount,
            batch.SkippedCount,
            samples) { CapturedAt = now, CpuInterval = cpuInterval.Interval,
                HasUnidentifiedProcesses = batch.HasUnidentifiedProcesses };
    }

    internal static bool IsStructurallyExcludedProcessId(int processId)
        => processId <= 0;

    internal SchedulingProcessSampleAccounting CaptureSchedulingProcessSampleAccounting()
    {
        var batch = CaptureProcessSamples(
            includeCpu: false,
            ProcessSampleDetailLevel.SmartSchedulingLite,
            hostedProcessCpuDeltaTracker);
        var governableIdentityCount = batch.Samples.Count(static sample =>
            sample.ProcessId > 0 && sample.StartKey is > 0);
        return new SchedulingProcessSampleAccounting(
            batch.Status,
            batch.EnumeratedCount,
            batch.ExcludedCount,
            batch.SkippedCount,
            checked((uint)batch.Samples.Count),
            checked((uint)governableIdentityCount));
    }

    private bool TryCreateProcessSample(
        WindowsProcessInventoryEntry process,
        bool includeCpu,
        ProcessSampleDetailLevel detailLevel,
        ProcessMetricReadMask basicMask,
        bool preserveProductIdentity,
        IReadOnlyDictionary<int, ShellProcessIdentity> windowIdentities,
        long? creation,
        out ProcessResourceSample sample,
        out TimeSpan cpuState,
        out ProcessInstanceKey? processInstanceKey,
        WindowsProcessFieldOwner.CapturedProcessBasic? prepared = null, ProcessInstanceTextSharing? textSharing = null)
    {
        sample = default;
        cpuState = default;
        processInstanceKey = null;

        try
        {
            var processId = process.Id;
            var startKey = prepared is null ? creation : prepared.Creation;
            processInstanceKey = startKey is { } stableStartKey
                ? new ProcessInstanceKey(processId, stableStartKey)
                : null;
            var processName = preserveProductIdentity ? process.ProcessName : string.Empty;
            var cachedIdentity = TryGetCachedProcessIdentity(processInstanceKey);
            var queryMask = basicMask;
            if (preserveProductIdentity && cachedIdentity is null) queryMask |= ProcessMetricReadMask.ImagePath;
            if (preserveProductIdentity) queryMask |= ProcessMetricReadMask.UserSid;
            var basic = prepared?.Basic ?? processBasicMetricReader.Read(processId, startKey ?? 0, queryMask);
            if (processInstanceKey is { } textKey && textSharing is not null) basic = textSharing.Share(textKey, basic);
            // Scheduling keeps the original cache; its path now comes from the same verified SDK query.
            if (preserveProductIdentity && cachedIdentity is null && processInstanceKey is { } cacheKey)
            {
                cachedIdentity = processIdentityCache.GetOrAdd(
                    cacheKey,
                    () => ReadStableProcessIdentity(basic.ImagePath, processName));
            }

            string? executablePath;
            ProcessFileMetadata metadata;
            if (cachedIdentity is not null)
            {
                executablePath = cachedIdentity.ExecutablePath;
                metadata = cachedIdentity.Metadata;
            }
            else if (preserveProductIdentity && processInstanceKey is null)
            {
                executablePath = basic.ImagePath;
                metadata = ShouldReadFileMetadata(detailLevel)
                    ? ReadProcessFileMetadata(executablePath)
                    : ProcessFileMetadata.Empty;
            }
            else
            {
                executablePath = null;
                metadata = ProcessFileMetadata.Empty;
            }

            var windowIdentity = preserveProductIdentity && ShouldReadWindowIdentities(detailLevel) ? windowIdentities.GetValueOrDefault(processId) : null;
            var applicationUserModelId = preserveProductIdentity && ShouldReadApplicationUserModelId(detailLevel)
                ? shellIdentityReader.TryReadProcessApplicationUserModelId(processId)
                : cachedIdentity?.ApplicationUserModelId;
            var userName = preserveProductIdentity && ShouldReadUserName(detailLevel) ? TranslateProcessUserSid(basic.UserSid) : cachedIdentity?.UserName;
            var architecture = preserveProductIdentity && ShouldReadArchitecture(detailLevel) ? TryReadProcessArchitecture(processId) : cachedIdentity?.Architecture;
            var hasWorkingSet = basic.Memory?.WorkingSet is <= long.MaxValue;
            var hasPrivateMemory = basic.Memory?.PrivateCommit is <= long.MaxValue;
            var hasPrivateWorkingSet = basic.Memory?.PrivateWorkingSet is <= long.MaxValue;
            var hasProcessorTime = includeCpu && basic.CpuTime100Ns is { } cpuTime && cpuTime <= (UInt128)long.MaxValue;
            var workingSet = hasWorkingSet ? (long)basic.Memory!.WorkingSet : 0;
            var privateMemory = hasPrivateMemory ? (long)basic.Memory!.PrivateCommit : 0;
            var totalProcessorTime = hasProcessorTime ? TimeSpan.FromTicks((long)basic.CpuTime100Ns!.Value) : TimeSpan.Zero;
            var parentProcessId = process.ParentProcessId;

            sample = new ProcessResourceSample(
                processId,
                parentProcessId > 0 ? parentProcessId : null,
                startKey,
                processName,
                executablePath,
                workingSet,
                privateMemory,
                totalProcessorTime,
                hasWorkingSet,
                hasPrivateMemory,
                hasProcessorTime,
                0,
                false,
                false,
                metadata.FileDescription,
                metadata.ProductName,
                metadata.CompanyName,
                applicationUserModelId,
                windowIdentity?.WindowApplicationUserModelId,
                windowIdentity?.WindowTitle,
                userName,
                architecture,
                hasPrivateWorkingSet ? (long)basic.Memory!.PrivateWorkingSet!.Value : 0,
                hasPrivateWorkingSet) { BasicMetrics = basic };
            cpuState = totalProcessorTime;
            if (preserveProductIdentity) CacheProcessIdentity(detailLevel, processInstanceKey, cachedIdentity, sample, metadata);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            if (processInstanceKey is { } identity)
            {
                sample = new ProcessResourceSample() with
                { ProcessId = identity.ProcessId, ParentProcessId = process.ParentProcessId > 0 ? process.ParentProcessId : null,
                    StartKey = identity.StartKey, Name = string.Empty,
                    BasicMetrics = ProcessBasicMetricObservation.Unavailable };
                return true;
            }
            return false;
        }
    }

    internal static bool TryReadProcessLong(
        Func<long> read,
        out long value)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            value = read();
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            value = 0;
            return false;
        }
    }

    internal static bool TryReadProcessTimeSpan(
        Func<TimeSpan> read,
        out TimeSpan value)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            value = read();
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            value = TimeSpan.Zero;
            return false;
        }
    }

    private CachedProcessIdentity? TryGetCachedProcessIdentity(ProcessInstanceKey? processInstanceKey)
    {
        return processInstanceKey is { } key && processIdentityCache.TryGet(key, out var cached)
            ? cached
            : null;
    }

    private CachedProcessIdentity? ReadStableProcessIdentity(string? executablePath, string processName)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        return new CachedProcessIdentity(
            processName,
            executablePath,
            ReadProcessFileMetadata(executablePath),
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private void CacheProcessIdentity(
        ProcessSampleDetailLevel detailLevel,
        ProcessInstanceKey? processInstanceKey,
        CachedProcessIdentity? cachedIdentity,
        ProcessResourceSample sample,
        ProcessFileMetadata metadata)
    {
        if (detailLevel < ProcessSampleDetailLevel.ResourceTableBasic
            || processInstanceKey is null
            || string.IsNullOrWhiteSpace(sample.ExecutablePath))
        {
            return;
        }

        if (cachedIdentity is not null && detailLevel < ProcessSampleDetailLevel.ResourceTableFull)
        {
            return;
        }

        var nextIdentity = new CachedProcessIdentity(
            sample.Name,
            sample.ExecutablePath,
            metadata,
            sample.ApplicationUserModelId,
            sample.WindowApplicationUserModelId,
            sample.WindowTitle,
            sample.UserName,
            sample.Architecture,
            DateTimeOffset.UtcNow);
        if (cachedIdentity is not null && HasSameProcessIdentity(cachedIdentity, nextIdentity))
        {
            return;
        }

        processIdentityCache.Set(processInstanceKey.Value, nextIdentity);
    }

    private static bool HasSameProcessIdentity(
        CachedProcessIdentity current,
        CachedProcessIdentity next)
    {
        return current.ProcessName == next.ProcessName
            && current.ExecutablePath == next.ExecutablePath
            && current.Metadata == next.Metadata
            && current.ApplicationUserModelId == next.ApplicationUserModelId
            && current.WindowApplicationUserModelId == next.WindowApplicationUserModelId
            && current.WindowTitle == next.WindowTitle
            && current.UserName == next.UserName
            && current.Architecture == next.Architecture;
    }

    private static bool ShouldReadFileMetadata(ProcessSampleDetailLevel detailLevel)
    {
        return detailLevel >= ProcessSampleDetailLevel.ResourceTableBasic;
    }

    private static bool ShouldReadWindowIdentities(ProcessSampleDetailLevel detailLevel)
    {
        return detailLevel >= ProcessSampleDetailLevel.ResourceTableFull;
    }

    private static bool ShouldReadApplicationUserModelId(ProcessSampleDetailLevel detailLevel)
    {
        return detailLevel >= ProcessSampleDetailLevel.ResourceTableFull;
    }

    private static bool ShouldReadUserName(ProcessSampleDetailLevel detailLevel)
    {
        return detailLevel >= ProcessSampleDetailLevel.ResourceTableFull;
    }

    private static bool ShouldReadArchitecture(ProcessSampleDetailLevel detailLevel)
    {
        return detailLevel >= ProcessSampleDetailLevel.ResourceTableFull;
    }

    private ProcessFileMetadata ReadProcessFileMetadata(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return ProcessFileMetadata.Empty;
        }

        return fileMetadataCache.GetOrAdd(executablePath, static path =>
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                return new ProcessFileMetadata(
                    CleanMetadata(version.FileDescription),
                    CleanMetadata(version.ProductName),
                    CleanMetadata(version.CompanyName));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
            {
                return ProcessFileMetadata.Empty;
            }
        });
    }

    private static List<ProcessResourceSample> MarkSelfDescendants(List<ProcessResourceSample> samples)
    {
        var identities = samples.Where(sample => sample.StartKey is > 0)
            .ToDictionary(sample => sample.ProcessId, sample => new ProcessAncestryIdentity(
                sample.ProcessId, sample.ParentProcessId ?? 0, sample.StartKey!.Value));
        var family = ProcessAncestry.FindFamily(identities, new HashSet<int> { Environment.ProcessId });
        for (var index = 0; index < samples.Count; index++)
        {
            if (family.Contains(samples[index].ProcessId)) samples[index] = samples[index] with { IsSelfDescendant = true };
        }
        return samples;
    }

    private static string? TranslateProcessUserSid(string? sid)
    {
        if (sid is null) return null;
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid; }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return null; }
    }
    private static string? TryReadProcessArchitecture(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            if (!NativeMethods.IsWow64Process2(handle, out var processMachine, out var nativeMachine))
            {
                return null;
            }

            var machine = processMachine == NativeMethods.ImageFileMachineUnknown ? nativeMachine : processMachine;
            return machine switch
            {
                NativeMethods.ImageFileMachineI386 => "x86",
                NativeMethods.ImageFileMachineAmd64 => "x64",
                NativeMethods.ImageFileMachineArm64 => "ARM64",
                NativeMethods.ImageFileMachineArm or NativeMethods.ImageFileMachineArmNt => "ARM",
                _ => $"0x{machine:x4}"
            };
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}


