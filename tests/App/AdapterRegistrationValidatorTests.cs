using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

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
    public void Validate_AcceptsIndependentCpuSchedulingCapabilities()
    {
        var request = CreateRequest(new AdapterSoftwareSchedulingCapabilities(
            new AdapterCpuSchedulingCapabilities(
                [AdapterCpuSchedulingGrade.Normal, AdapterCpuSchedulingGrade.Optimize]),
            null));

        Assert.Null(AdapterRegistrationValidator.Validate(request));
    }

    [Fact]
    public void Validate_RejectsCapabilityObjectWithoutDimension()
    {
        var validation = AdapterRegistrationValidator.Validate(
            CreateRequest(new AdapterSoftwareSchedulingCapabilities()));

        Assert.Equal("schedulingCapabilities must declare cpu or gpu capabilities.", validation);
    }

    [Fact]
    public void Validate_RejectsGpuCapabilitiesWithoutNormalRecoveryGrade()
    {
        var request = CreateRequest(new AdapterSoftwareSchedulingCapabilities(
            null,
            new AdapterGpuSchedulingCapabilities([AdapterGpuSchedulingGrade.Optimize])));

        var validation = AdapterRegistrationValidator.Validate(request);

        Assert.Equal("GPU scheduling capabilities must include normal.", validation);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"D:\")]
    [InlineData(@"D:Apps")]
    public void ValidateRejectsUnboundedOrNonabsoluteProgramRoots(string path)
        => Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest(null) with { ProgramRootPaths = [path] }));

    [Theory]
    [InlineData(@"D:\Apps\Other\worker.exe")]
    [InlineData(@"D:\Apps\WordMemory2\worker.exe")]
    [InlineData(@"D:\Apps\WordMemory\..\Other\worker.exe")]
    [InlineData("worker.exe")]
    public void ExecutableDeclarationCannotExpandTheRegisteredRoot(string path)
        => Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest(null) with { Processes = [new("worker", null, path)] }));

    [Fact]
    public void ExecutableInsideRootAndNameHintsRemainValid()
        => Assert.Null(AdapterRegistrationValidator.Validate(CreateRequest(null) with
            { Processes = [new("worker", null, @"D:\Apps\WordMemory\bin\worker.exe"), new("other", null, null)] }));

    [Fact]
    public void WindowsSubdirectoriesCannotBeClaimedAsAnAdapterProgramRoot()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windows));
        Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest(null) with { ProgramRootPaths = [Path.Combine(windows, "System32")] }));
        Assert.NotNull(AdapterRegistrationValidator.Validate(CreateRequest(null) with { ProgramRootPaths = [windows] }));
    }

    private static AdapterSoftwareRegistrationRequest CreateRequest(
        AdapterSoftwareSchedulingCapabilities? schedulingCapabilities)
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
                "http://127.0.0.1:9322/resource-ledger"),
            schedulingCapabilities);
    }
}
