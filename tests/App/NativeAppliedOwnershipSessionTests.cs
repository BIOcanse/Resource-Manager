using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

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
    public void WorkspaceAllocatesOnlyConfiguredFixedBuffers()
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

        var workspace = new NativeAppliedOwnershipWorkspace(in capacity);

        Assert.Equal(7, workspace.Records.Length);
        Assert.Equal(4096, workspace.Image.Length);
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
            () => new NativeAppliedOwnershipWorkspace(in capacity));
    }
}
