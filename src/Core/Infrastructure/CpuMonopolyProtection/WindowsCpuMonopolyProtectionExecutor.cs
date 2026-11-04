using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.CpuMonopolyProtection;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Infrastructure.Optimization;

namespace ResourceManager.App.Infrastructure.CpuMonopolyProtection;

// The host's remaining Windows boundary: verify the exact process and delegate its write
// to the same batch writer used by scheduling. No restoration is registered here.
public sealed class WindowsCpuMonopolyProtectionExecutor(IProcessResourcePolicyWriter writer)
    : ICpuMonopolyProtectionExecutor
{
    public event Action<int, string>? UserPrioritySet
    {
        add { if (writer is IExplicitProcessPriorityChangeSource source) source.UserPrioritySet += value; }
        remove { if (writer is IExplicitProcessPriorityChangeSource source) source.UserPrioritySet -= value; }
    }

    public IDisposable GuardAutomaticPriorityWrites(Func<int, string, bool> canWrite)
        => writer is IExplicitProcessPriorityChangeSource source ? source.GuardAutomaticPriorityWrites(canWrite)
            : EmptyRegistration.Instance;

    private sealed class EmptyRegistration : IDisposable
    {
        internal static readonly EmptyRegistration Instance = new();
        public void Dispose() { }
    }

    public string? ExclusionReason(CpuMonopolyProcess process)
    {
        using var target = OpenChecked(process, out var reason);
        return reason;
    }

    public CpuMonopolyPriorityResult SetNormal(CpuMonopolyProcess process, Func<bool>? stillEligible = null,
        Action<bool>? completed = null)
    {
        lock (WindowsProcessResourcePolicyWriter.PriorityWriteGate)
        {
            var result = SetNormalUnderGate(process, stillEligible);
            completed?.Invoke(result.Succeeded);
            return result;
        }
    }

    private CpuMonopolyPriorityResult SetNormalUnderGate(CpuMonopolyProcess process, Func<bool>? stillEligible)
    {
            if (stillEligible?.Invoke() == false) return new(false, "user-priority-set");
            using var target = OpenChecked(process, out var exclusion);
            if (exclusion is not null) return new(false, exclusion);
            try
            {
                var before = writer.ReadPlacementStateForRecovery(process.ProcessId, ProcessPlacementReadFields.PriorityClass);
                if (before.Status != RecoveryReadStatus.Found || before.Value is not { } current)
                    return new(false, before.Status == RecoveryReadStatus.NotFoundOrExited ? "process-exited" : "priority-unavailable", before.Message);
                if (current.StartedAt.ToFileTime().ToString(CultureInfo.InvariantCulture) != process.ProcessStartKey)
                    return new(false, "identity-changed");
                if (!string.Equals(current.PriorityClass, process.PriorityClass, StringComparison.OrdinalIgnoreCase))
                    return new(false, "priority-changed");
                if (current.PriorityClass is not ("High" or "RealTime")) return new(false, "priority-changed");
                // Criticality is checked on the still-open exact-instance handle immediately before
                // the existing exact-instance writer opens its effect handle.
                if (CriticalityExclusion(target!) is { } critical) return new(false, critical);
                ProcessResourcePolicyBatchFieldResult? Write() => writer.TryApplyBatch([new(process.ProcessId, current.StartedAt, PriorityClass: "Normal")])
                    .SingleOrDefault()?.Find(ProcessResourcePolicyBatchFields.PriorityClass);
                var written = writer is WindowsProcessResourcePolicyWriter windows ? windows.WithPriorityWriteBypass(Write) : Write();
                if (written is not { Succeeded: true }) return new(false, "priority-write-failed", written?.Message);
                var after = writer.ReadPlacementStateForRecovery(process.ProcessId, ProcessPlacementReadFields.PriorityClass);
                if (after.Status == RecoveryReadStatus.NotFoundOrExited) return new(true, "process-exited");
                if (after.Status != RecoveryReadStatus.Found || after.Value is not { } verified)
                    return new(false, "priority-readback-unavailable", after.Message);
                if (verified.StartedAt != current.StartedAt) return new(false, "identity-changed");
                return string.Equals(verified.PriorityClass, "Normal", StringComparison.OrdinalIgnoreCase)
                    ? new(true) : new(false, "priority-readback-mismatch");
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException
                or UnauthorizedAccessException or IOException or OverflowException)
            {
                return new(false, "priority-write-failed", error.Message);
            }
    }

    private static SafeProcessHandle? OpenChecked(CpuMonopolyProcess process, out string? reason)
    {
        reason = null;
        if (process.ProcessId <= 4) reason = "critical-process";
        else if (process.ProcessId == Environment.ProcessId || process.IsResourceManager) reason = "resource-manager-process";
        else if (!OperatingSystem.IsWindows()) reason = "unsupported-platform";
        else if (!ulong.TryParse(process.ProcessStartKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key == 0)
            reason = "identity-changed";
        if (reason is not null) return null;

        // PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION. No terminate,
        // suspend, VM-write or hardware rights are requested by CPU protection.
        var handle = HostMemoryProtectionNative.OpenProcess(0x0400 | 0x1000, false, process.ProcessId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            reason = "cannot-open";
            return null;
        }
        if (!HostMemoryProtectionNative.GetProcessTimes(handle, out var creation, out _, out _, out _)) reason = "cannot-open";
        else if (creation.ToString(CultureInfo.InvariantCulture) != process.ProcessStartKey) reason = "identity-changed";
        else if (CriticalityExclusion(handle) is { } critical) reason = critical;
        else
        {
            var image = new StringBuilder(32768); // Windows maximum extended-length path, not a sampling policy.
            var length = (uint)image.Capacity;
            if (!HostMemoryProtectionNative.QueryFullProcessImageName(handle, 0, image, ref length)) reason = "cannot-open";
            else
            {
                var name = Path.GetFileNameWithoutExtension(image.ToString());
                if (name.Equals("ResourceManager", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("ResourceManager.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Resource Manager-APP", StringComparison.OrdinalIgnoreCase)) reason = "resource-manager-process";
            }
        }
        if (reason is null) return handle;
        handle.Dispose();
        return null;
    }

    private static string? CriticalityExclusion(SafeProcessHandle handle)
    {
        var status = HostMemoryProtectionNative.NtQueryInformationProcess(handle, 29, out var critical, sizeof(uint), out var length);
        return status < 0 || length != sizeof(uint) ? "criticality-unavailable" : critical != 0 ? "critical-process" : null;
    }
}
