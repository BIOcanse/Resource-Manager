using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

[Collection(NativeLaunchPolicyProcessStateCollection.Name)]
public sealed class GpuWindowActionPackagingTests(ITestOutputHelper output)
{
    [Fact]
    public void ProductBuildAndPublishRequireTheWindowHelper()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Core", "ResourceManager.App.csproj"));
        var target = Assert.Single(project.Root!.Elements("Target"), item => (string?)item.Attribute("Name") == "BuildGpuWindowAction");
        Assert.Equal("PrepareForBuild", (string?)target.Attribute("BeforeTargets"));
        Assert.Equal("@(GpuWindowActionSource)", (string?)target.Attribute("Inputs"));
        Assert.Equal("$(GpuWindowActionRoot)\\bin\\win-x64\\ResourceManager.GpuWindowAction.exe", (string?)target.Attribute("Outputs"));
        Assert.Equal("'$(DesignTimeBuild)' != 'true' and '$(SkipGpuWindowActionNativeBuild)' != 'true'", (string?)target.Attribute("Condition"));
        var exec = Assert.Single(target.Elements("Exec"));
        Assert.Equal("$(GpuWindowActionRoot)", (string?)exec.Attribute("WorkingDirectory"));
        Assert.Equal(@"cmd.exe /d /c .\build.cmd", (string?)exec.Attribute("Command"));
        Assert.Equal(new[] { "$(GpuWindowActionRoot)\\ResourceManagerGpuWindowAction.cpp", "$(GpuWindowActionRoot)\\WindowActionProtocol.h",
                "$(GpuWindowActionRoot)\\..\\GpuPlacementCommon\\WorkerPipeClient.h", "$(GpuWindowActionRoot)\\build.cmd" },
            project.Descendants("GpuWindowActionSource").Select(item => (string?)item.Attribute("Include")).ToArray());
        var projectDirectory = Path.Combine(RepositoryRoot(), "src", "Core");
        var rootProperty = Assert.Single(project.Descendants("GpuWindowActionRoot"));
        Assert.Equal("'$(GpuWindowActionRoot)' == ''", (string?)rootProperty.Attribute("Condition"));
        Assert.Equal(@"$(MSBuildProjectDirectory)\Native\GpuWindowAction", rootProperty.Value);
        var defaultRoot = Path.GetFullPath(rootProperty.Value.Replace("$(MSBuildProjectDirectory)", projectDirectory));
        var externalRoot = Path.Combine(Path.GetTempPath(), "recorded-window-native", "GpuWindowAction");
        foreach (var root in new[] { defaultRoot, externalRoot })
        {
            var inputs = project.Descendants("GpuWindowActionSource").Select(item =>
                Path.GetFullPath(((string)item.Attribute("Include")!).Replace("$(GpuWindowActionRoot)", root)
                    .Replace("$(MSBuildProjectDirectory)", projectDirectory))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine(root, "build.cmd"), inputs);
            Assert.True(QuotedNativeInputs(defaultRoot, root).IsSubsetOf(inputs),
                "Every local native include must invalidate the helper for both default and external roots.");
            var common = Path.GetFullPath(Path.Combine(root, "..", "GpuPlacementCommon", "WorkerPipeClient.h"));
            Assert.Contains(common, inputs);
            if (root == defaultRoot)
                Assert.Equal(Path.Combine(projectDirectory, "Native", "GpuPlacementCommon", "WorkerPipeClient.h"), common);
            else
                Assert.DoesNotContain(Path.Combine(projectDirectory, "Native", "GpuPlacementCommon", "WorkerPipeClient.h"), inputs);
        }
        var file = Assert.Single(project.Descendants("None"), item => (string?)item.Attribute("TargetPath") == "GpuPlacementShim\\ResourceManager.GpuWindowAction.exe");
        Assert.Equal("$(GpuWindowActionRoot)\\bin\\win-x64\\ResourceManager.GpuWindowAction.exe", (string?)file.Attribute("Include"));
        Assert.Null(file.Attribute("Condition"));
        Assert.Equal("PreserveNewest", (string?)file.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)file.Attribute("CopyToPublishDirectory"));
        Assert.Equal("true", (string?)file.Attribute("ExcludeFromSingleFile"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductDefaultPathStartsTheRealHelperWithoutAuthorizingAWindow(bool simulateMissingNoGpFaultBit)
    {
        var originalErrorMode = GetErrorMode();
        try
        {
            // The launch owner must repair the process-mode loss from the full run.
            if (simulateMissingNoGpFaultBit)
                _ = SetErrorMode(originalErrorMode & ~0x0002U);
            var errorModeBeforeLaunch = GetErrorMode();
            var logger = new LaunchLogger();
            var helper = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", "ResourceManager.GpuWindowAction.exe");
            Assert.True(File.Exists(helper), $"The mandatory product helper is missing: {helper}");
            using var current = Process.GetCurrentProcess();
            Assert.True(GetProcessTimes(current.SafeHandle, out var creation, out _, out _, out _));
            var limits = new GpuWindowActionProcessLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 4096, 4096);
            await using var executor = new WindowsGpuWindowActionRuntime(logger).Create(limits, CancellationToken.None,
                checked((ulong)Environment.TickCount64 + 5000));
            // This process owns no requested HWND; the helper must reject before requesting persistence.
            var result = await executor.RunAsync(new(Environment.ProcessId, creation, ulong.MaxValue, GpuWindowActionMethod.Resize),
                _ => throw new Xunit.Sdk.XunitException("An invalid HWND reached persistence."));
            var jobQuerySucceeded = IsProcessInJob(current.SafeHandle, IntPtr.Zero, out var inJob);
            output.WriteLine("windowHelperResult=" + JsonSerializer.Serialize(new {
                simulateMissingNoGpFaultBit, originalErrorMode, errorModeBeforeLaunch,
                processState = new { errorMode = GetErrorMode(), threadErrorMode = GetThreadErrorMode(), inJob, jobQuerySucceeded,
                    currentDirectory = Environment.CurrentDirectory, baseDirectory = AppContext.BaseDirectory },
                worker = executor.Worker, result, logs = logger.Entries }));
            Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
            Assert.NotNull(result.Rejection);
            Assert.Null(result.Prepared);
            Assert.Null(result.Failure);
            Assert.False(result.AuthorizationMayHaveBeenSent);
            Assert.True(result.Cleanup.Complete);
            Assert.Equal(0U, result.Cleanup.ExitCode);
            Assert.Equal(0U, result.Cleanup.ActiveProcessCount);
            Assert.False(result.Cleanup.TerminationRequested);
            if ((errorModeBeforeLaunch & WindowsNativeLaunchErrorPolicy.RequiredMode) != WindowsNativeLaunchErrorPolicy.RequiredMode)
            {
                var warning = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, warning.Level);
                Assert.Equal(errorModeBeforeLaunch, Assert.IsType<uint>(warning.Values.Single(value => value.Key == "BeforeMode").Value));
                Assert.Equal(Environment.ProcessId, Assert.IsType<int>(warning.Values.Single(value => value.Key == "ProcessId").Value));
                Assert.Contains($"before=0x{errorModeBeforeLaunch:X8}", warning.Message);
            }
            else Assert.Empty(logger.Entries);
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
        finally
        {
            _ = SetErrorMode(originalErrorMode);
        }
    }

    private sealed record LaunchLogEntry(LogLevel Level, string Message, KeyValuePair<string, object?>[] Values);

    private sealed class LaunchLogger : ILogger<WindowsGpuWindowActionRuntime>
    {
        internal List<LaunchLogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToArray()));
    }

    private static HashSet<string> QuotedNativeInputs(string actualRoot, string projectedRoot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Actual, string Projected)>();
        pending.Enqueue((Path.Combine(actualRoot, "ResourceManagerGpuWindowAction.cpp"),
            Path.Combine(projectedRoot, "ResourceManagerGpuWindowAction.cpp")));
        while (pending.TryDequeue(out var entry))
        {
            if (!result.Add(entry.Projected)) continue;
            Assert.InRange(result.Count, 1, 64);
            Assert.True(File.Exists(entry.Actual), "Missing actual native source: " + entry.Actual);
            Assert.InRange(new FileInfo(entry.Actual).Length, 0L, 1024L * 1024);
            foreach (Match include in Regex.Matches(File.ReadAllText(entry.Actual),
                         "(?m)^\\s*#include\\s*\"([^\"]+)\""))
            {
                var relative = include.Groups[1].Value;
                Assert.InRange(pending.Count, 0, 255);
                pending.Enqueue((Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry.Actual)!, relative)),
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry.Projected)!, relative))));
            }
        }
        return result;
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetThreadErrorMode();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job, out bool inJob);
}
