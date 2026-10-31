using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Persistence;

/// <summary>
/// Build-time persistence choices of the service's own stores (freedom-point table persistence). Database maintenance
/// runs before the first runtime plan publication, so it reads <see cref="ProductDefaults"/>; the plan compiler
/// consumes the same declarations through <see cref="Compile"/>.
/// </summary>
internal static class PersistenceSettings
{
    private const string Consumer = "PersistenceSettings.Compile";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledPersistenceSettings ProductDefaults { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static CompiledPersistenceSettings Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        return new(freedom.Consume<SqliteVacuumOptions>(BackendFreedomPointPaths.SqliteVacuum, Consumer).Validate());
    }
}
