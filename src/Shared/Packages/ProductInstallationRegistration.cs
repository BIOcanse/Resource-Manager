using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Shared.Packages;

/// <summary>The single writer used by installation and manager relocation.</summary>
public static class ProductInstallationRegistration
{
    private const string Contract = "resource-manager-directory-registration-v1";
    private const string Product = @"Software\ResourceManager";
    private const string AppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe";
    private const string ManagerAppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.UpdateManager.exe";
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager";
    public static void Write(string root, string version)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var key = machine.CreateSubKey(AppPath))
        {
            SetOwned(key, root);
            key.SetValue("", Path.Combine(UpdateManagerPaths.InstalledDirectory(root), "Start.exe"));
            key.SetValue("Path", UpdateManagerPaths.InstalledDirectory(root));
        }
        using (var key = machine.CreateSubKey(ManagerAppPath))
        {
            SetOwned(key, root);
            key.SetValue("", UpdateManagerPaths.InstalledExecutable(root));
            key.SetValue("Path", UpdateManagerPaths.InstalledDirectory(root));
        }
        using (var key = machine.CreateSubKey(Uninstall))
        {
            SetOwned(key, root);
            key.SetValue("DisplayName", "Resource Manager");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "BIOcanse");
            key.SetValue("InstallLocation", root);
            key.SetValue("DisplayIcon", Path.Combine(UpdateManagerPaths.InstalledDirectory(root), "Start.exe"));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            WriteUninstallCommand(key, UpdateManagerPaths.InstalledDirectory(root));
        }
        using (var key = machine.CreateSubKey(Product))
        {
            SetOwned(key, root);
            key.SetValue("BackendPath", Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe"));
            key.SetValue("NativeUiPath", ProductDesktopLayout.WindowFor(root));
            key.SetValue("DesktopPath", ProductDesktopLayout.ResidentFor(root));
            key.SetValue("LauncherPath", Path.Combine(root, "Bin", "ResourceManagerLauncher", "ResourceManager.Launcher.exe"));
            key.SetValue("StartPath", Path.Combine(UpdateManagerPaths.InstalledDirectory(root), "Start.exe"));
            key.SetValue("UpdateManagerPath", UpdateManagerPaths.InstalledExecutable(root));
            key.SetValue("ServiceName", WindowsServiceRegistration.ProductServiceName);
            key.SetValue("AppUserModelId", "ResourceManager.Desktop");
        }
    }

    /// <summary>Refresh the already owned product key; uninstall requires the user's data-retention choice.</summary>
    public static void WriteUninstallCommand(RegistryKey key, string managerDirectory)
    {
        key.SetValue("UninstallString", "\"" + Path.Combine(managerDirectory, UninstallationPlan.ExecutableName) + "\"");
        key.DeleteValue("QuietUninstallString", false);
        key.DeleteValue("NoRemove", false);
    }

    private static void SetOwned(RegistryKey key, string root)
    {
        key.SetValue("InstallationContract", Contract);
        key.SetValue("InstallRoot", root);
    }
}
