using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class AdapterRegistrationValidatorTests
{
    [Fact]
    public void Validate_AcceptsLoopbackResourceMarkerEndpoint()
    {
        var request = new AdapterSoftwareRegistrationRequest(
            AdapterRegistrationSchemaVersions.Current,
            "word-memory.adapter",
            "word-memory",
            "背单词",
            null,
            [@"D:\Apps\WordMemory"],
            [],
            [],
            new AdapterResourceMarkerEndpoint(
                AdapterResourceMarkerTransports.LoopbackHttp,
                "http://127.0.0.1:9322/resource-ledger"));

        var validation = AdapterRegistrationValidator.Validate(request);

        Assert.Null(validation);
    }

    [Fact]
    public void Validate_RejectsNonLoopbackResourceMarkerEndpoint()
    {
        var request = new AdapterSoftwareRegistrationRequest(
            AdapterRegistrationSchemaVersions.Current,
            "word-memory.adapter",
            "word-memory",
            "背单词",
            null,
            [@"D:\Apps\WordMemory"],
            [],
            [],
            new AdapterResourceMarkerEndpoint(
                AdapterResourceMarkerTransports.LoopbackHttp,
                "https://example.com/resource-ledger"));

        var validation = AdapterRegistrationValidator.Validate(request);

        Assert.Equal("resourceMarkerEndpoint.address must point to loopback.", validation);
    }

    [Fact]
    public void Validate_RejectsMissingResourceMarkerEndpoint()
    {
        var request = new AdapterSoftwareRegistrationRequest(
            AdapterRegistrationSchemaVersions.Current,
            "word-memory.adapter",
            "word-memory",
            "背单词",
            null,
            [@"D:\Apps\WordMemory"],
            [],
            [],
            null);

        var validation = AdapterRegistrationValidator.Validate(request);

        Assert.Equal("resourceMarkerEndpoint is required.", validation);
    }

    [Fact]
    public void Validate_IgnoresRetiredSchedulingCapabilitiesInLegacyRequest()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var document = System.Text.Json.JsonSerializer.SerializeToNode(CreateRequest(), options)!;
        document["schedulingCapabilities"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"cpu":{"supportedGrades":["freeze","normal"]},"gpu":{"supportedGrades":["extreme"]}}""");
        var request = System.Text.Json.JsonSerializer.Deserialize<AdapterSoftwareRegistrationRequest>(document.ToJsonString(), options)!;
        Assert.Null(AdapterRegistrationValidator.Validate(request));
        Assert.Equal("word-memory.adapter", request.AdapterId);
        Assert.DoesNotContain("schedulingCapabilities", System.Text.Json.JsonSerializer.Serialize(request, options));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"D:\")]
    [InlineData(@"D:Apps")]
    public void ValidateRejectsUnboundedOrNonabsoluteProgramRoots(string path)
        => Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest() with { ProgramRootPaths = [path] }));

    [Theory]
    [InlineData(@"D:\Apps\Other\worker.exe")]
    [InlineData(@"D:\Apps\WordMemory2\worker.exe")]
    [InlineData(@"D:\Apps\WordMemory\..\Other\worker.exe")]
    [InlineData("worker.exe")]
    public void ExecutableDeclarationCannotExpandTheRegisteredRoot(string path)
        => Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest() with { Processes = [new("worker", null, path)] }));

    [Fact]
    public void ExecutableInsideRootAndNameHintsRemainValid()
        => Assert.Null(AdapterRegistrationValidator.Validate(CreateRequest() with
            { Processes = [new("worker", null, @"D:\Apps\WordMemory\bin\worker.exe"), new("other", null, null)] }));

    [Fact]
    public void WindowsSubdirectoriesCannotBeClaimedAsAnAdapterProgramRoot()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windows));
        Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest() with { ProgramRootPaths = [Path.Combine(windows, "System32")] }));
        Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest() with { ProgramRootPaths = [windows] }));
    }

    private static AdapterSoftwareRegistrationRequest CreateRequest()
    {
        return new AdapterSoftwareRegistrationRequest(
            AdapterRegistrationSchemaVersions.Current,
            "word-memory.adapter",
            "word-memory",
            "背单词",
            null,
            [@"D:\Apps\WordMemory"],
            [],
            [],
            new AdapterResourceMarkerEndpoint(
                AdapterResourceMarkerTransports.LoopbackHttp,
                "http://127.0.0.1:9322/resource-ledger"));
    }
}
