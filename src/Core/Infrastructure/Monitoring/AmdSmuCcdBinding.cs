using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>Owns the product module selection until the component inventory changes.</summary>
internal sealed class AmdSmuCcdBinding(
    Func<string?> locateModule,
    Func<string> readComponentRevision,
    Func<AmdCcdCpuTarget, string, AmdCcdTemperatureReader> createReader) : IDisposable
{
    private readonly object gate = new();
    private AmdCcdMetricResource? resource;
    private string? componentRevision;
    private string? modulePath;
    private bool discovered;
    private bool disposed;

    internal AmdCcdTopologyRead Discover(CpuOsTopologyInformation? topology, uint maximum)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RefreshComponent();
            var result = resource!.Discover(topology, maximum);
            discovered = result.Identities.Count > 0;
            return result;
        }
    }

    internal double? Read(AmdCcdCatalogIdentity identity)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RefreshComponent();
            return discovered ? resource!.Read(identity) : null;
        }
    }

    private void RefreshComponent()
    {
        var revision = readComponentRevision();
        if (resource is not null && string.Equals(componentRevision, revision, StringComparison.Ordinal)) return;
        resource?.Dispose();
        resource = null;
        discovered = false;
        modulePath = locateModule();
        resource = new(target => createReader(target, modulePath!), () => modulePath is not null);
        componentRevision = revision;
    }

    internal void ReleaseSession()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            resource?.ReleaseSession();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            resource?.Dispose();
            resource = null;
        }
    }
}
