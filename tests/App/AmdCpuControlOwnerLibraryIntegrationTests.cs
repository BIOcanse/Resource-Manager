using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
namespace Resource_Manager_APP.Tests;

public sealed class AmdCpuControlOwnerLibraryIntegrationTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"rm-amd-cpu-owner-"+Guid.NewGuid());
    private static AmdCpuControlTarget Cpu => new("cpu:package",true);
    private static AmdCpuControlCapability Power => new(AmdCpuControlField.PowerLimit,new(0,100,1,"W"));
    [Fact]
    public async Task ProbeDoesNotCaptureAndExplicitWriteSurvivesRestartThenRetiresBaseline()
    {
        var helper=new RecordedControlHelper();
        using(var owner=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory)))
        {
            Assert.Empty(helper.Calls); Assert.False(Directory.Exists(directory));
            var probe=owner.Probe(Cpu,Power);Assert.True(probe.CanWrite); Assert.Null(probe.Range!.DefaultValue);
            Assert.False(Directory.Exists(directory));
            helper.Stapm = 65; // An OEM profile change after opening the page is the real pre-write value.
            Assert.Equal(AmdCpuControlStatus.Applied,(await owner.WriteAsync(Cpu,Power,new(Power.Field,20))).Status);
            Assert.Equal(65,owner.Probe(Cpu,Power).Range!.DefaultValue);
            Assert.Equal(20,(await owner.ReadAsync(Cpu,Power,default))!.Number);
        }
        Assert.Equal(0,helper.Disposals);
        using var restart=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory));
        var restored=Power with {Range=restart.Probe(Cpu,Power).Range};
        Assert.True(await restart.RestoreAsync(Cpu,restored)); Assert.Equal(65,helper.Stapm);
        Assert.Empty(Directory.GetFiles(directory));
        Assert.False(await restart.RestoreAsync(Cpu, Power));
        helper.Stapm = 55;
        Assert.Equal(AmdCpuControlStatus.Applied, (await restart.WriteAsync(Cpu, Power, new(Power.Field, 80))).Status);
        Assert.True(await restart.RestoreAsync(Cpu, Power));
        Assert.Equal(55, helper.Stapm);
    }
    [Fact]
    public async Task CpuPersistenceFailurePreventsHardwareWriteAndDoesNotRetainMemoryBaseline()
    {
        Directory.CreateDirectory(directory); var occupied=Path.Combine(directory,"occupied");File.WriteAllText(occupied,"file");
        var warnings=new List<AmdCpuControlMessage>();var helper=new RecordedControlHelper();
        using var owner=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(occupied),warning:(_,code,_)=>warnings.Add(code));
        var range=owner.Probe(Cpu,Power).Range;Assert.Null(range!.DefaultValue);
        Assert.Empty(warnings);
        Assert.Equal(AmdCpuControlStatus.Failed,(await owner.WriteAsync(Cpu,Power,new(Power.Field,20))).Status);
        Assert.Contains(AmdCpuControlMessage.WriteBaselineWarning,warnings);
        Assert.DoesNotContain(helper.Calls, call => call.Operation.StartsWith("set-", StringComparison.Ordinal));
        Assert.False(await owner.RestoreAsync(Cpu, Power));
    }
    [Theory]
    [InlineData(AmdCpuControlField.TdcLimit)] [InlineData(AmdCpuControlField.EdcLimit)] [InlineData(AmdCpuControlField.TemperatureLimit)]
    public async Task UnconfirmedCapabilitiesArePubliclyNotImplementedAndNeverSent(AmdCpuControlField field)
    {
        var helper=new RecordedControlHelper();using var owner=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory));
        var cap=new AmdCpuControlCapability(field);
        Assert.Equal(AmdCpuControlUnavailableKind.NotImplemented,owner.Probe(Cpu,cap).UnavailableKind);
        helper.Calls.Clear();Assert.Equal(AmdCpuControlStatus.Failed,(await owner.WriteAsync(Cpu,cap,new(field,50))).Status);Assert.Empty(helper.Calls);
        Assert.False(owner.Probe(Cpu with{IsAmdCpu=false},Power).IsMine);
        Assert.Equal(AmdCpuControlStatus.Failed,(await owner.WriteAsync(Cpu with{IsAmdCpu=false},Power,new(Power.Field,20))).Status);
        Assert.Empty(helper.Calls);
    }
    [Theory]
    [InlineData("null")] [InlineData("{\"ok\":false}")] [InlineData("{\"ok\":true,\"accepted\":false}")]
    [InlineData("{\"ok\":true,\"status\":\"TIMEOUT_MAILBOX_READY\"}")]
    [InlineData("{\"ok\":true,\"status\":\"OK\"}")]
    [InlineData("{\"ok\":true,\"status\":\"OK\",\"stapmLimitWatts\":5}")]
    public async Task HelperEnvelopeStatusAndExactFieldMustActuallyVerify(string response)
    {
        var helper=new RecordedControlHelper {Override=(_,_)=>response == "null"
            ? null:System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response)};
        using var owner=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory));
        Assert.Equal(AmdCpuControlStatus.Failed,(await owner.WriteAsync(Cpu,Power,new(Power.Field,20))).Status);
    }
    [Fact]
    public async Task PerTargetOriginalIsNotOverwrittenAndFailedReleaseKeepsIt()
    {
        var helper = new RecordedControlHelper();
        using var owner = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));
        Assert.Equal(AmdCpuControlStatus.Applied, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 80))).Status);
        var other = Cpu with { Id = "cpu:second-package" };
        helper.Stapm = 55;
        Assert.Equal(AmdCpuControlStatus.Applied, (await owner.WriteAsync(other, Power, new(Power.Field, 70))).Status);
        Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
        Assert.Equal(AmdCpuControlStatus.Applied, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 90))).Status);
        helper.Override = (_, _) => RecordedControlHelper.Envelope(new { ok=true, status="OK", stapmLimitWatts=90 });
        Assert.False(await owner.RestoreAsync(Cpu, Power));
        Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
        helper.Override = null;
        Assert.True(await owner.RestoreAsync(Cpu, Power));
        Assert.Equal(45, helper.Stapm);
        Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.True(await owner.RestoreAsync(other, Power));
        Assert.Equal(55, helper.Stapm);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrUnboundLegacyBaselineCannotBecomeARestoreValue(bool legacy)
    {
        Directory.CreateDirectory(directory);
        var helper = new RecordedControlHelper();
        using var owner = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));
        if (legacy) File.WriteAllText(Path.Combine(directory, "cpu-power-baseline.txt"), "45");
        else
        {
            Assert.Equal(AmdCpuControlStatus.Applied, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 80))).Status);
            File.WriteAllText(Assert.Single(Directory.GetFiles(directory, "*.json")), "{\"Version\":1,\"TargetId\":\"unrelated-cpu\",\"CapabilityId\":\"cpu.power-limit\",\"Value\":45}");
        }
        helper.Calls.Clear();
        Assert.Equal(AmdCpuControlStatus.Failed, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 70))).Status);
        Assert.False(await owner.RestoreAsync(Cpu, Power));
        Assert.DoesNotContain(helper.Calls, call => call.Operation.StartsWith("set-", StringComparison.Ordinal));
    }
    [Fact]
    public async Task UnconfirmedPrewriteReadingCannotCreateBaselineOrReachSetter()
    {
        var helper = new RecordedControlHelper { ReadingsOverride = () => RecordedControlHelper.Readings() };
        using var owner = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));
        Assert.Equal(AmdCpuControlStatus.Failed, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 70))).Status);
        Assert.False(Directory.Exists(directory));
        Assert.DoesNotContain(helper.Calls, call => call.Operation.StartsWith("set-", StringComparison.Ordinal));
    }
    [Fact]
    public async Task DurableBoundOriginalExistsBeforeSetterAndMissingValueCannotBecomeZero()
    {
        var helper = new RecordedControlHelper { Stapm = 65 };
        helper.Override = (operation, _) =>
        {
            Assert.Equal("set-power-limit", operation);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "*.json"))));
            Assert.Equal(Cpu.Id, saved.RootElement.GetProperty("TargetId").GetString());
            Assert.Equal(Power.Id, saved.RootElement.GetProperty("CapabilityId").GetString());
            Assert.Equal(65, saved.RootElement.GetProperty("Value").GetDouble());
            return RecordedControlHelper.Envelope(new { ok=true, status="OK", stapmLimitWatts=80 });
        };
        using var owner = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));
        Assert.Equal(AmdCpuControlStatus.Applied, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 80))).Status);
        var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
        File.WriteAllText(path, "{\"Version\":1,\"TargetId\":\"cpu:package\",\"CapabilityId\":\"cpu.power-limit\"}");
        helper.Calls.Clear();
        Assert.False(await owner.RestoreAsync(Cpu, Power));
        Assert.Equal(AmdCpuControlStatus.Failed, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 70))).Status);
        Assert.Empty(helper.Calls);
    }
    [Fact]
    public async Task WholeTableUnitWhichClampToleranceAndUnreadableScalarContractsArePreserved()
    {
        var helper=new RecordedControlHelper();using var owner=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory));
        foreach(var (field,which,read,file) in new[]{(AmdCpuControlField.PowerLimit,"stapm",45d,"cpu-power-baseline.txt"),
            (AmdCpuControlField.SlowPowerLimit,"slow",55d,"cpu-slow-power-baseline.txt"),(AmdCpuControlField.FastPowerLimit,"fast",65d,"cpu-fast-power-baseline.txt")})
        {
            var cap=new AmdCpuControlCapability(field,new(0,100,1,"W"));Assert.Null(owner.Probe(Cpu,cap).Range!.DefaultValue);
            Assert.Equal(read,(await owner.ReadAsync(Cpu,cap))!.Number);
            Assert.Empty(Directory.Exists(directory) ? Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(file) + "-*.json") : []);
            Assert.Equal(AmdCpuControlStatus.Applied,(await owner.WriteAsync(Cpu,cap,new(field,150))).Status);
            Assert.Single(Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(file) + "-*.json"));
            var args=helper.Calls.Last().Arguments!;Assert.Equal("W",args["unit"]);Assert.Equal(which,args["which"]);Assert.Equal(100d,args["value"]);
        }
        var co=new AmdCpuControlCapability(AmdCpuControlField.CurveOptimizer,new(-30,30,1,"档"));
        Assert.Null(owner.Probe(Cpu,co).Range!.DefaultValue);
        Assert.Equal(AmdCpuControlStatus.Applied,(await owner.WriteAsync(Cpu,co,new(co.Field,-10))).Status);Assert.Equal("档",helper.Calls.Last().Arguments!["unit"]);
        var scalar=new AmdCpuControlCapability(AmdCpuControlField.PowerBoostScalar,new(1,10,0.5,"×",1));
        Assert.Equal(1,owner.Probe(Cpu,scalar).Range!.DefaultValue);
        Assert.Null((await owner.ReadAsync(Cpu,scalar,default))!.Number);
        helper.Override=(_,_)=>RecordedControlHelper.Envelope(new {ok=true,status="OK",stapmLimitWatts=20.8});
        Assert.Equal(AmdCpuControlStatus.Applied,(await owner.WriteAsync(Cpu,Power,new(Power.Field,20))).Status);
    }
    [Fact]
    public async Task RepeatedProbesKeepFactsWithoutReadingAndReadsFollowOnlyExplicitCalls()
    {
        var helper = new RecordedControlHelper();
        using var owner = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));
        var missingFileExceptions = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> capture = (_,args) =>
        {
            if ((args.Exception is FileNotFoundException or DirectoryNotFoundException)
                && args.Exception.Message.Contains(directory,StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref missingFileExceptions);
        };
        AppDomain.CurrentDomain.FirstChanceException += capture;
        try
        {
            for (var round = 0; round < 20; round++) Assert.True(owner.Probe(Cpu, Power).CanWrite);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= capture; }
        Assert.Equal(0,missingFileExceptions);
        Assert.Equal(20, helper.Calls.Count);
        Assert.Equal(new[] { "describe" }, helper.Requests);
        Assert.False(Directory.Exists(directory));
        helper.Stapm = 61;
        Assert.Equal(61, (await owner.ReadAsync(Cpu, Power))!.Number);
        helper.Restart(); helper.Stapm = 72;
        Assert.Equal(72, (await owner.ReadAsync(Cpu, Power))!.Number);
        Assert.True(owner.Probe(Cpu, Power).CanWrite);
        Assert.Equal(new[] { "describe", "describe" }, helper.Requests);
        Assert.Equal(2, helper.ReadingReads);
        helper.Override = (_, _) => RecordedControlHelper.Envelope(new { available=false, reason="new-session-unavailable" });
        helper.Restart();
        Assert.Equal("new-session-unavailable", owner.Probe(Cpu, Power).Reason);
        var requests = helper.Requests.Count;
        for (var round = 0; round < 20; round++) Assert.False(owner.Probe(Cpu,Power).CanWrite);
        Assert.Null((await owner.ReadAsync(Cpu,Power))!.Number);
        Assert.Equal(requests,helper.Requests.Count);
    }
    [Fact]
    public void RealFactoryOnlyConstructsAndBorrowedDisposeNeverClosesSharedTransport()
    {
        var shared = new RecordedControlHelper();
        using(var actual=AmdCpuControlResource.Create(shared,AmdCpuControlOptions.InDirectory(directory))) { }
        Assert.Equal(0, shared.Disposals);
        Assert.Empty(shared.Calls);
        Assert.False(Directory.Exists(directory));var helper=new RecordedControlHelper();var borrowed=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory));
        borrowed.Dispose();borrowed.Dispose();Assert.Equal(0,helper.Disposals);
        Assert.Throws<ObjectDisposedException>(()=>borrowed.Probe(Cpu,Power));
    }
    [Fact]
    public async Task ApplyRestoreApplyUsesPublishedRestoredValueForTheNextBaseline()
    {
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with { ReadingInterval = TimeSpan.FromMinutes(1) });
        using var readings = client.SubscribeReadings();
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = client.Owner.RunAsync(stop.Token, () => ready.SetResult());
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var resource = new AmdCpuControlResource(client, AmdCpuControlOptions.InDirectory(directory));
            Assert.Equal(AmdCpuControlStatus.Applied, (await resource.WriteAsync(Cpu, Power, new(Power.Field, 20))).Status);
            await client.SendAsync("sample-current-readings", null, default);
            Assert.Equal(20, client.ReadCurrentReadings()!.Value.StapmLimitWatts);
            Assert.True(await resource.RestoreAsync(Cpu, Power));
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Equal(AmdCpuControlStatus.Applied, (await resource.WriteAsync(Cpu, Power, new(Power.Field, 30))).Status);
            using var baseline = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "*.json"))));
            Assert.Equal(45, baseline.RootElement.GetProperty("Value").GetDouble());
            Assert.True(await resource.RestoreAsync(Cpu, Power));
            Assert.Equal(45, client.ReadCurrentReadings()!.Value.StapmLimitWatts);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public void FactoryRequiresCallerOwnedClientInsteadOfHidingANeverRunOwner()
    {
        Assert.NotNull(typeof(AmdCpuControlResource).GetMethod("Create", [typeof(IHardwareBridgeProcessClient), typeof(AmdCpuControlOptions)]));
        Assert.DoesNotContain(typeof(AmdCpuControlResource).GetMethods(), method => method.Name == "Create"
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)));
    }
    [Fact]
    public async Task CancellationDuringPrewriteReadingDoesNotPersistBaselineOrMutate()
    {
        using var stop = new CancellationTokenSource();
        var helper = new RecordedControlHelper
        {
            ReadingsOverride = () => { stop.Cancel(); return RecordedControlHelper.Readings(stapm: 45); }
        };
        using var resource = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.WriteAsync(Cpu, Power, new(Power.Field, 20), stop.Token));

        Assert.False(Directory.Exists(directory));
        Assert.DoesNotContain(helper.Calls, call => call.Operation.StartsWith("set-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledReadWriteAndRestoreNeverReachTheHelperOrBaseline()
    {
        var helper = new RecordedControlHelper();
        using var resource = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.ReadAsync(Cpu, Power, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.WriteAsync(Cpu, Power, new(Power.Field, 20), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.RestoreAsync(Cpu, Power, stop.Token));
        Assert.Empty(helper.Calls);
        Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public async Task BothFactoriesUseTheCallerRunAndDisposingResourcesLeavesThatRunOwnedByCaller()
    {
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        using var readings = client.SubscribeReadings();
        using var cpu = AmdCpuControlResource.Create(client, AmdCpuControlOptions.InDirectory(directory));
        using var gpu = IntegratedGpuControlResource.Create(client, new(Path.Combine(directory, "gpu.json"), _ => Task.FromResult(true)));
        var target = new IntegratedGpuControlTarget("gpu:amd", IntegratedGpuControlRoute.AmdIntegrated);
        var capability = new IntegratedGpuControlCapability(IntegratedGpuControlField.CurveOptimizer, new(-30, 10, 1, "档"));
        Assert.False(cpu.Probe(Cpu, Power).CanWrite);
        Assert.False(gpu.Probe(target, capability).CanWrite);
        Assert.Null(client.ReadCurrentReadings());
        Assert.False(Directory.Exists(directory));
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = client.Owner.RunAsync(stop.Token, () => ready.SetResult());
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(cpu.Probe(Cpu, Power).CanWrite);
            Assert.True(gpu.Probe(target, capability).CanWrite);
            Assert.Equal(45, (await cpu.ReadAsync(Cpu, Power))!.Number);
            Assert.Equal(-5, (await gpu.ReadAsync(target, capability))!.Number);
            cpu.Dispose();
            gpu.Dispose();
            Assert.NotNull(client.ReadCurrentReadings());
            Assert.NotNull(await client.SendAsync("describe", null, default));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Null(client.ReadCurrentReadings());
        Assert.Null(await client.SendAsync("describe", null, default));
    }
    public void Dispose(){if (Directory.Exists(directory))
        {
            // Fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(directory)) Directory.Delete(child);
            Directory.Delete(directory);
        }}
}
