using System.Security.Cryptography;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsIfeoGpuLaunchInterceptionRegistry
{
    private const ushort ImageFileMachineAmd64 = 0x8664;

    private static EligibilityResult EvaluateEligibility(string? path, string brokerPath)
    {
        var executablePath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new EligibilityResult(
                false,
                GpuLaunchInterceptionStatuses.ExecutableMissing,
                "启动拦截要求一个当前存在的可执行文件完整路径。",
                executablePath);
        }

        if (IsPathUnder(executablePath, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
        {
            return new EligibilityResult(
                false,
                GpuLaunchInterceptionStatuses.SystemPathBlocked,
                "Windows 系统目录下的程序不接受 GPU 启动拦截。",
                executablePath);
        }

        var imageName = Path.GetFileName(executablePath);
        if (imageName.Equals("ResourceManager.exe", StringComparison.OrdinalIgnoreCase)
            || imageName.Equals("ResourceManager.NativeUi.exe", StringComparison.OrdinalIgnoreCase)
            || imageName.Equals(BrokerFileName, StringComparison.OrdinalIgnoreCase))
        {
            return new EligibilityResult(
                false,
                GpuLaunchInterceptionStatuses.ExecutableUnsupported,
                "Resource Manager 自身入口和启动代理不得注册固定启动拦截。",
                executablePath);
        }

        if (!TryReadMachine(executablePath, out var machine) || machine != ImageFileMachineAmd64)
        {
            return new EligibilityResult(
                false,
                GpuLaunchInterceptionStatuses.ExecutableUnsupported,
                "当前固定启动 Provider 只支持 x64 PE 可执行文件。",
                executablePath);
        }

        if (!File.Exists(brokerPath))
        {
            return new EligibilityResult(
                false,
                GpuLaunchInterceptionStatuses.BrokerMissing,
                "GPU 启动代理不存在，未写入 IFEO 规则。",
                executablePath);
        }

        return new EligibilityResult(true, GpuLaunchInterceptionStatuses.Registered, string.Empty, executablePath);
    }

    internal static string CreateRuleName(string executablePath)
    {
        var normalized = NormalizePath(executablePath)
            ?? throw new ArgumentException("Executable path is required.", nameof(executablePath));
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()));
        return $"ResourceManager-{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    internal static bool TryReadMachine(string executablePath, out ushort machine)
    {
        machine = 0;
        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
            {
                return false;
            }

            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6)
            {
                return false;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return false;
            }

            machine = reader.ReadUInt16();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsPathUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
