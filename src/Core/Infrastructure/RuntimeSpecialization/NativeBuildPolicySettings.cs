using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

internal static class NativeBuildPolicySettings
{
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledNativeBuildPolicy ProductDefaults { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static CompiledNativeBuildPolicy Compile(FreedomPointCompilation freedom)
        => freedom.Consume<CompiledNativeBuildPolicy>(BackendFreedomPointPaths.NativeBuildPolicy,
            "NativeBuildPolicySettings.Compile").Validate();
}
