namespace ResourceManager.App.Application.ProcessAttribution;

/// <summary>A short reader reference. The provider owns the catalog; return this lease
/// after the actual attribution or discovery operation, without retaining its pipeline.</summary>
public sealed class RuntimeProcessAttributionCatalogLease : IDisposable
{
    private readonly RuntimeProcessAttributionCatalog catalog;
    private Action? release;

    internal RuntimeProcessAttributionCatalogLease(RuntimeProcessAttributionCatalog catalog, Action release)
    {
        this.catalog = catalog;
        this.release = release;
    }

    public RuntimeProcessAttributionCatalog Catalog
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref release) is null, this);
            return catalog;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
