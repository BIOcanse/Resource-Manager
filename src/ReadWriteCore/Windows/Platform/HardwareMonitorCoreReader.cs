using LibreHardwareMonitor.Hardware;
using ResourceManager.App.Infrastructure.Monitoring;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Platform;

/// <summary>Owned, demand-driven core-library session. No GUI, network or control calls.</summary>
public sealed class HardwareMonitorCoreReader : IDisposable
{
    public const string ProviderName = "LibreHardwareMonitor Core";
    public const string ProviderIdentity = "librehardwaremonitor.core";
    public const int MaximumNodes = 8192;
    private readonly object gate = new();
    private readonly Func<IHardwareMonitorCoreSession> createSession;
    private IHardwareMonitorCoreSession? computer;
    private string? openError;
    private bool disposed;

    public HardwareMonitorCoreReader() : this(() => new LibrarySession()) { }

    internal HardwareMonitorCoreReader(Func<IHardwareMonitorCoreSession> createSession)
        => this.createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));

    internal static Computer CreateComputer() => new()
    {
        IsMotherboardEnabled = true, IsMemoryEnabled = true,
        IsCpuEnabled = false, IsGpuEnabled = false, IsStorageEnabled = false,
        IsNetworkEnabled = false, IsBatteryEnabled = false, IsControllerEnabled = false,
        IsPsuEnabled = false, IsPowerMonitorEnabled = false
    };

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public HardwareMonitorSensorSnapshot Read(TimeSpan timeout)
    {
        var rows = ReadRows(timeout);
        return HardwareMonitorWmiReader.BuildHardwareMonitorSnapshot(ProviderName,
            new(rows.Sensors, rows.Error), new(rows.Hardware, rows.Error), ProviderIdentity);
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public HardwareMonitorFanIdentityRead ReadFanIdentities(TimeSpan timeout)
    {
        var rows = ReadRows(timeout, skipMemory: true);
        return HardwareMonitorWmiReader.ProjectFanIdentities(ProviderIdentity, new(rows.Sensors, rows.Error));
    }

    private Rows ReadRows(TimeSpan timeout, bool skipMemory = false)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (openError is not null) return new([], [], openError);
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (computer is null)
                {
                    try
                    {
                        var opened = createSession();
                        try { opened.Open(); computer = opened; }
                        catch { opened.Close(); throw; }
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        openError = "lhm-core-" + error.GetType().Name;
                        return new([], [], openError);
                    }
                }
                var sensors = new List<Dictionary<string, object?>>();
                var hardware = new List<Dictionary<string, object?>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var stack = new Stack<IHardware>(computer.Hardware);
                var nodes = 0;
                while (stack.TryPop(out var item))
                {
                    if (started.Elapsed > timeout) throw new TimeoutException("Core sensor collection timed out.");
                    if (++nodes > MaximumNodes || !seen.Add(item.Identifier.ToString()))
                        throw new InvalidDataException("Invalid core hardware tree.");
                    // Fan identity refreshes need the motherboard tree only. LHM's Memory
                    // hardware updates poll SPD over SMBus, so leave that tree untouched here.
                    if (skipMemory && item.HardwareType == HardwareType.Memory)
                        continue;
                    item.Update();
                    hardware.Add(new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Identifier"] = item.Identifier.ToString(), ["Name"] = item.Name,
                        ["HardwareType"] = item.HardwareType.ToString()
                    });
                    foreach (var sensor in item.Sensors)
                    {
                        if (++nodes > MaximumNodes || !seen.Add(sensor.Identifier.ToString()))
                            throw new InvalidDataException("Invalid core sensor identity.");
                        sensors.Add(new(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Identifier"] = sensor.Identifier.ToString(), ["Parent"] = item.Identifier.ToString(),
                            ["Name"] = sensor.Name, ["SensorType"] = sensor.SensorType.ToString(),
                            ["Value"] = sensor.Value is { } value && float.IsFinite(value) ? (double?)value : null
                        });
                    }
                    if (item.SubHardware.Length > MaximumNodes - nodes - stack.Count)
                        throw new InvalidDataException("Core hardware tree exceeds the node limit.");
                    foreach (var child in item.SubHardware) stack.Push(child);
                }
                return new(sensors, hardware, null);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Do not retry here or substitute values from another provider.
                return new([], [], "lhm-core-" + error.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            var owned = computer;
            computer = null;
            owned?.Close();
        }
    }

    private sealed record Rows(IReadOnlyList<Dictionary<string, object?>> Sensors,
        IReadOnlyList<Dictionary<string, object?>> Hardware, string? Error);

    private sealed class LibrarySession : IHardwareMonitorCoreSession
    {
        private readonly Computer computer = CreateComputer();
        public IList<IHardware> Hardware => computer.Hardware;
        public void Open()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PawnIO requires Windows.");
            if (!LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled)
                throw new FileNotFoundException("The explicitly installed PawnIO driver is missing.");
            // A registry entry alone is not access proof. Without this check the
            // library can expose an empty tree, falsely declaring sensors unsupported.
            using var access = File.OpenHandle(@"\\?\GLOBALROOT\Device\PawnIO", FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite);
            computer.Open();
        }
        public void Close() => computer.Close();
    }
}

internal interface IHardwareMonitorCoreSession
{
    IList<IHardware> Hardware { get; }
    void Open();
    void Close();
}
