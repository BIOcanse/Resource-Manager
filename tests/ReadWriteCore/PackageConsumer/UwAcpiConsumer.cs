using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Vendors.Mechrevo;
using ResourceManager.ReadWrite.Windows.Platform;

internal static class UwAcpiConsumer
{
    internal static void Run()
    {
        Require(typeof(MechrevoUwAcpiReader).Assembly == typeof(ReadWriteCore).Assembly,
            "UWACPI implementation belongs to the independent package");
        var opens = 0;
        var transport = new RecordedSession();
        var reader = new MechrevoUwAcpiReader("explicit-recorded-path.dll", path =>
        {
            Require(path == "explicit-recorded-path.dll", "caller supplies the library path");
            opens++;
            return transport;
        });
        try
        {
            Require(opens == 0 && transport.Reads == 0, "construction does not open or read");
            var value = reader.Read();
            Require(value.Status == MechrevoUwAcpiReadStatus.Active && value.CpuFanSpeedRpm == 2400
                && value.GpuFanSpeedRpm == 3200 && value.CpuFanSpeedPercent == 39
                && value.GpuFanSpeedPercent == 100, "original addresses, RPM and fixed PWM scale");
            var gpuFan = new PlatformGpuSensor(0, "dGPU fan", "recorded", null, null,
                value.GpuFanSpeedRpm, value.GpuFanSpeedPercent, null, null);
            var projected = WindowsNotebookFanProjection.Create(new MechrevoUwAcpiSensorSnapshot(
                new HardwareSensorProviderState("Mechrevo UWACPI", "Active", "recorded"),
                value.CpuFanSpeedRpm, value.CpuFanSpeedPercent, gpuFan));
            Require(projected.PhysicalFans.Count == 2
                && projected.PhysicalFans[0] == new NotebookOemPhysicalFanReading(
                    WindowsNotebookFanProjection.CpuFanIdentityKey, NotebookOemPhysicalFanRole.Cpu, 2400, 39)
                && projected.PhysicalFans[1] == new NotebookOemPhysicalFanReading(
                    WindowsNotebookFanProjection.DiscreteGpuFanIdentityKey, NotebookOemPhysicalFanRole.DiscreteGpu, 3200, 100)
                && transport.Reads == 6, "two physical role projections reuse the single six-address read");
            Require(reader.Read() == value && opens == 1 && transport.Reads == 12,
                "explicit repeated reads share one owned session");
        }
        finally { reader.Dispose(); }
        reader.Dispose();
        Require(transport.Releases == 1, "exactly one transport release");
        try { reader.Read(); throw new InvalidDataException("Disposed reader reopened."); }
        catch (ObjectDisposedException) { }
        Require(opens == 1, "released reader cannot reopen");
        Console.WriteLine("UWACPI package consumer passed: recorded transport, explicit path and release; no hardware access.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class RecordedSession : IUwAcpiReadSession
    {
        internal int Reads;
        internal int Releases;
        public int ReadEc(int address)
        {
            Require(Releases == 0, "session is still loaded during reads");
            Reads++;
            return address switch
            {
                1124 => 9, 1125 => 96, 1132 => 12, 1131 => 128, 1883 => 78, 1884 => 200,
                _ => throw new InvalidDataException("Unexpected EC address.")
            };
        }
        public void Dispose() => Releases++;
    }
}
