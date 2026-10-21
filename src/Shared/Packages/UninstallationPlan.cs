using Microsoft.Win32;
using ResourceManager.Shared.Desktop;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Shared.Packages;

public sealed record UninstallationPlan(string Root, string Backend, string ManagerDirectory)
{
    public const string ExecutableName = "UninstallResourceManager.exe";
    public const string RegistrationContract = "resource-manager-directory-registration-v1";
    public static IReadOnlyList<string> RegistrationKeys { get; } = [
        @"Software\ResourceManager", @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe",
        @"Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.UpdateManager.exe",
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager"];

    public static UninstallationPlan? ReadIfInstalled()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var product = machine.OpenSubKey(RegistrationKeys[0]);
        return product is null ? null : Read();
    }

    public static UninstallationPlan Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var product = machine.OpenSubKey(RegistrationKeys[0]) ?? throw new InvalidOperationException("Resource Manager is not installed.");
        var root = Path.GetFullPath(product.GetValue("InstallRoot") as string ?? throw new InvalidDataException("Missing installation root."))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(root) is null) throw new InvalidDataException("Unsafe installation root.");
        var backend = ReleasePackageLayout.ForRoot(root, "").Backend;
        if (product.GetValue("ServiceName") as string != WindowsServiceRegistration.ProductServiceName
            || !string.Equals(product.GetValue("BackendPath") as string, backend, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installation registration does not match the product layout.");
        var manager = UpdateManagerPaths.RequireOwnedManager(root, product.GetValue("UpdateManagerPath") as string);
        var plan = new UninstallationPlan(root, backend, Path.GetDirectoryName(manager)!);
        plan.RequireOwnedRegistration(machine);
        return plan;
    }

    public void RequireOwnedRegistration(RegistryKey machine)
    {
        foreach (var path in RegistrationKeys)
        {
            using var key = machine.OpenSubKey(path);
            if (key is null) continue;
            if (key.SubKeyCount != 0 || key.GetValue("InstallationContract") as string != RegistrationContract
                || !string.Equals(key.GetValue("InstallRoot") as string, Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Registration belongs to another installation: " + path);
        }
        foreach (var shortcut in new[] { (StartMenuShortcut.PathForAllUsers, Path.Combine(ManagerDirectory, "Start.exe")),
                     (StartMenuShortcut.ManagerPathForAllUsers, Path.Combine(ManagerDirectory, UpdateManagerPaths.ExecutableName)) })
            if (File.Exists(shortcut.Item1) && !string.Equals(StartMenuShortcut.ReadTarget(shortcut.Item1), shortcut.Item2, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(StartMenuShortcut.ReadTarget(shortcut.Item1), Path.Combine(Root, "Start.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Shortcut belongs to another installation: " + shortcut.Item1);
    }
}
