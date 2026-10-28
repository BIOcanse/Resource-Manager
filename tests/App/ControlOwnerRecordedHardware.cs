using System.Text.Json;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Intel;
namespace Resource_Manager_APP.Tests;

internal sealed class RecordedControlHelper : IHardwareBridgeProcessClient
{
    public bool IsInstalled { get; set; } = true;
    public string? UnavailableReason => "recorded-unavailable";
    public int Disposals;
    public double Stapm = 45, Slow = 55, Fast = 65, CpuCounts = -5, GpuCounts = 8;
    public List<(string Operation,IReadOnlyDictionary<string,object?>? Arguments)> Calls = [];
    public List<string> Requests = [];
    private JsonElement? description;
    public bool ReuseDescription = true;
    public void Restart() => description = null;
    public Func<string,IReadOnlyDictionary<string,object?>?,JsonElement?>? Override;
    public Func<HardwareBridgeReadingsSnapshot?>? ReadingsOverride;
    public int ReadingReads;
    public HardwareBridgeReadingsSnapshot? ReadCurrentReadings()
    {
        ReadingReads++;
        return ReadingsOverride is { } read ? read() : Readings(Stapm, Slow, Fast, CpuCounts, GpuCounts);
    }
    public IDisposable SubscribeReadings() => new ReadingSubscription();
    private sealed class ReadingSubscription : IDisposable { public void Dispose() { } }
    public static HardwareBridgeReadingsSnapshot Readings(double? stapm = null, double? slow = null,
        double? fast = null, double? cpuCounts = null, double? gpuCounts = null,
        HardwareBridgeReadingStatus emptyStatus = HardwareBridgeReadingStatus.Missing)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HardwareBridgeReading Reading(double? value, string unit) => new(value, unit,
            value is null ? emptyStatus : HardwareBridgeReadingStatus.Valid, timestamp);
        return new(Reading(stapm,"W"), Reading(null,"W"), Reading(fast,"W"), Reading(slow,"W"),
            Reading(null,"A"), Reading(null,"A"), Reading(cpuCounts,"档"), Reading(gpuCounts,"档"), Reading(null,"MHz"));
    }
    public Task<JsonElement?> SendAsync(string operation,IReadOnlyDictionary<string,object?>? arguments,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Calls.Add((operation,arguments));
        if (ReuseDescription && operation == "describe" && description is { } known) return Task.FromResult<JsonElement?>(known);
        Requests.Add(operation);
        var response = Respond(operation, arguments);
        if (operation == "describe") description = response;
        return Task.FromResult(response);
    }
    private JsonElement? Respond(string operation,IReadOnlyDictionary<string,object?>? arguments)
    {
        if (Override is not null) return Override(operation,arguments);
        if (operation == "set-power-limit")
        {
            var value=Convert.ToDouble(arguments!["value"]);
            if ((string?)arguments["which"] == "stapm") Stapm=value;
            else if ((string?)arguments["which"] == "slow") Slow=value; else Fast=value;
        }
        if (operation == "set-curve-optimizer") CpuCounts=Convert.ToDouble(arguments!["value"]);
        if (operation == "set-igpu-curve-optimizer") GpuCounts=Convert.ToDouble(arguments!["value"]);
        return Envelope(new {ok=true,available=true,powerTable=true,curveOptimizer=true,
            integratedGpuCurveOptimizer=true,accepted=true,status="OK",stapmLimitWatts=Stapm,slowLimitWatts=Slow,fastLimitWatts=Fast,
            curveOptimizerCounts=CpuCounts,integratedGpuCurveOptimizerCounts=GpuCounts});
    }
    public static JsonElement Envelope(object value) => JsonSerializer.SerializeToElement(value);
    public void Dispose() => Disposals++;
}
internal sealed class RecordedIntegratedIntelHardware : IIntelGraphicsControlHardware, IDisposable
{
    public bool IsAvailable { get; set; } = true;
    public bool WaiverAccepted = true; public int WriteResult = 0; public double Offset = 15;
    public double? OverrideReadback; public bool UseOverrideReadback;
    public Action? AfterWrite;
    public List<string> Calls = [];
    public IntPtr? FindIntegratedDevice(ulong? key) => key == 0x800000001 ? new IntPtr(22) : null;
    public bool TryAcceptOverclockWaiver(IntPtr device) { Calls.Add("waiver:"+device); return WaiverAccepted; }
    public double? ReadFrequencyOffsetMhz(IntPtr device) { Calls.Add("read:"+device); return UseOverrideReadback ? OverrideReadback : Offset; }
    public int WriteFrequencyOffsetMhz(IntPtr device,double value) { Calls.Add("write:"+device); Offset=value; AfterWrite?.Invoke(); return WriteResult; }
    public int Disposals; public void Dispose()=>Disposals++;
}
