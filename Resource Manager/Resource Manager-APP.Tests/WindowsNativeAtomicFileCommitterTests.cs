using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using System.Security.Cryptography;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsNativeAtomicFileCommitterTests
{
    [Fact]
    public void LongUnicodeDestinationKeepsMultipleReadersAcrossReplacements()
    {
        var root = CreateRoot();
        try
        {
            var target = CreatePathAtLength(root, "\u8BBE\u7F6E-\U0001F680.json", 290);
            var candidate = target + ".tmp";
            var first = new string('a', 32768);
            var second = new string('b', 32768);
            File.WriteAllText(target, first);
            using var reader1 = new StreamReader(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            File.WriteAllText(candidate, second);
            WindowsNativeAtomicFileCommitter.CommitReplace(candidate, target);
            using var reader2 = new StreamReader(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            File.WriteAllText(candidate, "final");
            WindowsNativeAtomicFileCommitter.CommitReplace(candidate, target);
            Assert.Equal(first, reader1.ReadToEnd());
            Assert.Equal(second, reader2.ReadToEnd());
            Assert.Equal("final", File.ReadAllText(target));
            Assert.Equal(target, Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(target)!)));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void NewCommitNeverReplacesExistingFile()
    {
        var root = Directory.CreateDirectory(CreateRoot()).FullName;
        try
        {
            var target = Path.Combine(root, "settings.json");
            var candidate = Path.Combine(root, "candidate.tmp");
            File.WriteAllText(target, "old");
            File.WriteAllText(candidate, "new");
            Assert.Throws<IOException>(() => WindowsNativeAtomicFileCommitter.CommitNew(candidate, target));
            Assert.Equal("old", File.ReadAllText(target));
            Assert.Equal("new", File.ReadAllText(candidate));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void ReplacementKeepsOldReaderAndPublishesNewFileWithoutChangingOldBytes()
    {
        var root = Directory.CreateDirectory(CreateRoot()).FullName;
        try
        {
            var target = Path.Combine(root, "settings.json");
            var candidate = Path.Combine(root, "candidate.tmp");
            File.WriteAllText(target, "old");
            File.WriteAllText(candidate, "new");
            using var reader = new StreamReader(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            WindowsNativeAtomicFileCommitter.CommitReplace(candidate, target);
            Assert.Equal("old", reader.ReadToEnd());
            Assert.Equal("new", File.ReadAllText(target));
            Assert.False(File.Exists(candidate));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void FailedReplacementPreservesReadonlyTargetAndCandidate()
    {
        var root = Directory.CreateDirectory(CreateRoot()).FullName;
        var target = Path.Combine(root, "settings.json");
        try
        {
            var candidate = Path.Combine(root, "candidate.tmp");
            File.WriteAllText(target, "old");
            File.WriteAllText(candidate, "new");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            Assert.Throws<IOException>(() => WindowsNativeAtomicFileCommitter.CommitReplace(candidate, target));
            Assert.Equal("old", File.ReadAllText(target));
            Assert.Equal("new", File.ReadAllText(candidate));
        }
        finally { File.SetAttributes(target, FileAttributes.Normal); DeleteRoot(root); }
    }

    [Fact]
    public void CommitNewAndReplaceSupportTemporaryPathBeyondLegacyLimit()
    {
        var root = CreateRoot();
        try
        {
            var destinationPath = CreatePathAtLength(
                root,
                "process-effect-scope.json.audit-fence.json",
                228);
            var firstTemporaryPath =
                $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            Assert.Equal(265, firstTemporaryPath.Length);

            File.WriteAllText(firstTemporaryPath, "first");
            WindowsNativeAtomicFileCommitter.CommitNew(
                firstTemporaryPath,
                destinationPath);
            Assert.Equal("first", File.ReadAllText(destinationPath));
            Assert.False(File.Exists(firstTemporaryPath));

            var replacementTemporaryPath =
                $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            Assert.Equal(265, replacementTemporaryPath.Length);
            File.WriteAllText(replacementTemporaryPath, "replacement");
            WindowsNativeAtomicFileCommitter.CommitReplace(
                replacementTemporaryPath,
                destinationPath);
            Assert.Equal("replacement", File.ReadAllText(destinationPath));
            Assert.False(File.Exists(replacementTemporaryPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void DeleteExactSupportsPathBeyondLegacyLimit()
    {
        var root = CreateRoot();
        try
        {
            var path = CreatePathAtLength(root, "durable-state.json", 280);
            File.WriteAllText(path, "state");

            Assert.Equal(
                WindowsNativeFileDeleteResult.Deleted,
                WindowsNativeAtomicFileCommitter.DeleteExact(path));
            Assert.Equal(
                WindowsNativeFileDeleteResult.NotFound,
                WindowsNativeAtomicFileCommitter.DeleteExact(path));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ValidationScopeLifecycleSupportsFormalAuditFencePathLength()
    {
        var root = CreateRoot();
        try
        {
            var statePath = CreatePathAtLength(
                root,
                "process-effect-scope.json",
                211,
                createDirectory: false);
            var auditFencePath = $"{statePath}.audit-fence.json";
            Assert.Equal(228, auditFencePath.Length);
            Assert.Equal(
                265,
                $"{auditFencePath}.{Guid.NewGuid():N}.tmp".Length);
            var authority = new HostManagerProcessEffectValidationScopeAuthority(
                statePath,
                TimeProvider.System,
                new ExactMemberProbe(),
                WindowsHostManagerProcessEffectValidationScopeFileCommitter.Instance,
                authorityManifestPath: Path.Combine(
                    root,
                    "Authority",
                    "process-effect-scope.json"));

            OpenAndClose(authority, new HostManagerComputeProcessIdentity(401, 4001));
            OpenAndClose(authority, new HostManagerComputeProcessIdentity(402, 4002));

            Assert.True(File.Exists(statePath));
            Assert.True(File.Exists(auditFencePath));
            Assert.Equal("closed", authority.GetStatus().State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot()
        => Path.Combine(
            Path.GetTempPath(),
            $"rm-atomic-{Guid.NewGuid():N}");

    private static string CreatePathAtLength(
        string root,
        string fileName,
        int targetLength,
        bool createDirectory = true)
    {
        var paddingLength = checked(
            targetLength - root.Length - fileName.Length - 2);
        if (paddingLength is < 1 or > 240)
        {
            throw new InvalidOperationException(
                "The test root cannot produce the requested path length.");
        }

        var directory = Path.Combine(root, new string('x', paddingLength));
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
        }
        var path = Path.Combine(directory, fileName);
        Assert.Equal(targetLength, path.Length);
        return path;
    }

    private static void OpenAndClose(
        HostManagerProcessEffectValidationScopeAuthority authority,
        HostManagerComputeProcessIdentity identity)
    {
        var runNonce = Guid.NewGuid();
        var releaseToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var opened = authority.Open(new(
            runNonce,
            $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddMinutes(10),
            releaseToken,
            [new(identity.ProcessId, checked((long)identity.ProcessStartKey))],
            AllowAutomaticMemoryCleanup: true,
            AllowNonAdaptedMemoryTransaction: true));
        Assert.Equal("active", opened.State);

        var closed = authority.Close(new(
            opened.ScopeId!.Value,
            runNonce,
            releaseToken));
        Assert.Equal("closed", closed.State);
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ExactMemberProbe : IWindowsJobMembershipProbe
    {
        public WindowsJobMembershipResult Probe(
            string jobName,
            HostManagerComputeProcessIdentity identity)
            => new(WindowsJobMembershipStatus.ExactMember, 0);
    }
}
