using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Control;

internal static class GpuVfCurveSettings
{
    private const string Address = "resource-manager/backend/hardware_bridge/build/complex/3";
    private const string Consumer = "GpuVfCurveSettings.Compile";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledGpuVfCurveSettings ProductDefaults { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static CompiledGpuVfCurveSettings Compile(FreedomPointCompilation freedom)
        => freedom.Consume<CompiledGpuVfCurveSettings>(Address, Consumer).Validate();
}
