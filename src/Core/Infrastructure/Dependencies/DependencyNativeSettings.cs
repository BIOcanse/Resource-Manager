using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Dependencies;

internal static class DependencyNativeSettings
{
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static uint MaximumWorkspaceBytes { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static uint Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        var value = freedom.Consume<int>(BackendFreedomPointPaths.DependencyOwnerMaximumBytes, "DependencyNativeSettings.Compile");
        if (value <= 0) throw new InvalidDataException("The dependency owner workspace limit must be a positive integer.");
        return checked((uint)value);
    }
}
