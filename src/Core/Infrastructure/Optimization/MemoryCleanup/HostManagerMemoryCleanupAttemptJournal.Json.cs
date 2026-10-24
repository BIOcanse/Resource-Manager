using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

partial class HostManagerMemoryCleanupAttemptJournal
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerMemoryCleanupAttemptDocument))]
    private partial class HostManagerMemoryCleanupAttemptJournalJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
