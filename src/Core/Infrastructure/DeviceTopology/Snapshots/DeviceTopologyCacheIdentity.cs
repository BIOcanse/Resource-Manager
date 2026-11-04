using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

internal static class DeviceTopologyCacheIdentity
{
    private static readonly Reader Current = new(TryReadMachineGuid, static () => Environment.MachineName);

    public static string ReadCurrent() => ReadCurrentDescriptor().Sha256;

    internal static DeviceTopologyCacheIdentityDescriptor ReadCurrentDescriptor() => Current.Read();

    internal sealed class Reader
    {
        private readonly Lazy<DeviceTopologyCacheIdentityDescriptor> current;

        internal Reader(Func<string?> readMachineGuid, Func<string> readMachineName)
        {
            current = new(() =>
            {
                var machineGuid = readMachineGuid();
                var source = machineGuid is null ? "machine-name" : "machine-guid";
                var identity = machineGuid ?? readMachineName();
                return new(source, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
            });
        }

        [ResourceManager.ReadWrite.RuntimeReadCache]
        internal DeviceTopologyCacheIdentityDescriptor Read() => current.Value;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
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
