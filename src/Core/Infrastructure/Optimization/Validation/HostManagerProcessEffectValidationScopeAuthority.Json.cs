using static ResourceManager.App.Infrastructure.Optimization.ProcessEffectValidationScopeDocuments;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Optimization;

partial class HostManagerProcessEffectValidationScopeAuthority
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerProcessEffectValidationAuthorityManifest))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerProcessEffectValidationScopeDocument))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerProcessEffectValidationAuditFenceDocument))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerProcessEffectValidationDurableHandoffDocument))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerProcessEffectValidationCommittedHandoffDocument))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(HostManagerProcessEffectValidationScopeAuditDocument))]
    private partial class HostManagerProcessEffectValidationScopeAuthorityJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
