using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using ResourceManager.Shared.Desktop;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Updater;

internal sealed record LegacyAdoptionPlan(ReleasePackageLayout Package, string InstallRoot,
    string PreviousVersion, string ManagerExecutable);

internal static class LegacyInstallationAdoption
{
    private const string Product = @"Software\ResourceManager";
    private const string ManagerAppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.UpdateManager.exe";
    private const string MainAppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe";
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager";
    private const string Contract = "resource-manager-directory-registration-v1";
    private const string Pending = "LegacyDesktopRegistrationPending";

    public static LegacyAdoptionPlan Plan(string packageRoot, string installRoot, string executablePath)
    {
        var package = ReleasePackageLayout.Verify(packageRoot);
        var packagedManager = UpdateManagerPaths.PackagedExecutable(package.Root);
        if (!SamePath(executablePath, packagedManager))
            throw new InvalidOperationException("旧版接入只能由已校验发行包内的更新管理器执行。");
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!SamePath(Path.GetDirectoryName(root)!, programFiles)
            || !Path.GetFileName(root).Equals("ResourceManager Beta", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(root))
            throw new InvalidOperationException("仅接入本机已登记的旧版 Beta 安装目录。");
        ReleasePackageLayout.RejectReparse(programFiles);
        ReleasePackageLayout.RejectReparse(root);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var product = machine.OpenSubKey(Product);
        using var uninstall = machine.OpenSubKey(Uninstall);
        using var managerAppPath = machine.OpenSubKey(ManagerAppPath);
        if (product?.GetValue("InstallationContract") as string != Contract
            || !SamePath(product.GetValue("InstallRoot") as string, root)
            || !SamePath(product.GetValue("BackendPath") as string,
                Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe"))
            || product.GetValue("UpdateManagerPath") is not null
            || uninstall?.GetValue("InstallationContract") as string != Contract
            || !SamePath(uninstall.GetValue("InstallRoot") as string, root)
            || managerAppPath is not null)
            throw new InvalidOperationException("旧版安装登记与接入前提不一致，未修改系统。");
        var managerDirectory = UpdateManagerPaths.InstalledDirectory(root);
        if (Directory.Exists(managerDirectory) || File.Exists(managerDirectory)
            || File.Exists(StartMenuShortcut.ManagerPathForAllUsers))
            throw new InvalidOperationException("外置管理器目标已存在，拒绝覆盖。");
        var service = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
            ?? throw new InvalidOperationException("旧版产品服务不存在。");
        WindowsServiceRegistration.RequireExpectedBinary(service,
            Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe"));
        var previous = UpdatePlan.ReadInstalledVersion(root, UpdateOperation.Update);
        if (!ReleaseVersion.TryParse(previous, out var previousKey)
            || !ReleaseVersion.TryParse(package.Version, out var nextKey)
            || nextKey!.CompareTo(previousKey) <= 0)
            throw new InvalidOperationException("旧版接入需要严格更高的目标版本。");
        UpdatePlan.CheckCompatibility(package.Root, root);
        return new LegacyAdoptionPlan(package, root, previous,
            UpdateManagerPaths.InstalledExecutable(root));
    }

    public static void Adopt(LegacyAdoptionPlan plan, string executablePath)
    {
        RequireAdministrator();
        _ = Plan(plan.Package.Root, plan.InstallRoot, executablePath);
        var managerRoot = UpdateManagerPaths.InstalledDirectory(plan.InstallRoot);
        var parent = Path.GetDirectoryName(managerRoot)!;
        var stage = Path.Combine(parent, "ResourceManager.UpdateManager.adopting-" + Guid.NewGuid().ToString("N"));
        var stageExecutable = Path.Combine(stage, UpdateManagerPaths.ExecutableName);
        var managerMoved = false;
        var productSet = false;
        var appPathCreated = false;
        var shortcutCreated = false;
        try
        {
            Directory.CreateDirectory(stage);
            File.Copy(UpdateManagerPaths.PackagedExecutable(plan.Package.Root), stageExecutable);
            Directory.Move(stage, managerRoot);
            managerMoved = true;
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var product = machine.OpenSubKey(Product, writable: true)!)
            {
                product.SetValue("UpdateManagerPath", plan.ManagerExecutable);
                productSet = true;
                product.SetValue(Pending, 1, RegistryValueKind.DWord);
            }
            using (var appPath = machine.CreateSubKey(ManagerAppPath))
            {
                appPathCreated = true;
                appPath.SetValue("InstallationContract", Contract);
                appPath.SetValue("InstallRoot", plan.InstallRoot);
                appPath.SetValue("", plan.ManagerExecutable);
                appPath.SetValue("Path", managerRoot);
            }
            shortcutCreated = true;
            StartMenuShortcut.CreateManager(plan.ManagerExecutable);
        }
        catch
        {
            if (shortcutCreated && File.Exists(StartMenuShortcut.ManagerPathForAllUsers))
                File.Delete(StartMenuShortcut.ManagerPathForAllUsers);
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (appPathCreated)
            {
                using var appPath = machine.OpenSubKey(ManagerAppPath);
                if (appPath?.SubKeyCount == 0
                    && appPath.GetValueNames().All(name => name is "InstallationContract" or "InstallRoot" or "" or "Path")
                    && (appPath.GetValue("InstallRoot") is null
                        || SamePath(appPath.GetValue("InstallRoot") as string, plan.InstallRoot)))
                {
                    appPath.Close();
                    machine.DeleteSubKey(ManagerAppPath, false);
                }
            }
            if (productSet)
            {
                using var product = machine.OpenSubKey(Product, writable: true);
                if (product is not null && SamePath(product.GetValue("UpdateManagerPath") as string, plan.ManagerExecutable))
                {
                    product.DeleteValue("UpdateManagerPath", false);
                    product.DeleteValue(Pending, false);
                }
            }
            DeleteOwnManagerDirectory(managerMoved ? managerRoot : stage);
            throw;
        }
    }

    public static async Task<string> AdoptAndApplyAsync(LegacyAdoptionPlan plan, string executablePath,
        CancellationToken cancellationToken)
    {
        Adopt(plan, executablePath);
        using var process = Process.Start(new ProcessStartInfo(plan.ManagerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "--apply", plan.Package.Root, plan.InstallRoot }
        }) ?? throw new InvalidOperationException("已接入旧版，但无法启动外置更新管理器。");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("旧版已接入外置管理器，但更新失败；旧安装及事务记录已保留，可从开始菜单重试或恢复。");
        return $"已从 {plan.PreviousVersion} 更新到 {plan.Package.Version}，旧安装根保留在更新事务备份中。";
    }

