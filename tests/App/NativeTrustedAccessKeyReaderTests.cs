using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.NativeUi;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class NativeTrustedAccessKeyReaderTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    [Fact]
    public void StableStorageUsesOneValidationAndReplacementWithSameLengthAndWriteTimeReloads()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var reads = 0;
        var reader = new NativeTrustedAccessKeyReader(temporary.Root, () => { reads++; return File.ReadAllText(path); });
        Assert.Equal("first", reader.Read());
        for (var index = 0; index < 100; index++) Assert.Equal("first", reader.Read());
        Assert.Equal(1, reads);
        var replacement = path + ".replacement";
        File.WriteAllText(replacement, "other");
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(path));
        File.Move(replacement, path, overwrite: true);
        Assert.Equal("other", reader.Read());
        Assert.Equal(2, reads);
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => reader.Read());
        File.WriteAllText(path, "third");
        Assert.Equal("third", reader.Read());
    }

    [Fact]
    public void SecurityMetadataChangeInvalidatesAndValidationFailureCannotReturnTheCachedSecret()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var deny = false;
        var reader = new NativeTrustedAccessKeyReader(temporary.Root,
            () => deny ? throw new UnauthorizedAccessException() : File.ReadAllText(path));
        Assert.Equal("first", reader.Read());
        deny = true;
        var security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAttributes, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        Assert.Throws<UnauthorizedAccessException>(() => reader.Read());
        Assert.Throws<UnauthorizedAccessException>(() => reader.Read());
        deny = false;
        Assert.Equal("first", reader.Read());
    }

    [Fact]
    public void StorageChangingDuringValidationIsRejectedAndNextReadRetries()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var change = true;
        var reader = new NativeTrustedAccessKeyReader(temporary.Root, () =>
        {
            var value = File.ReadAllText(path);
            if (change) { change = false; File.WriteAllText(path, "second"); }
            return value;
        });
        Assert.Throws<IOException>(() => reader.Read());
        Assert.Equal("second", reader.Read());
    }

    [Fact]
    public async Task ReplacingAnAncestorWithAJunctionRejectsEvenWhenTheTargetKeyIsUnchanged()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var reader = new NativeTrustedAccessKeyReader(temporary.Root, () => File.ReadAllText(path));
        Assert.Equal("first", reader.Read());
        var userData = Path.Combine(temporary.Root, "UserData");
        var moved = Path.Combine(temporary.Root, "original-user-data");
        Directory.Move(userData, moved);
        using var command = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", userData, moved }
        })!;
        await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, command.ExitCode);
        Assert.Throws<UnauthorizedAccessException>(() => reader.Read());
    }

    [Fact]
    public void WarmNativeKeyReadMeasuresValidatedFileBaselineAndMetadataCache()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true
            || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var key = "rm_sk_" + new string('a', 64);
        Func<string?> baseline;
        if (elevated)
        {
            TrustedAccessKeyFile.WriteAtomic(temporary.Root, key);
            baseline = () => TrustedAccessKeyFile.Read(temporary.Root);
        }
        else
        {
            // Cost replay only: an unelevated runner cannot create an administrator-owned fixture.
            // Production owner acceptance is unchanged. Replay metadata/ACL/path/content work
            // against captured fixture facts, without changing ownership or ACLs.
            var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, key);
            var directoryFacts = BenchmarkSecurityFacts.Capture(new DirectoryInfo(directory).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access));
            var fileFacts = BenchmarkSecurityFacts.Capture(new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access));
            baseline = () => ReadBenchmarkFixture(temporary.Root, directoryFacts, fileFacts);
            output.WriteLine("SourceReconstructed unelevated baseline: path metadata/two ACL queries/read/parse against captured fixture owner and ACL facts; this does not run the production security validator. Privileged production-owner benchmark remains unverified.");
        }
        var validations = 0;
        var reader = new NativeTrustedAccessKeyReader(temporary.Root, () => { validations++; return baseline(); });
        Measure("full file validation on each native request", baseline);
        Measure("cached native request with identity/change metadata", reader.Read);
        Assert.Equal(1, validations);
        output.WriteLine("Warm cache ran the actual identity/change checks on unchanged storage; no contents or ACL revalidation occurred in measured requests. The cold validator used captured fixture facts, not production privileged ACLs.");

        void Measure(string label, Func<string?> read)
        {
            for (var index = 0; index < 250; index++) Assert.Equal(key, read());
            const int count = 2000;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var index = 0; index < count; index++)
                if (read() != key) throw new InvalidOperationException("Unexpected key.");
            var elapsed = Stopwatch.GetElapsedTime(start);
            output.WriteLine($"{label}: count={count}, us/request={elapsed.TotalMicroseconds / count:F2}, bytes/request={(GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count:F1}");
        }
    }

    private static string ReadBenchmarkFixture(string root, BenchmarkSecurityFacts directoryFacts, BenchmarkSecurityFacts fileFacts)
    {
        var path = TrustedAccessKeyFile.PathForPackage(root);
        var directory = Path.GetDirectoryName(path)!;
        _ = File.GetAttributes(directory);
        TrustedAccessKeyFile.VerifyNoReparsePoints(directory);
        InspectBenchmarkSecurity(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), directoryFacts);
        _ = File.GetAttributes(path);
        TrustedAccessKeyFile.VerifyNoReparsePoints(path);
        TrustedAccessKeyFile.ValidateKeyFileLength(new FileInfo(path).Length);
        TrustedAccessKeyFile.VerifyNoReparsePoints(path);
        InspectBenchmarkSecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), fileFacts);
        var value = File.ReadAllText(path, StrictUtf8).TrimEnd('\r', '\n');
        if (!TrustedAccessKeyFile.IsApiKey(value)) throw new InvalidDataException();
        return value;

    }

    private static void InspectBenchmarkSecurity(CommonObjectSecurity security, BenchmarkSecurityFacts expected)
    {
        if (!expected.Owner.Equals(security.GetOwner(typeof(SecurityIdentifier)))
            || expected.Protected != security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count != expected.Rules.Length) throw new UnauthorizedAccessException();
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = (FileSystemAccessRule)rules[index];
            var accepted = expected.Rules[index];
            if (!rule.IdentityReference.Equals(accepted.IdentityReference)
                || rule.FileSystemRights != accepted.FileSystemRights
                || rule.AccessControlType != accepted.AccessControlType || rule.IsInherited != accepted.IsInherited
                || rule.InheritanceFlags != accepted.InheritanceFlags || rule.PropagationFlags != accepted.PropagationFlags)
                throw new UnauthorizedAccessException();
        }
    }

    private sealed record BenchmarkSecurityFacts(IdentityReference Owner, bool Protected, FileSystemAccessRule[] Rules)
    {
        internal static BenchmarkSecurityFacts Capture(CommonObjectSecurity security)
            => new(security.GetOwner(typeof(SecurityIdentifier))!, security.AreAccessRulesProtected,
                security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray());
    }
}
