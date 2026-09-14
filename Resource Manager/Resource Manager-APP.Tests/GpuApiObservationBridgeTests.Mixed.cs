using System.Buffers.Binary;
using System.Reflection;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuApiObservationBridgeTests
{
    private static readonly WindowsGpuPlacementInjector.ApiObservationEntries LayerEntry =
        new(new(201), new(202), new(203), GpuGraphicsApi.Vulkan);
    private static readonly WindowsGpuPlacementInjector.ApiObservationEntries OtherEntry =
        new(new(301), new(302), new(303), GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryCandidateMaskIsCoveredOnceWithoutCompetingVulkanHooks(bool existingLayer)
    {
        const string root = @"C:\fixture";
        var layer = new WindowsGpuPlacementInjector.RemoteModule(new(200),
            Path.Combine(root, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanLayerFileName));
        for (var mask = 1; mask <= 31; mask++)
        {
            var providers = WindowsGpuPlacementInjector.SelectApiObservationProviders((GpuGraphicsApi)mask, root,
                name => existingLayer && name == GpuStartupProviderArtifacts.VulkanLayerFileName ? layer : null);
            var combined = (GpuGraphicsApi)0;
            foreach (var provider in providers)
            {
                Assert.Equal((GpuGraphicsApi)0, combined & provider.Apis);
                combined |= provider.Apis;
                if (provider.LoadedModule is not null)
                {
                    Assert.Equal(GpuGraphicsApi.Vulkan, provider.Apis);
                    Assert.Equal(layer, provider.LoadedModule);
                }
                else if (existingLayer) Assert.Equal((GpuGraphicsApi)0, provider.Apis & GpuGraphicsApi.Vulkan);
            }
            Assert.Equal((GpuGraphicsApi)mask, combined);
            Assert.Equal(existingLayer && (mask & 4) != 0 && mask != 4 ? 2 : 1, providers.Count);
            if (!existingLayer) Assert.Equal((GpuGraphicsApi)mask, Assert.Single(providers).Apis);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    public async Task TwoDisjointBindingsHaveOneWaitAndOnlyUnionActualResults(int layer, int other)
    {
        var calls = new List<GpuRemoteCallRequest>();
        var order = new List<string>();
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                var request = call.Request;
                calls.Add(request);
                order.Add(request.FunctionAddress.ToString());
                Assert.Equal(16, request.ParameterByteLength);
                if (request.Kind == GpuRemoteCallKind.StartApiObservation)
                {
                    var payload = Assert.IsType<byte[]>(typeof(WindowsGpuPlacementInjector.RemoteCall)
                        .GetField("payload", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(call));
                    Assert.Equal(16U, BinaryPrimitives.ReadUInt32LittleEndian(payload));
                    Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)));
                    Assert.Equal(request.FunctionAddress == 201 ? 4U : 3U, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)));
                    Assert.Equal(37U, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12)));
                    return Task.FromResult(Completed(null));
                }
                return Task.FromResult(Completed(Response((GpuGraphicsApi)(request.FunctionAddress == 203 ? layer : other), false)));
            }
        });
        owner.ApiObservations = [LayerEntry, OtherEntry];
        var result = await owner.ObserveApiOnceAsync(37, (duration, _) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(37), duration);
            order.Add("wait");
            return Task.CompletedTask;
        }, default, default);
        Assert.Equal(new GpuApiObservationSnapshot((GpuGraphicsApi)(layer | other), false), result.Snapshot);
        Assert.Equal(new[] { "201", "301", "wait", "203", "303" }, order);
        Assert.Equal(4, calls.Select(call => call.CallId).Distinct().Count());
        Assert.All(calls, call => Assert.Equal(owner.Identity, call.Process));
    }

    [Theory]
    [InlineData(0, "exit")]
    [InlineData(1, "exit")]
    [InlineData(0, "missing")]
    [InlineData(1, "missing")]
    [InlineData(0, "length")]
    [InlineData(1, "length")]
    [InlineData(0, "outside")]
    [InlineData(1, "outside")]
    [InlineData(0, "still-recording")]
    [InlineData(1, "still-recording")]
    public async Task EitherMalformedStoppedBindingRejectsTheWholeObservation(int broken, string boundary)
    {
        var stops = 0;
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                if (!call.Request.ReadResponse) return Task.FromResult(Completed(null));
                var index = stops++;
                var response = Response(index == 0 ? GpuGraphicsApi.Vulkan : GpuGraphicsApi.D3D11, false);
                if (index != broken) return Task.FromResult(Completed(response));
                if (boundary == "outside") response = Response(index == 0 ? GpuGraphicsApi.D3D11 : GpuGraphicsApi.Vulkan, false);
                if (boundary == "still-recording") BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), 1);
                if (boundary == "length") response = response[..15];
                return Task.FromResult(Completed(boundary == "missing" ? null : response) with { ExitCode = boundary == "exit" ? 0U : 1U });
            }
        });
        owner.ApiObservations = [LayerEntry, OtherEntry];
        var result = await owner.ObserveApiOnceAsync(37, (_, _) => Task.CompletedTask, default, default);
        Assert.Null(result.Snapshot);
        Assert.Equal(2, stops);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("released-pending")]
    [InlineData("unreleased-pending")]
    [InlineData("throw")]
    public async Task FailedSecondStartStopsOnlyTheConfirmedFirstBindingWhenOriginalOwnerAllowsIt(string boundary)
    {
        var addresses = new List<ulong>();
        var failure = new IOException("second-start");
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                addresses.Add(call.Request.FunctionAddress);
                if (call.Request.FunctionAddress == 301)
                {
                    if (boundary == "throw") throw failure;
                    if (boundary == "rejected") return Task.FromResult(Completed(null) with { ExitCode = 0 });
                    return Task.FromResult(new GpuRemoteCallSnapshot(0, null, null, null, null,
                        boundary == "released-pending", "fixture-pending", 123));
                }
                return Task.FromResult(Completed(call.Request.ReadResponse ? Response(0, false) : null));
            }
        });
        owner.ApiObservations = [LayerEntry, OtherEntry];
        Task<GpuApiObservationReadResult> Run() => owner.ObserveApiOnceAsync(37,
            (_, _) => throw new InvalidOperationException("unexpected-wait"), default, default);
        if (boundary == "throw") Assert.Same(failure, await Assert.ThrowsAsync<IOException>(Run));
        else if (boundary == "unreleased-pending") Assert.Equal(2, (await Assert.ThrowsAsync<AggregateException>(Run)).InnerExceptions.Count);
        else Assert.Null((await Run()).Snapshot);
        Assert.Equal(boundary == "unreleased-pending" ? new ulong[] { 201, 301 } : new ulong[] { 201, 301, 203 }, addresses);
    }

    [Theory]
    [InlineData("before", 0)]
    [InlineData("first", 1)]
    [InlineData("second", 2)]
    [InlineData("wait", 2)]
    public async Task CancellationKeepsOneCleanupTokenAndOnlyStopsThisCallsConfirmedBindings(string when, int starts)
    {
        using var work = new CancellationTokenSource();
        using var cleanup = new CancellationTokenSource();
        var seenStarts = 0;
        var seenStops = 0;
        var waits = 0;
        using var owner = CreateOwner((call, token) =>
        {
            using (call)
            {
                if (call.Request.Kind == GpuRemoteCallKind.StartApiObservation)
                {
                    Assert.Equal(work.Token, token);
                    seenStarts++;
                    if ((when == "first" && seenStarts == 1) || (when == "second" && seenStarts == 2)) work.Cancel();
                }
                else { Assert.Equal(cleanup.Token, token); seenStops++; }
                return Task.FromResult(Completed(call.Request.ReadResponse ? Response(0, false) : null));
            }
        });
        owner.ApiObservations = [LayerEntry, OtherEntry];
        if (when == "before") work.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.ObserveApiOnceAsync(37, (_, _) =>
        {
            waits++;
            work.Cancel();
            return Task.CompletedTask;
        }, work.Token, cleanup.Token));
        Assert.Equal(starts, seenStarts);
        Assert.Equal(starts, seenStops);
        Assert.Equal(when == "wait" ? 1 : 0, waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingFirstStopRetainsOriginalOwnerRulesAndNeverReturnsPartialData(bool released)
    {
        var stops = new List<ulong>();
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                if (!call.Request.ReadResponse) return Task.FromResult(Completed(null));
                stops.Add(call.Request.FunctionAddress);
                return Task.FromResult(stops.Count == 1
                    ? new GpuRemoteCallSnapshot(0, null, null, null, null, released, "fixture-pending", 123)
                    : Completed(Response(GpuGraphicsApi.D3D11, false)));
            }
        });
        owner.ApiObservations = [LayerEntry, OtherEntry];
        Task<GpuApiObservationReadResult> Run() => owner.ObserveApiOnceAsync(37,
            (_, _) => Task.CompletedTask, default, default);
        if (released) Assert.Null((await Run()).Snapshot);
        else Assert.Equal(2, (await Assert.ThrowsAsync<AggregateException>(Run)).InnerExceptions.Count);
        Assert.Equal(released ? new ulong[] { 203, 303 } : new ulong[] { 203 }, stops);
    }

    [Fact]
    public async Task ExpiredCleanupDoesNotSubmitEitherStopOrReturnObservedData()
    {
        using var cleanup = new CancellationTokenSource();
        var starts = 0;
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                Assert.Equal(GpuRemoteCallKind.StartApiObservation, call.Request.Kind);
                starts++;
                return Task.FromResult(Completed(null));
            }
        });
        owner.ApiObservations = [LayerEntry, OtherEntry];
        var failure = await Assert.ThrowsAsync<AggregateException>(() => owner.ObserveApiOnceAsync(37,
            (_, _) => { cleanup.Cancel(); return Task.CompletedTask; }, default, cleanup.Token));
        Assert.Equal(2, starts);
        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothStopExceptionsArePreservedAndDoNotSkipTheSecondStop(bool failWait)
    {
        var waitFailure = new IOException("wait");
        var stops = new[] { new IOException("first-stop"), new IOException("second-stop") };
        var stopCount = 0;
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                if (call.Request.ReadResponse) throw stops[stopCount++];
                return Task.FromResult(Completed(null));
            }
        });
        owner.ApiObservations = [LayerEntry, OtherEntry];
        var error = await Assert.ThrowsAsync<AggregateException>(() => owner.ObserveApiOnceAsync(37,
            (_, _) => failWait ? throw waitFailure : Task.CompletedTask, default, default));
        Exception[] expected = failWait ? [waitFailure, .. stops] : stops;
        Assert.Equal(expected, error.InnerExceptions);
        Assert.Equal(2, stopCount);
    }
}
