using System.Globalization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class HostManagerTargetIdentity
{
    internal static string CreateProcessTargetId(int processId, ulong processStartKey)
        => CreateProcessScopedTargetId("process", processId, processStartKey);

    internal static string CreateProcessMemoryPolicyTargetId(
        int processId,
        ulong processStartKey)
        => CreateProcessScopedTargetId(
            "process-memory-policy",
            processId,
            processStartKey);

    private static string CreateProcessScopedTargetId(
        string prefix,
        int processId,
        ulong processStartKey)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfZero(processStartKey);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}:{processId}:{processStartKey}");
    }
}
