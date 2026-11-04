namespace ResourceManager.App.Application.Optimization;

public interface IWindowsGraphicsPreferenceStore
{
    RecoveryReadResult<string> ReadValueForRecovery(string executablePath);

    void WriteValue(string executablePath, string value);

    void DeleteValue(string executablePath);

    string BuildPreferIntegratedGpuValue(string? currentValue);

    string BuildPreferHighPerformanceGpuValue(string? currentValue);

    string DescribePreference(string? value);
}
