using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsSecurityProductInformationCollectorTests
{
    internal sealed partial class Product
    {
        internal void SetQuery(SecurityProductQueryInterface query) => Put(0, query);
    }

    internal sealed class ExtendedProduct : IDisposable
    {
        private readonly List<Delegate> roots = [];
        private readonly IntPtr table;
        internal readonly IntPtr Pointer;
        internal readonly List<Guid> Queries = [];
        internal readonly List<int> Requested = [];
        internal readonly Dictionary<int, (int Status, uint Value)> Numbers = new();
        internal int Product3Status, Product2Status, BooleanStatus, BooleanValue, Releases;
        internal int GuidStatus;
        internal string? GuidValue = "guid\0raw";
        internal bool EmptyProduct3, ThrowRelease;

        internal ExtendedProduct(Com owner, Product product)
        {
            table = Marshal.AllocHGlobal(21 * IntPtr.Size); Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            for (var i = 0; i < 21; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(Pointer, table);
            Put(2, new SecurityProductRelease(self => {
                Assert.Equal(Pointer, self); Releases++; if (ThrowRelease) throw new COMException("extended release failed"); return 0;
            }));
            Put(12, new SecurityProductGetString((IntPtr self, out IntPtr value) => {
                Assert.Equal(Pointer, self); Requested.Add(12);
                value = GuidStatus != 0 ? new IntPtr(0x1234) : GuidValue is null ? IntPtr.Zero : owner.Allocate(GuidValue);
                return GuidStatus;
            }));
            Put(13, new SecurityProductGetBoolean((IntPtr self, out int value) => {
                Assert.Equal(Pointer, self); Requested.Add(13); value = BooleanValue; return BooleanStatus;
            }));
            for (var i = 14; i <= 20; i++)
            {
                var slot = i; Numbers[slot] = (0, (uint)(i % 4));
                Put(slot, new SecurityProductGetNumber((IntPtr self, out uint value) => {
                    Assert.Equal(Pointer, self); Requested.Add(slot); var property = Numbers[slot]; value = property.Value; return property.Status;
                }));
            }
            product.SetQuery((IntPtr self, in Guid id, out IntPtr value) => {
                Assert.Equal(product.Pointer, self); Queries.Add(id);
                Assert.True(id == WindowsSecurityProductListReadSession.Product3Id || id == WindowsSecurityProductListReadSession.Product2Id);
                var status = id == WindowsSecurityProductListReadSession.Product3Id ? Product3Status : Product2Status;
                value = status != 0 ? new IntPtr(0x1234) : id == WindowsSecurityProductListReadSession.Product3Id && EmptyProduct3 ? IntPtr.Zero : Pointer;
                return status;
            });
        }
        internal void Missing(int slot) => Marshal.WriteIntPtr(table, slot * IntPtr.Size, IntPtr.Zero);
        private void Put(int slot, Delegate method) { roots.Add(method); Marshal.WriteIntPtr(table, slot * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(method)); }
        public void Dispose() { Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(table); GC.KeepAlive(roots); }
    }
}
