using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace Resource_Manager_APP.Tests;

public sealed class ExternalGpuPlacementTests
{
    [Theory]
    [InlineData("HYP.exe", "HYPHelper.exe")]
    [InlineData("chrome.exe", "chrome.exe")]
    public void ChromiumControllerPreservesSeparateHostAndGpuImages(string hostName, string gpuName)
    {
        var host = new GpuPlacementProcessInstance(123, 134341663324753288, "host", @"C:\fixture\" + hostName);
        var gpu = new GpuPlacementProcessInstance(124, 134341663339065847, "gpu", @"C:\fixture\" + gpuName);
        Assert.Equal(new[] { "123", "134341663324753288", "124", "134341663339065847", "stop", "ready", "69842", "123456",
            host.ExecutablePath, gpu.ExecutablePath },
            WindowsExternalGpuPlacementRuntime.ChromiumControllerArguments(new(host, gpu), "stop", "ready", 69842, 123456));
    }

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(1, 3, false)]
    [InlineData(1, 4, false)]
    [InlineData(1, 5, true)]
    [InlineData(1, 6, false)]
    [InlineData(1, -1, false)]
    public void QtD3D12ModulePresenceDoesNotOverrideItsActualBackend(byte applied, int backend, bool expected)
    {
        var state = new byte[8];
        state[0] = applied;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(4), backend);
        Assert.Equal(expected, QtQuickGpuProcessIdentity.HasD3D12Backend(state));
        Assert.False(QtQuickGpuProcessIdentity.HasD3D12Backend(state.AsSpan(0, 7)));
    }

