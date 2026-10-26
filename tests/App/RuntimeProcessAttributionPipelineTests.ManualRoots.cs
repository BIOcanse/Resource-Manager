using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed partial class RuntimeProcessAttributionPipelineTests
{
    [Fact]
    public void ExplicitManualRootOwnsUnrelatedNamedImagesAtFirstObservation()
    {
        var manual = CreateSoftware("manual:probe", "Unbound frame rate evidence", [@"F:\code\UnboundWebView\evidence", @"F:\code\UnboundWebView\chromium_git\chromium\src\out\Release_GN_x64"]) with
        {
            Kind = SoftwareKinds.HighPerformance, HasExplicitKind = true,
            SourceFacts = [new(SoftwareSourceKinds.Manual)]
        };
        using var pipeline = CreatePipeline([manual]);
        var path = manual.RootPaths[1] + @"\bin\RmWebHost.exe";
        var attribution = pipeline.Match(new RuntimeProcessIdentity(53042, null, "RmWebHost", path, false, StartKey: 10));
        Assert.Equal(manual.Id, attribution.Id);
        Assert.Equal(manual.Kind, attribution.Kind);
        Assert.NotEqual(manual.Id, pipeline.Match(new(53043, null, "RmWebHost", manual.RootPaths[1] + @"-other\RmWebHost.exe", false, StartKey: 11)).Id);
    }
}
