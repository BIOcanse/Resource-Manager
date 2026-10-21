using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Desktop;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Shared.Packages;

public sealed class WindowsUninstallationRuntime : IUninstallationRuntime
{
    public void RequireOwned(UninstallationPlan plan)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        plan.RequireOwnedRegistration(machine);
        var service = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName);
        if (service is not null) WindowsServiceRegistration.RequireExpectedBinary(service, plan.Backend);
    }

    public void StopProduct(UninstallationPlan plan)
    {
        foreach (var manager in Process.GetProcessesByName("ResourceManager.UpdateManager"))
        {
            using (manager)
                if (SameExecutable(manager, Path.Combine(plan.ManagerDirectory, UpdateManagerPaths.ExecutableName))) manager.CloseMainWindow();
        }
        var ui = ReleasePackageLayout.ForRoot(plan.Root, "").NativeUi;
        foreach (var process in Process.GetProcessesByName("ResourceManager.NativeUi"))
        {
            using (process)
            {
                if (!SameExecutable(process, ui)) continue;
                process.CloseMainWindow();
                if (!process.WaitForExit(5000)) { process.Kill(); if (!process.WaitForExit(10_000)) throw new TimeoutException("The product interface did not exit."); }
            }
        }
        if (WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName) is not null)
            WindowsServiceRegistration.StopExisting(WindowsServiceRegistration.ProductServiceName, plan.Backend);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                if (path is not null && (path.StartsWith(plan.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(plan.ManagerDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    if (!process.WaitForExit(30_000)) throw new TimeoutException("A product process still holds installation files: " + Path.GetFileName(path));
            }
        }
    }

    private static bool SameExecutable(Process process, string path)
    {
        try { return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public void RemoveService(UninstallationPlan plan) => WindowsServiceRegistration.DeleteOwned(WindowsServiceRegistration.ProductServiceName, plan.Backend);

    public void RemoveRegistration(UninstallationPlan plan)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        plan.RequireOwnedRegistration(machine);
        foreach (var shortcut in new[] { StartMenuShortcut.PathForAllUsers, StartMenuShortcut.ManagerPathForAllUsers })
            if (File.Exists(shortcut)) File.Delete(shortcut);
        foreach (var path in UninstallationPlan.RegistrationKeys.Reverse()) machine.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    public void ProtectArchive(string archive)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(archive).SetAccessControl(security);
    }
}
