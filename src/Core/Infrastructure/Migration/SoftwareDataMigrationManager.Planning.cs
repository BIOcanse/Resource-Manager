using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Infrastructure.Migration;

public sealed partial class SoftwareDataMigrationManager
{
    public SoftwareDataMigrationPlan Preview(SoftwareDataMigrationRequest request)
    {
        var normalizedSoftwareName = NormalizeSoftwareName(request.SoftwareName);
        var targetCategory = NormalizeCategory(request.TargetCategory);
        var migrationKind = NormalizeKind(request.MigrationKind);
        var targetRoot = ResolveTargetRoot(targetCategory, migrationKind);
        var items = request.SourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => BuildItem(normalizedSoftwareName, targetCategory, migrationKind, targetRoot, path, request.AllowMediumRisk))
            .ToArray();
        var canExecute = items.Length > 0 && items.All(static item => item.CanExecute);
        var summary = canExecute
            ? BuildExecutableSummary(migrationKind)
            : BuildBlockedSummary(migrationKind);

        return new SoftwareDataMigrationPlan(
            normalizedSoftwareName,
            targetCategory,
            migrationKind,
            targetRoot,
            canExecute,
            summary,
            items);
    }

    private SoftwareDataMigrationItem BuildItem(
        string softwareName,
        string targetCategory,
        string migrationKind,
        string targetRoot,
        string sourcePath,
        bool allowMediumRisk)
    {
        var normalizedSource = NormalizeSourcePath(sourcePath);
        var exists = Directory.Exists(normalizedSource) || File.Exists(normalizedSource);
        var isDirectory = Directory.Exists(normalizedSource);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{ShortHash($"{migrationKind}|{softwareName}|{normalizedSource}")}";
        var destinationPath = BuildDestinationPath(targetRoot, softwareName, migrationKind, normalizedSource);
        var backupPath = BuildBackupPath(normalizedSource);

        if (!exists)
        {
            return CreateItem(id, normalizedSource, destinationPath, backupPath, false, false, null, "Blocked", "Missing", false, "源路径不存在。");
        }

        var classification = ClassifyPath(normalizedSource, isDirectory);
        var risk = ClassifyRisk(normalizedSource, isDirectory, classification);
        var size = isDirectory ? TryGetDirectorySize(normalizedSource) : TryGetFileSize(normalizedSource);
        var canExecute = CanExecuteItem(migrationKind, risk, classification, isDirectory, destinationPath, allowMediumRisk);
        var message = BuildMessage(migrationKind, risk, classification, isDirectory, destinationPath, allowMediumRisk);
        return CreateItem(id, normalizedSource, destinationPath, backupPath, true, isDirectory, size, risk, classification, canExecute, message);
    }

    private string ResolveTargetRoot(string category, string migrationKind)
    {
        var roots = GetRoots();
        if (migrationKind == "Root")
        {
            return roots.ManagedSoftwareRoot;
        }

        return category.Equals("Misc", StringComparison.OrdinalIgnoreCase)
            ? roots.MiscRoot
            : roots.UserDataRoot;
    }

    private static string NormalizeCategory(string category)
    {
        return category.Equals("Misc", StringComparison.OrdinalIgnoreCase) ? "Misc" : "UserData";
    }

    private static string NormalizeKind(string migrationKind)
    {
        return migrationKind.Equals("Root", StringComparison.OrdinalIgnoreCase) ? "Root" : "Data";
    }

    private static string NormalizeSoftwareName(string softwareName)
    {
        var trimmed = string.IsNullOrWhiteSpace(softwareName) ? "UnknownSoftware" : softwareName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(trimmed.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "UnknownSoftware" : sanitized;
    }

    private static string NormalizeSourcePath(string sourcePath)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath.Trim()));
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildDestinationPath(string targetRoot, string softwareName, string migrationKind, string sourcePath)
    {
        var leaf = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            leaf = "Root";
        }

        var bucket = migrationKind == "Root" ? "Root" : "Data";
        return Path.Combine(targetRoot, "Software", softwareName, bucket, $"{leaf}-{ShortHash(sourcePath)}");
    }

    private static string BuildBackupPath(string sourcePath)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMddHHmmss");
        return $"{sourcePath}.resource-manager-backup-{timestamp}";
    }

    private static SoftwareDataMigrationItem CreateItem(
        string id,
        string sourcePath,
        string destinationPath,
        string backupPath,
        bool exists,
        bool isDirectory,
        long? sizeBytes,
        string risk,
        string classification,
        bool canExecute,
        string message)
    {
        return new SoftwareDataMigrationItem(
            id,
            sourcePath,
            destinationPath,
            backupPath,
            exists,
            isDirectory,
            sizeBytes,
            risk,
            classification,
            canExecute,
            message);
    }

    private static bool CanExecuteItem(
        string migrationKind,
        string risk,
        string classification,
        bool isDirectory,
        string destinationPath,
        bool allowMediumRisk)
    {
        if (!isDirectory
            || risk == "Blocked"
            || Directory.Exists(destinationPath)
            || File.Exists(destinationPath))
        {
            return false;
        }

        if (migrationKind == "Data")
        {
            return classification != "ApplicationRoot";
        }

        return allowMediumRisk && classification is "ApplicationRoot" or "Directory";
    }

    private static string BuildMessage(
        string migrationKind,
        string risk,
        string classification,
        bool isDirectory,
        string destinationPath,
        bool allowMediumRisk)
    {
        if (!isDirectory)
        {
            return "当前自动迁移只处理目录。";
        }

        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            return "目标路径已存在，不能覆盖。";
        }

        if (risk == "Blocked")
        {
            return "系统/驱动/根目录等高危路径被阻止。";
        }

        if (migrationKind == "Data")
        {
            return classification == "ApplicationRoot"
                ? "该路径看起来像软件根目录，请切换为根目录迁移。"
                : "数据目录可迁移。执行前请关闭相关软件。";
        }

        if (classification != "ApplicationRoot" && classification != "Directory")
        {
            return "根目录迁移只用于软件安装根目录。数据目录请使用数据迁移。";
        }

        return allowMediumRisk
            ? "根目录迁移已允许。底层软件、驱动、服务、反作弊类软件不适用。"
            : "根目录迁移风险更高，需要勾选允许后执行。";
    }

    private static string BuildExecutableSummary(string migrationKind)
    {
        return migrationKind == "Root"
            ? "可执行根目录迁移。执行时会复制软件根目录、备份原目录，并在原位置创建 junction。"
            : "可执行数据迁移。执行时会复制数据目录、备份原目录，并在原位置创建 junction。";
    }

    private static string BuildBlockedSummary(string migrationKind)
    {
        return migrationKind == "Root"
            ? "存在不可迁移或未允许的根目录。底层软件、驱动、服务、反作弊类软件不适合根目录迁移。"
            : "存在不可自动迁移的路径。数据迁移适用于普通数据/cache/config 目录，不用于软件根目录。";
    }

    private static string ClassifyPath(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            return "File";
        }

        if (IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            || IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            || IsUnder(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")))
        {
            return DirectoryContainsExecutableAtTop(path) ? "ApplicationRoot" : "UserAppData";
        }

        if (IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
        {
            return "ProgramData";
        }

        if (IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)))
        {
            return "ApplicationRoot";
        }

        return DirectoryContainsExecutableAtTop(path) ? "ApplicationRoot" : "Directory";
    }

    private static string ClassifyRisk(string path, bool isDirectory, string classification)
    {
        if (IsDriveRoot(path)
            || IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            || IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.System))
            || HasLowLevelHint(path))
        {
            return "Blocked";
        }

        if (!isDirectory)
        {
            return "Blocked";
        }

        return classification switch
        {
            "ApplicationRoot" => "Root",
            "ProgramData" => "Medium",
            "Directory" => "Medium",
            _ => "Low"
        };
    }

    private static bool DirectoryContainsExecutableAtTop(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Any(file => ExecutableExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }

    private static bool HasLowLevelHint(string path)
    {
        var normalized = path.Replace('\\', '/');
        return LowLevelPathHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root)
            && path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string path, string candidateRoot)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long? TryGetDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..10].ToLowerInvariant();
    }
}
