using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Infrastructure.LocalSystem;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsSystemProcessActionServiceTests : IDisposable
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "rm-process-actions-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SearchAndPropertiesAreDelegatedToTheVerifiedUserDesktop()
    {
        var broker = new RecordedSessionBroker();
        var service = CreateService(broker);
        var caller = new SoftwareOperationCaller(UserSid, 7);
        var search = service.SearchOnline(new("two words"), caller);
        var first = Assert.Single(broker.Requests);
        Assert.Equal(search.Url, first.FileName);
        Assert.Null(first.Verb);
        Directory.CreateDirectory(testRoot);
        var properties = service.OpenProperties(new(testRoot), caller);
        var second = broker.Requests[1];
        Assert.Equal(properties.OpenedPath, second.FileName);
        Assert.Equal("properties", second.Verb);
        Assert.Equal(2, broker.Requests.Count);
    }

    [Fact]
    public void ChangedSessionCannotLaunchSearchOrProperties()
    {
        var broker = new RecordedSessionBroker { Active = false };
        var service = CreateService(broker);
        var caller = new SoftwareOperationCaller(UserSid, 7);
        Assert.Throws<UnauthorizedAccessException>(() => service.SearchOnline(new("query"), caller));
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenProperties(new(testRoot), caller));
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public void TerminateProcesses_ProtectsSystemPidBeforeOpeningAnEffectHandle()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var result = WindowsSystemProcessActionService.TerminateProcess(new(4, 1), identity.AccessToken);
        Assert.Equal("Skipped", result.State);
        Assert.Equal(SystemProcessOutcome.CriticalProcessProtected, result.Outcome);
    }

    [Fact]
    public void CriticalProcessCheck_DoesNotTreatQueryFailureAsNonCritical()
    {
        Assert.Throws<System.ComponentModel.Win32Exception>(
            () => WindowsSystemProcessActionService.ReadProcessCriticality(IntPtr.Zero));
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, Environment.ProcessId);
        Assert.NotEqual(IntPtr.Zero, handle);
        try { Assert.False(WindowsSystemProcessActionService.ReadProcessCriticality(handle)); }
        finally { NativeMethods.CloseHandle(handle); }
    }

    [Fact]
    public void TerminateProcesses_RequiresExactCreationIdentityOnTheEffectHandle()
    {
        using var process = StartOwnedProcess();
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            var startKey = ReadProcessStartKey(process.Id);
            var skipped = WindowsSystemProcessActionService.TerminateProcess(
                new(process.Id, checked(startKey + 1)), identity.AccessToken);
            Assert.Equal("Skipped", skipped.State);
            Assert.Equal(SystemProcessOutcome.IdentityChanged, skipped.Outcome);
            Assert.False(process.HasExited);

            var succeeded = WindowsSystemProcessActionService.TerminateProcess(
                new(process.Id, startKey), identity.AccessToken);
            Assert.Equal("Succeeded", succeeded.State);
            Assert.Equal(SystemProcessOutcome.Terminated, succeeded.Outcome);
            Assert.Equal(
                startKey.ToString(CultureInfo.InvariantCulture),
                succeeded.ProcessStartKey);
            Assert.True(process.WaitForExit(5_000));
        }
        finally { StopOwnedProcess(process); }
    }

    [Fact]
    public void CreateProcessDumps_MismatchedCreationIdentityCreatesNoDump()
    {
        using var process = StartOwnedProcess();
        try
        {
            var startKey = ReadProcessStartKey(process.Id);
            Directory.CreateDirectory(testRoot);
            using var identity = WindowsIdentity.GetCurrent();
            var skipped = WindowsSystemProcessActionService.CreateProcessDump(
                new(process.Id, checked(startKey + 1)), testRoot, identity.AccessToken);
            Assert.Equal("Skipped", skipped.State);
            Assert.Equal(SystemProcessOutcome.IdentityChanged, skipped.Outcome);
            Assert.False(process.HasExited);
            Assert.Empty(Directory.EnumerateFiles(testRoot, "*.dmp"));
        }
        finally
        {
            StopOwnedProcess(process);
        }
    }

    [Fact]
    public void TerminateProcesses_RejectsAmbiguousPidGenerations()
    {
        var request = new SystemProcessOperationRequest(
        [
            new SystemProcessIdentity(1234, "100"),
            new SystemProcessIdentity(1234, "101")
        ]);

        var error = Assert.Throws<InvalidOperationException>(
            () => WindowsSystemProcessActionService.NormalizeProcessTargets(request.Targets));

        Assert.Equal("没有可操作的进程。", error.Message);
    }

    [Fact]
    public void StandardUserTokenCannotBorrowServicePrivileges()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        using var process = StartOwnedProcess();
        try
        {
            var request = new SystemProcessOperationRequest([
                new(process.Id, ReadProcessStartKey(process.Id).ToString(CultureInfo.InvariantCulture))]);
            var service = CreateService();
            Assert.Throws<UnauthorizedAccessException>(() => service.TerminateProcesses(request, identity.AccessToken));
            Assert.Throws<UnauthorizedAccessException>(() => service.CreateProcessDumps(request, identity.AccessToken));
            Assert.False(process.HasExited);
            Assert.False(Directory.Exists(Path.Combine(testRoot, "Misc", "ProcessDumps")));
        }
        finally { StopOwnedProcess(process); }
    }

    [Fact]
    public async Task DumpDirectoriesRemoveBroadReadAccessAndRejectReparsePoints()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var root = Directory.CreateDirectory(Path.Combine(testRoot, "Misc", "ProcessDumps")).FullName;
        var oldPath = Path.Combine(root, "old.dmp");
        File.WriteAllText(oldPath, "owned test data");
        var directory = ProcessDumpDirectoryAccess.Prepare(testRoot, sid.Value);
        Assert.Equal(Path.Combine(root, sid.Value), directory);
        AssertPrivate(new DirectoryInfo(directory).GetAccessControl(), sid);
        AssertPrivate(new FileInfo(oldPath).GetAccessControl(), null);
        var rootRules = new DirectoryInfo(root).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        Assert.All(rootRules.OfType<FileSystemAccessRule>().Where(rule => rule.IdentityReference.Equals(users)),
            rule => Assert.Equal((FileSystemRights)0, rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.CreateDirectories)));

        // The owned temp ACL must be restored before creating a test-only junction.
        OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(testRoot);
        var target = Directory.CreateDirectory(Path.Combine(testRoot, "other")).FullName;
        var junction = Path.Combine(root, "S-1-5-21-1-2-3-1001");
        using var command = Process.Start(new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", junction, target } })!;
        await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, command.ExitCode);
        try { Assert.Throws<InvalidDataException>(() => ProcessDumpDirectoryAccess.Prepare(testRoot, "S-1-5-21-1-2-3-1001")); }
        finally { Directory.Delete(junction); }
    }

    private static void AssertPrivate(FileSystemSecurity access, SecurityIdentifier? reader)
    {
        Assert.True(access.AreAccessRulesProtected);
        var permitted = new[] {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), reader };
        Assert.All(access.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
            rule => Assert.Contains(rule.IdentityReference, permitted));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testRoot))
            {
                OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(testRoot);
                var dumpRoot = Path.Combine(testRoot, "Misc", "ProcessDumps");
                foreach (var path in Directory.Exists(dumpRoot) ? Directory.EnumerateFiles(dumpRoot, "*.dmp", SearchOption.TopDirectoryOnly) : [])
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    var access = new FileInfo(path).GetAccessControl();
                    access.AddAccessRule(new(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(path).SetAccessControl(access);
                }
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

    private WindowsSystemProcessActionService CreateService(RecordedSessionBroker? broker = null)
    {
        var contentRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        return new WindowsSystemProcessActionService(
            new TestWebHostEnvironment(contentRoot), broker ?? new RecordedSessionBroker());
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

        process.Kill();
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

    private sealed class RecordedSessionBroker : IInteractiveUserSessionBroker
    {
        internal bool Active { get; init; } = true;
        internal List<UserShellExecuteRequest> Requests { get; } = [];
        public IReadOnlyList<uint> GetActiveSessionIds() => Active ? [7u] : [];
        public string? GetUserSid(uint sessionId) => UserSid;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string executablePath) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string executablePath, string arguments) => throw new NotSupportedException();
        public uint LaunchUserShell(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        {
            Assert.Equal(7u, sessionId);
            Assert.Equal(UserSid, expectedUserSid);
            Assert.EndsWith("ResourceManager.NativeUi.exe", executablePath);
            Requests.Add(UserShellExecuteRequest.Decode(arguments[(UserShellExecuteRequest.Argument.Length + 1)..]));
            return 1;
        }
    }
}
