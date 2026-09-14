using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed class FreedomPointRegistryTests
{
    private const string Address = "test/owner/build/simple/0";
    private const string Source = """
        {
          "namespace": "test",
          "children": [{
            "id": "owner", "description": "Test owner",
            "build": { "simple": [{
              "index": 0, "id": "interval", "description": "Test interval",
              "value_type": "positive_int32", "update_class": "rebuild_backend",
              "status": "active", "value_source": "registry", "value": 5,
              "consumers": ["consumer"]
            }] }
          }]
        }
        """;

    [Fact]
    public void NestedOwnerKeepsItsOwnPointAndItsChildPoints()
    {
        var registry = Parse(root =>
        {
            var owner = root["children"]![0]!;
            var child = owner.DeepClone();
            child["id"] = "child";
            owner["children"] = new JsonArray(child);
        });
        var compilation = registry.BeginCompilation(Profile());
        Assert.Equal(5, compilation.Consume<int>(Address, "consumer"));
        Assert.Equal(5, compilation.Consume<int>("test/owner/child/build/simple/0", "consumer"));
        var tree = compilation.Complete();
        var owner = Assert.Single(tree.Children);
        Assert.Equal(Address, Assert.Single(owner.Build.Simple).Address);
        Assert.Equal("test/owner/child/build/simple/0", Assert.Single(Assert.Single(owner.Children).Build.Simple).Address);
        Assert.Equal(2, tree.EnumeratePoints().Count());
    }

    [Fact]
    public void ActivePointMustBeConsumedByEveryDeclaredCompilerTarget()
    {
        var registry = Parse(root => Point(root)["consumers"] = new JsonArray("first", "second"));
        var compilation = registry.BeginCompilation(Profile());
        Assert.Throws<InvalidDataException>(() => compilation.Complete());
        Assert.Equal(5, compilation.Consume<int>(Address, "first"));
        Assert.Throws<InvalidDataException>(() => compilation.Complete());
        Assert.Equal(5, compilation.Consume<int>(Address, "second"));
        var compiled = Assert.Single(compilation.Complete().EnumeratePoints());
        Assert.Equal(5, compiled.Value!.Value.GetInt32());
    }

    [Fact]
    public void UndeclaredConsumerOrAddressCannotReadAValue()
    {
        var compilation = Parse().BeginCompilation(Profile());
        Assert.Throws<InvalidDataException>(() => compilation.Consume<int>(Address, "another-consumer"));
        Assert.Throws<InvalidDataException>(() => compilation.Consume<int>("test/missing/build/simple/0", "consumer"));
        Assert.Throws<InvalidDataException>(() => compilation.Complete());
    }

    [Fact]
    public void PendingPointIsSearchableButCannotSupplyAValue()
    {
        var registry = Parse(root =>
        {
            var point = Point(root);
            point["status"] = "pending";
            point["pending_reason"] = "Not implemented";
            point.Remove("consumers");
            point.Remove("value_source");
            point.Remove("value");
        });
        var compilation = registry.BeginCompilation(Profile());
        var point = Assert.Single(compilation.Complete().EnumeratePoints());
        Assert.Equal("Not implemented", point.PendingReason);
        Assert.Null(point.Value);
        Assert.Empty(point.Consumers);
        Assert.Throws<InvalidDataException>(() => compilation.Consume<int>(Address, "consumer"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.25")]
    [InlineData("2147483648")]
    [InlineData("\"5\"")]
    [InlineData("null")]
    public void SimpleValueMustSatisfyItsDeclaredType(string literal)
    {
        Assert.Throws<InvalidDataException>(() => Parse(root => Point(root)["value"] = JsonNode.Parse(literal)));
    }

    [Theory]
    [InlineData("index", "2")]
    [InlineData("id", "\"with/slash\"")]
    [InlineData("value_type", "\"opaque\"")]
    [InlineData("value_type", "\"array\"")]
    [InlineData("update_class", "\"publish_plan\"")]
    [InlineData("status", "\"almost_active\"")]
    [InlineData("status", "\"pending\"")]
    [InlineData("consumers", "[]")]
    [InlineData("consumers", "[\"consumer\",\"consumer\"]")]
    [InlineData("consumers", "null")]
    [InlineData("value_source", "\"fallback\"")]
    [InlineData("source_path", "\"/another/value\"")]
    [InlineData("pending_reason", "\"Already active\"")]
    [InlineData("undeclared_field", "true")]
    public void InvalidDeclarationDoesNotReachCompilation(string field, string literal)
    {
        Assert.Throws<InvalidDataException>(() => Parse(root => Point(root)[field] = JsonNode.Parse(literal)));
    }

    [Fact]
    public void NamespaceCannotOwnPointsAndSiblingOwnerIdsCannotCollide()
    {
        Assert.Throws<InvalidDataException>(() => Parse(root => root["build"] = new JsonObject()));
        Assert.Throws<InvalidDataException>(() => Parse(root => root["children"]!.AsArray().Add(root["children"]![0]!.DeepClone())));
        Assert.Throws<InvalidDataException>(() => Parse(root => root["children"] = null));
    }

    [Fact]
    public void DuplicatePropertiesAreRejectedBeforeDeserialization()
    {
        var duplicate = Source.Replace("\"value\": 5", "\"value\": 5, \"value\": 9", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => FreedomPointRegistry.Parse(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Fact]
    public void MissingProfileFieldHasNoRegistryFallback()
    {
        var registry = Parse(root =>
        {
            var point = Point(root);
            point["value_source"] = "host_manager_profile";
            point["source_path"] = "/hot_publish/missing";
            point.Remove("value");
        });
        var compilation = registry.BeginCompilation(Profile());
        Assert.Throws<InvalidDataException>(() => compilation.Consume<int>(Address, "consumer"));
        Assert.Throws<InvalidDataException>(() => compilation.Complete());
    }

    private static FreedomPointRegistry Parse(Action<JsonObject>? edit = null)
    {
        var root = JsonNode.Parse(Source)!.AsObject();
        edit?.Invoke(root);
        return FreedomPointRegistry.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static JsonObject Point(JsonObject root) => root["children"]![0]!["build"]!["simple"]![0]!.AsObject();

    private static HostManagerConfigurationProfile Profile()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return StrictHostManagerProfileLoader.LoadBytes(bytes.ToArray(), "test/default.json").Profile;
    }
}
