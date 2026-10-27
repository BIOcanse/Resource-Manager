using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

public sealed class WindowsHostMemoryProtectionExecutor : IHostMemoryProtectionExecutor
{
    private static readonly object ActionGate = new();
    private readonly Func<SafeProcessHandle, int> suspendProcess;
    private readonly Action<SafeProcessHandle, ulong, ulong>? chunkDecommitted;
    private readonly Action<nuint, nuint, int>? decommitFailed;

    public WindowsHostMemoryProtectionExecutor() : this(HostMemoryProtectionNative.NtSuspendProcess, null) { }

    // Tests inject failures only for children they started. Production always uses NtSuspendProcess.
    internal WindowsHostMemoryProtectionExecutor(Func<SafeProcessHandle, int> suspendProcess,
        Action<SafeProcessHandle, ulong, ulong>? chunkDecommitted = null,
        Action<nuint, nuint, int>? decommitFailed = null)
    {
        this.suspendProcess = suspendProcess;
        this.chunkDecommitted = chunkDecommitted;
        this.decommitFailed = decommitFailed;
    }

    public IHostMemoryProtectionTarget Open(HostMemoryProcess process)
    {
        if (process.ProcessId <= 4) return new Target(process, null, "critical-process", this);
        if (process.ProcessId == Environment.ProcessId || process.IsResourceManager)
            return new Target(process, null, "resource-manager-process", this);
        if (!OperatingSystem.IsWindows()) return new Target(process, null, "cannot-open", this);
        if (!ulong.TryParse(process.ProcessStartKey, NumberStyles.None, CultureInfo.InvariantCulture, out var startKey) || startKey == 0)
            return new Target(process, null, "identity-changed", this);

        var handle = HostMemoryProtectionNative.OpenProcess(HostMemoryProtectionNative.RequiredAccess, false, process.ProcessId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return new Target(process, null, "cannot-open", this);
        }
        string? exclusion = null;
        if (!HostMemoryProtectionNative.GetProcessTimes(handle, out var creation, out _, out _, out _)) exclusion = "cannot-open";
        else if (creation != startKey) exclusion = "identity-changed";
        else
        {
            exclusion = CriticalityExclusion(handle);
            if (exclusion is null)
            {
                var image = new StringBuilder(32768);
                var size = (uint)image.Capacity;
                if (!HostMemoryProtectionNative.QueryFullProcessImageName(handle, 0, image, ref size)) exclusion = "cannot-open";
                else
                {
                    process = process with { ExecutablePath = image.ToString() };
                    if (IsProductImage(Path.GetFileNameWithoutExtension(image.ToString()))) exclusion = "resource-manager-process";
                }
            }
        }
        if (exclusion is not null)
        {
            handle.Dispose();
            handle = null;
        }
        return new Target(process, handle, exclusion, this);
    }

