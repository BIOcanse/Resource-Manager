using System.Text.Json;
using System.Runtime.InteropServices;
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
            using(var actualCpu=AmdCpuControlResource.Create(helper,AmdCpuControlOptions.InDirectory(directory))) { }
            using(var actualGpu=IntegratedGpuControlResource.Create(helper,new(baseline,_=>Task.FromResult(false)))) { }
            Require(helper.Disposals==0 && helper.Calls==0,"Factory owns or invokes caller-owned helper.");
            Require(!Directory.Exists(directory),"Actual factories performed IO or started helper.");
            var target=new AmdCpuControlTarget("cpu:package",true);var power=new AmdCpuControlCapability(AmdCpuControlField.PowerLimit,new(0,100,1,"W"));
            var gpuTarget=new IntegratedGpuControlTarget("gpu:A",IntegratedGpuControlRoute.AmdIntegrated);
            var co=new IntegratedGpuControlCapability(IntegratedGpuControlField.CurveOptimizer,new(-30,10,1,"档"));
            using(var cpu=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(directory)))
            using(var gpu=new IntegratedGpuControlResource(helper,intel,new(baseline,_=>Task.FromResult(true))))
            {
                var initial=cpu.Probe(target,power);Require(initial.CanWrite && initial.Range!.DefaultValue is null,"CPU Probe sampled or fabricated a current value.");
                var gpuInitial=gpu.Probe(gpuTarget,co);Require(gpuInitial.CanWrite && gpuInitial.Range!.DefaultValue is null,"iGPU Probe sampled or fabricated a baseline.");
                Require(!Directory.Exists(directory),"Probe captured a restore baseline before an explicit write.");
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
                intel.ReleaseErrorCode = -17;
                var releaseWarnings = new List<Exception?>();
                using var releaseOwner = new IntegratedGpuControlResource(helper, intel, new(baseline, _ => Task.FromResult(true)),
                    warning: (error, code, _) =>
                    {
                        Require(code == IntegratedGpuControlMessage.IntelReleaseWarning, "Release warning reused a rejection code.");
                        releaseWarnings.Add(error);
                    });
                var releasedWrite = await releaseOwner.WriteAsync(intelTarget, offset, new(offset.Field, 55.5));
                Require(releasedWrite.Status == IntegratedGpuControlStatus.Applied && releasedWrite.ReadbackVerified && intel.Offset == 55.5,
                    "Release error hid an applied, verified Intel write.");
                Require(releaseWarnings.Count == 1 && releaseWarnings[0] is ExternalException { ErrorCode: -17 },
                    "Release error was not reported exactly once through the public result contract.");
                var releasedRead = await releaseOwner.ReadAsync(intelTarget, offset);
                Require(releasedRead is { Number: 55.5, UnreadableReason: null } && releaseWarnings.Count == 2,
                    "Release error hid a successful Intel read or escaped the log-only path.");
                intel.IsAvailable = false;
                var missing = releaseOwner.Probe(intelTarget, offset);
                Require(!missing.CanWrite && missing.UnavailableKind == IntegratedGpuControlUnavailableKind.Component,
                    "Missing driver was not a component failure.");
                Require((await releaseOwner.ReadAsync(intelTarget, offset))?.UnreadableReason?.Contains("IntelDriverMissing") == true,
                    "Read DriverMissing mapping was lost.");
                Require((await releaseOwner.WriteAsync(intelTarget, offset, new(offset.Field, 1))).Message?.Contains("IntelDriverMissing") == true,
                    "Write DriverMissing mapping was lost.");
                intel.IsAvailable = true;
                Console.WriteLine("[IGCL-RELEASE-RECEIPT] applied=verified; read=55.5; release=-17; write-warnings=1; read-warnings=1; driver-missing=component; recorded.");
            }
            var occupied=Path.Combine(directory,"occupied");File.WriteAllText(occupied,"file");var warnings=0;
            using(var cpu=new AmdCpuControlResource(helper,AmdCpuControlOptions.InDirectory(occupied),warning:(_,_,_)=>warnings++))
            {
                Require(cpu.Probe(target,power).CanWrite && warnings==0,"CPU Probe attempted to capture a restore baseline.");
                var count=helper.Calls;
                var readings=helper.ReadingReads;
                Require((await cpu.WriteAsync(target,power,new(power.Field,25),default)).Status==AmdCpuControlStatus.Failed && warnings>0,"CPU write proceeded without a durable restore baseline.");
                Require(helper.Calls==count && helper.ReadingReads==readings+1,"CPU persistence failure reached the setter or did not read the current snapshot.");
            }
            using(var gpu=new IntegratedGpuControlResource(helper,intel,new(Path.Combine(occupied,"gpu.json"),_=>Task.FromResult(false))))
            {
                Require(gpu.Probe(gpuTarget,co).CanWrite,"iGPU Probe tried to capture a restore baseline.");
                var count=helper.Calls;
                var readings=helper.ReadingReads;
                Require((await gpu.WriteAsync(gpuTarget,co,new(co.Field,-10),default)).Status==IntegratedGpuControlStatus.Failed,"iGPU write proceeded without a durable restore baseline.");
                Require(helper.Calls==count && helper.ReadingReads==readings+1,"iGPU persistence failure reached the setter or did not read the current snapshot.");
            }
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
        public int ReadingReads;
        public double Power=45,GpuCounts=8;public string? LastUnit,LastWhich,LastGpuUnit;
        public HardwareBridgeReadingsSnapshot? ReadCurrentReadings()
        {
            ReadingReads++;
            var timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            HardwareBridgeReading Reading(double? value,string unit)=>new(value,unit,
                value is null?HardwareBridgeReadingStatus.Missing:HardwareBridgeReadingStatus.Valid,timestamp);
            return new(Reading(Power,"W"),Reading(null,"W"),Reading(null,"W"),Reading(null,"W"),
                Reading(null,"A"),Reading(null,"A"),Reading(null,"档"),Reading(GpuCounts,"档"),Reading(null,"MHz"));
        }
        public IDisposable SubscribeReadings()=>new ReadingSubscription();
        private sealed class ReadingSubscription:IDisposable {public void Dispose() { }}
        public Task<JsonElement?> SendAsync(string operation,IReadOnlyDictionary<string,object?>? arguments,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls++;
            if(operation=="read")throw new InvalidOperationException("Current readings must come from the typed snapshot.");
            if(operation=="set-power-limit"){Power=Convert.ToDouble(arguments!["value"]);LastUnit=(string?)arguments["unit"];LastWhich=(string?)arguments["which"];}
            if(operation=="set-igpu-curve-optimizer"){GpuCounts=Convert.ToDouble(arguments!["value"]);LastGpuUnit=(string?)arguments["unit"];}
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new{ok=true,available=true,powerTable=true,curveOptimizer=true,
                integratedGpuCurveOptimizer=true,accepted=true,status="OK",stapmLimitWatts=Power,integratedGpuCurveOptimizerCounts=GpuCounts}));
        }
        public void Dispose()=>Disposals++;
    }
    private sealed class RecordedIntel : IIntelGraphicsControlHardware
    {
        public bool IsAvailable = true;public List<string> Calls=[];public double Offset=15;
        public int? ReleaseErrorCode;
        public bool IsDriverAvailable => IsAvailable;
        public IntelGraphicsControlProbeResult Probe(ulong? key, CancellationToken token = default)
            => new(!IsAvailable ? IntelGraphicsControlProbe.DriverMissing : FindIntegratedDevice(key) is null ? IntelGraphicsControlProbe.TargetMissing : IntelGraphicsControlProbe.Ready);
        public IntelGraphicsControlRead ReadFrequencyOffsetMhz(ulong? key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAvailable) return new(null, IntelGraphicsControlUnreadable.DriverMissing);
            if (FindIntegratedDevice(key) is not { } device) return new(null, IntelGraphicsControlUnreadable.TargetMissing);
            return ReadFrequencyOffsetMhz(device) is { } value ? new(value, ReleaseErrorCode: ReleaseErrorCode) : new(null, IntelGraphicsControlUnreadable.ReadingMissing, ReleaseErrorCode);
        }
        public IntelGraphicsControlWrite WriteFrequencyOffsetMhz(ulong? key, double value, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAvailable) return new(-1, null, IntelGraphicsControlWriteFailure.DriverMissing);
            if (FindIntegratedDevice(key) is not { } device) return new(-1, null, IntelGraphicsControlWriteFailure.TargetMissing);
            if (!TryAcceptOverclockWaiver(device)) return new(-1, null, IntelGraphicsControlWriteFailure.WaiverRejected);
            token.ThrowIfCancellationRequested();
            var code = WriteFrequencyOffsetMhz(device, value);
            return code != 0 ? new(code, null, IntelGraphicsControlWriteFailure.DriverRejected, ReleaseErrorCode)
                : new(code, ReadFrequencyOffsetMhz(device), ReleaseErrorCode: ReleaseErrorCode);
        }
        private IntPtr? FindIntegratedDevice(ulong? key)=>key==0x800000001 ? new IntPtr(22):null;
        private bool TryAcceptOverclockWaiver(IntPtr device){Calls.Add("waiver:"+device);return true;}
        private double? ReadFrequencyOffsetMhz(IntPtr device){Calls.Add("read:"+device);return Offset;}
        private int WriteFrequencyOffsetMhz(IntPtr device,double value){Calls.Add("write:"+device);Offset=value;return 0;}
    }
}
