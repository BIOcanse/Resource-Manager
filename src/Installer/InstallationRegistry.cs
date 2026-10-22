using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Desktop;

namespace ResourceManager.Installer;

internal static class InstallationRegistry
{
    private const string Contract = "resource-manager-directory-registration-v1";
    private const string Product = @"Software\ResourceManager";
    private const string AppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe";
    private const string ManagerAppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.UpdateManager.exe";
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager";
    private static readonly string[] OwnedKeys = [Product, AppPath, ManagerAppPath, Uninstall];

    public static void RequireVacant(PackageLayout target)
    {
        if (File.Exists(target.Root))
            throw new InvalidOperationException($"安装目录已存在，不会覆盖或升级：{target.Root}");
        _ = UserDataArchive.RetainedArchiveAt(target.Root);
        var managerRoot = UpdateManagerPaths.InstalledDirectory(target.Root);
        if (Directory.Exists(managerRoot) || File.Exists(managerRoot))
            throw new InvalidOperationException($"更新管理器目录已存在，不会覆盖：{managerRoot}");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var path in OwnedKeys)
        {
            using var key = machine.OpenSubKey(path);
            if (key is not null) throw new InvalidOperationException("检测到已有产品注册；安装器不负责升级。");
        }
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using (var key = user.OpenSubKey(Product))
            if (key is not null) throw new InvalidOperationException("检测到旧版当前用户安装；安装器不负责升级。");
        if (WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName) is not null)
            throw new InvalidOperationException("同名系统服务已存在；安装器不会覆盖它。");
        if (File.Exists(StartMenuShortcut.PathForAllUsers))
            throw new InvalidOperationException("开始菜单已有同名快捷方式；安装器不会覆盖它。");
        if (File.Exists(StartMenuShortcut.ManagerPathForAllUsers))
            throw new InvalidOperationException("开始菜单已有更新管理器快捷方式；安装器不会覆盖它。");
    }

    public static void Write(PackageLayout target)
        => ProductInstallationRegistration.Write(target.Root, target.Version);

    public static void Undo(PackageLayout target)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var path in OwnedKeys)
        {
            using var key = machine.OpenSubKey(path);
            if (key is null) continue;
            if (key.SubKeyCount != 0 || key.GetValue("InstallationContract") as string != Contract ||
                !string.Equals(key.GetValue("InstallRoot") as string, target.Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"拒绝回滚不属于本次安装的注册项：{path}");
            key.Close();
            machine.DeleteSubKey(path, false);
        }
    }

    private static void SetOwned(RegistryKey key, string root)
    {
        key.SetValue("InstallationContract", Contract);
        key.SetValue("InstallRoot", root);
    }
}
