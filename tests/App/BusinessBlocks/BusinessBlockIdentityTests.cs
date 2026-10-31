using System.Text.Json;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockIdentityTests
{
    [Fact]
    public async Task ProductionAdaptersUseCompiledNamesSwitchAndRegistryVersion()
    {
        var logger = new BusinessBlockControlSystemTests.CapturedLogger<BusinessBlockScheduler>();
        var block = new BusinessBlockTestSupport.Probe();
        await using var scheduler = BusinessBlockTestSupport.Create(
            [BusinessBlockTestSupport.Register(BackendUids.PresentFrameEtwSource, block)], logger: logger);
        await using var ui = new ResourceManager.NativeUi.Blocks.NativeWindowBlockTrees(
            _ => new BusinessBlockTestSupport.Probe(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BusinessBlockController>.Instance);
#if RM_STATE_NAMES
        const string mode = "names-on";
        var expectedBackendName = ResourceManager.App.StateItems.StateNames.Name(BackendUids.PresentFrameEtwSource);
        var expectedBackendProgramName = ResourceManager.App.StateItems.StateNames.Name(BackendUids.Program);
        var expectedUiName = ResourceManager.NativeUi.StateItems.StateNames.Name(UiUids.MainWindow);
        var expectedUiProgramName = ResourceManager.NativeUi.StateItems.StateNames.Name(UiUids.Program);
        Assert.Equal("PresentFrameEtwSource", expectedBackendName);
        Assert.Equal("MainWindow", expectedUiName);
#else
        const string mode = "names-off";
        string? expectedBackendName = null;
        string? expectedBackendProgramName = null;
        string? expectedUiName = null;
        string? expectedUiProgramName = null;
#endif
        try
        {
            await scheduler.StartAsync(BackendUids.PresentFrameEtwSource,
                BusinessBlockTestSupport.Reason());
            await block.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            var snapshot = scheduler.Read();
            Assert.Equal(ResourceManager.App.StateItems.StateRegistry.Version, snapshot.RegistryVersion);
            Assert.Equal(BackendUids.Program, snapshot.Program);
            Assert.Equal(expectedBackendProgramName, snapshot.ProgramName);
            Assert.Equal(expectedBackendName, Assert.Single(snapshot.Blocks).Name);
            var backendExecution = Assert.Single(snapshot.Items, item => item.ItemUid == TestStateUids.ExecutionEnabled
                && item.BlockUid == BackendUids.PresentFrameEtwSource);
            Assert.True(Assert.IsType<bool>(backendExecution.Value));
            Assert.Equal(expectedBackendName, backendExecution.BlockName);
            Assert.Null(backendExecution.Name);
            var uiSnapshot = ui.Read();
            Assert.Equal(ResourceManager.NativeUi.StateItems.StateRegistry.Version, uiSnapshot.RegistryVersion);
            Assert.Equal(UiUids.Program, uiSnapshot.Program);
            Assert.Equal(expectedUiProgramName, uiSnapshot.ProgramName);
            Assert.Equal(expectedUiName, Assert.Single(uiSnapshot.Blocks, value => value.Id == UiUids.MainWindow).Name);
            var childLabel = BackendUids.PresentFrameEtwSource.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (expectedBackendName is null ? "" : $" ({expectedBackendName})");
            Assert.Contains(logger.Messages, value => value.Contains("children=" + childLabel, StringComparison.Ordinal));

            var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
            var json = JsonSerializer.Serialize(snapshot, options);
            var uiJson = JsonSerializer.Serialize(uiSnapshot, options);
            using var backendDocument = JsonDocument.Parse(json);
            using var uiDocument = JsonDocument.Parse(uiJson);
            Assert.False(backendDocument.RootElement.TryGetProperty("demands", out _));
            Assert.False(uiDocument.RootElement.TryGetProperty("demands", out _));
            Assert.Equal(expectedBackendName is not null,
                backendDocument.RootElement.GetProperty("blocks")[0].TryGetProperty("name", out _));
            Assert.Equal(expectedUiName is not null,
                uiDocument.RootElement.GetProperty("blocks")[0].TryGetProperty("name", out _));
            if (Environment.GetEnvironmentVariable("RM_STATE_UID_EVIDENCE") is { Length: > 0 } evidence)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "production-" + mode + "-snapshot.json"), json);
                await File.WriteAllLinesAsync(Path.Combine(evidence, "production-" + mode + "-logs.txt"), logger.Messages);
                await File.WriteAllTextAsync(Path.Combine(evidence, "production-ui-" + mode + "-snapshot.json"), uiJson);
            }
        }
        finally { await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamesOnlyAnnotateIntegerIdentitiesInLogsAndSnapshots(bool namesEnabled)
    {
        const int program = 1000;
        const int worker = 101;
        const string registryVersion = "fixture-registry-version";
        string? Lookup(int id) => namesEnabled ? id switch
        {
            program => "Program", worker => "Worker", TestStateUids.ExecutionEnabled => "ExecutionEnabled",
            BackendItems.ProgramShuttingDown => "ProgramShuttingDown", _ => null
        } : null;
        var controlLog = new BusinessBlockControlSystemTests.CapturedLogger<BusinessBlockController>();
        var decisionLog = new BusinessBlockControlSystemTests.CapturedLogger<ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler>();
        var control = new BusinessBlockController(controlLog, 8, Lookup);
        var block = new BusinessBlockTestSupport.Probe();
        var authority = BusinessBlockTestSupport.CreateAuthority([program, worker]);
        await using var scheduler = new TestBlockScheduler(
            [new TestBlockRegistration(worker, [], _ => block) { ParentId = program }], authority,
            historyLimit: 8, logger: decisionLog, control: control, program: program,
            shutdownItemUid: BackendItems.ProgramShuttingDown,
            registryVersion: registryVersion, nameLookup: Lookup);
        var graph = scheduler.Graph;
        var reason = BusinessBlockTestSupport.Reason(owner: "operator", cause: "observe", version: 19);
        try
        {
            await scheduler.StartAsync(worker, reason);
            await block.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            var snapshot = scheduler.Read();
            Assert.Equal(program, graph.Program);
            Assert.Equal(program, snapshot.Program);
            Assert.Equal(registryVersion, snapshot.RegistryVersion);
            Assert.Equal(namesEnabled ? "Program" : null, snapshot.ProgramName);
            var fact = Assert.Single(snapshot.Blocks);
            Assert.Equal(worker, fact.Id);
            Assert.Equal(program, fact.ParentId);
            Assert.Equal(namesEnabled ? "Worker" : null, fact.Name);
            var executionItem = Assert.Single(snapshot.Items, value => value.ItemUid == TestStateUids.ExecutionEnabled && value.BlockUid == worker);
            Assert.True(Assert.IsType<bool>(executionItem.Value));
            Assert.Equal(namesEnabled ? "ExecutionEnabled" : null, executionItem.Name);
            Assert.Equal(namesEnabled ? "Worker" : null, executionItem.BlockName);
            Assert.Contains(snapshot.CurrentDecisions, value => value.ParentId == program && value.ShouldRun.SequenceEqual([worker]));
            Assert.Contains(controlLog.Messages, value => value == (namesEnabled
                ? "Business block 101 (Worker): Stopped -> Starting"
                : "Business block 101: Stopped -> Starting"));
            Assert.Contains(decisionLog.Messages, value => value.Contains(namesEnabled
                ? "Business block decision 1000 (Program)/"
                : "Business block decision 1000/", StringComparison.Ordinal));
            if (!namesEnabled)
            {
                Assert.DoesNotContain(controlLog.Messages.Concat(decisionLog.Messages), value => value.Contains("Worker", StringComparison.Ordinal));
                Assert.DoesNotContain(controlLog.Messages.Concat(decisionLog.Messages), value => value.Contains("Program", StringComparison.Ordinal));
            }

            var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
            var json = JsonSerializer.Serialize(snapshot, options);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(program, root.GetProperty("program").GetInt32());
            Assert.Equal(registryVersion, root.GetProperty("registryVersion").GetString());
            var serializedExecution = root.GetProperty("items").EnumerateArray().Single(row =>
                row.GetProperty("itemUid").GetInt32() == TestStateUids.ExecutionEnabled
                && row.GetProperty("blockUid").GetInt32() == worker);
            Assert.True(serializedExecution.GetProperty("value").GetBoolean());
            Assert.Equal(namesEnabled, serializedExecution.TryGetProperty("name", out var serializedItemName));
            Assert.Equal(namesEnabled, serializedExecution.TryGetProperty("blockName", out var serializedItemBlockName));
            Assert.False(root.TryGetProperty("facts", out _));
            Assert.False(root.TryGetProperty("demands", out _));
            if (namesEnabled)
            {
                Assert.Equal("ExecutionEnabled", serializedItemName.GetString());
                Assert.Equal("Worker", serializedItemBlockName.GetString());
            }
            Assert.All(root.GetProperty("blocks").EnumerateArray(), row => Assert.False(row.TryGetProperty("outputs", out _)));
            Assert.All(root.GetProperty("graph").EnumerateArray(), row => Assert.False(row.TryGetProperty("outputs", out _)));
            var serializedBlock = root.GetProperty("blocks")[0];
            Assert.Equal(worker, serializedBlock.GetProperty("id").GetInt32());
            Assert.Equal(namesEnabled, serializedBlock.TryGetProperty("name", out var serializedName));
            Assert.Equal(namesEnabled, root.TryGetProperty("programName", out var serializedProgramName));
            if (namesEnabled)
            {
                Assert.Equal("Worker", serializedName.GetString());
                Assert.Equal("Program", serializedProgramName.GetString());
            }
            var debugSnapshot = new ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks.BusinessBlockDebugSnapshot(snapshot, []);
            var debugJson = JsonSerializer.Serialize(debugSnapshot, options);
            using var debugDocument = JsonDocument.Parse(debugJson);
            var scheduling = debugDocument.RootElement.GetProperty("scheduling");
            Assert.Equal(registryVersion, scheduling.GetProperty("registryVersion").GetString());
            Assert.Equal(worker, scheduling.GetProperty("blocks")[0].GetProperty("id").GetInt32());
            Assert.Equal(namesEnabled, scheduling.GetProperty("blocks")[0].TryGetProperty("name", out _));
            Assert.Equal(namesEnabled, scheduling.TryGetProperty("programName", out _));
            if (Environment.GetEnvironmentVariable("RM_STATE_UID_EVIDENCE") is { Length: > 0 } evidence)
            {
                // The validation owner supplies its already registered evidence directory.
                var mode = namesEnabled ? "names-on" : "names-off";
                await File.WriteAllTextAsync(Path.Combine(evidence, mode + "-snapshot.json"), json);
                await File.WriteAllTextAsync(Path.Combine(evidence, mode + "-debug-snapshot.json"), debugJson);
                await File.WriteAllLinesAsync(Path.Combine(evidence, mode + "-logs.txt"), controlLog.Messages.Concat(decisionLog.Messages));
            }
        }
        finally
        {
            await scheduler.ShutdownAsync(reason);
            await block.Returned.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
        }
    }
}
