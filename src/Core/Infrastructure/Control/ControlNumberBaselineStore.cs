using ResourceManager.ReadWrite.Windows.Gpu.Control;
namespace ResourceManager.App.Infrastructure.Control;
/// <summary>Compatibility adapter for original tests; the single bounded baseline implementation is SDK owned.</summary>
internal sealed class ControlNumberBaselineStore(string path)
{
    private readonly IntegratedGpuControlBaselineStore store = new(path);
    internal double? Capture(string targetId,double? current) => store.Capture(targetId,current);
}
