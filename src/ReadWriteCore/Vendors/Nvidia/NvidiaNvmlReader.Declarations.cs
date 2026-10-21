using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Globalization;
using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

internal interface INvmlGpuDeclarationApi
{
    bool Initialize();
    int Count(out uint count);
    int Handle(uint index, out IntPtr device);
    int Pci(IntPtr device, out NvmlPciInfo pci);
    int Vbios(IntPtr device, byte[] buffer);
    int Cuda(IntPtr device, out int major, out int minor);
}

public sealed partial class NvidiaNvmlReader
{
    public IReadOnlyList<DeviceTopologyNvidiaGpuInformation>? ReadDeclarations(WindowsGpuAdapterInventoryRead inventory)
    {
        lock (gate) return ReadDeclarations(inventory, new DeclarationApi(this));
    }

    internal static IReadOnlyList<DeviceTopologyNvidiaGpuInformation>? ReadDeclarations(
        WindowsGpuAdapterInventoryRead inventory, INvmlGpuDeclarationApi? api)
    {
        if (inventory.Status != SamplingObservationStatus.Current || inventory.Generation == 0
            || inventory.ObservedCount != inventory.Adapters.Count || inventory.SkippedCount != 0 || inventory.OverflowCount != 0)
            return null;
        var known = new Dictionary<string, DeviceTopologyNvidiaGpuInformation>(StringComparer.Ordinal);
        foreach (var adapter in inventory.Adapters)
        {
            if (adapter.IsSoftware || adapter.VendorId != 0x10DE) continue;
            if (adapter.PhysicalIdentityStatus != SamplingObservationStatus.Current || adapter.PhysicalIdentities.Count == 0)
                return null;
            foreach (var physical in adapter.PhysicalIdentities)
            {
                if (physical.Status != WindowsGpuPhysicalIdentityStatus.Bound || physical.VendorId != 0x10DE
                    || string.IsNullOrWhiteSpace(physical.PnpInstanceId)) return null;
                var identity = physical.PnpInstanceId.Trim().ToUpperInvariant();
                if (!known.TryAdd(identity,new(identity,null,null))) return null;
            }
        }
        if (known.Count == 0 || api is null) return known.Values.ToArray();
        try
        {
            if (!api.Initialize() || api.Count(out var count) != NvmlSuccess || count > 128)
                return known.Values.ToArray();
            // Borrowed NVML handles; query only exact, unique Windows physical members.
            var matched = new List<(string Identity, IntPtr Device)>();
            for (uint index = 0; index < count; index++)
            {
                if (api.Handle(index,out var device) != NvmlSuccess || api.Pci(device,out var pci) != NvmlSuccess
                    || !WindowsGpuProviderIdentityResolver.TryParseNvmlPciEvidence(pci,out var evidence)) continue;
                var match = WindowsGpuProviderIdentityResolver.ResolvePci(inventory,evidence);
                if (!match.IsCurrent) continue;
                var identity = match.Binding.PhysicalIdentity.PnpInstanceId.Trim().ToUpperInvariant();
                if (known.ContainsKey(identity)) matched.Add((identity,device));
            }
            foreach (var group in matched.GroupBy(static item => item.Identity,StringComparer.Ordinal))
            {
                if (group.Count() != 1) continue;
                var target = group.Single();
                string? version = null, capability = null;
                try
                {
                    var buffer = new byte[32];
                    if (api.Vbios(target.Device,buffer) == NvmlSuccess) version = DecodeVbios(buffer);
                }
                catch (EntryPointNotFoundException) { }
                try
                {
                    if (api.Cuda(target.Device,out var major,out var minor) == NvmlSuccess && major >= 0 && minor >= 0)
                        capability = major.ToString(CultureInfo.InvariantCulture)+"."+minor.ToString(CultureInfo.InvariantCulture);
                }
                catch (EntryPointNotFoundException) { }
                known[target.Identity] = new(target.Identity,version,capability);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return known.Values.OrderBy(static item => item.PnpInstanceId,StringComparer.Ordinal).ToArray();
    }

    internal static string? DecodeVbios(byte[] buffer)
    {
        if (buffer.Length != 32) return null;
        var length = Array.IndexOf(buffer,(byte)0);
        if (length <= 0) return null;
        for (var index=0;index<length;index++) if (buffer[index] < 32 || buffer[index] > 126) return null;
        var version = Encoding.ASCII.GetString(buffer,0,length);
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private sealed class DeclarationApi(NvidiaNvmlReader owner) : INvmlGpuDeclarationApi
    {
        public bool Initialize() => owner.EnsureInitialized();
        public int Count(out uint count) => NativeMethods.nvmlDeviceGetCount(out count);
        public int Handle(uint index,out IntPtr device) => NativeMethods.nvmlDeviceGetHandleByIndex(index,out device);
        public int Pci(IntPtr device,out NvmlPciInfo pci) => NativeMethods.nvmlDeviceGetPciInfo(device,out pci);
        public int Vbios(IntPtr device,byte[] buffer) => NativeMethods.GetVbiosVersion(device,buffer,(uint)buffer.Length);
        public int Cuda(IntPtr device,out int major,out int minor) => NativeMethods.GetCudaComputeCapability(device,out major,out minor);
    }
}
