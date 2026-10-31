using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.ReadWrite.ProcessFields;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceFieldFactsTests
{
    private const string Handles = "process_all_handle_count_actual";
    private const string Path = "process_all_image_path_actual";

    [Fact]
    public void PublishesExactTypedReadingsAndOnlyDeclaredSoftwareAggregates()
    {
        var facts = Create();
        var handles = Assert.Single(facts.Fields, field => field.Field == Handles);
        Assert.Equal("9007199254740993", Assert.Single(handles.Processes).Value.IntegerValue!.Value.ToBigInteger().ToString());
        Assert.Equal(Assert.Single(handles.Processes).Value, Assert.Single(handles.Software).Value);
        Assert.Equal("example", Assert.Single(handles.Software).SoftwareId);
        var path = Assert.Single(facts.Fields, field => field.Field == Path);
        Assert.Equal("C:/example.exe", Assert.Single(path.Processes).Value.StringValue);
        Assert.Empty(path.Software);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(facts, options));
        Assert.Equal("9007199254740993", json.RootElement.GetProperty("fields")[0].GetProperty("processes")[0]
            .GetProperty("value").GetProperty("integerValue").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("fields")[0].GetProperty("processes")[0].GetProperty("limit").ValueKind);
        Assert.DoesNotContain("columns", json.RootElement.GetRawText());
    }

    [Fact]
    public void FailureAndPidReuseCannotRetainAReading()
    {
        foreach (var facts in new[] { Create(failed: true), Create(reused: true) })
        {
            var handles = Assert.Single(facts.Fields, field => field.Field == Handles);
            Assert.All(handles.Processes, process => Assert.Null(process.Value.IntegerValue));
            Assert.All(handles.Software, software => Assert.Null(software.Value.IntegerValue));
        }
    }

    [Fact]
    public void CatalogDeclaresSourcesAndProcessOnlyVersusAggregatedScope()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var definitions = ProcessMetricFieldRegistry.Definitions.ToArray();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(definitions, options));
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var table = item.GetProperty("table");
            Assert.Contains(table.GetProperty("scopes").EnumerateArray(), scope => scope.GetString() == "process");
            var source = Assert.Single(table.GetProperty("sources").EnumerateArray());
            Assert.Equal(item.GetProperty("name").GetString(), source.GetProperty("fieldName").GetString());
            Assert.Equal(item.GetProperty("sourceId").GetString(), source.GetProperty("sourceId").GetString());
        }
        Assert.Contains("software", ProcessMetricFieldRegistry.Find(Handles)!.Table.Scopes);
        Assert.DoesNotContain("software", ProcessMetricFieldRegistry.Find(Path)!.Table.Scopes);
        Assert.DoesNotContain("software", ProcessMetricFieldRegistry.Find("process_all_peak_private_commit_actual")!.Table.Scopes);
    }

    [Fact]
    public void DifferentGpuEngineScopesRemainSeparateInsteadOfInventingATotal()
    {
        var field = ProcessMetricFieldRegistry.Find(WindowsPdhProcessGpuFields.FieldName)!;
        var snapshot = ResourceFactsPublicationTests.Snapshot();
        var source = snapshot with { Bars = [], Datasets = [snapshot.Datasets[0] with { DatasetId = field.DatasetId }],
            ProcessFields = new(snapshot.CapturedAt, null, [field], new uint[] { 0, 1 }.Select(engine =>
                new ProcessMetricFieldValues(42, long.MaxValue.ToString(), [new(field.Name, null, 20 + engine)])
                { GpuEngine = new(1, 0, engine) }).ToArray()) };
        var value = Assert.Single(Assert.Single(ResourceFactsSnapshot.Create(source, [field.Name],
            ResourceFactsPublicationTests.Foundations()).Fields).Processes);
        Assert.Null(value.Value.NumericValue);
        Assert.Equal(2, value.Scopes.Count);
        Assert.Equal(new double?[] { 20, 21 }, value.Scopes.Select(scope => scope.Value.NumericValue));
    }

    private static ResourceFactsSnapshot Create(bool failed = false, bool reused = false)
    {
        var snapshot = ResourceFactsPublicationTests.Snapshot();
        var definitions = new[] { Handles, Path }.Select(name => ProcessMetricFieldRegistry.Find(name)!).ToArray();
        var source = snapshot with { Bars = [], Datasets = [snapshot.Datasets[0] with {
            DatasetId = definitions[0].DatasetId, Status = failed ? "failed" : "ready" }],
            ProcessFields = new(snapshot.CapturedAt, null, definitions,
                [new(42, (reused ? long.MaxValue - 1 : long.MaxValue).ToString(),
                    [new(Handles, MetricInteger.Parse("9007199254740993"), null), new(Path, null, null) { StringValue = "C:/example.exe" }])]) };
        return ResourceFactsSnapshot.Create(source, [Handles, Path], ResourceFactsPublicationTests.Foundations());
    }
}
