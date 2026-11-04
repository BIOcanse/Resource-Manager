using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Security;

internal static class LoopbackTcpSettings
{
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static uint MaximumTableBytes { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static uint Compile(FreedomPointCompilation freedom)
    {
        var value = freedom.Consume<int>(BackendFreedomPointPaths.LoopbackTcpMaximumBytes, "LoopbackTcpSettings.Compile");
        if (value < sizeof(uint)) throw new InvalidDataException("The TCP owner table limit must fit its count header.");
        return checked((uint)value);
    }
}
