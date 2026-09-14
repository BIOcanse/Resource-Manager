using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private static bool CanExecuteSoftwareLevelAdapterModesForMode(
        CompiledOptimizationModePlan modePlan,
        HostManagerTargetInfo target)
    {
        return modePlan.SoftwareSchedulingEnabled
            && target.CanApplyAdaptedPolicy
            && target.AdapterDispatchRoute == CompiledAdapterDispatchRoute.SoftwareLevelScheduler;
    }

    private static bool CanExecuteAutomaticProcessPolicyForMode(
        CompiledOptimizationModePlan modePlan,
        HostManagerTargetInfo target)
    {
        return modePlan.AutomaticProcessPoliciesEnabled && target.CanApplyAutomaticPolicy;
    }

    private static int? TryGetForegroundProcessId()
    {
        try
        {
            var window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            return processId == 0 ? null : (int)processId;
        }
        catch
        {
            return null;
        }
    }

    private static double ClampRatio(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static string CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string FormatBytes(double value)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var size = Math.Max(0, value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var digits = unit == 0 ? 0 : size >= 10 ? 1 : 2;
        return $"{size.ToString($"F{digits}")} {units[unit]}";
    }
}
