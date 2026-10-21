using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class NativeAppliedOwnershipSessionTests
{
    [Fact]
    public void SessionDeclaresEveryModuleLocalNativeEntryPoint()
    {
        var nativeMethods = typeof(NativeAppliedOwnershipSession).GetNestedType(
            "NativeMethods",
            BindingFlags.NonPublic);
        Assert.NotNull(nativeMethods);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GetAbiVersion"] = "rm_applied_ownership_abi_version",
            ["CreateNew"] = "rm_applied_ownership_create_new",
            ["OpenExisting"] = "rm_applied_ownership_open_existing",
            ["Destroy"] = "rm_applied_ownership_destroy",
            ["QueryCapacity"] = "rm_applied_ownership_query_capacity",
            ["Promote"] = "rm_applied_ownership_promote",
            ["PlanTransition"] = "rm_applied_ownership_plan_transition",
            ["Transition"] = "rm_applied_ownership_transition",
            ["Remove"] = "rm_applied_ownership_remove",
            ["Get"] = "rm_applied_ownership_get",
            ["Snapshot"] = "rm_applied_ownership_snapshot",
            ["Encode"] = "rm_applied_ownership_encode",
            ["DecodeReplace"] = "rm_applied_ownership_decode_replace"
        };

        foreach (var (methodName, entryPoint) in expected)
        {
            var method = nativeMethods!.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var import = method!.GetCustomAttribute<LibraryImportAttribute>();
            Assert.NotNull(import);
            Assert.Equal("ResourceManager.NativeCore", import!.LibraryName);
            Assert.Equal(entryPoint, import.EntryPoint);
        }
    }

    [Fact]
    public void WorkspaceUsesOneRegionForConfiguredRecordsAndImage()
    {
        var capacity = new NativeAppliedOwnershipCapacity
        {
            StructSize = NativeAppliedOwnershipAbi.CapacitySize,
            RecordCapacity = 7,
            PrimaryIndexCapacity = 8,
            PayloadIndexCapacity = 8,
            RecordSize = NativeAppliedOwnershipAbi.RecordSize,
            MaximumImageBytes = 4096,
            ResidentBytes = 8192
        };

        using var workspace = new NativeAppliedOwnershipWorkspace(in capacity, NativeAppliedOwnershipTestStorage.Create);
        using var operation = workspace.Acquire();

        Assert.Equal(7UL * NativeAppliedOwnershipAbi.RecordSize + 4096UL, workspace.Length);
        Assert.Equal(4096, operation.Image.Length);
        operation.Complete();
    }

    [Fact]
    public void WorkspaceRejectsInvalidCapacityWithoutRepairingIt()
    {
        var capacity = new NativeAppliedOwnershipCapacity
        {
            RecordCapacity = 1,
            RecordSize = NativeAppliedOwnershipAbi.RecordSize,
            MaximumImageBytes = (ulong)int.MaxValue + 1,
            ResidentBytes = 1
        };

        Assert.Throws<InvalidOperationException>(
            () => new NativeAppliedOwnershipWorkspace(in capacity, NativeAppliedOwnershipTestStorage.Create));
    }
}

internal static class NativeAppliedOwnershipTestStorage
{
    internal static IComputeScratch Create(ulong length) => new OrdinaryScratch(length);

    private sealed class OrdinaryScratch(ulong length) : IComputeScratch
    {
        private byte[]? bytes = new byte[checked((int)length)];
        private int active;
        public ulong Length => length;
        public ComputeScratchLease Acquire()
        {
            ObjectDisposedException.ThrowIf(bytes is null, this);
            active++;
            return new Lease(this, bytes);
        }
        public void Dispose()
        {
            if (active != 0) throw new InvalidOperationException("Fixture storage is still in use.");
            bytes = null;
        }
        private sealed class Lease(OrdinaryScratch owner, byte[] bytes) : ComputeScratchLease
        {
            private bool disposed;
            internal override ulong Length => (ulong)bytes.Length;
            internal override ulong AllocationNumber => 0;
            internal override bool IsLoading => false;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return bytes.AsMemory(checked((int)offset), count);
            }
            internal override void Complete() => ObjectDisposedException.ThrowIf(disposed, this);
            public override void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.active--;
            }
        }
    }
}
