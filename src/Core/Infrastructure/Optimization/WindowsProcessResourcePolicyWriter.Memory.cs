using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter
{
    public RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot> ReadMemoryPriorityStateForRecovery(
        int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return ClassifyWin32RecoveryFailure<ProcessMemoryPriorityRecoverySnapshot>(
                Marshal.GetLastWin32Error());
        }

        try
        {
            var identity = ReadProcessInstanceForRecovery(handle, processId);
            if (identity.Status != RecoveryReadStatus.Found || identity.Value is null)
            {
                return new RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>(
                    identity.Status, null, identity.NativeErrorCode, identity.Message);
            }

            var memory = TryReadMemoryPriority(handle, processId);
            if (memory is null)
            {
                var error = Marshal.GetLastWin32Error();
                return RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Unavailable(
                    error, new System.ComponentModel.Win32Exception(error).Message);
            }

            return RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Found(
                new ProcessMemoryPriorityRecoverySnapshot(
                    processId, identity.Value.StartedAt, memory.MemoryPriority));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
