using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Amd;

public sealed partial class AmdAdlxBridgeReader
{
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows device-bound driver package changed")]
    public IReadOnlyList<DeviceTopologyAmdGpuInformation>? ReadDeclarations(WindowsGpuAdapterInventoryRead inventory, bool nativeAllowed = true)
    {
        var fallback = ReadDeclarationsCore(inventory, nativeAllowed: false);
        if (!nativeAllowed || fallback is null || fallback.Count == 0) return fallback;
        inventory = CopyInventory(inventory);
        return readBoundary.RunAsync(() => ReadDeclarationsCore(inventory, nativeAllowed: true), fallback,
            maximumReadDuration, CancellationToken.None).GetAwaiter().GetResult();
    }

    private IReadOnlyList<DeviceTopologyAmdGpuInformation>? ReadDeclarationsCore(WindowsGpuAdapterInventoryRead inventory, bool nativeAllowed)
    {
        if (inventory.Status != SamplingObservationStatus.Current || inventory.Generation == 0
            || inventory.ObservedCount != inventory.Adapters.Count || inventory.SkippedCount != 0 || inventory.OverflowCount != 0) return null;
        var known = new Dictionary<string, DeviceTopologyAmdGpuInformation>(StringComparer.Ordinal);
        foreach (var adapter in inventory.Adapters)
        {
            if (adapter.IsSoftware || adapter.VendorId != 0x1002) continue;
            if (adapter.PhysicalIdentityStatus != SamplingObservationStatus.Current || adapter.PhysicalIdentities.Count == 0) return null;
            foreach (var physical in adapter.PhysicalIdentities)
            {
                if (physical.Status != WindowsGpuPhysicalIdentityStatus.Bound || physical.VendorId != 0x1002
                    || string.IsNullOrWhiteSpace(physical.PnpInstanceId)) return null;
                var pnp = physical.PnpInstanceId.Trim().ToUpperInvariant();
                if (!known.TryAdd(pnp, new(pnp, null, null, null, null))) return null;
            }
        }
        if (known.Count == 0 || !nativeAllowed) return known.Values.ToArray();
        try
        {
            if (!ReadDeclarationAbiOnce()) return known.Values.ToArray();
            var metadata = new NativeAdlxGpuMetrics[32]; var message = new StringBuilder(512);
            var count = bridge.ReadMetadata(metadata, message);
            ThrowIfFaulted(count);
            if (count <= 0 || count > metadata.Length) return known.Values.ToArray();
            var valid = metadata.Take(count).Where(item => item.IdentityValid == 1 && item.AbiVersion == BridgeAbiVersion
                && item.StructSize == Marshal.SizeOf<NativeAdlxGpuMetrics>()).ToArray();
            var duplicates = valid.GroupBy(static item => item.UniqueId).Where(static group => group.Count() > 1)
                .Select(static group => group.Key).ToHashSet();
            var matched = new List<(string Identity, NativeAdlxGpuMetrics Native)>();
            foreach (var item in valid)
            {
                if (duplicates.Contains(item.UniqueId)) continue;
                var match = WindowsGpuProviderIdentityResolver.ResolvePnp(inventory, Decode(item.PnpString));
                if (!match.IsCurrent) continue;
                var pnp = match.Binding.PhysicalIdentity.PnpInstanceId.Trim().ToUpperInvariant();
                if (known.ContainsKey(pnp)) matched.Add((pnp, item));
            }
            var targets = matched.GroupBy(static item => item.Identity, StringComparer.Ordinal)
                .Where(static group => group.Count() == 1).Select(static group => group.Single()).ToArray();
            if (targets.Length == 0) return known.Values.ToArray();
            var selections = targets.Select(static target => new NativeAdlxGpuSelection
                { UniqueId = target.Native.UniqueId, PnpString = target.Native.PnpString, RequestedFlags = 15 }).ToArray();
            var values = new NativeAdlxGpuDeclaration[32];
            count = bridge.ReadDeclarations(values, selections, message);
            ThrowIfFaulted(count);
            if (count <= 0 || count > selections.Length) return known.Values.ToArray();
            foreach (var target in targets)
            {
                var result = values.Take(count).Where(item => item.StructSize == Marshal.SizeOf<NativeAdlxGpuDeclaration>()
                    && item.AbiVersion == 1 && item.UniqueId == target.Native.UniqueId
                    && Decode(item.PnpString) == Decode(target.Native.PnpString)).ToArray();
                if (result.Length != 1) continue;
                var item = result[0];
                known[target.Identity] = new(target.Identity, (item.AvailableFlags & 1) != 0 ? DecodeDeclaration(item.VbiosVersion) : null,
                    (item.AvailableFlags & 2) != 0 ? DecodeDeclaration(item.VramType) : null,
                    (item.AvailableFlags & 4) != 0 ? DecodeDeclaration(item.VbiosPartNumber) : null,
                    (item.AvailableFlags & 8) != 0 ? DecodeDeclaration(item.VbiosDateRaw) : null);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }
        return known.Values.OrderBy(static item => item.PnpInstanceId, StringComparer.Ordinal).ToArray();
    }

    internal static string? DecodeDeclaration(AdlxFixedString128 value)
    {
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        var length = bytes.IndexOf((byte)0);
        if (length <= 0) return null;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes[..length]);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private sealed partial class NativeBridge
    {
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
        public uint GetDeclarationAbiVersion() => NativeMethods.ResourceManagerAdlxGetDeclarationAbiVersion();
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
        public int GetDeclarationSize() => NativeMethods.ResourceManagerAdlxGetGpuDeclarationSize();
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows device-bound driver package changed")]
        public int ReadDeclarations(NativeAdlxGpuDeclaration[] output, NativeAdlxGpuSelection[] selections, StringBuilder message)
            => NativeMethods.ResourceManagerAdlxReadGpuDeclarations(output, output.Length, selections, selections.Length, message, message.Capacity);
    }
    private static partial class NativeMethods
    {
        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint ResourceManagerAdlxGetDeclarationAbiVersion();
        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResourceManagerAdlxGetGpuDeclarationSize();
        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int ResourceManagerAdlxReadGpuDeclarations([Out] NativeAdlxGpuDeclaration[] outputs, int capacity,
            [In] NativeAdlxGpuSelection[] selections, int selectionCount, StringBuilder message, int messageCapacity);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAdlxGpuDeclaration
{
    public int StructSize;
    public uint AbiVersion;
    public int UniqueId;
    public AdlxFixedString512 PnpString;
    public int AvailableFlags;
    public AdlxFixedString128 VbiosVersion;
    public AdlxFixedString128 VramType;
    public AdlxFixedString128 VbiosPartNumber;
    public AdlxFixedString128 VbiosDateRaw;
}
