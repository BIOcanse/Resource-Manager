using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace Resource_Manager_APP.Tests;

public sealed class AmdSmuMonitoringZoneLifetimeTests
{
    private static readonly AmdSmuCpuSensorReadRequest Selected = new(false, false, false, true,
        false, false, false, false, false, false, false, false, false, false, false);

    [Fact]
    public void RepeatedReadsRetainOwnedSessionsAndPreserveDiscoveredCcdIdentities()
    {
        var pmSessions = new List<PmSession>();
        var smnSessions = new List<SmnSession>();
        var resolves = 0;
        using var zone = new AmdSmuMonitoringZone(
            new AmdSmuCpuSensorReader(() => { var session = new PmSession(); pmSessions.Add(session); return session; },
                2, TimeSpan.FromSeconds(10), TimeProvider.System),
            new AmdSmuCcdBinding(() => { resolves++; return "recorded.bin"; }, static () => "0",
                (target, _) => new AmdCcdTemperatureReader(target.Family, target.Model, 1,
                    () => { var session = new SmnSession(); smnSessions.Add(session); return session; })));
        var identities = zone.ReadCcdIdentities(Topology(), 16).Identities;
        var identity = Assert.Single(identities);
        var requested = new HashSet<string> { identity.ExactIdentityKey };

        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.Equal(75, zone.Read(Selected).TemperatureCelsius);
            Assert.Equal(51, zone.ReadCcd(identities, requested)[identity.ExactIdentityKey]);
            Assert.Equal(75, zone.Read(Selected).TemperatureCelsius);
            Assert.Single(pmSessions);
            Assert.Single(smnSessions);
            Assert.All(pmSessions, session => Assert.Equal(0, session.Disposals));
            Assert.All(smnSessions, session => Assert.Equal(0, session.Disposals));
        }
        Assert.Equal(1, resolves);
        zone.Dispose();
        Assert.All(pmSessions, session => Assert.Equal(1, session.Disposals));
        Assert.All(smnSessions, session => Assert.Equal(1, session.Disposals));
    }

    [Fact]
    public void ReleasingFailedReaderDoesNotBypassItsFailureCache()
    {
        var opens = 0;
        using var reader = new AmdSmuCpuSensorReader(() =>
        {
            opens++;
            throw new AmdSmuProviderUnavailableException("recorded unavailable");
        }, 2, TimeSpan.FromHours(1), TimeProvider.System);
        Assert.Equal(HardwareSensorProviderStates.Unavailable, reader.Read(Selected).ProviderState.State);
        reader.ReleaseSession();
        Assert.Equal(HardwareSensorProviderStates.Unavailable, reader.Read(Selected).ProviderState.State);
        Assert.Equal(1, opens);
    }

    private static CpuOsTopologyInformation Topology()
    {
        var data = new CpuThreadCpuidData(new("AuthenticAMD", 0x19, 0x61, 1, "recorded AMD", [], []),
            0xA60F12, null, null, null, null, null, []);
        return new([], []) { Packages = [new("package-opaque", [0, 1])],
            ThreadCpuid = [new(0, data), new(1, data)] };
    }

    private sealed class PmSession : IAmdSmuSession
    {
        public string ExecutorName => "Recorded";
        public string ModulePath => "recorded.bin";
        public uint TableVersion => 0x00540208;
        internal int Disposals;
        public float[] UpdateAndReadPmTable()
        {
            Assert.Equal(0, Disposals);
            var table = new float[1024];
            table[11] = 75;
            return table;
        }
        public void Dispose() => Disposals++;
    }

    private sealed class SmnSession : IAmdSmnSession
    {
        internal int Disposals;
        public uint ReadRegister(uint address)
        {
            Assert.Equal(0, Disposals);
            return address == 0x59B08 ? 0xB20U : 0;
        }
        public void Dispose() => Disposals++;
    }
}