    public static string FinishDesktopRegistration(string installRoot)
    {
        RequireAdministrator();
        var root = UpdatePlan.RequireRegisteredTarget(installRoot);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var product = machine.OpenSubKey(Product, writable: true)
            ?? throw new InvalidOperationException("产品登记不存在。");
        if (product.GetValue(Pending) is not int value || value != 1)
            return "桌面登记无需旧版收尾。";
        var start = Path.Combine(root, "Start.exe");
        if (!File.Exists(start)) throw new InvalidOperationException("新版 Start.exe 尚未切入安装根。");
        ReleasePackageLayout.RejectReparse(start);
        var backend = Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe");
        var service = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
            ?? throw new InvalidOperationException("产品服务不存在。");
        WindowsServiceRegistration.RequireExpectedBinary(service, backend);
        using var appPath = machine.OpenSubKey(MainAppPath, writable: true)
            ?? throw new InvalidOperationException("主程序 App Paths 不存在。");
        var priorLauncher = Path.Combine(root, "Bin", "ResourceManagerLauncher", "ResourceManager.Launcher.exe");
        var existing = appPath.GetValue("") as string;
        if (appPath.GetValue("InstallationContract") as string != Contract
            || !SamePath(appPath.GetValue("InstallRoot") as string, root)
            || (!SamePath(existing, priorLauncher) && !SamePath(existing, start)))
            throw new InvalidOperationException("主程序 App Paths 不属于此安装，拒绝覆盖。");
        using var uninstall = machine.OpenSubKey(Uninstall, writable: true)
            ?? throw new InvalidOperationException("卸载登记不存在。");
        if (uninstall.GetValue("InstallationContract") as string != Contract
            || !SamePath(uninstall.GetValue("InstallRoot") as string, root))
            throw new InvalidOperationException("卸载登记不属于此安装，拒绝覆盖。");
        var oldUninstall = uninstall.GetValue("UninstallString") as string;
        var expectedScript = Path.Combine(root, "scripts", "Register-ResourceManager.ps1");
        if (oldUninstall is not null && !oldUninstall.Contains(expectedScript, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("旧卸载入口不是预期脚本，拒绝覆盖。");
        if (File.Exists(StartMenuShortcut.PathForAllUsers))
        {
            if (!SamePath(StartMenuShortcut.ReadTarget(StartMenuShortcut.PathForAllUsers), start))
                throw new InvalidOperationException("主程序开始菜单快捷方式已存在，且不指向本安装。");
        }
        else StartMenuShortcut.Create(start);
        appPath.SetValue("", start);
        appPath.SetValue("Path", root);
        uninstall.SetValue("DisplayVersion", UpdatePlan.ReadInstalledVersion(root, UpdateOperation.Update));
        uninstall.SetValue("DisplayIcon", start);
        uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        uninstall.SetValue("NoRemove", 1, RegistryValueKind.DWord);
        uninstall.DeleteValue("UninstallString", false);
        product.DeleteValue(Pending, false);
        return "旧版桌面登记已切换到 Start.exe。";
    }

    private static void DeleteOwnManagerDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        ReleasePackageLayout.RejectReparse(path);
        var entries = Directory.EnumerateFileSystemEntries(path).ToArray();
        if (entries.Length > 1 || entries.Length == 1 && !Path.GetFileName(entries[0]).Equals(
                UpdateManagerPaths.ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"接入回滚发现非本次文件，保留目录：{path}");
        if (entries.Length == 1)
        {
            ReleasePackageLayout.RejectReparse(entries[0]);
            File.Delete(entries[0]);
        }
        Directory.Delete(path);
    }

    private static bool SamePath(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
            && Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("旧版接入和桌面登记需要管理员权限。");
    }
}
