using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class PortableRegistryOutputLedgerTests
{
    [Fact]
    public void RealSnapshotAndPersistenceOutputUseOneLazyIdentityAndRefillAfterIdleReclaim()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = new NativePortableSoftwareRegistryWorkspace(
            HostManagerTestPlanFactory.CreatePlan().PortableSoftwareRegistry,
            bytes => resource = owner.CreateScratch(bytes));
        Assert.NotNull(resource);
        var uid = resource.Uid;
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var expected = checked((ulong)workspace.OperationCapacity * (ulong)Unsafe.SizeOf<NativePortableSoftwarePersistenceOperation>()
            + (ulong)workspace.RegistrationCapacity * (ulong)Unsafe.SizeOf<NativePortableSoftwareRegistrationSnapshot>()
            + (ulong)workspace.PathCapacity * (ulong)Unsafe.SizeOf<NativePortableSoftwarePathSnapshot>());
        Assert.Equal(expected, resource.Length);
        owner.SetMode(LedgerTableMode.Optimize);
        var capacity = workspace.Session.Capacity;
        var payloads = new NativePortableSoftwareRegistryPayloadCatalog(1);
        var projection = new NativePortableSoftwareRegistryProjection(payloads,
            capacity.ExecutablePathByteCapacityPerPath, capacity.RootPathByteCapacityPerPath, 1000);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var observation = projection.ProjectObserve(new PortableSoftwareObservation(
            "catalog:tool", "tool", "Tool", SoftwareKinds.Other, "c:/portable/tool.exe", "c:/portable", true, false),
            workspace.Session.ConfigurationGeneration, 1, now, now);
        var observe = observation.Input;
        Assert.Equal(NativePortableSoftwareRegistryStatus.Ok, workspace.Session.Observe(in observe, observation.KeyBytes.Span));
        var input = new NativePortableSoftwareSnapshotInput
        {
            AbiVersion = NativePortableSoftwareRegistryAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativePortableSoftwareSnapshotInput>(),
            ConfigurationGeneration = workspace.Session.ConfigurationGeneration,
            SnapshotEpoch = 1,
            MaximumRegistrationCount = (uint)workspace.RegistrationCapacity,
            MaximumPathCount = (uint)workspace.PathCapacity,
            ValidMask = (ulong)NativePortableSoftwareSnapshotValidity.Required
        };
        Assert.Equal(NativePortableSoftwareRegistryStatus.Ok, workspace.ReadSnapshot(in input,
            out var registrations, out var paths, out var snapshot));
        Assert.Single(registrations); Assert.Single(paths); Assert.Equal(1U, snapshot.PathCount);
        var returnedPath = paths[0];
        Assert.Equal(expected, owner.ReadTable().RegisteredBytes);
        ulong first;
        using (var use = resource.Acquire())
        {
            first = use.AllocationNumber;
            Assert.Equal(0UL, owner.Tick().Released);
        }
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var plan = new NativePortableSoftwarePlanPersistenceInput
        {
            AbiVersion = NativePortableSoftwareRegistryAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativePortableSoftwarePlanPersistenceInput>(),
            ConfigurationGeneration = workspace.Session.ConfigurationGeneration,
            PlanEpoch = 1,
            MaximumOperationCount = (uint)workspace.OperationCapacity,
            ValidMask = (ulong)NativePortableSoftwarePlanValidity.Required
        };
        Assert.Equal(NativePortableSoftwareRegistryStatus.Ok, workspace.PlanPersistence(in plan, out var operations, out var output));
        Assert.Single(operations); Assert.Equal(1U, output.OperationCount);
        Assert.Equal(returnedPath.PathHandle, operations[0].PathHandle);
        Assert.Equal(returnedPath, paths[0]); // The previous snapshot copy survives reusing its region.
        Assert.Equal(uid, resource.Uid);
        using (var use = resource.Acquire()) Assert.NotEqual(first, use.AllocationNumber);
        Assert.Equal(1UL, owner.Tick().Released);
        input.SnapshotEpoch++;
        Assert.Equal(NativePortableSoftwareRegistryStatus.Ok, workspace.ReadSnapshot(in input, out _, out _, out _));
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FailedCloseRetainsTheWorkspaceForExplicitRetry()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var workspace = new NativePortableSoftwareRegistryWorkspace(
            HostManagerTestPlanFactory.CreatePlan().PortableSoftwareRegistry,
            bytes => resource = owner.CreateScratch(bytes));
        using (var use = resource!.Acquire())
        {
            use.Complete();
            Assert.Throws<LedgerException>(() => workspace.Dispose());
            Assert.NotEqual(0UL, workspace.Session.Capacity.ResidentByteCount);
            Assert.Equal(0UL, owner.Tick().Released);
        }
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }
}
