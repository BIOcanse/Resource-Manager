using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.CrashReports;

// HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<image> in the 64-bit view. Used only by the
// installed service (LocalSystem) when the user chose on; CrashDumpSettings never calls it outside the service.
[SupportedOSPlatform("windows")]
public sealed class WindowsCrashDumpRegistry : ICrashDumpRegistry
{
    internal const string LocalDumpsKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
    internal const string OwnerValueName = "ResourceManagerOwner";
    internal const string OwnerValue = "crash-dumps";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Crash dump settings applied or reapplied")]
    public IReadOnlyList<CrashDumpRegistryEntry> Read(IReadOnlyList<string> images)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var dumps = root.OpenSubKey(LocalDumpsKey, writable: false);
        var result = new CrashDumpRegistryEntry[images.Count];
        for (var index = 0; index < images.Count; index++)
        {
            using var key = dumps?.OpenSubKey(images[index], writable: false);
            result[index] = key is null
                ? new(images[index], false, false)
                : new(images[index], true,
                    string.Equals(key.GetValue(OwnerValueName) as string, OwnerValue, StringComparison.Ordinal),
                    key.GetValue("DumpFolder", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    key.GetValue("DumpType") as int?, key.GetValue("CustomDumpFlags") as int?, key.GetValue("DumpCount") as int?);
        }
        return result;
    }

    public void Write(CrashDumpRegistryKey desired)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(LocalDumpsKey + "\\" + desired.Image, writable: true)
            ?? throw new IOException("The LocalDumps key could not be created.");
        key.SetValue("DumpFolder", desired.DumpFolder, RegistryValueKind.ExpandString);
        key.SetValue("DumpType", desired.DumpType, RegistryValueKind.DWord);
        key.SetValue("DumpCount", desired.DumpCount, RegistryValueKind.DWord);
        if (desired.CustomDumpFlags is { } flags) key.SetValue("CustomDumpFlags", flags, RegistryValueKind.DWord);
        else key.DeleteValue("CustomDumpFlags", throwOnMissingValue: false);
        key.SetValue(OwnerValueName, OwnerValue, RegistryValueKind.String);
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Crash dump settings applied or reapplied")]
    public void RemoveOwned(string image)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var dumps = root.OpenSubKey(LocalDumpsKey, writable: true);
        if (dumps is null) return;
        using (var key = dumps.OpenSubKey(image, writable: false))
        {
            if (key is null || !string.Equals(key.GetValue(OwnerValueName) as string, OwnerValue, StringComparison.Ordinal))
                return;
        }
        dumps.DeleteSubKey(image, throwOnMissingSubKey: false);
    }

    public void PrepareFolder(string path, bool usersMayWrite)
    {
        var directory = Directory.CreateDirectory(path);
        var security = directory.GetAccessControl();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        // Users may add files to this folder only; each file's creator gets full control of it.
        var createFiles = new FileSystemAccessRule(users,
            FileSystemRights.CreateFiles | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
        var ownFiles = new FileSystemAccessRule(creatorOwner, FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow);
        security.RemoveAccessRuleSpecific(createFiles);
        security.RemoveAccessRuleSpecific(ownFiles);
        if (usersMayWrite)
        {
            security.AddAccessRule(createFiles);
            security.AddAccessRule(ownFiles);
        }
        directory.SetAccessControl(security);
    }
}
