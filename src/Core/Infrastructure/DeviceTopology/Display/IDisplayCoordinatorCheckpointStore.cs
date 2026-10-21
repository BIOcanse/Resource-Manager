namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal interface IDisplayCoordinatorCheckpointStore : IDisposable
{
    NativeDisplayCoordinatorPersistenceImage? Load(ulong expectedConfigurationGeneration);
    void Save(ulong configurationGeneration, NativeDisplayCoordinatorPersistenceImage source);
}
