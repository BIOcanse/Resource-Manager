using System.Text.Json;
using FanControlCore;

namespace ResourceManager.App.Infrastructure.Control.Writers;

internal sealed class EmbeddedFanControlCoreSession(string stateDirectory, string? libraryPath) : IFanControlCoreSession
{
    private readonly FanControlSession session = new(new FanCoreOptions(stateDirectory, libraryPath));
    public JsonElement Handle(JsonElement request) => session.Handle(request);
    public void PushTemperatures(IReadOnlyList<FanTemperatureSample> samples) => session.PushTemperatures(samples);
    public IReadOnlyList<string> SoftwareCurveTemperatureKinds => session.SoftwareCurveTemperatureKinds;
    public event Action<IReadOnlyList<string>>? SoftwareCurveTemperatureKindsChanged
    {
        add => session.SoftwareCurveTemperatureKindsChanged += value;
        remove => session.SoftwareCurveTemperatureKindsChanged -= value;
    }
    public bool SoftwareCurveRunNeeded => session.SoftwareCurveRunNeeded;
    public event Action<bool>? SoftwareCurveRunNeededChanged
    {
        add => session.SoftwareCurveRunNeededChanged += value;
        remove => session.SoftwareCurveRunNeededChanged -= value;
    }
    public Task RunSoftwareCurveAsync(CancellationToken stop) => session.RunSoftwareCurveAsync(stop);
    public void SetDiagnosticSink(Action<string> sink) => session.SetDiagnosticSink(sink);
    public string? InFlightOperation => session.InFlightOperation;
    public void Dispose() => session.Dispose();
}
