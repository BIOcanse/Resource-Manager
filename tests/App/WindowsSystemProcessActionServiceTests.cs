using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Infrastructure.LocalSystem;
using ResourceManager.App.Infrastructure.Windows;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsSystemProcessActionServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "ResourceManager.ProcessActions.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void TerminateProcesses_RequiresExactCreationIdentityOnTheEffectHandle()
    {
        using var process = StartOwnedProcess();
        var startKey = ReadProcessStartKey(process.Id);
        var service = CreateService();

        var mismatch = service.TerminateProcesses(new SystemProcessOperationRequest(
        [
            new SystemProcessIdentity(
                process.Id,
                checked(startKey + 1).ToString(CultureInfo.InvariantCulture))
        ]));

        var skipped = Assert.Single(mismatch.Items);
        Assert.Equal("Skipped", skipped.State);
        Assert.False(process.HasExited);

        var exact = service.TerminateProcesses(new SystemProcessOperationRequest(
        [
            new SystemProcessIdentity(
                process.Id,
                startKey.ToString(CultureInfo.InvariantCulture))
        ]));

        var succeeded = Assert.Single(exact.Items);
        Assert.Equal("Succeeded", succeeded.State);
        Assert.Equal(
            startKey.ToString(CultureInfo.InvariantCulture),
            succeeded.ProcessStartKey);
        Assert.True(process.WaitForExit(5_000));
    }

    [Fact]
    public void CreateProcessDumps_MismatchedCreationIdentityCreatesNoDump()
    {
        using var process = StartOwnedProcess();
        try
        {
            var startKey = ReadProcessStartKey(process.Id);
            var service = CreateService();

            var result = service.CreateProcessDumps(new SystemProcessOperationRequest(
            [
                new SystemProcessIdentity(
                    process.Id,
                    checked(startKey + 1).ToString(CultureInfo.InvariantCulture))
            ]));

            var skipped = Assert.Single(result.Items);
            Assert.Equal("Skipped", skipped.State);
            Assert.False(process.HasExited);
            Assert.Empty(Directory.EnumerateFiles(result.DirectoryPath!, "*.dmp"));
        }
        finally
        {
            StopOwnedProcess(process);
        }
    }

    [Fact]
    public void TerminateProcesses_RejectsAmbiguousPidGenerations()
    {
        var service = CreateService();
        var request = new SystemProcessOperationRequest(
        [
            new SystemProcessIdentity(1234, "100"),
            new SystemProcessIdentity(1234, "101")
        ]);

        var error = Assert.Throws<InvalidOperationException>(
            () => service.TerminateProcesses(request));

        Assert.Equal("没有可操作的进程。", error.Message);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private WindowsSystemProcessActionService CreateService()
    {
        var contentRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        return new WindowsSystemProcessActionService(
            new TestWebHostEnvironment(contentRoot));
    }

    private static Process StartOwnedProcess()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "ping.exe"),
            Arguments = "-t 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        return process ?? throw new InvalidOperationException("无法启动测试进程。");
    }

    private static ulong ReadProcessStartKey(int processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            Assert.True(NativeMethods.GetProcessTimes(
                handle,
                out var creationTime,
                out _,
                out _,
                out _));
            return creationTime.ToUInt64();
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static void StopOwnedProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        process.WaitForExit(5_000);
    }

    private sealed class TestWebHostEnvironment(string contentRootPath)
        : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
