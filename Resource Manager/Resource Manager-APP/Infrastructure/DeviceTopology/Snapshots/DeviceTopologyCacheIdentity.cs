using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

internal static class DeviceTopologyCacheIdentity
{
    public static string ReadCurrent() => ReadCurrentDescriptor().Sha256;

    internal static DeviceTopologyCacheIdentityDescriptor ReadCurrentDescriptor()
    {
        var machineGuid = TryReadMachineGuid();
        var source = machineGuid is null ? "machine-name" : "machine-guid";
        var identity = machineGuid ?? Environment.MachineName;
        return new DeviceTopologyCacheIdentityDescriptor(
            source,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    private static string? TryReadMachineGuid()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record DeviceTopologyCacheIdentityDescriptor(
    string Source,
    string Sha256);
