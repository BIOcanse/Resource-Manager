using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowActionPackagingTests(ITestOutputHelper output)
{
    [Fact]
    public void ProductBuildAndPublishRequireTheWindowHelper()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "Resource Manager", "Resource Manager-APP", "ResourceManager.App.csproj"));
        var target = Assert.Single(project.Root!.Elements("Target"), item => (string?)item.Attribute("Name") == "BuildGpuWindowAction");
        Assert.Equal("PrepareForBuild", (string?)target.Attribute("BeforeTargets"));
        Assert.Equal("@(GpuWindowActionSource)", (string?)target.Attribute("Inputs"));
        Assert.Equal("$(GpuWindowActionRoot)\\bin\\win-x64\\ResourceManager.GpuWindowAction.exe", (string?)target.Attribute("Outputs"));
        Assert.Equal("'$(DesignTimeBuild)' != 'true' and '$(SkipGpuWindowActionNativeBuild)' != 'true'", (string?)target.Attribute("Condition"));
        var exec = Assert.Single(target.Elements("Exec"));
        Assert.Equal("$(GpuWindowActionRoot)", (string?)exec.Attribute("WorkingDirectory"));
        Assert.Equal("cmd.exe /d /c build.cmd", (string?)exec.Attribute("Command"));
        Assert.Equal(new[] { "$(GpuWindowActionRoot)\\ResourceManagerGpuWindowAction.cpp", "$(GpuWindowActionRoot)\\WindowActionProtocol.h",
                "$(MSBuildProjectDirectory)\\Native\\GpuPlacementCommon\\WorkerPipeClient.h", "$(GpuWindowActionRoot)\\build.cmd" },
            project.Descendants("GpuWindowActionSource").Select(item => (string?)item.Attribute("Include")).ToArray());
        var file = Assert.Single(project.Descendants("None"), item => (string?)item.Attribute("TargetPath") == "GpuPlacementShim\\ResourceManager.GpuWindowAction.exe");
        Assert.Equal("$(GpuWindowActionRoot)\\bin\\win-x64\\ResourceManager.GpuWindowAction.exe", (string?)file.Attribute("Include"));
        Assert.Null(file.Attribute("Condition"));
        Assert.Equal("PreserveNewest", (string?)file.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)file.Attribute("CopyToPublishDirectory"));
        Assert.Equal("true", (string?)file.Attribute("ExcludeFromSingleFile"));
    }

    [Fact]
    public async Task ProductDefaultPathStartsTheRealHelperWithoutAuthorizingAWindow()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", "ResourceManager.GpuWindowAction.exe");
        Assert.True(File.Exists(helper), $"The mandatory product helper is missing: {helper}");
        using var current = Process.GetCurrentProcess();
        Assert.True(GetProcessTimes(current.SafeHandle, out var creation, out _, out _, out _));
        var limits = new GpuWindowActionProcessLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 4096, 4096);
        await using var executor = new WindowsGpuWindowActionRuntime().Create(limits, CancellationToken.None,
            checked((ulong)Environment.TickCount64 + 5000));
        // This process owns no requested HWND; the helper must reject before requesting persistence.
        var result = await executor.RunAsync(new(Environment.ProcessId, creation, ulong.MaxValue, GpuWindowActionMethod.Resize),
            _ => throw new Xunit.Sdk.XunitException("An invalid HWND reached persistence."));
        Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
        Assert.NotNull(result.Rejection);
        Assert.Null(result.Prepared);
        Assert.Null(result.Failure);
        Assert.False(result.AuthorizationMayHaveBeenSent);
        Assert.True(result.Cleanup.Complete);
        Assert.Equal(0U, result.Cleanup.ExitCode);
        Assert.Equal(0U, result.Cleanup.ActiveProcessCount);
        Assert.False(result.Cleanup.TerminationRequested);
        var worker = Assert.IsType<GpuWindowActionProcessIdentity>(executor.Worker);
        Assert.Equal(Path.GetFullPath(helper), worker.ExecutablePath, ignoreCase: true);
        using var image = File.OpenRead(helper);
        output.WriteLine("packagedWindowHelper=" + JsonSerializer.Serialize(new
        {
            path = helper, byteLength = image.Length, sha256 = Convert.ToHexString(SHA256.HashData(image)),
            testHost = new { processId = Environment.ProcessId, creationFileTimeUtc = creation }, worker, result,
            defaultProductPath = true, environmentOverrideUsed = false, userWindowUsed = false
        }));
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
