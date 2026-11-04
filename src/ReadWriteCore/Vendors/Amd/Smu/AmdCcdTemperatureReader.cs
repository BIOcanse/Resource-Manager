using System;
using System.Collections.Generic;

namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

/// <summary>A selected, read-only SMN session. Each implementation must hold the
/// shared PCI lock for the index/data pair. The reader owns disposal.</summary>
public interface IAmdSmnSession : IDisposable
{
    uint ReadRegister(uint address);
}

public readonly record struct AmdCcdTemperatureSample(
    int SensorIndex, uint SmnAddress, double? TemperatureCelsius);

/// <summary>One explicitly selected single-package AMD source. Construction has
/// no IO; discovering or reading opens only its selected module. No last-good cache.</summary>
public sealed class AmdCcdTemperatureReader : IDisposable
{
    private readonly object gate = new();
    private readonly AmdCcdTemperatureFormat? format;
    private readonly Func<IAmdSmnSession> openSession;
    private IAmdSmnSession? session;
    private bool disposed;

    public AmdCcdTemperatureReader(uint family, uint model, int physicalPackageCount,
        Func<IAmdSmnSession> openSession)
    {
        ArgumentNullException.ThrowIfNull(openSession);
        // The signed module currently addresses PCI 0:0:0 only. No multi-socket fallback.
        format = physicalPackageCount == 1 ? AmdCcdTemperatureFormat.Resolve(family, model) : null;
        this.openSession = openSession;
    }

    public bool Supported => format is not null;

    /// <summary>Enumerates actual valid sensor slots; indices remain sparse.
    /// Errors propagate to the existing catalog owner, never masquerading as an empty directory.</summary>
    public IReadOnlyList<AmdCcdTemperatureSample> Discover()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (format is null) return Array.Empty<AmdCcdTemperatureSample>();
            var result = new List<AmdCcdTemperatureSample>(format.MaximumSensorCount);
            for (var index = 0; index < format.MaximumSensorCount; index++)
            {
                var value = ReadCore(index);
                if (value.TemperatureCelsius is not null) result.Add(value);
            }
            return result.AsReadOnly();
        }
    }

    /// <summary>Reads one requested slot, never borrowing a package temperature.</summary>
    public AmdCcdTemperatureSample Read(int sensorIndex)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (format is null) throw new NotSupportedException("No single-package AMD CCD SMN layout is available.");
            return ReadCore(sensorIndex);
        }
    }

    private AmdCcdTemperatureSample ReadCore(int sensorIndex)
    {
        var address = format!.RegisterAddress(sensorIndex);
        session ??= openSession();
        try
        {
            return new(sensorIndex, address, AmdCcdTemperatureFormat.Decode(session.ReadRegister(address)));
        }
        catch
        {
            var failed = session;
            session = null;
            failed.Dispose();
            throw;
        }
    }

    /// <summary>Releases the native session; the selected CPU layout remains available for subsequent reads.</summary>
    public void ReleaseSession()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            session?.Dispose();
            session = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            var owned = session;
            session = null;
            owned?.Dispose();
        }
    }
}
