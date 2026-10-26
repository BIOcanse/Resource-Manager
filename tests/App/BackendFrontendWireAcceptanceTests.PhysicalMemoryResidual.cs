using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class BackendFrontendWireAcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasuredPhysicalResidualProductionBuildersFeedAllProductionTypeScriptDecoders(bool privateField)
    {
        var scores = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
        var attribution = new WindowsResourceBreakdownSampler.ProcessAttributionSnapshot([], new Dictionary<int,
            WindowsResourceBreakdownSampler.ProcessResourceSample>(), new Dictionary<int,
            ResourceManager.App.Domain.ProcessAttribution.RuntimeSoftwareAttribution>(), scores, []);
        var provider = PhysicalMemoryResidualBreakdownProviderTests.Provider(PhysicalMemoryResidualBreakdownProviderTests.Fixture());
        var bar = WindowsResourceBreakdownSampler.CreateBar("memory.usage", "B", "capacity", 2000, 1000,
            [], attribution, scores, provider, isBytes: true);
        var metricId = privateField ? "process_all_private_working_set_actual" : "memory.usage";
        var snapshot = new ResourceBreakdownSnapshot(CapturedAt, privateField ? [] : [bar])
        {
            Datasets = [new(SamplingDatasetIds.ForProcessMetric(metricId), "ready", 1, 1,
                CapturedAt, CapturedAt, CapturedAt, CapturedAt.AddSeconds(5), null, null)],
            ProcessMemoryFacts = privateField ? [new(metricId, "memory.usage", 1000, 2000, 0, []) { Residuals = bar.Software }] : []
        };
        string[] ids = [metricId];
        // The bar holds only the residual, so the request selects it by its total metric and no software.
        var noSoftware = new HashSet<string>();
        var residuals = new HashSet<string>(["memory.usage"]);
        var wire = ResourceBreakdownWireSnapshot.Create(snapshot, ids, noSoftware, residualDetailMetricIds: residuals);
        object[] payloads = [new ResourceMonitorWireSnapshot(ResourceMonitorWireSnapshot.CurrentVersion, wire.CapturedAt, wire),
            ResourceFactsSnapshot.Create(snapshot, ids),
            ResourceProcessDetailsWireSnapshot.Create(snapshot, ids, noSoftware, residualDetailMetricIds: residuals)];
        string[] kinds = ["monitor", "facts", "details"];
        var expected = bar.Software.SelectMany(software => software.Processes).ToArray();
        for (var index = 0; index < kinds.Length; index++)
        {
            var decoded = await DecodePhysicalResidual(kinds[index], payloads[index]);
            var rows = decoded.EnumerateArray().ToArray();
            Assert.Equal(expected.Select(row => row.AttributionKind), rows.Select(row => row.GetProperty("attributionKind").GetString()));
            Assert.Equal(expected.Select(row => row.Value), rows.Select(row => row.GetProperty("value").GetDouble()));
            Assert.Equal(expected.Select(row => row.Name), rows.Select(row => row.GetProperty("name").GetString()));
            Assert.All(rows, row => { Assert.Equal(-1, row.GetProperty("processId").GetInt32()); Assert.Equal(JsonValueKind.Null, row.GetProperty("processStartKey").ValueKind); });
        }
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("negative-private")]
    [InlineData("unavailable")]
    public async Task PfnDetailsFrameProductionFactoryFeedsProductionDecoder(string scenario)
    {
        var frame = scenario == "unavailable"
            ? PhysicalMemoryResidualFrame.Unavailable(PhysicalMemoryPfnTests.At,"profile-privilege-unavailable")
            : PhysicalMemoryPfnTests.Frame(scenario == "negative-private");
        var source = new ResourceBreakdownSnapshot(PhysicalMemoryPfnTests.At, []) { PhysicalMemoryResidual = frame };
        var wire = ResourceProcessDetailsWireSnapshot.Create(source,["memory.usage"],
            new HashSet<string>(), residualDetailMetricIds: new HashSet<string>(["memory.usage"]));
        var decoded = await DecodePhysicalResidual("pfn-details",wire);
        using var expected = JsonDocument.Parse(JsonSerializer.Serialize(frame,WebJson));
        Assert.True(JsonElement.DeepEquals(expected.RootElement,decoded));
    }

    // Tests the production decoders directly. Naming/localization is owned by the frontend session;
    // this test deliberately does not run the existing display-name harness for new backend kinds.
    private static async Task<JsonElement> DecodePhysicalResidual(string kind, object payload)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = Path.Combine(TestRepositoryPaths.FindRoot(), "src", "UI", "Frontend"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] {"--experimental-transform-types", "--experimental-strip-types", "--conditions=browser", "--input-type=module", "--eval",
            "import {readFileSync} from 'node:fs'; import {decodeResourceMonitorWireSnapshot as monitor, decodeResourceProcessDetailsWireSnapshot as details} from './src/api/resourceBreakdownWire.ts'; const r=JSON.parse(readFileSync(0,'utf8')); const d=(r.kind==='details'||r.kind==='pfn-details')?details(r.payload):monitor(r.payload); const metrics=(r.kind==='details'||r.kind==='pfn-details')?d.metrics:r.kind==='facts'?d.facts.metrics:d.breakdown.bars; process.stdout.write(JSON.stringify(r.kind==='pfn-details'?d.physicalMemoryResidual:metrics.flatMap(m=>m.software.flatMap(s=>s.processes))));"})
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new {kind, payload}, WebJson));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        Assert.True(process.ExitCode == 0, await error);
        using var json = JsonDocument.Parse(await output);
        return json.RootElement.Clone();
    }
}
