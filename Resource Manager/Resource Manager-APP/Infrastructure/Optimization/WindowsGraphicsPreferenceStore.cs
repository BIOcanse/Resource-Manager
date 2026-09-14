using Microsoft.Win32;
using System.Security;
using ResourceManager.App.Application.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class WindowsGraphicsPreferenceStore : IWindowsGraphicsPreferenceStore
{
    private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GpuPreferenceName = "GpuPreference";
    private const string IntegratedGpuPreference = "1";
    private const string DedicatedGpuPreference = "2";

    public RecoveryReadResult<string> ReadValueForRecovery(string executablePath)
    {
        try
        {
            var path = NormalizeExecutablePath(executablePath);
            using var key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey, writable: false);
            if (key is null)
            {
                return RecoveryReadResult<string>.NotFoundOrExited(0, "Windows 图形首选项注册表键不存在。");
            }

            var value = key.GetValue(path, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value switch
            {
                string text => RecoveryReadResult<string>.Found(text),
                null => RecoveryReadResult<string>.NotFoundOrExited(0, "可执行文件没有 Windows 图形首选项值。"),
                _ => RecoveryReadResult<string>.Unavailable(0, "Windows 图形首选项值不是字符串。")
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or UnauthorizedAccessException
            or SecurityException
            or IOException)
        {
            return RecoveryReadResult<string>.Unavailable(
                ex.HResult,
                $"Windows 图形首选项当前不可读：{ex.Message}");
        }
    }

    public void WriteValue(string executablePath, string value)
    {
        var path = NormalizeExecutablePath(executablePath);
        using var key = Registry.CurrentUser.CreateSubKey(UserGpuPreferencesKey, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 图形首选项注册表。");
        key.SetValue(path, value, RegistryValueKind.String);
    }

    public void DeleteValue(string executablePath)
    {
        var path = NormalizeExecutablePath(executablePath);
        using var key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey, writable: true);
        key?.DeleteValue(path, throwOnMissingValue: false);
    }

    public string BuildPreferIntegratedGpuValue(string? currentValue)
    {
        return BuildGpuPreferenceValue(currentValue, IntegratedGpuPreference);
    }

    public string BuildPreferHighPerformanceGpuValue(string? currentValue)
    {
        return BuildGpuPreferenceValue(currentValue, DedicatedGpuPreference);
    }

    public string DescribePreference(string? value)
    {
        var preference = ParsePairs(value)
            .FirstOrDefault(static pair => pair.Name.Equals(GpuPreferenceName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return preference switch
        {
            IntegratedGpuPreference => "节能 GPU / 核显优先",
            DedicatedGpuPreference => "高性能 GPU / 独显优先",
            null => "未设置",
            _ => $"未知图形偏好 {preference}"
        };
    }

    private static string BuildGpuPreferenceValue(string? currentValue, string preference)
    {
        var pairs = ParsePairs(currentValue)
            .Where(static pair => !pair.Name.Equals(GpuPreferenceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        pairs.Insert(0, new PreferencePair(GpuPreferenceName, preference));
        return string.Join("", pairs.Select(static pair => $"{pair.Name}={pair.Value};"));
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("可执行文件路径不能为空。");
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(executablePath.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"可执行文件路径无效：{executablePath}", ex);
        }
    }

    private static IEnumerable<PreferencePair> ParsePairs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                continue;
            }

            var name = part[..separator].Trim();
            var pairValue = part[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(pairValue))
            {
                yield return new PreferencePair(name, pairValue);
            }
        }
    }

    private sealed record PreferencePair(string Name, string Value);
}
