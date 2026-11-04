using Microsoft.Win32;
using ResourceManager.Shared.Desktop;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Installer;

internal sealed class WindowsInstallationReplacementRuntime : IInstallationReplacementRuntime
{
    private sealed record Value(string Name, object Data, RegistryValueKind Kind);
    private readonly UninstallationPlan plan;
    private readonly WindowsUninstallationRuntime identity = new();
    private readonly Dictionary<string, Value[]> registration = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> shortcuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly WindowsServiceState? service;

    internal WindowsInstallationReplacementRuntime(UninstallationPlan plan)
    {
        this.plan = plan;
        RequireOwned();
        service = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName);
        if (service is not null && service.State is not (1 or 4))
            throw new InvalidOperationException("Wait for the previous service to finish starting or stopping before reinstalling.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var path in UninstallationPlan.RegistrationKeys)
        {
            using var key = machine.OpenSubKey(path);
            if (key is not null) registration.Add(path, key.GetValueNames().Select(name =>
                new Value(name, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name))).ToArray());
        }
        foreach (var path in new[] { StartMenuShortcut.PathForAllUsers, StartMenuShortcut.ManagerPathForAllUsers })
            if (File.Exists(path))
            {
                ReleasePackageLayout.RejectReparse(path);
                shortcuts.Add(path, File.ReadAllBytes(path));
            }
    }

    public void RequireOwned() => identity.RequireOwned(plan);
    public void StopProduct() => identity.StopProduct(plan);

    public void RemoveIdentity()
    {
        RequireOwned();
        identity.RemoveService(plan); // Service registration owns the previous automatic startup.
        identity.RemoveRegistration(plan);
    }

    public void RestoreIdentity()
    {
        RequireOwned(); // Never replace a registration claimed by another installation during failure handling.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var (path, values) in registration)
        {
            using var key = machine.CreateSubKey(path);
            foreach (var value in values) key.SetValue(value.Name, value.Data, value.Kind);
        }
        foreach (var (path, bytes) in shortcuts)
        {
            if (File.Exists(path)) ReleasePackageLayout.RejectReparse(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        if (service is not null)
        {
            WindowsServiceRegistration.RegisterOnly(WindowsServiceRegistration.ProductServiceName, plan.Backend);
            WindowsServiceRegistration.SetStartType(WindowsServiceRegistration.ProductServiceName, plan.Backend, service.StartType);
            if (service.Running) WindowsServiceRegistration.StartExisting(WindowsServiceRegistration.ProductServiceName, plan.Backend, false);
        }
    }
}
