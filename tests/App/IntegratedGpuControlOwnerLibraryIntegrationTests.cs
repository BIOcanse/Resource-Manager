using System.Text.Json;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
namespace Resource_Manager_APP.Tests;

public sealed class IntegratedGpuControlOwnerLibraryIntegrationTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"rm-igpu-owner-"+Guid.NewGuid());
    private string Baseline=>Path.Combine(directory,"baseline.json");
    private static IntegratedGpuControlTarget Amd(string id="gpu:A")=>new(id,IntegratedGpuControlRoute.AmdIntegrated);
    private static IntegratedGpuControlCapability Co=>new(IntegratedGpuControlField.CurveOptimizer,new(-30,10,1,"档"));
    private IntegratedGpuControlResource Owner(RecordedControlHelper helper,RecordedIntegratedIntelHardware intel,bool accepted=true)
        => new(helper,intel,new(Baseline,_=>Task.FromResult(accepted)));
    [Fact]
    public async Task BothOwnersBorrowSameHelperButMaintainOriginalSeparateBaselinesAndRestoration()
    {
        var helper=new RecordedControlHelper();var intel=new RecordedIntegratedIntelHardware();
        using(var cpu=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory)))
        using(var gpu=Owner(helper,intel))
        {
            var cpuCap=new AmdCpuControlCapability(AmdCpuControlField.CurveOptimizer,new(-30,30,1,"档"));
            Assert.Empty(helper.Calls);Assert.Null(cpu.Probe(new("cpu:0",true),cpuCap).Range!.DefaultValue);
            var cap=Co with{Range=gpu.Probe(Amd(),Co).Range};Assert.Null(cap.Range!.DefaultValue);
            Assert.False(Directory.Exists(directory));
            Assert.Equal(IntegratedGpuControlStatus.Applied,(await gpu.WriteAsync(Amd(),Co,new(Co.Field,-20),default)).Status);
            Assert.Equal(8,gpu.Probe(Amd(),Co).Range!.DefaultValue);
            Assert.Equal("set-igpu-curve-optimizer",helper.Calls.Last(c => c.Operation.StartsWith("set-",StringComparison.Ordinal)).Operation);
            Assert.Equal(-5,helper.CpuCounts); Assert.Equal(-20,helper.GpuCounts);
        }
        Assert.Equal(0,helper.Disposals);Assert.Equal(0,intel.Disposals);
        using(var restart=Owner(helper,intel))
        {
            var cap=Co with{Range=restart.Probe(Amd(),Co).Range};Assert.True(await restart.RestoreAsync(Amd(),cap));Assert.Equal(8,helper.GpuCounts);
            helper.GpuCounts=-4;Assert.Null(restart.Probe(Amd("gpu:B"),Co).Range!.DefaultValue);
            Assert.Equal(IntegratedGpuControlStatus.Applied,(await restart.WriteAsync(Amd("gpu:B"),Co,new(Co.Field,-10))).Status);
        }
        var saved=JsonSerializer.Deserialize<Dictionary<string,double>>(File.ReadAllText(Baseline))!;
        Assert.Equal(8,saved["gpu:A"]);Assert.Equal(-4,saved["gpu:B"]);
    }
    [Fact]
    public async Task LegacyScalarAndStrictSaveFailureDisableAdjustmentWithoutDestroyingRead()
    {
        Directory.CreateDirectory(directory);File.WriteAllText(Baseline,"8");var helper=new RecordedControlHelper();var intel=new RecordedIntegratedIntelHardware();
        using(var legacy=Owner(helper,intel))
        {
            var probe=legacy.Probe(Amd(),Co);Assert.False(probe.CanWrite);Assert.True(probe.CanRead);
            Assert.Equal("8",File.ReadAllText(Baseline));Assert.Equal(8,(await legacy.ReadAsync(Amd(),Co,default))!.Number);
        }
        var occupied=Path.Combine(directory,"occupied");File.WriteAllText(occupied,"file");var warnings=new List<IntegratedGpuControlMessage>();
        using var failure=new IntegratedGpuControlResource(helper,intel,new(Path.Combine(occupied,"baseline.json"),_=>Task.FromResult(false)),warning:(_,code,_)=>warnings.Add(code));
        Assert.True(failure.Probe(Amd(),Co).CanWrite);
        Assert.Equal(IntegratedGpuControlStatus.Failed,(await failure.WriteAsync(Amd(),Co,new(Co.Field,-10))).Status);
        Assert.Contains(IntegratedGpuControlMessage.BaselineWarning,warnings);
        Assert.DoesNotContain(helper.Calls,call=>call.Operation.StartsWith("set-",StringComparison.Ordinal));
    }
    [Fact]
    public async Task IntelProbeNeverSubmitsWaiverAndWriteUsesExactLuidFractionalMhzThenVerifies()
    {
        var helper=new RecordedControlHelper();var intel=new RecordedIntegratedIntelHardware();using var owner=Owner(helper,intel);
        var target=new IntegratedGpuControlTarget("gpu:intel",IntegratedGpuControlRoute.IntelIntegrated,0x800000001);
        var cap=new IntegratedGpuControlCapability(IntegratedGpuControlField.CoreClockOffset,new(-200,200,1,"MHz",0));
        Assert.True(owner.Probe(target,cap).CanWrite);Assert.Empty(intel.Calls);Assert.Empty(helper.Calls);
        Assert.Equal(IntegratedGpuControlStatus.Applied,(await owner.WriteAsync(target,cap,new(cap.Field,45.5),default)).Status);
        Assert.Equal(new[]{"waiver:22","write:22","read:22"},intel.Calls);Assert.Equal(45.5,intel.Offset);
        Assert.True(await owner.RestoreAsync(target,cap));Assert.Equal(0,intel.Offset);Assert.Empty(helper.Calls);
    }
    [Fact]
    public async Task MissingConsentIdentityAndDiscreteRouteNeverReadWaiveOrSetOtherIntelAdapter()
    {
        var helper=new RecordedControlHelper();var intel=new RecordedIntegratedIntelHardware();
        var target=new IntegratedGpuControlTarget("gpu:intel",IntegratedGpuControlRoute.IntelIntegrated,0x800000001);
        var cap=new IntegratedGpuControlCapability(IntegratedGpuControlField.CoreClockOffset);
        using(var denied=Owner(helper,intel,false))
        {
            Assert.Equal(IntegratedGpuControlUnavailableKind.Prerequisite,denied.Probe(target,cap).UnavailableKind);
            Assert.Equal(IntegratedGpuControlStatus.Failed,(await denied.WriteAsync(target,cap,new(cap.Field,45),default)).Status);
            Assert.Empty(intel.Calls);
        }
        using var owner=Owner(helper,intel);
        foreach(var invalid in new[]{target with{AdapterKey=null},target with{AdapterKey=0},target with{AdapterKey=0x800000002},target with{Route=IntegratedGpuControlRoute.Rejected}})
        {
            Assert.False(owner.Probe(invalid,cap).CanWrite);Assert.Equal(IntegratedGpuControlStatus.Failed,(await owner.WriteAsync(invalid,cap,new(cap.Field,45),default)).Status);
        }
        Assert.Empty(intel.Calls);Assert.Empty(helper.Calls);
    }
    [Theory]
    [InlineData(null)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(150d)]
    public async Task IntelReadbackMustBeFiniteAndWithinOriginalOneMhz(double? reading)
    {
        var intel=new RecordedIntegratedIntelHardware {UseOverrideReadback=true,OverrideReadback=reading};using var owner=Owner(new(),intel);
        var target=new IntegratedGpuControlTarget("gpu:intel",IntegratedGpuControlRoute.IntelIntegrated,0x800000001);
        var cap=new IntegratedGpuControlCapability(IntegratedGpuControlField.CoreClockOffset);
        Assert.Equal(IntegratedGpuControlStatus.Failed,(await owner.WriteAsync(target,cap,new(cap.Field,45),default)).Status);
    }
    [Theory]
    [InlineData("{\"ok\":false}",false)] [InlineData("{\"ok\":true,\"accepted\":false}",false)]
    [InlineData("{\"ok\":true,\"accepted\":true,\"integratedGpuCurveOptimizerCounts\":9}",false)]
    [InlineData("{\"ok\":true,\"accepted\":true}",true)]
    public async Task OriginalAmdIgpuAcceptedAndOptionalReadbackEnvelopeIsPreserved(string response,bool applied)
    {
        var helper=new RecordedControlHelper {Override=(_,_)=>JsonSerializer.Deserialize<JsonElement>(response)};using var owner=Owner(helper,new());
        Assert.Equal(applied?IntegratedGpuControlStatus.Applied:IntegratedGpuControlStatus.Failed,(await owner.WriteAsync(Amd(),Co,new(Co.Field,-10),default)).Status);
    }
    [Fact]
    public async Task ExplicitWritePersistsTargetOriginalBeforeSetterAndMissingReadingCannotBecomeZero()
    {
        var helper = new RecordedControlHelper { GpuCounts = 7 };
        helper.Override = (operation,_) =>
        {
            Assert.Equal("set-igpu-curve-optimizer",operation);
            var saved = JsonSerializer.Deserialize<Dictionary<string,double>>(File.ReadAllText(Baseline))!;
            Assert.Equal(7,saved[Amd().Id]);
            return RecordedControlHelper.Envelope(new {ok=true,accepted=true,integratedGpuCurveOptimizerCounts=-10});
        };
        using var gpu = Owner(helper,new());
        Assert.Equal(IntegratedGpuControlStatus.Applied,(await gpu.WriteAsync(Amd(),Co,new(Co.Field,-10))).Status);
        helper.Override = null;
        helper.ReadingsOverride = () => RecordedControlHelper.Readings();
        helper.Calls.Clear();
        Assert.Equal(IntegratedGpuControlStatus.Failed,(await gpu.WriteAsync(Amd("gpu:B"),Co,new(Co.Field,-10))).Status);
        Assert.DoesNotContain(helper.Calls,call=>call.Operation.StartsWith("set-",StringComparison.Ordinal));
        Assert.Single(JsonSerializer.Deserialize<Dictionary<string,double>>(File.ReadAllText(Baseline))!);
    }
    [Fact]
    public async Task SharedSessionFactsDoNotReadAndFreshReadAfterRestartDoesNotRetainOldValue()
    {
        // Replay the pre-fix Probe wire trace: CPU describe/read, then iGPU describe/read.
        // This comparator deliberately has no session description reuse, as in the old transport.
        var before = new RecordedControlHelper {ReuseDescription=false};
        for (var round = 0; round < 20; round++)
        {
            await before.SendAsync("describe",null,default);
            await before.SendAsync("read",null,default);
            await before.SendAsync("describe",null,default);
            await before.SendAsync("read",null,default);
        }
        Assert.Equal(40,before.Requests.Count(op=>op=="describe"));
        Assert.Equal(40,before.Requests.Count(op=>op=="read"));
        var helper = new RecordedControlHelper();
        using var cpu = new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory));
        using var gpu = Owner(helper,new());
        var power = new AmdCpuControlCapability(AmdCpuControlField.PowerLimit);
        for (var round = 0; round < 20; round++)
        {
            Assert.True(cpu.Probe(new("cpu:0",true),power).CanWrite);
            Assert.True(gpu.Probe(Amd(),Co).CanWrite);
        }
        Assert.Equal(40,helper.Calls.Count);
        Assert.Equal(new[] { "describe" }, helper.Requests);
        Assert.Equal(80,before.Requests.Count);
        Assert.Equal(1,helper.Requests.Count);
        Assert.False(Directory.Exists(directory));
        Assert.False(await gpu.RestoreAsync(Amd(),Co with {Range=Co.Range! with {DefaultValue=99}}));
        Assert.Equal(8,(await gpu.ReadAsync(Amd(),Co))!.Number);
        helper.Restart(); helper.GpuCounts=-6;
        Assert.Equal(-6,(await gpu.ReadAsync(Amd(),Co))!.Number);
        Assert.True(gpu.Probe(Amd(),Co).CanWrite);
        Assert.Equal(new[] { "describe", "describe" }, helper.Requests);
        Assert.Equal(2,helper.ReadingReads);
        helper.ReadingsOverride=()=>RecordedControlHelper.Readings(emptyStatus:HardwareBridgeReadingStatus.Failed);
        Assert.Null((await gpu.ReadAsync(Amd(),Co))!.Number);
    }
    [Fact]
    public void RealFactoryIsPassiveAndBorrowedLifetimeDoesNotCloseHelperOrIntelHardware()
    {
        var shared = new RecordedControlHelper();
        using(var factory=IntegratedGpuControlResource.Create(shared,new(Baseline,_=>Task.FromResult(false)))) { }
        Assert.Equal(0, shared.Disposals);
        Assert.Empty(shared.Calls);
        Assert.False(Directory.Exists(directory));var helper=new RecordedControlHelper();var intel=new RecordedIntegratedIntelHardware();var owner=Owner(helper,intel);
        owner.Dispose();Assert.Equal(0,helper.Disposals);Assert.Equal(0,intel.Disposals);Assert.Empty(helper.Calls);
        Assert.Throws<ObjectDisposedException>(()=>owner.Probe(Amd(),Co));
    }
    [Fact]
    public void FactoryRequiresCallerOwnedClientInsteadOfHidingANeverRunOwner()
    {
        Assert.NotNull(typeof(IntegratedGpuControlResource).GetMethod("Create", [typeof(IHardwareBridgeProcessClient), typeof(IntegratedGpuControlOptions)]));
        Assert.DoesNotContain(typeof(IntegratedGpuControlResource).GetMethods(), method => method.Name == "Create"
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)));
    }
    [Fact]
    public async Task CancelledReadWriteAndRestoreNeverReachHardwareOrBaseline()
    {
        var helper = new RecordedControlHelper();
        var intel = new RecordedIntegratedIntelHardware();
        using var resource = Owner(helper, intel);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.ReadAsync(Amd(), Co, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.WriteAsync(Amd(), Co, new(Co.Field, -10), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.RestoreAsync(Amd(), Co, stop.Token));
        Assert.Empty(helper.Calls);
        Assert.Empty(intel.Calls);
        Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public async Task CancellationDuringAcceptedIntelSetterStillReturnsVerifiedAppliedReceipt()
    {
        using var stop = new CancellationTokenSource();
        var helper = new RecordedControlHelper();
        var intel = new RecordedIntegratedIntelHardware { AfterWrite = stop.Cancel };
        using var resource = Owner(helper, intel);
        var target = new IntegratedGpuControlTarget("gpu:intel", IntegratedGpuControlRoute.IntelIntegrated, 0x800000001);
        var capability = new IntegratedGpuControlCapability(IntegratedGpuControlField.CoreClockOffset);

        var outcome = await resource.WriteAsync(target, capability, new(capability.Field, 45), stop.Token);

        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(IntegratedGpuControlStatus.Applied, outcome.Status);
        Assert.True(outcome.ReadbackVerified);
        Assert.Equal(45, intel.Offset);
        Assert.Equal(["waiver:22", "write:22", "read:22"], intel.Calls);
        Assert.Empty(helper.Calls);
    }

    [Fact]
    public async Task CancellationDuringAmdPrewriteReadingDoesNotPersistBaselineOrMutate()
    {
        using var stop = new CancellationTokenSource();
        var helper = new RecordedControlHelper
        {
            ReadingsOverride = () => { stop.Cancel(); return RecordedControlHelper.Readings(gpuCounts: 8); }
        };
        using var resource = Owner(helper, new());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.WriteAsync(Amd(), Co, new(Co.Field, -10), stop.Token));

        Assert.False(File.Exists(Baseline));
        Assert.DoesNotContain(helper.Calls, call => call.Operation.StartsWith("set-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntelConsentReceivesOperationTokenAndCancellationPreventsWaiverAndSetter()
    {
        var helper = new RecordedControlHelper();
        var intel = new RecordedIntegratedIntelHardware();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        CancellationToken received = default;
        using var resource = new IntegratedGpuControlResource(helper, intel, new(Baseline, async token =>
        {
            received = token;
            waiting.TrySetResult();
            return await accepted.Task.WaitAsync(token);
        }));
        var target = new IntegratedGpuControlTarget("gpu:intel", IntegratedGpuControlRoute.IntelIntegrated, 0x800000001);
        var capability = new IntegratedGpuControlCapability(IntegratedGpuControlField.CoreClockOffset, new(-200, 200, 1, "MHz"));
        var write = resource.WriteAsync(target, capability, new(capability.Field, 45), stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stop.Cancel();
            accepted.TrySetResult(true); // Even a consent provider that completes concurrently cannot allow a cancelled write.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.Equal(stop.Token, received);
            Assert.Empty(intel.Calls);
        }
        finally { stop.Cancel(); accepted.TrySetResult(true); await write.ContinueWith(_ => { }); }
    }
    public void Dispose(){if (Directory.Exists(directory))
        {
            // Fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(directory)) Directory.Delete(child);
            Directory.Delete(directory);
        }}
}
