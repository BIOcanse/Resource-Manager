using Microsoft.Win32;
using ResourceManager.Shared.Desktop;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Updater;

internal static class ManagerRelocationRegistration
{
    private sealed record Value(string Name, object Data, RegistryValueKind Kind);

    internal static string RequireOwned(string root)
    {
        _ = UpdatePlan.RequireProductTarget(root);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var product = machine.OpenSubKey(UninstallationPlan.RegistrationKeys[0]);
        var manager = UpdateManagerPaths.RequireOwnedManager(root, product?.GetValue("UpdateManagerPath") as string);
        if (product?.GetValue("ServiceName") as string != WindowsServiceRegistration.ProductServiceName
            || !string.Equals(product.GetValue("BackendPath") as string,
                ReleasePackageLayout.ForRoot(root, "").Backend, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installation registration does not match the product layout.");
        foreach (var path in UninstallationPlan.RegistrationKeys)
        {
            using var key = machine.OpenSubKey(path);
            if (key is not null && (key.SubKeyCount != 0
                || key.GetValue("InstallationContract") as string != UninstallationPlan.RegistrationContract
                || !string.Equals(key.GetValue("InstallRoot") as string, root, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Registration belongs to another installation: " + path);
        }
        foreach (var (link, name) in new[] { (StartMenuShortcut.PathForAllUsers, "Start.exe"),
            (StartMenuShortcut.ManagerPathForAllUsers, UpdateManagerPaths.ExecutableName) })
        {
            ManagerSelfUpdate.RejectOptionalReparse(link);
            if (!File.Exists(link)) continue;
            var target = StartMenuShortcut.ReadTarget(link);
            if (!new[] { Path.Combine(Path.GetDirectoryName(manager)!, name),
                Path.Combine(UpdateManagerPaths.InstalledDirectory(root), name), Path.Combine(root, name) }
                .Contains(target, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Shortcut belongs to another installation: " + link);
        }
        return manager;
    }

    internal static void Rewrite(string root)
    {
        _ = RequireOwned(root);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var registration = new Dictionary<string, Value[]>();
        foreach (var path in UninstallationPlan.RegistrationKeys)
        {
            using var key = machine.OpenSubKey(path);
            if (key is null) continue;
            registration.Add(path, key.GetValueNames().Select(name => new Value(name,
                key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name))).ToArray());
        }
        var shortcuts = new Dictionary<string, byte[]?>();
        foreach (var path in new[] { StartMenuShortcut.PathForAllUsers, StartMenuShortcut.ManagerPathForAllUsers })
        {
            ManagerSelfUpdate.RejectOptionalReparse(path);
            shortcuts.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
        try
        {
            StartMenuShortcut.Create(Path.Combine(UpdateManagerPaths.InstalledDirectory(root), "Start.exe"));
            StartMenuShortcut.CreateManager(UpdateManagerPaths.InstalledExecutable(root));
            ProductInstallationRegistration.Write(root, UpdatePlan.ReadInstalledVersion(root, UpdateOperation.Update));
        }
        catch
        {
            // Roll back only the four keys and two links whose ownership was
            // checked above. The old external executables are never moved.
            foreach (var path in UninstallationPlan.RegistrationKeys)
            {
                using var key = machine.OpenSubKey(path, writable: true);
                if (key is not null && (key.GetValue("InstallationContract") as string != UninstallationPlan.RegistrationContract
                    || !string.Equals(key.GetValue("InstallRoot") as string, root, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Registration changed during rollback: " + path);
                if (registration.TryGetValue(path, out var values))
                {
                    using var restore = machine.CreateSubKey(path);
                    foreach (var name in restore.GetValueNames())
                        if (!values.Any(value => value.Name == name)) restore.DeleteValue(name, false);
                    foreach (var value in values) restore.SetValue(value.Name, value.Data, value.Kind);
                }
                else if (key is not null) { key.Close(); machine.DeleteSubKey(path, false); }
            }
            foreach (var (path, bytes) in shortcuts)
            {
                ManagerSelfUpdate.RejectOptionalReparse(path);
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            throw;
        }
    }
}
