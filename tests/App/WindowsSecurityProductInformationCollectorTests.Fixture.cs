using System.Runtime.InteropServices;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsSecurityProductInformationCollectorTests
{
    internal sealed class Api : IWindowsSecurityInformationApi
    {
        internal readonly WindowsSecurityInformationCollectorTests.Api Scalars = new();
        internal readonly List<uint> Providers = [];
        internal Func<uint, Com> Open = _ => new();
        public int GetHealth(uint provider, out uint health) => Scalars.GetHealth(provider, out health);
        public IWindowsFirewallPolicyRead? OpenFirewall() => Scalars.OpenFirewall();
        public IReadOnlyList<DeviceTopologySecurityProductInformation>? ReadProducts(uint provider)
        {
            Providers.Add(provider);
            using var com = Open(provider);
            return WindowsSecurityInformationNativeApi.ReadProducts(provider,
                value => WindowsSecurityProductListReadSession.Open(value, com.Functions));
        }
    }

    internal sealed class Com : IDisposable
    {
        private readonly List<Delegate> roots = [];
        private readonly HashSet<IntPtr> strings = [];
        internal readonly IntPtr Table, Pointer;
        internal readonly List<Product> Products = [];
        internal readonly List<uint> Providers = [], Items = [];
        internal int Initializes, Creates, Uninitializes, Releases, Counts, StringReads, StringFrees;
        internal int ApartmentStatus, CreateStatus, InitializeStatus, CountStatus;
        internal int? CountOverride, FailedItem, NullItem;
        internal bool EmptyCreate, FailRelease;
        internal WindowsSecurityProductComFunctions Functions { get; }

        internal Com()
        {
            Table = Marshal.AllocHGlobal(10 * IntPtr.Size); Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            for (var i = 0; i < 10; i++) Marshal.WriteIntPtr(Table, i * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(Pointer, Table);
            Put(2, new SecurityProductRelease(self => { Assert.Equal(Pointer, self); Releases++; if (FailRelease) throw new COMException("release failed"); return 0; }));
            Put(7, new SecurityProductInitialize((self, provider) => { Assert.Equal(Pointer, self); Providers.Add(provider); return InitializeStatus; }));
            Put(8, new SecurityProductCount((IntPtr self, out int count) => { Assert.Equal(Pointer, self); Counts++; count = CountOverride ?? Products.Count; return CountStatus; }));
            Put(9, new SecurityProductItem((IntPtr self, uint index, out IntPtr pointer) => {
                Assert.Equal(Pointer, self); Items.Add(index);
                pointer = index == NullItem ? IntPtr.Zero : index == FailedItem ? new IntPtr(0x1234) : Products[(int)index].Pointer;
                return index == FailedItem ? unchecked((int)0x80004005) : 0;
            }));
            Functions = new(() => { Initializes++; return ApartmentStatus; },
                (Guid classId, Guid interfaceId, out IntPtr pointer) => {
                    Creates++; Assert.Equal(WindowsSecurityProductListReadSession.ClassId, classId);
                    Assert.Equal(WindowsSecurityProductListReadSession.InterfaceId, interfaceId);
                    pointer = CreateStatus != 0 ? new IntPtr(0x1234) : EmptyCreate ? IntPtr.Zero : Pointer; return CreateStatus;
                }, () => Uninitializes++, value => { Assert.Contains(value, strings); StringReads++; return (uint)Marshal.ReadInt32(value, -4) / 2; },
                value => { Assert.True(strings.Remove(value)); StringFrees++; Marshal.FreeBSTR(value); });
        }

        internal Product Add(string? name = "Product") { var product = new Product(this, name); Products.Add(product); return product; }
        internal void Missing(int slot) => Marshal.WriteIntPtr(Table, slot * IntPtr.Size, IntPtr.Zero);
        private void Put(int slot, Delegate method) { roots.Add(method); Marshal.WriteIntPtr(Table, slot * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(method)); }
        internal IntPtr Allocate(string value) { var pointer = Marshal.StringToBSTR(value); Assert.True(strings.Add(pointer)); return pointer; }
        internal void AssertStringsReleased() => Assert.Empty(strings);
        public void Dispose()
        {
            foreach (var pointer in strings) Marshal.FreeBSTR(pointer);
            foreach (var product in Products.Distinct()) product.Dispose();
            Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(Table); GC.KeepAlive(roots);
        }
    }

    internal sealed partial class Product : IDisposable
    {
        private readonly List<Delegate> roots = [];
        internal readonly IntPtr Table, Pointer;
        internal readonly Dictionary<int, (int Status, string? Value)> Text;
        internal readonly Dictionary<int, (int Status, uint Value)> Numbers = new() { [8] = (0, 0), [9] = (0, 1) };
        internal readonly List<int> Requested = [];
        internal int Releases;
        internal Product(Com owner, string? name)
        {
            Text = new() { [7] = (0, name), [10] = (0, "C:\\Repair.exe"), [11] = (0, "raw timestamp") };
            Table = Marshal.AllocHGlobal(12 * IntPtr.Size); Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            for (var i = 0; i < 12; i++) Marshal.WriteIntPtr(Table, i * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(Pointer, Table);
            Put(2, new SecurityProductRelease(self => { Assert.Equal(Pointer, self); Releases++; return 0; }));
            foreach (var slot in new[] { 7, 10, 11 }) Put(slot, new SecurityProductGetString((IntPtr self, out IntPtr value) => {
                Assert.Equal(Pointer, self); Requested.Add(slot); var property = Text[slot];
                value = property.Status != 0 ? new IntPtr(0x1234) : property.Value is null ? IntPtr.Zero : owner.Allocate(property.Value);
                return property.Status;
            }));
            foreach (var slot in new[] { 8, 9 }) Put(slot, new SecurityProductGetNumber((IntPtr self, out uint value) => {
                Assert.Equal(Pointer, self); Requested.Add(slot); var property = Numbers[slot]; value = property.Value; return property.Status;
            }));
        }
        internal void Missing(int slot) => Marshal.WriteIntPtr(Table, slot * IntPtr.Size, IntPtr.Zero);
        private void Put(int slot, Delegate method) { roots.Add(method); Marshal.WriteIntPtr(Table, slot * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(method)); }
        public void Dispose() { Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(Table); GC.KeepAlive(roots); }
    }
}
