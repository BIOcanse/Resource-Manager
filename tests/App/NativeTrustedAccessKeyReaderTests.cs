using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecurityMetadataChangeInvalidatesAndValidationFailureCannotReturnTheCachedSecret(bool directory)
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var deny = false;
        var validations = 0;
        var reader = new NativeTrustedAccessKeyReader(temporary.Root,
            () => { validations++; return deny ? throw new UnauthorizedAccessException() : File.ReadAllText(path); });
        Assert.Equal("first", reader.Read());
        var target = directory ? Path.GetDirectoryName(path)! : path;
        for (var index = 0; index < 128; index++)
        {
            var metadata = ReadBasicInformation(target);
            var security = ReadSecurity(target, directory);
            var before = security.GetSecurityDescriptorBinaryForm();
            security.SetAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                index % 2 == 0 ? FileSystemRights.ReadAttributes : FileSystemRights.ReadExtendedAttributes, AccessControlType.Allow));
            WriteSecurity(target, directory, security);
            Assert.False(before.AsSpan().SequenceEqual(ReadSecurity(target, directory).GetSecurityDescriptorBinaryForm()));
            // Timestamp collisions are real; force the exact collision deterministically without a timing delay.
            RestoreBasicInformation(target, metadata);
            var after = ReadBasicInformation(target);
            Assert.Equal(metadata.LastWriteTime, after.LastWriteTime);
            Assert.Equal(metadata.ChangeTime, after.ChangeTime);
            deny = true;
            var previousValidations = validations;
            Assert.Throws<UnauthorizedAccessException>(() => reader.Read());
            Assert.Equal(previousValidations + 1, validations);
            Assert.Throws<UnauthorizedAccessException>(() => reader.Read());
            Assert.Equal(previousValidations + 2, validations);
            deny = false;
            Assert.Equal("first", reader.Read());
        }
        output.WriteLine("Verified 128 actual {0} owner/DACL byte changes with equal metadata timestamps; each change reentered validation and failure never reused the secret.", directory ? "directory" : "file");
    }

    [Fact]
    public void LargerActualSecurityDescriptorIsReadAndUnchangedStorageStillUsesOneValidation()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var security = new FileInfo(path).GetAccessControl();
        for (var index = 0; index < 128; index++)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier($"S-1-5-21-100-200-300-{1000 + index}"),
                FileSystemRights.ReadAttributes, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        Assert.True(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm().Length > 512);
        var validations = 0;
        var reader = new NativeTrustedAccessKeyReader(temporary.Root, () => { validations++; return File.ReadAllText(path); });
        for (var index = 0; index < 100; index++) Assert.Equal("first", reader.Read());
        Assert.Equal(1, validations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableSecurityReadAccessClearsCacheUntilActualAclIsRestored(bool directory)
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var validations = 0;
        var reader = new NativeTrustedAccessKeyReader(temporary.Root, () => { validations++; return File.ReadAllText(path); });
        Assert.Equal("first", reader.Read());
        var target = directory ? Path.GetDirectoryName(path)! : path;
        var security = ReadSecurity(target, directory);
        var original = security.GetSecurityDescriptorBinaryForm();
        // Keep the already granted WRITE_DAC handle so recovery does not depend on
        // opening another handle after deliberately denying READ_CONTROL to the owner.
        using var recovery = CreateFileW(target, 0x60080, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        Assert.False(recovery.IsInvalid);
        try
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
                FileSystemRights.ReadPermissions, AccessControlType.Deny));
            WriteSecurity(target, directory, security);
            using var denied = CreateFileW(target, 0x20080, FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
            var deniedError = Marshal.GetLastWin32Error();
            Assert.True(denied.IsInvalid);
            Assert.Equal(5, deniedError);
            Assert.Throws<IOException>(() => reader.Read());
            Assert.Throws<IOException>(() => reader.Read());
            Assert.Equal(1, validations);
        }
        finally
        {
            if (!SetKernelObjectSecurity(recovery, 0x4, original))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        Assert.Equal("first", reader.Read());
        Assert.Equal(2, validations);
    }

    private static FileSystemSecurity ReadSecurity(string path, bool directory)
        => directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();

    private static void WriteSecurity(string path, bool directory, FileSystemSecurity security)
    {
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static BasicInformation ReadBasicInformation(string path)
    {
        using var handle = CreateFileW(path, 0x80, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 0, out var value, checked((uint)Marshal.SizeOf<BasicInformation>())))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }

    private static void RestoreBasicInformation(string path, BasicInformation value)
    {
        using var handle = CreateFileW(path, 0x100, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !SetFileInformationByHandle(handle, 0, ref value, checked((uint)Marshal.SizeOf<BasicInformation>())))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal uint Attributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, out BasicInformation value, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int information, ref BasicInformation value, uint length);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);

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
