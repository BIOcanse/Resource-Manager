using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Amd;
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
            var probe=owner.Probe(Cpu,Power);Assert.True(probe.CanWrite); Assert.Equal(45,probe.Range!.DefaultValue);
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
        var range=owner.Probe(Cpu,Power).Range;Assert.Equal(45,range!.DefaultValue);
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
        var helper=new RecordedControlHelper {Override=(operation,_)=>operation == "read"
            ? RecordedControlHelper.Envelope(new {ok=true,stapmLimitWatts=45})
            : response == "null" ? null:System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response)};
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
        var helper = new RecordedControlHelper { Override = (_, _) => RecordedControlHelper.Envelope(new { ok=true }) };
        using var owner = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(directory));
        Assert.Equal(AmdCpuControlStatus.Failed, (await owner.WriteAsync(Cpu, Power, new(Power.Field, 70))).Status);
        Assert.False(Directory.Exists(directory));
        Assert.DoesNotContain(helper.Calls, call => call.Operation.StartsWith("set-", StringComparison.Ordinal));
    }
    [Fact]
    public async Task DurableBoundOriginalExistsBeforeSetterAndMissingValueCannotBecomeZero()
    {
        var helper = new RecordedControlHelper();
        helper.Override = (operation, _) =>
        {
            if (operation == "read") return RecordedControlHelper.Envelope(new { ok=true, stapmLimitWatts=65 });
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
            var cap=new AmdCpuControlCapability(field,new(0,100,1,"W"));Assert.Equal(read,owner.Probe(Cpu,cap).Range!.DefaultValue);
            Assert.Empty(Directory.Exists(directory) ? Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(file) + "-*.json") : []);
            Assert.Equal(AmdCpuControlStatus.Applied,(await owner.WriteAsync(Cpu,cap,new(field,150))).Status);
            Assert.Single(Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(file) + "-*.json"));
            var args=helper.Calls.Last().Arguments!;Assert.Equal("W",args["unit"]);Assert.Equal(which,args["which"]);Assert.Equal(100d,args["value"]);
        }
        var co=new AmdCpuControlCapability(AmdCpuControlField.CurveOptimizer,new(-30,30,1,"档"));
        Assert.Equal(-5,owner.Probe(Cpu,co).Range!.DefaultValue);
        Assert.Equal(AmdCpuControlStatus.Applied,(await owner.WriteAsync(Cpu,co,new(co.Field,-10))).Status);Assert.Equal("档",helper.Calls.Last().Arguments!["unit"]);
        var scalar=new AmdCpuControlCapability(AmdCpuControlField.PowerBoostScalar,new(1,10,0.5,"×",1));
        Assert.Equal(1,owner.Probe(Cpu,scalar).Range!.DefaultValue);
        Assert.Null((await owner.ReadAsync(Cpu,scalar,default))!.Number);
        helper.Override=(_,_)=>RecordedControlHelper.Envelope(new {ok=true,status="OK",stapmLimitWatts=20.8});
        Assert.Equal(AmdCpuControlStatus.Applied,(await owner.WriteAsync(Cpu,Power,new(Power.Field,20))).Status);
    }
    [Fact]
    public void RealFactoryOnlyConstructsAndBorrowedDisposeNeverClosesSharedTransport()
    {
        var opts=new HardwareBridgeProcessOptions(TimeSpan.FromSeconds(90),TimeSpan.FromSeconds(1),new(128,1024,1024,".NET","10","missing","failed"));
        using(var actual=AmdCpuControlResource.Create(Path.Combine(directory,"missing-helper.exe"),opts,AmdCpuControlOptions.InDirectory(directory))) { }
        Assert.False(Directory.Exists(directory));var helper=new RecordedControlHelper();var borrowed=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory));
        borrowed.Dispose();borrowed.Dispose();Assert.Equal(0,helper.Disposals);
        Assert.Throws<ObjectDisposedException>(()=>borrowed.Probe(Cpu,Power));
    }
    public void Dispose(){if (Directory.Exists(directory))
        {
            // Fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(directory)) Directory.Delete(child);
            Directory.Delete(directory);
        }}
}
