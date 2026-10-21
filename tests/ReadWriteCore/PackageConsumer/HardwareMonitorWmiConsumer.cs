using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.App.Infrastructure.Monitoring;

internal static class HardwareMonitorWmiConsumer
{
    internal static void Run()
    {
        Require(typeof(HardwareMonitorWmiReader).Assembly == typeof(ReadWriteCore).Assembly
            && typeof(SystemFanSensor).Assembly == typeof(ReadWriteCore).Assembly, "reader/shared models are independent package types");
        var calls = 0;
        var timeout = TimeSpan.FromMilliseconds(91);
        var reader = new HardwareMonitorWmiReader((path, query, suppliedTimeout) =>
        {
            calls++;
            Require(path == @"root\LibreHardwareMonitor" && suppliedTimeout == timeout, "explicit source/timeout retained");
            return query.EndsWith("FROM Hardware", StringComparison.Ordinal) ? new([], null)
                : new([new Dictionary<string, object?> { ["Identifier"] = "/case/fan", ["Parent"] = "/lpc/0", ["Name"] = "Case fan", ["SensorType"] = "Fan", ["Value"] = 1450d }], null);
        });
        Require(calls == 0, "construction does not query");
        var first = reader.Read(timeout);
        Require(first.CpuFanSpeedRpm == 1450 && first.SystemFans.Single().SpeedRpm == 1450
            && first.ProviderState.Message is null && first.SystemFans.Single().Name == "Case fan", "single fan fallback/raw output retained");
        reader.Read(timeout);
        Require(calls == 4, "explicit reads bypass caching");
        var identities = reader.ReadFanIdentities(timeout);
        Require(identities.Complete && identities.Identities.Single().IdentityKey == first.SystemFans.Single().IdentityKey,
            "topology/read fan identity equality");
        Console.WriteLine("HardwareMonitor WMI package consumer passed: recorded rows, explicit timeout and raw shared models; no hardware access.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
