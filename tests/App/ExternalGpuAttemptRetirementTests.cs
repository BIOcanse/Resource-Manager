using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class ExternalGpuAttemptRetirementTests
{
    private static readonly GpuPlacementProcessInstance Controller = new(int.MaxValue, 1, "controller", @"C:\controller.exe");
    private static readonly GpuPlacementProcessInstance Root = new(int.MaxValue - 1, 2, "root", @"C:\root.exe");

    [Fact]
    public async Task ControlOwnershipReadReusesExitedControllerAndDetectsReplacement()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        using var store = new JsonHostManagerRollbackStateStore(Path.Combine(files.Root, "state.json"), TimeProvider.System, TimeSpan.FromMinutes(1));
        await store.LoadAsync(default);
        var runtime = Runtime(files, store);
        var directory = Attempt(files, RunningGpuPlacementActionStatuses.NotApplied);
        var reads = 0;
        bool Dead(GpuPlacementProcessInstance identity) { reads++; return false; }
        for (var i = 0; i < 20; i++) Assert.False(runtime.HasUnreleasedControlCore(Dead));
        Assert.Equal(1, reads);
        // Retirement consumes the same lifetime fact, and still checks the live root.
        runtime.RetireFinishedAttempts(identity => identity == Root ? true : throw new Xunit.Sdk.XunitException("Exited controller was queried again"));
        File.WriteAllText(Path.Combine(directory, "controller.json"), JsonSerializer.Serialize(Controller with { ProcessStartKey = 99 }));
        Assert.True(runtime.HasUnreleasedControlCore(identity => { reads++; return true; }));
        Assert.Equal(2, reads);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var replacement = new GpuPlacementProcessInstance(current.Id, (ulong)current.StartTime.ToFileTimeUtc(), current.ProcessName, Environment.ProcessPath!);
        File.WriteAllText(Path.Combine(directory, "controller.json"), JsonSerializer.Serialize(replacement));
        Assert.True(runtime.HasUnreleasedControl);
    }

    [Fact]
    public async Task ExitedControllerIsNotProbedEveryAdmissionAndNewBirthIsProbed()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        using var store = new JsonHostManagerRollbackStateStore(Path.Combine(files.Root, "state.json"), TimeProvider.System, TimeSpan.FromMinutes(1));
        await store.LoadAsync(default);
        var runtime = Runtime(files, store);
        var directory = Attempt(files, RunningGpuPlacementActionStatuses.NotApplied);
        var controllerReads = 0;
        bool Probe(GpuPlacementProcessInstance identity)
        {
            if (identity == Root) return true;
            controllerReads++;
            return identity.ProcessStartKey != Controller.ProcessStartKey;
        }
        for (var i = 0; i < 20; i++) runtime.RetireFinishedAttempts(Probe);
        Assert.Equal(1, controllerReads); Assert.True(Directory.Exists(directory));
        File.WriteAllText(Path.Combine(directory, "controller.json"), JsonSerializer.Serialize(Controller with { ProcessStartKey = 9 }));
        runtime.RetireFinishedAttempts(Probe);
        Assert.Equal(2, controllerReads); Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void NativeLifetimeProbeDoesNotThrowForMissingPidAndChecksNewBirth()
    {
        for (var i = 0; i < 20; i++) Assert.False(WindowsExternalGpuPlacementRuntime.IsAlive(Controller));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var identity = new GpuPlacementProcessInstance(process.Id, (ulong)process.StartTime.ToFileTimeUtc(),
            process.ProcessName, Environment.ProcessPath!);
        Assert.False(WindowsExternalGpuPlacementRuntime.IsAlive(identity with { ProcessStartKey = identity.ProcessStartKey - 1 }));
        Assert.True(WindowsExternalGpuPlacementRuntime.IsAlive(identity));
    }

    [Fact]
    public async Task KeepsLiveAndUnreadableControllersAndLiveRootFailureSuppression()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        using var store = new JsonHostManagerRollbackStateStore(Path.Combine(files.Root, "state.json"), TimeProvider.System, TimeSpan.FromMinutes(1));
        await store.LoadAsync(default);
        var runtime = Runtime(files, store);
        var directory = Attempt(files, RunningGpuPlacementActionStatuses.NotApplied);
        runtime.RetireFinishedAttempts(identity => identity == Controller);
        Assert.True(Directory.Exists(directory));
        runtime.RetireFinishedAttempts(_ => throw new Win32Exception(5));
        Assert.True(Directory.Exists(directory));
        runtime.RetireFinishedAttempts(identity => identity == Root);
        Assert.True(Directory.Exists(directory));
        runtime.RetireFinishedAttempts(identity => identity == Root ? throw new Win32Exception(5) : false);
        Assert.True(Directory.Exists(directory));
        Assert.True(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(Attempts(files), Root));
        // Once the exact root exits, it no longer needs the failure suppression evidence.
        runtime.RetireFinishedAttempts(_ => false);
        Assert.False(Directory.Exists(directory));
        Assert.False(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(Attempts(files), Root));
        Assert.False(runtime.HasUnreleasedControl);
        var reads = 0;
        runtime.RetireFinishedAttempts(_ => { reads++; return false; });
        Assert.Equal(0, reads);
        var successful = Attempt(files, RunningGpuPlacementActionStatuses.RecreateRequested);
        File.WriteAllText(Path.Combine(successful, "request.json"), "invalid");
        runtime.RetireFinishedAttempts(identity => identity == Root);
        Assert.False(Directory.Exists(successful)); // A successful attempt does not need root failure suppression.
    }

    [Fact]
    public async Task ReceiptReferenceSurvivesFailedSettlementAndRestartUntilDurablyRemoved()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var path = Path.Combine(files.Root, "state.json");
        var directory = Attempt(files, RunningGpuPlacementActionStatuses.RecreateRequested);
        var fail = false;
        using (var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1),
                   () => { if (fail) throw new IOException("fake commit failure"); }))
        {
            var runtime = Runtime(files, store);
            Assert.Null(store.ReadExternalGpuAttemptReferences());
            runtime.Prepare([]);
            Assert.True(Directory.Exists(directory));
            await store.SaveAsync(State(directory), default);
            Assert.Contains(directory, store.ReadExternalGpuAttemptReferences()!);
            runtime.Prepare([]);
            Assert.True(Directory.Exists(directory));
            fail = true;
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(HostManagerRollbackStateDocument.Empty, default));
            runtime.Prepare([]);
            Assert.True(Directory.Exists(directory));
            Assert.Contains(directory, store.ReadExternalGpuAttemptReferences()!);
        }
        using (var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1)))
        {
            await store.LoadAsync(default);
            Runtime(files, store).Prepare([]);
            Assert.True(Directory.Exists(directory));
            await store.SaveAsync(HostManagerRollbackStateDocument.Empty, default);
            Assert.Empty(store.ReadExternalGpuAttemptReferences()!);
            // Simulate a crash after settlement, before the next admission/retirement.
        }
        using (var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1)))
        {
            await store.LoadAsync(default);
            Runtime(files, store).Prepare([]);
            Assert.False(Directory.Exists(directory));
        }
    }

    [Fact]
    public async Task DiagnosticsAreBoundedAndRetiredDeletionResumesAfterCrash()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        using var store = new JsonHostManagerRollbackStateStore(Path.Combine(files.Root, "state.json"), TimeProvider.System, TimeSpan.FromMinutes(1));
        await store.LoadAsync(default);
        var runtime = Runtime(files, store);
        for (var index = 0; index < 100; index++)
        {
            var directory = Attempt(files, RunningGpuPlacementActionStatuses.RecreateRequested);
            File.WriteAllText(Path.Combine(directory, "stderr.log"), new string('x', 10000));
            runtime.Prepare([]);
            Assert.False(Directory.Exists(directory));
        }
        var summaries = Directory.GetFiles(Path.Combine(Attempts(files), "Diagnostics"));
        Assert.InRange(summaries.Length, 1, WindowsExternalGpuPlacementRuntime.DiagnosticSummaryLimit);
        Assert.All(summaries, path => Assert.InRange(new FileInfo(path).Length, 1, 256));
        Assert.False(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(Attempts(files), Root));
        // A crash after moving out of admission scans can leave a partially deleted flat directory.
        var staged = Path.Combine(Attempts(files), "Retired", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "stderr.log"), "remaining");
        File.WriteAllText(Path.Combine(staged, "controller.json"), "invalid");
        Assert.False(runtime.HasUnreleasedControl);
        Runtime(files, store).Prepare([]);
        Assert.False(Directory.Exists(staged));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("{}")]
    public async Task CorruptControllerAndUnknownReceiptAuthorityDoNotAuthorizeRetirement(string controller)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        using var store = new JsonHostManagerRollbackStateStore(Path.Combine(files.Root, "state.json"), TimeProvider.System, TimeSpan.FromMinutes(1));
        var directory = Attempt(files, RunningGpuPlacementActionStatuses.NotApplied);
        await store.LoadAsync(default);
        File.WriteAllText(Path.Combine(directory, "controller.json"), controller);
        Runtime(files, store).Prepare([]);
        Assert.True(Directory.Exists(directory));
        Assert.True(Runtime(files, store).HasUnreleasedControl);
        var state = State(directory);
        var receipt = state.AppliedPlacements[0];
        await store.SaveAsync(state with { AppliedPlacements = [receipt with
        {
            Records = [receipt.Records[0] with { Metadata = new Dictionary<string, string> { ["runtimeActionResult"] = "invalid" } }]
        }] }, default);
        Assert.Null(store.ReadExternalGpuAttemptReferences());
    }

    private static WindowsExternalGpuPlacementRuntime Runtime(GpuGraphicsApiIdentificationTests.Fixture files,
        JsonHostManagerRollbackStateStore store)
        => new(files, null!, NullLogger<WindowsExternalGpuPlacementRuntime>.Instance, store);

    private static string Attempts(GpuGraphicsApiIdentificationTests.Fixture files)
        => Path.Combine(files.Root, "UserData", "GpuPlacement", "External");

    private static string Attempt(GpuGraphicsApiIdentificationTests.Fixture files, string status)
    {
        var directory = Path.Combine(Attempts(files), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "controller.json"), JsonSerializer.Serialize(Controller));
        File.WriteAllText(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new { expected = new { Root } }));
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new RunningGpuPlacementActionResult([], "fake", status)));
        return directory;
    }

    private static HostManagerRollbackStateDocument State(string directory)
    {
        var result = new RunningGpuPlacementActionResult([new("attempt", "external",
            new Dictionary<string, string> { ["evidenceDirectory"] = directory })], "fake", RunningGpuPlacementActionStatuses.RecreateRequested);
        var record = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger, "legacy-or-current",
            new Dictionary<string, string> { ["runtimeActionResult"] = JsonSerializer.Serialize(result) });
        return HostManagerRollbackStateDocument.Empty with
        {
            AppliedPlacements = [new("target", "Target", "software", "Gpu", [record], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]
        };
    }
}
