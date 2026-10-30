using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class PortableRegistryOutputLedgerTests
{
    [Fact]
    public void SnapshotAndPersistenceCreateLiveRowsPerCallAndHoldNoScratchBetweenCalls()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var created = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativePortableSoftwareRegistryWorkspace(
            HostManagerTestPlanFactory.CreatePlan().PortableSoftwareRegistry,
            bytes => { var scratch = owner.CreateScratch(bytes); created.Add(scratch); return scratch; });
        Assert.Empty(created);
        Assert.Empty(owner.ReadWorkspaceCapacities());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);

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
        var live = workspace.Session.WorkspaceRows;
        Assert.Equal((1U, 1U, 1U), (live.RegistrationCount, live.PathCount, live.DirtyPathCount));

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
        var snapshotScratch = Assert.Single(created);
        Assert.Equal((ulong)Unsafe.SizeOf<NativePortableSoftwareRegistrationSnapshot>()
            + (ulong)Unsafe.SizeOf<NativePortableSoftwarePathSnapshot>(), snapshotScratch.Length);
        Assert.Equal(NativePortableSoftwareRegistryWorkspace.ScratchWorkArea, snapshotScratch.WorkArea);
        Assert.Equal("registrations=1/live=1, paths=1/live=1", snapshotScratch.Rows);
        Assert.Empty(owner.ReadWorkspaceCapacities()); // Closed before the call returned.

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
        Assert.Single(operations); Assert.Equal(1U, output.OperationCount); Assert.Equal(1U, output.TotalDirtyCount);
        Assert.Equal(paths[0].PathHandle, operations[0].PathHandle);
        Assert.Equal(2, created.Count);
        Assert.Equal((ulong)Unsafe.SizeOf<NativePortableSoftwarePersistenceOperation>(), created[1].Length);
        Assert.Equal("operations=1/live=1", created[1].Rows);
        Assert.Empty(owner.ReadWorkspaceCapacities());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void ARegistryLargerThanAPageLendsLedgerRowsOnlyForTheCall()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var created = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativePortableSoftwareRegistryWorkspace(
            HostManagerTestPlanFactory.CreatePlan().PortableSoftwareRegistry,
            bytes => { var scratch = owner.CreateScratch(bytes); created.Add(scratch); return scratch; });
        var capacity = workspace.Session.Capacity;
        var projection = new NativePortableSoftwareRegistryProjection(new NativePortableSoftwareRegistryPayloadCatalog(1),
            capacity.ExecutablePathByteCapacityPerPath, capacity.RootPathByteCapacityPerPath, 1000);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var rows = checked((int)(owner.NativePageSize / 80) + 1);
        var index = 0;
        while (index < rows)
        {
            var observation = projection.ProjectObserve(new PortableSoftwareObservation(
                $"catalog:tool{index}", $"tool{index}", $"Tool {index}", SoftwareKinds.Other,
                $"c:/portable/tool{index}/tool.exe", $"c:/portable/tool{index}", true, false),
                workspace.Session.ConfigurationGeneration, checked((ulong)index + 1), now, now);
            var observe = observation.Input;
            Assert.Equal(NativePortableSoftwareRegistryStatus.Ok, workspace.Session.Observe(in observe, observation.KeyBytes.Span));
            index++;
        }

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
            out var registrations, out var paths, out _));
        Assert.Equal(rows, registrations.Length); Assert.Equal(rows, paths.Length);
        var scratch = Assert.Single(created);
        Assert.True(scratch.InLedger);
        Assert.Equal(checked((ulong)rows * 160UL), scratch.Length);
        Assert.Equal($"registrations={rows}/live={rows}, paths={rows}/live={rows}", scratch.Rows);
        Assert.Empty(owner.ReadWorkspaceCapacities());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }
}
