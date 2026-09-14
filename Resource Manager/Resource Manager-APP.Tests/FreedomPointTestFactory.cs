using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

internal static class FreedomPointTestFactory
{
    public static FreedomPointRegistry Load(Action<JsonObject> edit)
    {
        using var stream = typeof(FreedomPointRegistry).Assembly.GetManifestResourceStream(FreedomPointRegistry.ManifestResourceName)!;
        var root = JsonNode.Parse(stream)!.AsObject();
        edit(root);
        return FreedomPointRegistry.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    public static JsonObject Point(JsonObject root, string address)
    {
        var segments = address.Split('/');
        Assert.Equal(root["namespace"]!.GetValue<string>(), segments[0]);
        var node = root;
        for (var index = 1; index < segments.Length - 3; index++)
        {
            node = node["children"]!.AsArray().Single(child => child!["id"]!.GetValue<string>() == segments[index])!.AsObject();
        }
        return node[segments[^3]]![segments[^2]]![int.Parse(segments[^1], CultureInfo.InvariantCulture)]!.AsObject();
    }
}