    private static bool IsProductImage(string name)
        => name.Equals("ResourceManager", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ResourceManager.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Resource Manager-APP", StringComparison.OrdinalIgnoreCase);

    private static string? CriticalityExclusion(SafeProcessHandle handle)
    {
        var status = HostMemoryProtectionNative.NtQueryInformationProcess(handle, 29, out var critical, sizeof(uint), out var length);
        return status < 0 || length != sizeof(uint) ? "criticality-unavailable"
            : critical != 0 ? "critical-process" : null;
    }

    private sealed class Target(HostMemoryProcess process, SafeProcessHandle? handle, string? exclusion,
        WindowsHostMemoryProtectionExecutor executor) : IHostMemoryProtectionTarget
    {
        private SafeProcessHandle? processHandle = handle;
        private bool executed;
        public string? ExclusionReason { get; } = exclusion;

        public HostMemoryActionResult Execute(IHostMemoryProtectionReserve reserve, HostMemoryActionOptions? options = null)
        {
            options ??= new();
            if (!HostMemoryProtectionActionModes.IsSupported(options.ActionMode))
                throw new ArgumentException("invalid-action-mode", nameof(options));
            // Callers normally serialize actions; keep that guarantee for direct callers and separate
            // executor instances too. The retained handle still identifies the originally opened process.
            lock (ActionGate) return ExecuteCore(reserve, options);
        }

        private HostMemoryActionResult ExecuteCore(IHostMemoryProtectionReserve reserve, HostMemoryActionOptions options)
        {
            if (ExclusionReason is not null) return new("excluded", ExclusionReason, [], 0);
            ObjectDisposedException.ThrowIf(processHandle is null, this);
            if (executed) throw new InvalidOperationException("The process action has already been executed.");
            executed = true;
            // An opened target can wait behind a previous action. Recheck its critical flag on the
            // same effect handle instead of relying on classification performed before that wait.
            if (CriticalityExclusion(processHandle) is { } criticality)
                return new("excluded", criticality, [], 0);
            if (HostMemoryProtectionWhitelist.Matches(process, options.ProtectedProcesses)) return new("excluded", "protected-process", [], 0);
            if (options.ActionMode == HostMemoryProtectionActionModes.TerminateOnly)
                return TerminateWithoutDump("terminate-only");
            if (options.SoftwareNoDump) return TerminateWithoutDump("software-no-dump");
            using var lease = reserve.TryAcquire();
            if (lease is null) return TerminateWithoutDump(reserve.ReadStatus().Reason ?? "reserve-unavailable");
            if (process.PrivateCommitBytes is not { } privateCommit)
                return new("excluded", "private-commit-unavailable", [], 0);
            if (privateCommit > lease.RemainingBytes)
                return TerminateWithoutDump("private-commit-exceeds-reserve");
            ulong requiredBytes;
            try
            {
                var layout = HostMemoryMinidumpWriter.ReadLayout(processHandle);
                requiredBytes = HostMemoryMinidumpWriter.Measure(processHandle, process.ProcessId, layout);
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
            {
                return new("preflight-failed", error.Message, [], 0);
            }
            if (!lease.CanFit(requiredBytes)) return TerminateWithoutDump("minidump-exceeds-reserve");
            if (CriticalityExclusion(processHandle) is { } currentCriticality)
                return new("excluded", currentCriticality, [], 0);
            var suspended = executor.suspendProcess(processHandle);
            if (suspended < 0)
                return new HostMemoryActionResult("suspend-failed", $"ntstatus-{suspended:x8}", [], 0) { SuspendFailed = true };

            Stream? file = null;
            string? failure = null;
            string? releaseLimit = null;
            ulong bytesWritten = 0;
            try
            {
                var regions = HostMemoryMinidumpWriter.ReadLayout(processHandle);
                var fileName = $"process-{process.ProcessId}-{process.ProcessStartKey}-{Guid.NewGuid():N}.dmp";
                file = lease.CreateSlice(fileName);
                releaseLimit = HostMemoryMinidumpWriter.Write(processHandle, process.ProcessId, file, regions,
                    executor.chunkDecommitted, executor.decommitFailed);
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
            {
                failure = error is Win32Exception native ? $"{native.Message} ({native.NativeErrorCode})" : error.Message;
            }
            finally
            {
                // Host protection wins over an incomplete slice. Keep the file open until termination,
                // even when layout, file creation, reading, writing or decommitting has failed.
                if (Terminate() is { } terminationFailure) failure = $"{terminationFailure}; {failure}";
                if (file is not null)
                {
                    try { bytesWritten = checked((ulong)file.Length); }
                    catch (IOException error) { failure ??= error.Message; }
                    try { file.Dispose(); }
                    catch (IOException error) { failure ??= error.Message; }
                }
            }
            return new(failure is not null ? "slice-incomplete" : releaseLimit is not null ? "completed-with-release-limit" : "completed",
                failure ?? releaseLimit, lease.SlicePaths.ToArray(), bytesWritten);
        }

        private HostMemoryActionResult TerminateWithoutDump(string reason)
        {
            if (CriticalityExclusion(processHandle!) is { } criticality)
                return new("excluded", criticality, [], 0);
            var failure = Terminate();
            return new(failure is null ? "terminated-without-dump" : "termination-failed", failure ?? reason, [], 0);
        }

        private string? Terminate()
            => !HostMemoryProtectionNative.TerminateProcess(processHandle!, 1)
                ? $"terminate-failed-{Marshal.GetLastWin32Error()}"
                : HostMemoryProtectionNative.WaitForSingleObject(processHandle!, 5000) != 0 ? "termination-not-observed" : null;

        public void Dispose()
        {
            processHandle?.Dispose();
            processHandle = null;
        }
    }
}
