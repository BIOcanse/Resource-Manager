namespace ResourceManager.ReadWrite.Windows.Gpu;

public static class WindowsGpuAdapterIdentity
{
    private const ulong FnvOffsetBasis = 0xcbf29ce484222325;
    private const ulong FnvPrime = 0x100000001b3;

    public static ulong Pack(AdapterLuid luid)
    {
        return ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;
    }

    public static ulong ComputeTopologyFingerprint(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var hash = FnvOffsetBasis;
        foreach (var adapter in adapters
            .Where(static adapter => !adapter.IsSoftware)
            .OrderBy(static adapter => Pack(adapter.Luid)))
        {
            var luid = Pack(adapter.Luid);
            Add(ref hash, (uint)(luid >> 32));
            Add(ref hash, (uint)luid);
            Add(ref hash, adapter.VendorId);
            Add(ref hash, adapter.DeviceId);
            Add(ref hash, adapter.SubSystemId);
            Add(ref hash, (uint)adapter.Kind);
            var identities = adapter.PhysicalIdentities
                .OrderBy(
                    static identity => identity.PnpInstanceId,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(static identity => identity.PhysicalAdapterIndex)
                .ToArray();
            Add(ref hash, checked((uint)identities.Length));
            foreach (var identity in identities)
            {
                Add(ref hash, identity.PnpInstanceId);
                Add(ref hash, identity.VendorId);
                Add(ref hash, identity.DeviceId);
                Add(ref hash, identity.SubVendorId);
                Add(ref hash, identity.SubsystemId);
                Add(ref hash, identity.RevisionId);
            }
        }

        return hash;
    }

    private static void Add(ref ulong hash, uint value)
    {
        for (var offset = 0; offset < sizeof(uint); offset++)
        {
            hash ^= (byte)(value >> (offset * 8));
            hash *= FnvPrime;
        }
    }

    private static void Add(ref ulong hash, string value)
    {
        Add(ref hash, checked((uint)value.Length));
        foreach (var character in value)
        {
            var normalized = char.ToUpperInvariant(character);
            hash ^= (byte)normalized;
            hash *= FnvPrime;
            hash ^= (byte)(normalized >> 8);
            hash *= FnvPrime;
        }
    }

}
