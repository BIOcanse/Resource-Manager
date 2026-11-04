using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Vendors.Intel;

// IGCL Version 0 x64 PCI structures, including the unconsumed bandwidth member.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct IntelGclPciSpeed
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public byte Version;
    [FieldOffset(8)] public int Generation;
    [FieldOffset(12)] public int Width;
    [FieldOffset(16)] public long MaximumBandwidth;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct IntelGclPciState
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public byte Version;
    [FieldOffset(8)] public IntelGclPciSpeed Speed;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct IntelGclPciAddress
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public byte Version;
    [FieldOffset(8)] public uint Domain;
    [FieldOffset(12)] public uint Bus;
    [FieldOffset(16)] public uint Device;
    [FieldOffset(20)] public uint Function;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct IntelGclPciProperties
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public byte Version;
    [FieldOffset(8)] public IntelGclPciAddress Address;
    [FieldOffset(32)] public IntelGclPciSpeed MaximumSpeed;
    [FieldOffset(56)] public byte ResizableBarSupported;
    [FieldOffset(57)] public byte ResizableBarEnabled;
}
