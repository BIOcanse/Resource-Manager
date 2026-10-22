using System.Text.Json;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Gpu.Control;

internal static class AmdCpuIntegratedGpuControlOwnerConsumer
{
    // Recorded lower seam only. Factories are constructed and disposed without probing or starting a helper.
    internal static async Task RunAsync()
    {
        var directory=Path.Combine(Path.GetTempPath(),"rm-cpu-igpu-consumer-"+Guid.NewGuid());
        var baseline=Path.Combine(directory,"igpu.json");var helper=new RecordedHelper();var intel=new RecordedIntel();
        try
        {
            var helperOptions=new HardwareBridgeProcessOptions(TimeSpan.FromSeconds(90),TimeSpan.FromSeconds(1),new(128,1024,1024,".NET","10","missing","failed"));
            using(var actualCpu=AmdCpuControlResource.Create(Path.Combine(directory,"missing.exe"),helperOptions,AmdCpuControlOptions.InDirectory(directory))) { }
            using(var actualGpu=IntegratedGpuControlResource.Create(Path.Combine(directory,"missing.exe"),helperOptions,new(baseline,_=>Task.FromResult(false)))) { }
            Require(!Directory.Exists(directory),"Actual factories performed IO or started helper.");
            var target=new AmdCpuControlTarget("cpu:package",true);var power=new AmdCpuControlCapability(AmdCpuControlField.PowerLimit,new(0,100,1,"W"));
            var gpuTarget=new IntegratedGpuControlTarget("gpu:A",IntegratedGpuControlRoute.AmdIntegrated);
            var co=new IntegratedGpuControlCapability(IntegratedGpuControlField.CurveOptimizer,new(-30,10,1,"档"));
            using(var cpu=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory)))
            using(var gpu=new IntegratedGpuControlResource(helper,intel,new(baseline,_=>Task.FromResult(true))))
            {
                var initial=cpu.Probe(target,power);Require(initial.CanWrite && initial.Range!.DefaultValue==45,"CPU baseline/limit contract changed.");
                var gpuInitial=gpu.Probe(gpuTarget,co);Require(gpuInitial.CanWrite && gpuInitial.Range!.DefaultValue==8,"iGPU target baseline missing.");
                Require((await cpu.WriteAsync(target,power,new(power.Field,20),default)).Status==AmdCpuControlStatus.Applied,"Recorded CPU setting failed.");
                Require(helper.LastUnit=="W" && helper.LastWhich=="stapm","CPU unit/which was not transmitted.");
                Require((await gpu.WriteAsync(gpuTarget,co,new(co.Field,-20),default)).Status==IntegratedGpuControlStatus.Applied,"Recorded iGPU setting failed.");
                Require(helper.Power==20 && helper.GpuCounts==-20,"Shared transport mixed independent fields.");
                Require(helper.LastGpuUnit=="档","iGPU unit was not transmitted.");
                var count=helper.Calls;
                Require((await cpu.WriteAsync(target,new(AmdCpuControlField.TdcLimit),new(AmdCpuControlField.TdcLimit,50),default)).Status==AmdCpuControlStatus.Failed && helper.Calls==count,"Unconfirmed current mapping was sent.");
            }
            Require(helper.Disposals==0,"Borrowed helper was terminated.");
            using(var cpu=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory)))
            using(var gpu=new IntegratedGpuControlResource(helper,intel,new(baseline,_=>Task.FromResult(true))))
            {
                Require(await cpu.RestoreAsync(target,power with{Range=cpu.Probe(target,power).Range}) && helper.Power==45,"CPU restart restoration lost first value.");
                Require(await gpu.RestoreAsync(gpuTarget,co with{Range=gpu.Probe(gpuTarget,co).Range}) && helper.GpuCounts==8,"iGPU restart restoration lost first target value.");
                var intelTarget=new IntegratedGpuControlTarget("gpu:intel",IntegratedGpuControlRoute.IntelIntegrated,0x800000001);
                var offset=new IntegratedGpuControlCapability(IntegratedGpuControlField.CoreClockOffset,new(-200,200,1,"MHz"));
                Require(gpu.Probe(intelTarget,offset).CanWrite && intel.Calls.Count==0,"Intel Probe submitted waiver or setting.");
                Require((await gpu.WriteAsync(intelTarget,offset,new(offset.Field,45.5),default)).Status==IntegratedGpuControlStatus.Applied,"Intel fractional MHz failed.");
                Require(intel.Calls.SequenceEqual(new[]{"waiver:22","write:22","read:22"}),"Selected Intel device/waiver/write/read order changed.");
            }
            var occupied=Path.Combine(directory,"occupied");File.WriteAllText(occupied,"file");var warnings=0;
            using(var cpu=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(occupied),warning:(_,_,_)=>warnings++))
            {
                Require(cpu.Probe(target,power).CanWrite && warnings>0,"CPU disk failure incorrectly became strict write gate.");
                Require((await cpu.WriteAsync(target,power,new(power.Field,25),default)).Status==AmdCpuControlStatus.Applied,"CPU memory baseline/disk failure contract changed.");
            }
            using(var gpu=new IntegratedGpuControlResource(helper,intel,new(Path.Combine(occupied,"gpu.json"),_=>Task.FromResult(false))))
                Require(gpu.Probe(gpuTarget,co) is{CanWrite:false,CanRead:true},"iGPU strict persistence admission changed.");
            Require(helper.Disposals==0,"Shared helper disposed by resource.");
            Console.WriteLine("AMD CPU and integrated GPU control owners passed recorded shared-transport/table/unit/baseline/restart/unsupported and Intel identity/waiver/fractional MHz contracts; actual factories passive, no helper process/driver or hardware setter executed.");
        }
        finally {helper.Dispose();if (Directory.Exists(directory))
        {
            // Fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(directory)) Directory.Delete(child);
            Directory.Delete(directory);
        }}
    }
    private static void Require(bool ok,string error){if(!ok)throw new InvalidDataException(error);}
    private sealed class RecordedHelper : IHardwareBridgeProcessClient
    {
        public bool IsInstalled=>true;public string? UnavailableReason=>null;public int Calls,Disposals;
        public double Power=45,GpuCounts=8;public string? LastUnit,LastWhich,LastGpuUnit;
        public Task<JsonElement?> SendAsync(string operation,IReadOnlyDictionary<string,object?>? arguments,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls++;
            if(operation=="set-power-limit"){Power=Convert.ToDouble(arguments!["value"]);LastUnit=(string?)arguments["unit"];LastWhich=(string?)arguments["which"];}
            if(operation=="set-igpu-curve-optimizer"){GpuCounts=Convert.ToDouble(arguments!["value"]);LastGpuUnit=(string?)arguments["unit"];}
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new{ok=true,available=true,powerTable=true,curveOptimizer=true,
                integratedGpuCurveOptimizer=true,accepted=true,status="OK",stapmLimitWatts=Power,integratedGpuCurveOptimizerCounts=GpuCounts}));
        }
        public void Dispose()=>Disposals++;
    }
    private sealed class RecordedIntel : IIntelGraphicsControlHardware
    {
        public bool IsAvailable=>true;public List<string> Calls=[];public double Offset=15;
        public IntPtr? FindIntegratedDevice(ulong? key)=>key==0x800000001 ? new IntPtr(22):null;
        public bool TryAcceptOverclockWaiver(IntPtr device){Calls.Add("waiver:"+device);return true;}
        public double? ReadFrequencyOffsetMhz(IntPtr device){Calls.Add("read:"+device);return Offset;}
        public int WriteFrequencyOffsetMhz(IntPtr device,double value){Calls.Add("write:"+device);Offset=value;return 0;}
    }
}
