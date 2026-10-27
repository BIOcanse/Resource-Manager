using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using ResourceManager.Shared.Desktop;

namespace ResourceManager.Shared.Packages;

public static class DesktopEntryRegistration
{
    /// <summary>Refresh owned desktop entries after an installation has been committed.</summary>
    [System.ComponentModel.Description("RuntimeRead: Sampled")]
    public static void Refresh(string installRoot)
    {
        var plan = UninstallationPlan.Read();
        if (!string.Equals(plan.Root, Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Desktop registration targets another installation.");
        var stableStart = Path.Combine(plan.ManagerDirectory, "Start.exe");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.Root, "release-manifest.json")));
        foreach (var name in new[] { "Start.exe", UninstallationPlan.ExecutableName })
        {
            var source = Path.Combine(plan.Root, name);
            ReleasePackageLayout.RejectReparse(source);
            var member = manifest.RootElement.GetProperty("files").EnumerateArray().Single(entry => entry.GetProperty("path").GetString() == name);
            using (var input = File.OpenRead(source))
                if (input.Length != member.GetProperty("length").GetInt64() ||
                    !Convert.ToHexString(SHA256.HashData(input)).Equals(member.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Desktop entry payload does not match the committed manifest.");
            var destination = Path.Combine(plan.ManagerDirectory, name);
            if (File.Exists(destination)) ReleasePackageLayout.RejectReparse(destination);
            var temporary = destination + ".new-" + Guid.NewGuid().ToString("N");
            try { File.Copy(source, temporary); File.Move(temporary, destination, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var product = machine.OpenSubKey(UninstallationPlan.RegistrationKeys[0], writable: true)!;
        product.SetValue("LauncherPath", ReleasePackageLayout.ForRoot(plan.Root, "").InternalLauncher);
        product.SetValue("StartPath", stableStart);
        using var appPath = machine.OpenSubKey(UninstallationPlan.RegistrationKeys[1], writable: true)!;
        appPath.SetValue("", stableStart);
        appPath.SetValue("Path", plan.ManagerDirectory);
        using var uninstall = machine.OpenSubKey(UninstallationPlan.RegistrationKeys[3], writable: true)!;
        ProductInstallationRegistration.WriteUninstallCommand(uninstall, plan.ManagerDirectory);
        uninstall.SetValue("DisplayIcon", stableStart);
        StartMenuShortcut.Create(stableStart);
    }
}
