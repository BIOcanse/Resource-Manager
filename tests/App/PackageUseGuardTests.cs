using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ResourceManager.Shared.Packages;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class PackageUseGuardTests
{
    [Fact]
    public void MissingGuardNeverCreatesAClientOrMaintenanceIdentity()
    {
        using var fixture = new Fixture();
        Assert.ThrowsAny<IOException>(() => PackageUseGuard.Acquire(fixture.Root));
        Assert.ThrowsAny<IOException>(() => PackageUseGuard.TryAcquireMaintenance(fixture.Root));
        Assert.False(Directory.Exists(UpdateManagerPaths.InstalledDirectory(fixture.Root)));
    }

    [Fact]
    public void BothClientsMustExitAndExclusiveMaintenanceRejectsNewClients()
    {
        using var fixture = new Fixture();
        if (!fixture.Prepare()) return;
        using var first = PackageUseGuard.Acquire(fixture.Root);
        using var second = PackageUseGuard.Acquire(fixture.Root);
        Assert.Null(PackageUseGuard.TryAcquireMaintenance(fixture.Root));
        first.Dispose();
        Assert.Null(PackageUseGuard.TryAcquireMaintenance(fixture.Root));
        second.Dispose();
        using (var maintenance = PackageUseGuard.TryAcquireMaintenance(fixture.Root))
        {
            Assert.NotNull(maintenance);
            Assert.Throws<IOException>(() => PackageUseGuard.Acquire(fixture.Root));
            Assert.Throws<IOException>(() => File.Move(PackageUseGuard.PathForPackage(fixture.Root),
                PackageUseGuard.PathForPackage(fixture.Root) + ".moved"));
        }
        using var resumed = PackageUseGuard.Acquire(fixture.Root);
    }

    [Fact]
    public void NativeClientLifetimeHoldsGuardUntilDisposedAndDifferentRootsAreIndependent()
    {
        using var fixture = new Fixture();
        using var other = new Fixture();
        if (!fixture.Prepare() || !other.Prepare()) return;
        using var lifetime = new NativeUiPackageUseLifetime(fixture.Root);
        Assert.Null(PackageUseGuard.TryAcquireMaintenance(fixture.Root));
        using var unrelated = PackageUseGuard.TryAcquireMaintenance(other.Root);
        Assert.NotNull(unrelated);
        lifetime.Dispose();
        using var available = PackageUseGuard.TryAcquireMaintenance(fixture.Root);
        Assert.NotNull(available);
    }

    [Fact]
    public void ProvisioningExistingGuardWhileInUseDoesNotReplaceItOrChangeRules()
    {
        using var fixture = new Fixture();
        if (!fixture.Prepare()) return;
        var path = PackageUseGuard.PathForPackage(fixture.Root);
        var before = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        using var client = PackageUseGuard.Acquire(fixture.Root);
        PackageUseGuard.Prepare(fixture.Root);
        Assert.Null(PackageUseGuard.TryAcquireMaintenance(fixture.Root));
        Assert.Equal(before, new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    public async Task WaitingCancellationReleasesNoClientAndDoesNotAcquireLater()
    {
        using var fixture = new Fixture();
        if (!fixture.Prepare()) return;
        using var client = PackageUseGuard.Acquire(fixture.Root);
        using var cancellation = new CancellationTokenSource();
        var wait = PackageUseGuard.AcquireMaintenanceAsync(fixture.Root, cancellation.Token);
        Assert.False(wait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        client.Dispose();
        using var exclusive = PackageUseGuard.TryAcquireMaintenance(fixture.Root);
        Assert.NotNull(exclusive);
    }

    [Fact]
    public async Task CrossProcessClientExitReleasesItsGuardWithoutInspectionOrForceClose()
    {
        using var fixture = new Fixture();
        if (!fixture.Prepare()) return;
        var literal = PackageUseGuard.PathForPackage(fixture.Root).Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference='Stop'; $lease=[IO.File]::Open('" + literal
            + "',[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read); try { [Console]::WriteLine('held'); [Console]::ReadLine() | Out-Null } finally { $lease.Dispose() }";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }
        }) ?? throw new InvalidOperationException("Cannot start isolated guard fixture.");
        try
        {
            Assert.Equal("held", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Null(PackageUseGuard.TryAcquireMaintenance(fixture.Root));
            using var anotherClient = PackageUseGuard.Acquire(fixture.Root);
            await process.StandardInput.WriteLineAsync("exit");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, process.ExitCode);
            Assert.Null(PackageUseGuard.TryAcquireMaintenance(fixture.Root));
            anotherClient.Dispose();
            using var maintenance = PackageUseGuard.TryAcquireMaintenance(fixture.Root);
            Assert.NotNull(maintenance);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public void RestrictedUserCanHoldReadGuardButCannotWriteOrReplaceIt()
    {
        using var fixture = new Fixture();
        if (!fixture.Prepare()) return;
        using var token = PackageUserDirectoryTokenTests.CreateUserToken();
        WindowsIdentity.RunImpersonated(token, () =>
        {
            using var lease = PackageUseGuard.Acquire(fixture.Root);
            var path = PackageUseGuard.PathForPackage(fixture.Root);
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(path, "blocked"));
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(path));
        });
    }

    [Fact]
    public void UnexpectedWritePermissionFailsClosedInsteadOfReportingNoClients()
    {
        using var fixture = new Fixture();
        if (!fixture.Prepare()) return;
        var path = PackageUseGuard.PathForPackage(fixture.Root);
        var security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        Assert.Throws<UnauthorizedAccessException>(() => PackageUseGuard.Acquire(fixture.Root));
        Assert.Throws<UnauthorizedAccessException>(() => PackageUseGuard.TryAcquireMaintenance(fixture.Root));
        Assert.Throws<UnauthorizedAccessException>(() => PackageUseGuard.Prepare(fixture.Root));
    }

    [Fact]
    public async Task ReparseAncestorIsRejectedBeforeOpeningAnyGuard()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(Path.Combine(target, "Management"));
        File.WriteAllText(Path.Combine(target, "Management", "package-use.guard"), string.Empty);
        var link = Path.Combine(fixture.Root, "Internal");
        using var command = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        }) ?? throw new InvalidOperationException("Cannot create isolated guard junction.");
        await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, command.ExitCode);
        try
        {
            Assert.Throws<InvalidDataException>(() => PackageUseGuard.Acquire(fixture.Root));
            Assert.Throws<InvalidDataException>(() => PackageUseGuard.TryAcquireMaintenance(fixture.Root));
            Assert.Throws<InvalidDataException>(() => PackageUseGuard.Verify(fixture.Root));
        }
        finally { Directory.Delete(link); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rm-package-guard-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);

        public bool Prepare()
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true
                && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Assert.Throws<UnauthorizedAccessException>(() => PackageUseGuard.Prepare(Root));
                return false;
            }
            PackageUseGuard.Prepare(Root);
            return true;
        }

        public void Dispose()
        {
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(Root);
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(Root);
            while (pending.TryPop(out var directory))
            {
                if (directories.Count >= 32) throw new InvalidOperationException("Guard fixture exceeds its bound.");
                directories.Add(directory);
                foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            for (var index = directories.Count - 1; index >= 0; index--) Directory.Delete(directories[index]);
        }
    }
}
