using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Updater;

internal enum UpdateOperation { Update, Repair }

internal sealed record UpdatePlan(
    ReleasePackageLayout Package,
    string InstallRoot,
    string PreviousVersion,
    bool ServiceWasRunning,
    UpdateOperation Operation = UpdateOperation.Update)
{
    public static UpdatePlan Create(string packageRoot, string installRoot,
        UpdateOperation operation = UpdateOperation.Update)
    {
        var package = ReleasePackageLayout.Verify(packageRoot);
        var target = RequireRegisteredTarget(installRoot);
        if (!Directory.Exists(target) || string.Equals(package.Root, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("现有安装目录不存在，或目标与发行包相同。");
        ReleasePackageLayout.RejectReparse(target);
        var backend = Path.Combine(target, "Bin", "ResourceManager", "ResourceManager.exe");
        var service = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
            ?? throw new InvalidOperationException("产品服务不存在。");
        WindowsServiceRegistration.RequireExpectedBinary(service, backend);

        var previous = ReadInstalledVersion(target, operation);
        RequireVersion(previous, package.Version, operation);
        CheckCompatibility(package.Root, target);
        return new UpdatePlan(package, target, previous, service.Running, operation);
    }

    internal static void RequireVersion(string previous, string target, UpdateOperation operation)
    {
        if (!ReleaseVersion.TryParse(previous, out var previousKey)
            || !ReleaseVersion.TryParse(target, out var targetKey)
            || (operation == UpdateOperation.Update && targetKey!.CompareTo(previousKey) <= 0)
            || (operation == UpdateOperation.Repair && targetKey!.CompareTo(previousKey) != 0))
            throw new UpdatePlanException(operation == UpdateOperation.Update
                ? "update.versionNotNewer" : "update.repairVersionMismatch", previous, target);
    }

    public static string RequireRegisteredTarget(string installRoot)
    {
        var target = RequireProductTarget(installRoot);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\ResourceManager");
        if (!string.Equals(key?.GetValue("UpdateManagerPath") as string,
                UpdateManagerPaths.InstalledExecutable(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标目录没有本产品及外置更新管理器登记。");
        return target;
    }

    internal static string RequireProductTarget(string installRoot)
    {
        var target = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("安装目录缺少父目录。");
        ReleasePackageLayout.RejectReparse(parent);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\ResourceManager");
        if (key?.GetValue("InstallationContract") as string != "resource-manager-directory-registration-v1"
            || !string.Equals(key.GetValue("InstallRoot") as string, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标目录没有本产品及外置更新管理器登记。");
        return target;
    }

    internal static string ReadInstalledVersion(string target, UpdateOperation operation)
    {
        try { return ReadVersion(Path.Combine(target, "release-manifest.json")); }
        catch (Exception exception) when (operation == UpdateOperation.Repair &&
            exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager");
            if (key?.GetValue("InstallationContract") as string != "resource-manager-directory-registration-v1"
                || !string.Equals(key.GetValue("InstallRoot") as string, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法从安装登记确认待修复版本。", exception);
            return key.GetValue("DisplayVersion") as string
                ?? throw new InvalidOperationException("安装登记缺少待修复版本。", exception);
        }
    }

    private static string ReadVersion(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidDataException("现有安装缺少发行版本。");
    }

    internal static void CheckCompatibility(string packageRoot, string target)
        => UpdatePackageCompatibility.RequireCompatible(packageRoot, target, ReadDatabaseSchema);

    private static int ReadDatabaseSchema(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
