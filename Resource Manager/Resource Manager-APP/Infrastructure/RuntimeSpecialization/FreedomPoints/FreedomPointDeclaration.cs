using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FreedomPointRegistryDeclaration
{
    [JsonPropertyName("namespace")]
    public required string Namespace { get; init; }

    [JsonPropertyName("children")]
    public required FreedomPointTableDeclaration[] Children { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FreedomPointTableDeclaration
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("build")]
    public FreedomPointFamilyDeclaration? Build { get; init; }

    [JsonPropertyName("runtime")]
    public FreedomPointFamilyDeclaration? Runtime { get; init; }

    [JsonPropertyName("children")]
    public FreedomPointTableDeclaration[] Children { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FreedomPointFamilyDeclaration
{
    [JsonPropertyName("simple")]
    public FreedomPointDeclaration[] Simple { get; init; } = [];

    [JsonPropertyName("complex")]
    public FreedomPointDeclaration[] Complex { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FreedomPointDeclaration
{
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("value_type")]
    public required string ValueType { get; init; }

    [JsonPropertyName("update_class")]
    public required string UpdateClass { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("pending_reason")]
    public string? PendingReason { get; init; }

    [JsonPropertyName("value_source")]
    public string? ValueSource { get; init; }

    [JsonPropertyName("source_path")]
    public string? SourcePath { get; init; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }

    [JsonPropertyName("consumers")]
    public string[] Consumers { get; init; } = [];
}
