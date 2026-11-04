using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private static bool CanExecuteAutomaticProcessPolicyForMode(
        CompiledOptimizationModePlan modePlan,
        HostManagerTargetInfo target)
    {
        return modePlan.AutomaticProcessPoliciesEnabled
            && target.CanApplyAutomaticPolicy
            && !target.GpuPlacementPolicy.CpuSchedulingExcluded;
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

    internal static string CreateStableId(string value)
    {
        var limits = OptimizationSettings.ProductDefaults.RuntimeLimits;
        var StackInputCharacterLimit = limits.StackInputCharacterLimit;
        var StackUtf8ByteLimit = limits.StackUtf8ByteLimit;
        const string HexDigits = "0123456789abcdef";

        char[]? uppercaseBuffer = null;
        byte[]? utf8Buffer = null;
        try
        {
            Span<char> uppercase = value.Length <= StackInputCharacterLimit
                ? stackalloc char[value.Length]
                : (uppercaseBuffer = ArrayPool<char>.Shared.Rent(value.Length));
            var uppercaseLength = value.AsSpan().ToUpperInvariant(uppercase);
            var maximumUtf8Length = Encoding.UTF8.GetMaxByteCount(uppercaseLength);
            Span<byte> utf8 = maximumUtf8Length <= StackUtf8ByteLimit
                ? stackalloc byte[maximumUtf8Length]
                : (utf8Buffer = ArrayPool<byte>.Shared.Rent(maximumUtf8Length));
            var utf8Length = Encoding.UTF8.GetBytes(uppercase[..uppercaseLength], utf8);

            Span<byte> hash = stackalloc byte[32];
            if (!SHA256.TryHashData(utf8[..utf8Length], hash, out var hashLength) || hashLength != hash.Length)
            {
                throw new CryptographicException("Stable identity hashing did not produce a complete SHA-256 digest.");
            }

            Span<char> stableId = stackalloc char[16];
            for (var index = 0; index < stableId.Length / 2; index++)
            {
                var valueByte = hash[index];
                stableId[index * 2] = HexDigits[valueByte >> 4];
                stableId[index * 2 + 1] = HexDigits[valueByte & 0x0F];
            }
            return new string(stableId);
        }
        finally
        {
            if (uppercaseBuffer is not null) ArrayPool<char>.Shared.Return(uppercaseBuffer, clearArray: true);
            if (utf8Buffer is not null) ArrayPool<byte>.Shared.Return(utf8Buffer, clearArray: true);
        }
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

        var limits = OptimizationSettings.ProductDefaults.RuntimeLimits;
        var digits = unit == 0 ? limits.FormatByteDigits : size >= limits.FormatLargeValueThreshold
            ? limits.FormatLargeValueDigits : limits.FormatSmallValueDigits;
        return $"{size.ToString($"F{digits}")} {units[unit]}";
    }
}
