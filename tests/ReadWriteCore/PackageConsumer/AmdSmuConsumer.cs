using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;

static class AmdSmuConsumer
{
    public static void Run()
    {
        var owner = typeof(ReadWriteCore).Assembly;
        Require(new[] { typeof(AmdSmuCpuSensorReader), typeof(AmdSmuPawnIoSession), typeof(AmdSmuPawnIoOpenOptions),
            typeof(IAmdSmuSession), typeof(AmdSmuSensorIndexResolver), typeof(CpuSensorMetrics) }.All(type => type.Assembly == owner),
            "AMD read and session implementations have independent package ownership");
        var none = new AmdSmuCpuSensorReadRequest(false, false, false, false, false, false, false, false,
            false, false, false, false, false, false, false);
        var selected = none with { IncludeActualPower = true, IncludeTemperature = true, IncludeSmuFrequency = true };
        var session = new RecordedSession(); var opens = 0;
        var reader = new AmdSmuCpuSensorReader(() => { opens++; return session; }, 16, TimeSpan.Zero, TimeProvider.System);
        Require(reader.Read(none).ProviderState.State == "NotRequested" && opens == 0, "construction/empty demand has no driver or WMI access");
        var value = reader.Read(selected);
        Require(value.ActualPowerWatts == 42 && value.PackagePowerWatts is null && value.TemperatureCelsius == 75
            && value.SmuFrequencyMhz == 4250 && value.CoreVoltageVolts is null, "original selected table fields and units");
        var description = reader.DescribeSession();
        Require(description.LayoutMapped && description.TableVersion == "0x00540208" && description.Sample?.Count == 256,
            "original explicit table diagnosis");
        session.Fail = true;
        Require(reader.Read(selected).ProviderState.State == "Unavailable" && session.Disposals == 1, "failed session releases without keeping current values");
        session.Fail = false;
        Require(reader.Read(selected).ActualPowerWatts == 42 && opens == 2, "caller zero interval explicitly allows the next selected read to reopen");
        reader.Dispose(); reader.Dispose();
        Require(session.Disposals == 2, "each selected session is released once");
        try { reader.Read(none); throw new InvalidOperationException("Disposed reader reopened"); }
        catch (ObjectDisposedException) { }
        Require(AmdSmuSensorIndexResolver.Resolve(123) is null, "unknown table versions do not infer a layout");

        var path = Path.Combine(Path.GetTempPath(), "amd-smu-selected-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, [0]);
        try
        {
            try { AmdSmuPawnIoSession.Open(new(path, [], false, TimeSpan.Zero)); throw new InvalidOperationException("Unselected executor used"); }
            catch (AmdSmuProviderUnavailableException) { }
        }
        finally { File.Delete(path); }
        Console.WriteLine("ReadWriteCore AMD SMU consumer passed: original recorded table mapping, selected units, explicit options and no constructor collection, failure/recovery and session release; no driver installation or hardware writes.");
    }

    private static void Require(bool value, string description)
    { if (!value) throw new InvalidOperationException(description); }
    private sealed class RecordedSession : IAmdSmuSession
    {
        public string ExecutorName => "Recorded";
        public string ModulePath => "recorded-module";
        public uint TableVersion => 0x00540208;
        public bool Fail;
        public int Disposals;
        public float[] UpdateAndReadPmTable()
        {
            if (Fail) throw new IOException("Recorded failure");
            var values = new float[1024]; values[3] = 42; values[11] = 75; values[346] = 4.25f;
            return values;
        }
        public void Dispose() => Disposals++;
    }
}
