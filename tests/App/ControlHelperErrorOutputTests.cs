using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class ControlHelperErrorOutputTests
{
    [Fact]
    public async Task DrainsLargeStartupErrorsAndRetainsBoundedRuntimeDiagnosis()
    {
        var output = new ControlHelperErrorOutput(new StringReader(
            "You must install or update .NET. Framework: Microsoft.NETCore.App, version 10.0.0\n"
            + new string('x', 200000)));
        await output.Completion;
        Assert.Equal(ControlHelperErrorOutput.MissingRuntime, output.FailureReason);
        Assert.InRange(output.Detail.Length, 1, 6145);
    }

    [Fact]
    public async Task OtherStartupFailuresAreNotMisreportedAsMissingRuntime()
    {
        var output = new ControlHelperErrorOutput(new StringReader("Access denied loading helper library"));
        await output.Completion;
        Assert.Equal(ControlHelperErrorOutput.StartupFailed, output.FailureReason);
    }
}
