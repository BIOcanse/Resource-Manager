using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuApiObservationBridgeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-1)]
    public async Task InvalidCandidatesAreRejectedBeforeOpeningAProcess(int candidates)
    {
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => injector.OpenForApiObservationAsync(
            new(42, 1, "target", @"C:\fixture\target.exe"), (GpuGraphicsApi)candidates,
            GpuRemoteCallTestOwner.RejectUnexpected, default));
    }

    [Theory]
    [InlineData("pid", "invalid-target")]
    [InlineData("self", "invalid-target")]
    [InlineData("birth", "process-identity-invalid")]
    [InlineData("relative", "process-identity-invalid")]
    public async Task ObservationUsesOriginalTargetAdmissionWithoutCallingAnEntry(string boundary, string expected)
    {
        var target = new GpuPlacementProcessInstance(42, 1, "target", @"C:\fixture\target.exe");
        target = boundary switch
        {
            "pid" => target with { ProcessId = 4 },
            "self" => target with { ProcessId = Environment.ProcessId },
            "birth" => target with { ProcessStartKey = 0 },
            _ => target with { ExecutablePath = "target.exe" }
        };
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        using var owner = await injector.OpenForApiObservationAsync(target, GpuGraphicsApi.OpenGL | GpuGraphicsApi.Vulkan,
            GpuRemoteCallTestOwner.RejectUnexpected, default);
        Assert.False(owner.Result.Success);
        Assert.Equal(expected, owner.Result.Status);
        Assert.Empty(owner.ApiObservations);
        Assert.Null(owner.Result.PolicyPath);
    }

    [Fact]
    public async Task StartReadStopUseOnlyOriginalObservationKindsAndExplicitArguments()
    {
        var calls = new List<GpuRemoteCallRequest>();
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                calls.Add(call.Request);
                Assert.Equal(16, call.Request.ParameterByteLength);
                var kind = call.Request.Kind;
                Assert.Equal(kind != GpuRemoteCallKind.StartApiObservation, call.Request.ReadResponse);
                return Task.FromResult(Completed(kind == GpuRemoteCallKind.StartApiObservation ? null : Response(
                    GpuGraphicsApi.D3D11, kind != GpuRemoteCallKind.StopApiObservation)));
            }
        });
        var start = await owner.StartApiObservationAsync(Assert.Single(owner.ApiObservations), 5000, default);
        Assert.True(start.Completed);
        Assert.Equal(1U, start.ExitCode);
        var read = await owner.ReadApiObservationAsync(Assert.Single(owner.ApiObservations), default);
        Assert.Equal(new GpuApiObservationSnapshot(GpuGraphicsApi.D3D11, true), read.Snapshot);
        var stop = await owner.StopApiObservationAsync(Assert.Single(owner.ApiObservations), default);
        Assert.Equal(new GpuApiObservationSnapshot(GpuGraphicsApi.D3D11, false), stop.Snapshot);
        Assert.Equal(new[] { GpuRemoteCallKind.StartApiObservation, GpuRemoteCallKind.ReadApiObservation, GpuRemoteCallKind.StopApiObservation },
            calls.Select(call => call.Kind));
        Assert.Equal(new ulong[] { 101, 102, 103 }, calls.Select(call => call.FunctionAddress));
        Assert.False(owner.CallsStopped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidDurationDoesNotSubmitAnyCall(int duration)
    {
        using var owner = CreateOwner(GpuRemoteCallTestOwner.RejectUnexpected);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => owner.StartApiObservationAsync(Assert.Single(owner.ApiObservations), duration, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyActualSetIsAValueNotAnInferredApi(bool recording)
    {
        using var owner = CreateOwner((call, _) =>
        {
            call.Dispose();
            return Task.FromResult(Completed(Response(0, recording)));
        });
        var result = await owner.ReadApiObservationAsync(Assert.Single(owner.ApiObservations), default);
        Assert.Equal(new GpuApiObservationSnapshot(0, recording), result.Snapshot);
    }

    [Theory]
    [InlineData("exit", false)]
    [InlineData("missing", false)]
    [InlineData("length", false)]
    [InlineData("outside", false)]
    [InlineData("recording", false)]
    [InlineData("still-recording", true)]
    public async Task RejectedOrMalformedResponsesNeverBecomeAnObservation(string boundary, bool stop)
    {
        using var owner = CreateOwner((call, _) =>
        {
            call.Dispose();
            var response = Response(boundary == "outside" ? GpuGraphicsApi.Vulkan : GpuGraphicsApi.D3D11, true);
            if (boundary == "recording") BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), 2);
            if (boundary == "length") response = response[..15];
            return Task.FromResult(Completed(boundary == "missing" ? null : response) with { ExitCode = boundary == "exit" ? 0U : 1U });
        });
        var result = stop ? await owner.StopApiObservationAsync(Assert.Single(owner.ApiObservations), default) : await owner.ReadApiObservationAsync(Assert.Single(owner.ApiObservations), default);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyReleasedStoppedCallsPermitExplicitStopAndStopDoesNotReenableActions(bool released)
    {
        var calls = new List<GpuRemoteCallKind>();
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                calls.Add(call.Request.Kind);
                return Task.FromResult(calls.Count == 1
                    ? new GpuRemoteCallSnapshot(0, null, null, null, null, released, "fixture-stopped", null)
                    : Completed(Response(GpuGraphicsApi.D3D11, false)));
            }
        });
        Assert.Null((await owner.ReadApiObservationAsync(Assert.Single(owner.ApiObservations), default)).Snapshot);
        Assert.True(owner.CallsStopped);
        Assert.Null((await owner.ReadApiObservationAsync(Assert.Single(owner.ApiObservations), default)).Snapshot);
        Assert.False((await owner.StartApiObservationAsync(Assert.Single(owner.ApiObservations), 5000, default)).Completed);
        var stopped = await owner.StopApiObservationAsync(Assert.Single(owner.ApiObservations), default);
        Assert.Equal(released, stopped.Snapshot.HasValue);
        Assert.Equal(released ? 2 : 1, calls.Count);
        Assert.True(owner.CallsStopped);
        Assert.False((await owner.StartApiObservationAsync(Assert.Single(owner.ApiObservations), 5000, default)).Completed);
        Assert.Equal(released ? 2 : 1, calls.Count);
    }

    [Fact]
    public async Task CancellationAndDisposeNeverSubmitImplicitCalls()
    {
        using var owner = CreateOwner(GpuRemoteCallTestOwner.RejectUnexpected);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False((await owner.StartApiObservationAsync(Assert.Single(owner.ApiObservations), 5000, cancellation.Token)).Completed);
        Assert.Null((await owner.ReadApiObservationAsync(Assert.Single(owner.ApiObservations), cancellation.Token)).Snapshot);
        Assert.Null((await owner.StopApiObservationAsync(Assert.Single(owner.ApiObservations), cancellation.Token)).Snapshot);
        owner.Dispose();
        Assert.False((await owner.StartApiObservationAsync(Assert.Single(owner.ApiObservations), 5000, default)).Completed);
        Assert.Null((await owner.ReadApiObservationAsync(Assert.Single(owner.ApiObservations), default)).Snapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData((int)GpuGraphicsApi.D3D11)]
    public async Task OnceWaitsExactlyOnceThenReturnsTheActualStoppedValue(int actual)
    {
        var order = new List<string>();
        using var work = new CancellationTokenSource();
        using var cleanup = new CancellationTokenSource();
        using var owner = CreateOwner((call, token) =>
        {
            using (call)
            {
                order.Add(call.Request.Kind.ToString());
                Assert.Equal(call.Request.Kind == GpuRemoteCallKind.StartApiObservation ? work.Token : cleanup.Token, token);
                return Task.FromResult(Completed(call.Request.ReadResponse ? Response((GpuGraphicsApi)actual, false) : null));
            }
        });
        var result = await owner.ObserveApiOnceAsync(37, (duration, token) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(37), duration);
            Assert.Equal(work.Token, token);
            order.Add("wait");
            return Task.CompletedTask;
        }, work.Token, cleanup.Token);
        Assert.Equal(new GpuApiObservationSnapshot((GpuGraphicsApi)actual, false), result.Snapshot);
        Assert.Equal(new[] { "StartApiObservation", "wait", "StopApiObservation" }, order);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("pending")]
    [InlineData("unstarted")]
    [InlineData("throw")]
    public async Task OnceNeverWaitsOrStopsWithoutItsOwnConfirmedStart(string boundary)
    {
        var count = 0;
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                count++;
                Assert.Equal(GpuRemoteCallKind.StartApiObservation, call.Request.Kind);
                if (boundary == "throw") throw new IOException("start");
                return Task.FromResult(boundary == "rejected" ? Completed(null) with { ExitCode = 0 }
                    : new GpuRemoteCallSnapshot(0, null, null, null, null, boundary == "unstarted", boundary, null));
            }
        });
        Task<GpuApiObservationReadResult> Run() => owner.ObserveApiOnceAsync(37,
            (_, _) => throw new InvalidOperationException("Unexpected wait"), default, default);
        if (boundary == "throw") Assert.Equal("start", (await Assert.ThrowsAsync<IOException>(Run)).Message);
        else Assert.Null((await Run()).Snapshot);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after-start")]
    [InlineData("during-wait")]
    public async Task OnceCancellationOnlyCleansUpAConfirmedStart(string when)
    {
        using var work = new CancellationTokenSource();
        using var cleanup = new CancellationTokenSource();
        var calls = new List<GpuRemoteCallKind>();
        var waits = 0;
        using var owner = CreateOwner((call, token) =>
        {
            using (call)
            {
                calls.Add(call.Request.Kind);
                if (call.Request.Kind == GpuRemoteCallKind.StartApiObservation && when == "after-start") work.Cancel();
                if (call.Request.Kind == GpuRemoteCallKind.StopApiObservation) Assert.Equal(cleanup.Token, token);
                return Task.FromResult(Completed(call.Request.ReadResponse ? Response(0, false) : null));
            }
        });
        if (when == "before") work.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.ObserveApiOnceAsync(37, (_, _) =>
        {
            waits++;
            work.Cancel();
            return Task.CompletedTask;
        }, work.Token, cleanup.Token));
        Assert.Equal(work.Token, error.CancellationToken);
        Assert.Equal(when == "during-wait" ? 1 : 0, waits);
        Assert.Equal(when == "before" ? [] : new[] { GpuRemoteCallKind.StartApiObservation, GpuRemoteCallKind.StopApiObservation }, calls);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("rejected")]
    [InlineData("pending")]
    [InlineData("throw")]
    [InlineData("cleanup-cancelled")]
    public async Task OncePreservesWaitFailureAndAnyCleanupFailureWithoutRetry(string cleanupState)
    {
        var waitFailure = new IOException("wait failed");
        var stopFailure = new IOException("stop failed");
        using var cleanup = new CancellationTokenSource();
        var calls = new List<GpuRemoteCallKind>();
        using var owner = CreateOwner((call, _) =>
        {
            using (call)
            {
                calls.Add(call.Request.Kind);
                if (call.Request.Kind == GpuRemoteCallKind.StartApiObservation) return Task.FromResult(Completed(null));
                if (cleanupState == "throw") throw stopFailure;
                if (cleanupState == "pending") return Task.FromResult(new GpuRemoteCallSnapshot(0, null, null, null, null, false, "pending", 5));
                return Task.FromResult(Completed(Response(0, false)) with { ExitCode = cleanupState == "rejected" ? 0U : 1U });
            }
        });
        Task<GpuApiObservationReadResult> Run() => owner.ObserveApiOnceAsync(37, (_, _) =>
        {
            if (cleanupState == "cleanup-cancelled") cleanup.Cancel();
            throw waitFailure;
        }, default, cleanup.Token);
        if (cleanupState == "stopped") Assert.Same(waitFailure, await Assert.ThrowsAsync<IOException>(Run));
        else
        {
            var error = await Assert.ThrowsAsync<AggregateException>(Run);
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.Same(waitFailure, error.InnerExceptions[0]);
            if (cleanupState == "throw") Assert.Same(stopFailure, error.InnerExceptions[1]);
        }
        Assert.Equal(cleanupState == "cleanup-cancelled" ? 1 : 2, calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OnceRejectsInvalidDurationBeforeAnyRemoteAction(int duration)
    {
        using var owner = CreateOwner(GpuRemoteCallTestOwner.RejectUnexpected);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => owner.ObserveApiOnceAsync(duration,
            (_, _) => throw new InvalidOperationException("Unexpected wait"), default, default));
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => injector.ObserveApiOnceAsync(
            new(42, 1, "target", @"C:\fixture\target.exe"), GpuGraphicsApi.D3D11, duration,
            GpuRemoteCallTestOwner.RejectUnexpected, (_, _) => Task.CompletedTask, default, default));
    }

    [Fact]
    public async Task OncePreservesTheNativePreparationFailure()
    {
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        var target = new GpuPlacementProcessInstance(int.MaxValue, 1, "absent", @"C:\fixture\absent.exe");
        using var prepared = await injector.OpenForApiObservationAsync(target, GpuGraphicsApi.D3D11,
            GpuRemoteCallTestOwner.RejectUnexpected, default);
        Assert.False(prepared.Result.Success);
        Assert.Equal("open-process-failed", prepared.Result.Status);
        Assert.NotNull(prepared.Result.Win32Error);
        var result = await injector.ObserveApiOnceAsync(target, GpuGraphicsApi.D3D11, 37,
            GpuRemoteCallTestOwner.RejectUnexpected, (_, _) => throw new InvalidOperationException("Unexpected wait"), default, default);
        Assert.Null(result.Snapshot);
        Assert.Equal(prepared.Result.Status, result.Status);
        Assert.Equal(prepared.Result.Win32Error, result.NativeError);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("unrelated.dll", 0)]
    [InlineData("D3D9.DLL", 16)]
    [InlineData("d3d11.dll", 1)]
    [InlineData("d3d12.dll", 2)]
    [InlineData("vulkan-1.dll", 4)]
    [InlineData("opengl32.dll", 8)]
    [InlineData("d3d9.dll;d3d11.dll;d3d12.dll;vulkan-1.dll;opengl32.dll;D3D9.DLL", 31)]
    [InlineData("ResourceManager.GpuPlacementShim.dll;d3d11.dll;d3d12.dll", 3)]
    [InlineData("vulkan-1.dll;opengl32.dll", 12)]
    public void CandidateModulesAreOnlyAnUnfilteredSetNotActualApiIdentification(string modules, int expected)
    {
        Assert.Equal((GpuGraphicsApi)expected, WindowsGpuGraphicsApiDetector.CollectCandidates(modules.Split(';')));
    }

    [Theory]
    [InlineData("pid", "invalid-target")]
    [InlineData("birth", "process-identity-invalid")]
    [InlineData("relative", "process-identity-invalid")]
    [InlineData("absent", "open-process-failed")]
    public async Task ProductionCandidatesUseOriginalAdmissionBeforeAnyObservation(string boundary, string expected)
    {
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        var target = new GpuPlacementProcessInstance(int.MaxValue, 1, "absent", @"C:\fixture\absent.exe");
        target = boundary switch
        {
            "pid" => target with { ProcessId = 4 },
            "birth" => target with { ProcessStartKey = 0 },
            "relative" => target with { ExecutablePath = "absent.exe" },
            _ => target
        };
        var result = await injector.ObserveApiOnceAsync(target, 37, GpuRemoteCallTestOwner.RejectUnexpected,
            (_, _) => throw new InvalidOperationException("Unexpected wait"), default, default);
        Assert.Null(result.Snapshot);
        Assert.Equal(expected, result.Status);
        if (boundary == "absent") Assert.Equal(87, result.NativeError);
    }

    private static WindowsGpuPlacementInjector.ProviderProcess CreateOwner(
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute)
    {
        // Real handle ownership, but callbacks do not call Start or allocate/inject into this test host.
        var handle = OpenProcess(0x00100000, false, Environment.ProcessId);
        Assert.False(handle.IsInvalid);
        return new(new(42, 1, "target", @"C:\fixture\target.exe"),
            new(42, true, true, "fixture", "fixture", null, null, 0, null), execute, handle)
        {
            ApiObservations = [new(new IntPtr(101), new IntPtr(102), new IntPtr(103), GpuGraphicsApi.D3D11)]
        };
    }

    private static GpuRemoteCallSnapshot Completed(byte[]? response)
        => new(0, null, null, 1, response, true, "completed", null);

    private static byte[] Response(GpuGraphicsApi apis, bool recording)
    {
        var buffer = GpuApiObservationProtocol.CreateSnapshotBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)apis);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), recording ? 1U : 0U);
        return buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern WindowsGpuPlacementInjector.SafeKernelHandle OpenProcess(uint access, bool inherit, int pid);
}
