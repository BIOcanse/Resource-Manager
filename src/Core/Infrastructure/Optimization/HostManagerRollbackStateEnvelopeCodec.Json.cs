using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

partial class HostManagerRollbackStateEnvelopeCodec
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerRollbackStateDocument))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerCoordinatorFacts))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerRollbackStateEnvelope))]
    private partial class HostManagerRollbackStateEnvelopeCodecJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
