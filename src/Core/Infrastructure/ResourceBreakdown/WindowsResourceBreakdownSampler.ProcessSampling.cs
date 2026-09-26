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
        ProcessCpuDeltaTracker cpuDeltaTracker)
    {
        var now = DateTimeOffset.UtcNow;
        var currentCpu = new Dictionary<ProcessInstanceKey, TimeSpan>();
        var samples = new List<ProcessResourceSample>();
        var liveProcessInstances = new HashSet<ProcessInstanceKey>();
        var parentProcessIds = ReadProcessParentIds();
        var windowIdentities = ShouldReadWindowIdentities(detailLevel)
            ? shellIdentityReader.ReadWindowIdentities()
            : new Dictionary<int, ShellProcessIdentity>();
        Process[] processes;
        try
        {
            processes = processInventoryReader.GetProcesses();
        }
        catch
        {
            return new ProcessSampleBatch(
                SamplingObservationStatus.Unavailable,
                0,
                0,
                0,
                []);
        }

        uint excludedCount = 0;
        uint skippedCount = 0;
        foreach (var process in processes)
        {
            using (process)
            {
                if (IsStructurallyExcludedProcessId(process.Id))
                {
                    excludedCount++;
                    continue;
                }

                if (!TryCreateProcessSample(
                        process,
                        includeCpu,
                        detailLevel,
                        parentProcessIds,
                        windowIdentities,
                        out var sample,
                        out var cpuState,
                        out var processInstanceKey))
                {
                    skippedCount++;
                    continue;
                }

                if (processInstanceKey is { } liveProcessInstance)
                {
                    liveProcessInstances.Add(liveProcessInstance);
                }

                if (includeCpu
                    && sample.HasProcessorTime
                    && processInstanceKey is { } cpuProcessInstance)
                {
                    currentCpu[cpuProcessInstance] = cpuState;
                }

                samples.Add(sample);
            }
        }
        samples = MarkSelfDescendants(samples);
        processIdentityCache.Prune(liveProcessInstances);
        processAttributionCache.Prune(liveProcessInstances);
        var cpuPercentages = cpuDeltaTracker.Update(
            includeCpu,
            now,
            currentCpu,
            Environment.ProcessorCount);
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
            checked((uint)processes.Length),
            excludedCount,
            skippedCount,
            samples);
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
        Process process,
        bool includeCpu,
        ProcessSampleDetailLevel detailLevel,
        IReadOnlyDictionary<int, int> parentProcessIds,
        IReadOnlyDictionary<int, ShellProcessIdentity> windowIdentities,
        out ProcessResourceSample sample,
        out TimeSpan cpuState,
        out ProcessInstanceKey? processInstanceKey)
    {
        sample = default;
        cpuState = default;
        processInstanceKey = null;

        try
        {
            var processId = process.Id;
            var processName = process.ProcessName;
            var startKey = TryReadProcessStartKey(process);
            processInstanceKey = startKey is { } stableStartKey
                ? new ProcessInstanceKey(processId, stableStartKey)
                : null;
            var cachedIdentity = TryGetCachedProcessIdentity(processInstanceKey);
            // Scheduling needs the same executable identity without a prior UI capture.
            if (cachedIdentity is null && processInstanceKey is { } cacheKey)
            {
                cachedIdentity = processIdentityCache.GetOrAdd(
                    cacheKey,
                    () => ReadStableProcessIdentity(process, processName));
            }

            string? executablePath;
            ProcessFileMetadata metadata;
            if (cachedIdentity is not null)
            {
                executablePath = cachedIdentity.ExecutablePath;
                metadata = cachedIdentity.Metadata;
            }
            else if (processInstanceKey is null)
            {
                executablePath = TryReadExecutablePath(process);
                metadata = ShouldReadFileMetadata(detailLevel)
                    ? ReadProcessFileMetadata(executablePath)
                    : ProcessFileMetadata.Empty;
            }
            else
            {
                executablePath = null;
                metadata = ProcessFileMetadata.Empty;
            }

            var windowIdentity = ShouldReadWindowIdentities(detailLevel) ? windowIdentities.GetValueOrDefault(processId) : null;
            var applicationUserModelId = ShouldReadApplicationUserModelId(detailLevel)
                ? shellIdentityReader.TryReadProcessApplicationUserModelId(processId)
                : cachedIdentity?.ApplicationUserModelId;
            var userName = ShouldReadUserName(detailLevel) ? TryReadProcessUserName(processId) : cachedIdentity?.UserName;
            var architecture = ShouldReadArchitecture(detailLevel) ? TryReadProcessArchitecture(processId) : cachedIdentity?.Architecture;
            var hasWorkingSet = TryReadProcessLong(
                () => process.WorkingSet64,
                out var workingSet);
            var hasPrivateMemory = TryReadProcessLong(
                () => process.PrivateMemorySize64,
                out var privateMemory);
            var totalProcessorTime = TimeSpan.Zero;
            var hasProcessorTime = includeCpu && TryReadProcessTimeSpan(
                () => process.TotalProcessorTime,
                out totalProcessorTime);
            var parentProcessId = parentProcessIds.GetValueOrDefault(processId);

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
                architecture);
            cpuState = totalProcessorTime;
            CacheProcessIdentity(detailLevel, processInstanceKey, cachedIdentity, sample, metadata);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
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

    private CachedProcessIdentity? ReadStableProcessIdentity(Process process, string processName)
    {
        var executablePath = TryReadExecutablePath(process);
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

    private static long? TryReadProcessStartKey(Process process)
    {
        try
        {
            return process.StartTime.ToFileTimeUtc();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
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

    private static Dictionary<int, int> ReadProcessParentIds()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return [];
        }

        try
        {
            var parents = new Dictionary<int, int>();
            var entry = ProcessEntry32.Create();
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

            return parents;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    private static List<ProcessResourceSample> MarkSelfDescendants(List<ProcessResourceSample> samples)
    {
        var instanceByProcessId =
            new Dictionary<int, ProcessInstanceKey>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.StartKey is { } startKey)
            {
                instanceByProcessId[sample.ProcessId] =
                    new ProcessInstanceKey(sample.ProcessId, startKey);
            }
        }

        if (!instanceByProcessId.TryGetValue(
                Environment.ProcessId,
                out var selfInstance))
        {
            return samples;
        }

        var parentByInstance =
            new Dictionary<ProcessInstanceKey, ProcessInstanceKey>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.StartKey is not { } startKey
                || sample.ParentProcessId is not > 0
                || !instanceByProcessId.TryGetValue(
                    sample.ParentProcessId.Value,
                    out var parentInstance))
            {
                continue;
            }
            parentByInstance[
                new ProcessInstanceKey(sample.ProcessId, startKey)] =
                parentInstance;
        }

        var descendantState =
            new Dictionary<ProcessInstanceKey, byte>(samples.Count)
            {
                [selfInstance] = 2
            };

        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (sample.StartKey is { } startKey
                && IsSelfOrDescendant(
                    new ProcessInstanceKey(sample.ProcessId, startKey),
                    parentByInstance,
                    descendantState))
            {
                samples[index] = sample with { IsSelfDescendant = true };
            }
        }

        return samples;
    }

    private static bool IsSelfOrDescendant(
        ProcessInstanceKey process,
        IReadOnlyDictionary<ProcessInstanceKey, ProcessInstanceKey> parentByInstance,
        Dictionary<ProcessInstanceKey, byte> stateByInstance)
    {
        if (stateByInstance.TryGetValue(
                process,
                out var currentState))
        {
            return currentState == 2;
        }

        stateByInstance[process] = 1;
        var isDescendant =
            parentByInstance.TryGetValue(
                process,
                out var parent)
            && parent != process
            && IsSelfOrDescendant(
                parent,
                parentByInstance,
                stateByInstance);
        stateByInstance[process] =
            isDescendant ? (byte)2 : (byte)3;
        return isDescendant;
    }

    private static string? TryReadExecutablePath(Process process)
    {
        var queryPath = TryReadExecutablePathByQueryFullProcessImageName(process.Id);
        if (queryPath is not null)
        {
            return queryPath;
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryReadExecutablePathByQueryFullProcessImageName(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var size = 1024;
            var builder = new StringBuilder(size);
            return NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref size)
                ? builder.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? TryReadProcessUserName(int processId)
    {
        var processHandle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero || processHandle == new IntPtr(-1))
        {
            return null;
        }

        IntPtr tokenHandle = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TokenQuery, out tokenHandle))
            {
                return null;
            }

            _ = NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, buffer, length, out _))
            {
                return null;
            }

            var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
            var sid = new SecurityIdentifier(tokenUser.User.Sid);
            try
            {
                return sid.Translate(typeof(NTAccount)).Value;
            }
            catch (IdentityNotMappedException)
            {
                return sid.Value;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (tokenHandle != IntPtr.Zero && tokenHandle != new IntPtr(-1))
            {
                NativeMethods.CloseHandle(tokenHandle);
            }

            NativeMethods.CloseHandle(processHandle);
        }
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
