using ResourceManager.App.Infrastructure.NativeCore;
namespace ResourceManager.App.Infrastructure.DeviceTopology;
public sealed record NativeDisplayCoordinatorPersistenceImage(
    NativeDisplayPersistenceHeader Header,
    NativeDisplayFact[] Facts,
    NativeDisplayTextInput[] Texts,
    byte[] TextBytes,
    IReadOnlyList<NativeDisplayPayloadEntry> Payloads);

