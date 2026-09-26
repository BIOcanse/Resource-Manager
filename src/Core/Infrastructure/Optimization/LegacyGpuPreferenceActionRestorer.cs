using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class LegacyGpuPreferenceActionRestorer(IWindowsGraphicsPreferenceStore graphicsPreferenceStore)
{
    public OptimizationA1AppliedAction Restore(
        OptimizationA1AppliedAction action,
        DateTimeOffset now)
    {
        var result = RestoreCore(
            action.ExecutablePath,
            action.AppliedRawValue,
            action.PreviousRawValue);

        return result.Restored
            ? action with
            {
                RestoredAt = now,
                State = OptimizationA1RecordStates.Restored,
                Message = "已恢复图形首选项。"
            }
            : action with { Message = result.Message };
    }

    public OptimizationA2AppliedAction Restore(
        OptimizationA2AppliedAction action,
        DateTimeOffset now)
    {
        var result = RestoreCore(
            action.ExecutablePath,
            action.AppliedRawValue,
            action.PreviousRawValue);

        return result.Restored
            ? action with
            {
                RestoredAt = now,
                State = OptimizationA2RecordStates.Restored,
                Message = "已恢复图形首选项。"
            }
            : action with { Message = result.Message };
    }

    private RestoreResult RestoreCore(
        string? executablePath,
        string appliedValue,
        string? previousValue)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new RestoreResult(false, "记录缺少 exe 路径，跳过恢复。");
        }

        var current = graphicsPreferenceStore.ReadValueForRecovery(executablePath);
        if (current.Status != RecoveryReadStatus.Found
            || !string.Equals(current.Value, appliedValue, StringComparison.Ordinal))
        {
            return new RestoreResult(false, "当前图形首选项已被其他来源修改，跳过恢复。");
        }

        if (previousValue is null)
        {
            graphicsPreferenceStore.DeleteValue(executablePath);
        }
        else
        {
            graphicsPreferenceStore.WriteValue(executablePath, previousValue);
        }

        return new RestoreResult(true, "已恢复图形首选项。");
    }

    private readonly record struct RestoreResult(
        bool Restored,
        string Message);
}