    private sealed class QtAdmissionFactAttribute : FactAttribute
    {
        public QtAdmissionFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RM_QT_ADMISSION_ROOT")))
                Skip = "Requires an already-running owned Qt renderer; never starts graphics or a GPU action.";
        }
    }

    [Theory]
    [InlineData(0, 1, null)]
    [InlineData(1, 1, ExternalGpuRenderer.QtQuickVulkan)]
    [InlineData(1, 5, ExternalGpuRenderer.QtQuickD3D12)]
    [InlineData(1, 2, null)]
    [InlineData(1, 3, null)]
    [InlineData(1, 4, null)]
    [InlineData(1, 0, null)]
    public void QtBackendRoutesUseQrhiValuesNotModulePresence(byte applied, int backend, ExternalGpuRenderer? expected)
    {
        var state = new byte[8];
        state[0] = applied;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(4), backend);
        Assert.Equal(expected, QtQuickGpuProcessIdentity.ReadBackend(state));
        Assert.Null(QtQuickGpuProcessIdentity.ReadBackend(state.AsSpan(0, 7)));
    }

    [QtAdmissionFact]
    public void ActualOwnedQtBackendSelectsOnlyTheQualifiedRoute()
    {
        var root = Environment.GetEnvironmentVariable("RM_QT_ADMISSION_ROOT")!;
        using var request = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "admission.request.json")));
        var value = request.RootElement;
        var identity = new GpuPlacementProcessInstance(value.GetProperty("pid").GetInt32(),
            ulong.Parse(value.GetProperty("birth").GetString()!), "owned-qt", value.GetProperty("image").GetString()!);
        using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
        Assert.True(ChromiumGpuProcessIdentity.Matches(process, identity));
        Assert.Contains(process.Modules.Cast<System.Diagnostics.ProcessModule>(),
            module => module.ModuleName.Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase));
        var plan = QtQuickGpuProcessIdentity.Read(identity);
        var expected = value.GetProperty("expectedD3D12").GetBoolean();
        Assert.NotNull(plan);
        Assert.Equal(expected ? ExternalGpuRenderer.QtQuickD3D12 : ExternalGpuRenderer.QtQuickVulkan, plan.Renderer);
        File.WriteAllText(Path.Combine(root, "admission.result.json"), System.Text.Json.JsonSerializer.Serialize(
            new { identity, expectedD3D12 = expected, actualD3D12 = plan.Renderer == ExternalGpuRenderer.QtQuickD3D12, renderer = plan.Renderer.ToString(), gpuActionStarted = false }));
    }

    [Theory]
    [InlineData("qt6gui.dll", true)]
    [InlineData("QT6QUICK.DLL", true)]
    [InlineData("other.dll", false)]
    public void QtModuleExtractionIsCaseInsensitive(string name, bool expected)
        => Assert.Equal(expected, QtQuickGpuProcessIdentity.IsQtModule(name));

    [Theory]
    [InlineData(6, 8, 3, true, true)]
    [InlineData(6, 8, 3, false, false)]
    [InlineData(6, 8, 2, true, false)]
    [InlineData(6, 11, 2, true, false)]
    public void QtModuleQualificationIsRendererBasedAndVersionBounded(int major, int minor, int patch, bool d3d12, bool expected)
    {
        var modules = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
        {
            ["Qt6Gui.dll"] = new(major, minor, patch),
            ["Qt6Quick.dll"] = new(major, minor, patch)
        };
        if (d3d12) modules["D3D12Core.dll"] = new(10, 0, 1);
        Assert.Equal(expected, QtQuickGpuProcessIdentity.HasQualifiedModules(modules));
        modules.Remove("Qt6Quick.dll");
        Assert.False(QtQuickGpuProcessIdentity.HasQualifiedModules(modules));
    }

    [Fact]
    public void QtPresentationAndControlCleanupRemainSeparateFacts()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("renderer.exe", ExternalGpuRenderer.QtQuickD3D12);
        events.Accept("{\"kind\":\"qtD3D12Submission\",\"tid\":12}");
        events.Accept("{\"kind\":\"d3d12Observation\",\"luid\":23,\"targetPresents\":30,\"targetPresentationVerified\":true,\"luidObservedInsideSwapChainCreation\":true,\"swapChainLifetimeEnded\":false}");
        Assert.True(events.QtSubmissionObserved);
        Assert.True(events.Presentation!.Verified);
        Assert.Equal(23UL, events.Presentation.AdapterKey);
        Assert.Null(events.Summary);
        Assert.Null(events.Replacement);
        events.Accept("{\"kind\":\"summary\",\"passed\":true,\"cleanupPassed\":true,\"debuggerAbsent\":true}");
        Assert.True(events.Summary!.RootRestored);
        Assert.False(events.RecoveryRequired);
    }

    [Fact]
    public void QtDestroyedSwapChainIsNotCurrentPresentation()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("renderer.exe", ExternalGpuRenderer.QtQuickD3D12);
        events.Accept("{\"kind\":\"d3d12Observation\",\"luid\":23,\"targetPresents\":30,\"targetPresentationVerified\":true,\"luidObservedInsideSwapChainCreation\":true,\"swapChainLifetimeEnded\":true}");
        Assert.False(events.Presentation!.Verified);
    }

    [Theory]
    [InlineData(12UL, 23UL, 101UL, 102UL, true)]
    [InlineData(0UL, 23UL, 101UL, 102UL, false)]
    [InlineData(23UL, 23UL, 101UL, 102UL, false)]
    [InlineData(12UL, 23UL, 0UL, 102UL, false)]
    [InlineData(12UL, 23UL, 101UL, 0UL, false)]
    public void VulkanPresentationRequiresDistinctAdapterAndRendererOwner(ulong source, ulong target, ulong window, ulong instance, bool verified)
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("renderer.exe", ExternalGpuRenderer.QtQuickVulkan);
        events.Accept("{\"kind\":\"qtVulkanSubmission\",\"tid\":12}");
        var observation = System.Text.Json.JsonSerializer.Serialize(new { kind = "vulkanObservation", sourceLuid = source,
            luid = target, window, instance, targetPresents = 30, targetPresentationVerified = true });
        events.Accept(observation);
        Assert.True(events.QtSubmissionObserved);
        Assert.Equal(verified, events.Presentation!.Verified);
        Assert.Null(events.Summary);
        events.Accept("{\"kind\":\"summary\",\"passed\":true,\"cleanupPassed\":true,\"debuggerAbsent\":true}");
        Assert.True(events.Summary!.RootRestored);
        events.Accept(observation);
        Assert.True(events.RecoveryRequired);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(RunningGpuPlacementActionStatuses.NotApplied, true)]
    [InlineData(RunningGpuPlacementActionStatuses.Unresolved, true)]
    [InlineData(RunningGpuPlacementActionStatuses.RecreateRequested, false)]
    public void FailedAttemptBlocksTheBrowserInstanceAcrossGpuChildReplacement(string? status, bool blocked)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var browser = new GpuPlacementProcessInstance(123, 456, "chrome", @"C:\chrome.exe");
        var request = System.Text.Json.JsonSerializer.Serialize(new { expected = new ExternalGpuPlacementPlan(browser,
            new(124, 457, "chrome", browser.ExecutablePath)) });
        var directory = Path.Combine(files.ContentRootPath, "attempt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "request.json"), request);
        if (status is not null) File.WriteAllText(Path.Combine(directory, "result.json"),
            System.Text.Json.JsonSerializer.Serialize(new RunningGpuPlacementActionResult([], "fixture", status)));
        Assert.Equal(blocked, WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, browser));
        Assert.False(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, browser with { ProcessStartKey = 999 }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EvidenceFailureNeverStopsDrainingControllerPipe(bool creationFailure)
    {
        var lines = Enumerable.Range(0, 10000).Select(index => index.ToString()).ToArray();
        using var reader = new StringReader(string.Join('\n', lines));
        var received = new List<string>();
        var failures = new List<Exception>();
        await WindowsExternalGpuPlacementRuntime.DrainAsync(reader,
            () => creationFailure ? throw new IOException("create failed") : new FailingWriter(), received.Add, failures.Add);
        Assert.Equal(lines, received);
        Assert.NotEmpty(failures);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void CleanFailedControllerDoesNotPermanentlyBanFollowingScheduling(bool exited, bool cleaned, bool settled)
    {
        var result = new RunningGpuPlacementActionResult([new("attempt", "chromium-external-dxgi",
            new Dictionary<string, string> { ["controllerExited"] = exited.ToString(),
                ["cleanupPassed"] = cleaned.ToString(), ["controllerSucceeded"] = "False" })],
            "failed", RunningGpuPlacementActionStatuses.NotApplied);
        Assert.Equal(settled, WindowsExternalGpuPlacementRuntime.IsSettledFailure(result));
    }

    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => Task.FromException(new IOException("write failed"));
    }

    [Fact]
    public void UmaResidentBytesDoNotRequireDedicatedCapacityPercentage()
    {
        var gpu = new SchedulingProcessGpuFact(0, 69842, SchedulingProcessMetricMask.GpuUsage, 5, 0, 1, 0)
            { PrivateMemoryBytes = 32 * 1024 * 1024, SharedMemoryBytes = 16 * 1024 * 1024 };
        var process = new SchedulingProcessFact(123, 456, "chrome", @"C:\chrome.exe", "chrome", "Chrome", "Other", "Other",
            35, SchedulingProcessMetricMask.GpuUsage, 0, 0, 1, [gpu]);
        var source = new ResidentSource(process);
        var sample = new ExternalGpuResidentObservation(source, source).Read(new(123, 456, "chrome", @"C:\chrome.exe"));
        Assert.Equal(48 * 1024 * 1024, Assert.Single(sample!.Adapters).ResidentBytes);
        Assert.Equal(100, sample.ObservedAtUtcTicks);
        Assert.Empty(new ExternalGpuResidentObservation(source, source).Read(new(123, 457, "chrome", @"C:\chrome.exe"))!.Adapters);
    }

    private sealed class ResidentSource(SchedulingProcessFact process, long usageAt = 100) : IMetricSnapshotObservationSource, ISchedulingProcessFactObservationSource
    {
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => new(DateTimeOffset.UtcNow, null!, null!, null!, [],
            new(SamplingObservationStatus.Current, 1, DateTime.UtcNow.Ticks, 0, 0, 0, 1, []), new Dictionary<string, MetricValue>());
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) => new(SamplingObservationStatus.Current,
            1, DateTime.UtcNow.Ticks, 1, 1, 0, 0, request.RequestedMetricMask, process.ValidMetricMask, [process],
            GpuObservedAtUtcTicks: 999)
        {
            DatasetObservations = new Dictionary<SchedulingProcessMetricMask, SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.GpuDedicatedMemory] = SchedulingProcessDatasetObservation.CreateCurrent(
                    SchedulingProcessMetricMask.GpuDedicatedMemory, 1, 100, 1, 100, topologyGeneration: 1, topologyFingerprint: 1),
                [SchedulingProcessMetricMask.GpuUsage] = SchedulingProcessDatasetObservation.CreateCurrent(
                    SchedulingProcessMetricMask.GpuUsage, 1, usageAt, 1, 100, topologyGeneration: 1, topologyFingerprint: 1)
            }
        };
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval) => throw new NotSupportedException();
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(0, false)]
    [InlineData(128, true)]
    public void TargetResidencyMustChangeNotMerelyExist(double current, bool confirmed)
    {
        var before = new ExternalResidentSample(100, [new(23, 64, 0, null)], 100);
        var after = new ExternalResidentSample(200, [new(23, current, 0, null)], 200);
        Assert.Equal(confirmed, ExternalGpuResidentObservation.ConfirmsTarget(before, after, 23));
        Assert.False(ExternalGpuResidentObservation.ConfirmsTarget(before, null, 23));
        Assert.False(ExternalGpuResidentObservation.ConfirmsTarget(before with { Adapters = [] }, after, 23));
        Assert.Equal(current > 0, ExternalGpuResidentObservation.ConfirmsTarget(
            before with { Adapters = [new(99, 64, 0, null)] }, after, 23));
    }

    [Fact]
    public void SourceResidencyOrEngineActivityKeepsMultiDeviceMoveUnconfirmed()
    {
        var before = new ExternalResidentSample(100,
            [new(23, 64, 0, 12), new(99, 32, 0, 8)], 100);
        Assert.False(ExternalGpuResidentObservation.ConfirmsTarget(before,
            new(200, [new(23, 128, 0, 14), new(99, 28, 0, 7)], 200), 23));
        Assert.False(ExternalGpuResidentObservation.ConfirmsTarget(before,
            new(200, [new(23, 128, 0, 14), new(99, 0, 0, 7)], 200), 23));
        Assert.True(ExternalGpuResidentObservation.ConfirmsTarget(before,
            new(200, [new(23, 128, 0, 14)], 200), 23));
        Assert.True(ExternalGpuResidentObservation.ConfirmsTarget(before,
            new(200, [new(23, 128, 0, 14), new(99, 0, 0, 0)], 200), 23));
    }

    [Fact]
    public void TargetTransferDistinguishesPartialDisplayResidencyFromFullRelease()
    {
        var before = new ExternalResidentSample(100,
            [new(23, 6, 0, 0), new(99, 80, 6, 0)], 100);
        Assert.Equal(ExternalResidentTransfer.Partial, ExternalGpuResidentObservation.ClassifyTransfer(before,
            new(200, [new(23, 60, 0, 1), new(99, 10, 6, 0)], 200), 23));
        Assert.Equal(ExternalResidentTransfer.Full, ExternalGpuResidentObservation.ClassifyTransfer(before,
            new(200, [new(23, 60, 0, 1)], 200), 23));
        Assert.Equal(ExternalResidentTransfer.Unconfirmed, ExternalGpuResidentObservation.ClassifyTransfer(before,
            new(200, [new(23, 60, 0, 1), new(99, 80, 6, 0)], 200), 23));
        Assert.Equal(ExternalResidentTransfer.Unconfirmed, ExternalGpuResidentObservation.ClassifyTransfer(before,
            new(200, [new(23, 60, 0, 1), new(99, 10, 6, 0), new(77, 2, 0, 0)], 200), 23));
    }

    [Fact]
    public void EngineOnlySourceAdapterRemainsVisibleInResidentObservation()
    {
        var target = new SchedulingProcessGpuFact(0, 23, SchedulingProcessMetricMask.GpuUsage, 14, 0, 1, 0)
            { PrivateMemoryBytes = 128, SharedMemoryBytes = 0 };
        var sourceEngine = new SchedulingProcessGpuFact(1, 99, SchedulingProcessMetricMask.GpuUsage, 7, 0, 1, 0);
        var process = new SchedulingProcessFact(123, 456, "chrome", @"C:\chrome.exe", "chrome", "Chrome", "Other", "Other",
            35, SchedulingProcessMetricMask.GpuUsage, 0, 0, 1, [target, sourceEngine]);
        var source = new ResidentSource(process);
        var sample = new ExternalGpuResidentObservation(source, source).Read(new(123, 456, "chrome", @"C:\chrome.exe"));
        Assert.NotNull(sample);
        Assert.Equal(2, sample.Adapters.Count);
        Assert.Contains(sample.Adapters, row => row.AdapterKey == 99 && row.ResidentBytes == 0 && row.UsagePercent == 7);
    }

    [Fact]
    public void ResidentConfirmationWaitsForBothIndependentGpuDatasets()
    {
        var gpu = new SchedulingProcessGpuFact(0, 23, SchedulingProcessMetricMask.GpuUsage, 7, 0, 1, 0)
            { PrivateMemoryBytes = 128, SharedMemoryBytes = 0 };
        var process = new SchedulingProcessFact(123, 456, "chrome", @"C:\chrome.exe", "chrome", "Chrome", "Other", "Other",
            35, SchedulingProcessMetricMask.GpuUsage, 0, 0, 1, [gpu]);
        var source = new ResidentSource(process, usageAt: 50);
        var sample = new ExternalGpuResidentObservation(source, source).Read(new(123, 456, "chrome", @"C:\chrome.exe"));
        Assert.NotNull(sample);
        Assert.Equal(50, sample.ObservedAtUtcTicks);
        Assert.Equal(50, sample.LastAttemptAtUtcTicks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NextPublishedResidentValueSettlesOnceIncludingEmpty(bool empty)
    {
        var reads = 0;
        var next = new ExternalResidentSample(200, empty ? [] : [new(23, 128, 0, null)], 200);
        var result = await ExternalGpuResidentObservation.WaitForNextAsync(() => ++reads == 1
            ? new(100, [new(23, 64, 0, null)], 100) : next, 150,
            checked((ulong)Environment.TickCount64 + 2000), default);
        Assert.Same(next, result);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task ResidentWaitHasNoSamplingSideEffectAndHonorsCancellationAndDeadline()
    {
        var reads = 0;
        Assert.Null(await ExternalGpuResidentObservation.WaitForNextAsync(() => { reads++; return null; }, 150,
            checked((ulong)Environment.TickCount64), default));
        Assert.Equal(0, reads);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalGpuResidentObservation.WaitForNextAsync(
            () => { reads++; return null; }, 150, checked((ulong)Environment.TickCount64 + 2000), stop.Token));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    public void UnconfirmedSampleBlocksAutomaticRetryForTheSameRoot(bool exited, bool clean, bool nativeSucceeded, bool blocked)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var browser = new GpuPlacementProcessInstance(123, 456, "chrome", @"C:\chrome.exe");
        var directory = Path.Combine(files.ContentRootPath, "attempt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "request.json"), System.Text.Json.JsonSerializer.Serialize(
            new { expected = new ExternalGpuPlacementPlan(browser, browser) }));
        var metadata = new Dictionary<string, string>
        {
            ["residentConfirmation"] = "unconfirmed", ["controllerSucceeded"] = nativeSucceeded.ToString(),
            ["controllerExited"] = exited.ToString(), ["cleanupPassed"] = clean.ToString()
        };
        var result = new RunningGpuPlacementActionResult([new("sample", "fixture", metadata)], "unconfirmed", RunningGpuPlacementActionStatuses.NotApplied);
        File.WriteAllText(Path.Combine(directory, "result.json"), System.Text.Json.JsonSerializer.Serialize(result));
        Assert.False(result.Triggered);
        Assert.Equal(blocked, WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, browser));
        File.WriteAllText(Path.Combine(directory, "request.json"), System.Text.Json.JsonSerializer.Serialize(
            new { expected = new ExternalGpuPlacementPlan(browser, browser),
                controlBuildId = WindowsExternalGpuPlacementRuntime.ControlBuildId }));
        Assert.True(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, browser));
    }

    [Fact]
    public void CorruptUnrelatedRequestDoesNotVetoRootButItsOwnMissingResultStillDoes()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var root = new GpuPlacementProcessInstance(123, 456, "chrome", @"C:\chrome.exe");
        var corrupt = Path.Combine(files.ContentRootPath, "unrelated-corrupt");
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "request.json"), "{invalid");
        Assert.False(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, root));

        var matching = Path.Combine(files.ContentRootPath, "matching");
        Directory.CreateDirectory(matching);
        File.WriteAllText(Path.Combine(matching, "request.json"), System.Text.Json.JsonSerializer.Serialize(
            new { expected = new ExternalGpuPlacementPlan(root, root) }));
        Assert.True(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, root));
        Assert.False(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath,
            root with { ProcessStartKey = root.ProcessStartKey + 1 }));
        File.WriteAllText(Path.Combine(matching, "result.json"), "{invalid");
        Assert.True(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, root));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public void SettledRendererFailureBlocksAnotherGpuChildInTheSameRoot(bool clean, bool replacementKnown, bool siblingBlocked)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var host = new GpuPlacementProcessInstance(123, 456, "chrome", @"C:\chrome.exe");
        var original = host with { ProcessId = 124, ProcessStartKey = 457 };
        var replacement = host with { ProcessId = 125, ProcessStartKey = 458 };
        var sibling = host with { ProcessId = 126, ProcessStartKey = 459 };
        var directory = Path.Combine(files.ContentRootPath, "attempt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "request.json"), System.Text.Json.JsonSerializer.Serialize(
            new { expected = new ExternalGpuPlacementPlan(host, original),
                controlBuildId = WindowsExternalGpuPlacementRuntime.ControlBuildId }));
        var metadata = new Dictionary<string, string>
        {
            ["controllerExited"] = bool.TrueString, ["cleanupPassed"] = clean.ToString(),
            ["replacement"] = System.Text.Json.JsonSerializer.Serialize(replacementKnown ? replacement : null)
        };
        File.WriteAllText(Path.Combine(directory, "result.json"), System.Text.Json.JsonSerializer.Serialize(
            new RunningGpuPlacementActionResult([new("sample", "fixture", metadata)], "failed", RunningGpuPlacementActionStatuses.NotApplied)));
        Assert.True(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, host));
        Assert.Equal(siblingBlocked, WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath, host));
        Assert.False(WindowsExternalGpuPlacementRuntime.HasFailedAttempt(files.ContentRootPath,
            host with { ProcessStartKey = host.ProcessStartKey + 1 }));
    }

    [Fact]
    public void ReplacementRetainsExactNativeCreationWithoutDoubleConversion()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents(@"C:\Chrome\chrome.exe");
        events.Accept("{\"kind\":\"process\",\"pid\":123,\"creationFileTime\":\"134344032295803976\",\"gpu\":true}");
        Assert.Equal(134344032295803976UL, events.Replacement!.ProcessStartKey);
        Assert.Null(events.Summary);
        Assert.False(events.RecoveryRequired);
    }

    [Fact]
    public void ReplacementNameComesFromTheValidatedImage()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents(@"C:\Application\msedge.exe");
        events.Accept("{\"kind\":\"process\",\"pid\":123,\"creationFileTime\":\"134344032295803976\",\"gpu\":true}");
        Assert.Equal("msedge", events.Replacement!.ProcessName);
    }

    [Fact]
    public void ReplacementCreationCannotStopControllerBeforeEnumerationAndTargetResidency()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("chrome.exe");
        var resident = new ExternalResidentSample(10, [new(23, 4096, 0, null)], 10);
        events.Accept("{\"kind\":\"process\",\"pid\":123,\"creationFileTime\":\"134344032295803976\",\"gpu\":true}");
        Assert.False(events.CanReleaseChromium(resident, 23));
        events.Accept("{\"kind\":\"enum\",\"pid\":124,\"slot\":0}");
        Assert.False(events.CanReleaseChromium(resident, 23));
        events.Accept("{\"kind\":\"enum\",\"pid\":123,\"slot\":2}");
        Assert.False(events.CanReleaseChromium(resident, 23));
        events.Accept("{\"kind\":\"enum\",\"pid\":123,\"slot\":0}");
        Assert.False(events.CanReleaseChromium(null, 23));
        Assert.False(events.CanReleaseChromium(resident, 99));
        Assert.True(events.CanReleaseChromium(resident, 23));
        events.Accept("{\"kind\":\"recoveryRequired\",\"error\":\"fixture\"}");
        Assert.False(events.CanReleaseChromium(resident, 23));
    }

    [Theory]
    [InlineData("{\"kind\":\"process\",\"pid\":123,\"creationFileTime\":\"1.2\",\"gpu\":true}")]
    [InlineData("{\"kind\":\"summary\",\"passed\":true}")]
    [InlineData("not json")]
    public void MalformedEvidenceNeverAdmitsSuccess(string line)
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("chrome.exe");
        events.Accept(line);
        Assert.True(events.RecoveryRequired);
        Assert.NotNull(events.Error);
    }

    [Fact]
    public void CleanupFailureIsStickyEvenAfterGreenOuterSummary()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("chrome.exe");
        events.Accept("{\"kind\":\"recoveryRequired\",\"error\":\"restore\"}");
        events.Accept("{\"kind\":\"summary\",\"passed\":true,\"cleanupPassed\":true,\"rootRestored\":true}");
        Assert.True(events.RecoveryRequired);
        Assert.Equal("restore", events.Error);
    }

    [Fact]
    public void MultipleReplacementProcessesAndDuplicateSummariesAreNotCollapsed()
    {
        var events = new WindowsExternalGpuPlacementRuntime.ControllerEvents("chrome.exe");
        events.Accept("{\"kind\":\"process\",\"pid\":123,\"creationFileTime\":\"1\",\"gpu\":true}");
        events.Accept("{\"kind\":\"process\",\"pid\":124,\"creationFileTime\":\"2\",\"gpu\":true}");
        Assert.True(events.RecoveryRequired);
        Assert.Equal(123, events.Replacement!.ProcessId);
        var summaries = new WindowsExternalGpuPlacementRuntime.ControllerEvents("chrome.exe");
        summaries.Accept("{\"kind\":\"summary\",\"passed\":false,\"cleanupPassed\":false,\"rootRestored\":false}");
        summaries.Accept("{\"kind\":\"summary\",\"passed\":true,\"cleanupPassed\":true,\"rootRestored\":true}");
        Assert.True(summaries.RecoveryRequired);
        Assert.False(summaries.Summary!.Passed);
    }

    [Fact]
    public async Task UnsupportedExternalCandidateNeverInvokesLegacyApiObservation()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var sources = new NoSampling();
        var external = new WindowsExternalGpuPlacementRuntime(files, new(sources, sources), NullLogger<WindowsExternalGpuPlacementRuntime>.Instance);
        var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            new(files), null!, null!, new GpuGraphicsApiIdentificationTests.Plans(software), new(), external);
        var result = await service.PrepareWithFirstApiObservationAsync(new("target", "software", "Software",
            [new(int.MaxValue, 1, "other", @"C:\other.exe")], "automatic-placement", 1, GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover),
            (_, _) => throw new InvalidOperationException("Legacy observation must not run."), default);
        Assert.Null(result.Plan);
        Assert.Empty(result.ApiObservationProcesses);
        Assert.False(service.HasUnreleasedExternalControl);
    }

    [Fact]
    public async Task ProductRuntimeServiceDoesNotAcceptAProviderPolicyAsAnExternalAction()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var policy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var sources = new NoSampling();
        var external = new WindowsExternalGpuPlacementRuntime(files, new(sources, sources),
            NullLogger<WindowsExternalGpuPlacementRuntime>.Instance);
        var service = new ExternalOnlyRunningGpuPlacementActionService(
            new GpuGraphicsApiIdentificationTests.Plans(policy), external);
        var request = new RunningGpuPlacementActionRequest("target", "software", "Software",
            [new(123, 456, "renderer", @"C:\renderer.exe")], "automatic-placement", 69842,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover);
        var legacy = new RunningGpuPlacementActionPlan(request, [1, 2, 3],
            new Dictionary<int, GpuGraphicsApi> { [123] = GpuGraphicsApi.D3D11 });
        var execution = new RunningGpuPlacementExecution(1, _ => throw new InvalidOperationException("Legacy window action"),
            (_, _) => throw new InvalidOperationException("Legacy remote call"),
            (_, _, _) => throw new InvalidOperationException("Legacy callback"));

        var result = await service.TryApplyAsync(legacy, execution, default);

        Assert.Equal(RunningGpuPlacementActionStatuses.Skipped, result.Status);
        Assert.Empty(result.Records);
        Assert.False(service.HasUnreleasedExternalControl);
    }

    [Fact]
    public void ResidentObservationDoesNotAcquireSubscriptionOrSampleOnRead()
    {
        var sources = new NoSampling();
        var observation = new ExternalGpuResidentObservation(sources, sources);
        Assert.Null(observation.Read(new(123, 1, "chrome", @"C:\chrome.exe")));
        Assert.Equal(1, sources.Reads);
    }

    private sealed class NoSampling : IMetricSnapshotObservationSource, ISchedulingProcessFactObservationSource
    {
        public int Reads;
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) { Reads++; return null; }
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) => throw new InvalidOperationException("No inventory.");
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval) => throw new InvalidOperationException("No implicit subscription.");
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval) => throw new InvalidOperationException("No implicit subscription.");
    }
}
