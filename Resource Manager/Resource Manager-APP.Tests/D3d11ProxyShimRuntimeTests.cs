using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class D3d11ProxyShimRuntimeTests
{
    [Theory]
    [InlineData(0x123456789abcdef0UL, "0x12345678_0x9abcdef0")]
    [InlineData(0xfedcba9887654321UL, "0xfedcba98_0x87654321")]
    public void PrepareExactPreservesThePlannedLuidWithoutResolvingCurrentAdapterOrder(ulong key, string luid)
    {
        using var files = new PolicyFiles();
        var prepared = files.Runtime.PrepareExact("process-target", key);
        Assert.True(prepared.PolicyPrepared, prepared.ErrorMessage);
        Assert.Equal("targetLuid", prepared.PolicyMode);
        Assert.Equal(luid, prepared.TargetLuid);
        Assert.Null(prepared.TargetAdapterIndex);
        Assert.Equal("exact-planned-luid", prepared.TargetResolution);
        Assert.Contains($"targetLuid={luid}", File.ReadAllText(prepared.PolicyPath!));
        Assert.Contains($"assignedPositionId=gpu-luid:{key:x16}", File.ReadAllText(prepared.PolicyPath!));
        Assert.DoesNotContain("targetAdapterIndex", File.ReadAllText(prepared.PolicyPath!));
    }

    [Fact]
    public void PrepareExactRejectsMissingIdentityWithoutPublishing()
    {
        using var files = new PolicyFiles();
        Assert.False(files.Runtime.PrepareExact("target", 0).PolicyPrepared);
        Assert.False(files.Runtime.PrepareExact(" ", 1).PolicyPrepared);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    [Fact]
    public void PrepareExactUsesTheExistingAtomicPublicationAndReportsARealWriteFailure()
    {
        using var files = new PolicyFiles();
        var first = files.Runtime.PrepareExact("target", 1);
        Assert.True(first.PolicyPrepared, first.ErrorMessage);
        var bytes = File.ReadAllBytes(first.PolicyPath!);
        using var reader = new FileStream(first.PolicyPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(files.Runtime.PrepareExact("target", 2).PolicyPrepared);
        using var saved = new MemoryStream();
        reader.CopyTo(saved);
        Assert.Equal(bytes, saved.ToArray());
        File.SetAttributes(first.PolicyPath!, FileAttributes.ReadOnly);
        var failure = files.Runtime.PrepareExact("target", 3);
        Assert.False(failure.PolicyPrepared);
        Assert.Contains("targetLuid=0x00000000_0x00000002", File.ReadAllText(first.PolicyPath!));
    }

    [Fact]
    public void Prepare_PublishesNewFileWithoutChangingAnOpenReadersBytes()
    {
        using var files = new PolicyFiles();
        var runtime = new D3d11ProxyShimRuntime(files);
        var first = runtime.Prepare(new("target", "Target", "first", true));
        Assert.True(first.PolicyPrepared, first.ErrorMessage);
        var oldBytes = File.ReadAllBytes(first.PolicyPath!);
        using var reader = new FileStream(first.PolicyPath!, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var second = runtime.Prepare(new("target", "Target", "second", false));

        Assert.True(second.PolicyPrepared, second.ErrorMessage);
        Assert.Equal(first.PolicyPath, second.PolicyPath);
        Assert.Contains("assignedPositionId=second", File.ReadAllText(second.PolicyPath!));
        using var captured = new MemoryStream();
        reader.CopyTo(captured);
        Assert.Equal(oldBytes, captured.ToArray());
    }

    [Fact]
    public void ReadPolicy_MissingFileDoesNotCreateDirectories()
    {
        using var files = new PolicyFiles();
        Assert.Null(files.Runtime.ReadPolicy("target"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
        Assert.True(files.Runtime.TryWritePolicy("target", null, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TryWritePolicy_RestoresOriginalBytesOrAbsenceAfterReinstantiation(int originalKind)
    {
        using var files = new PolicyFiles();
        byte[]? original = originalKind switch { 0 => null, 1 => [], _ => [0xef, 0xbb, 0xbf, 0xff, 0, 13, 10, 0x80] };
        Assert.True(files.Runtime.TryWritePolicy("target", null, original));
        var captured = files.Runtime.ReadPolicy("target");
        var applied = PolicyBytes("first", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", captured, applied));

        var restarted = new D3d11ProxyShimRuntime(files);
        Assert.Equal(applied, restarted.ReadPolicy("target"));
        Assert.True(restarted.TryWritePolicy("target", applied, captured));
        Assert.Equal(original, restarted.ReadPolicy("target"));
        Assert.Equal(original is not null, File.Exists(restarted.GetPolicyPath("target")));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(restarted.GetPolicyPath("target"))!, "*.tmp"));
    }

    [Fact]
    public void TryWritePolicy_DoesNotTreatAnEmptyFileAsAbsent()
    {
        using var files = new PolicyFiles();
        Assert.True(files.Runtime.TryWritePolicy("target", null, []));
        Assert.False(files.Runtime.TryWritePolicy("target", null, PolicyBytes("new", 1)));
        Assert.Empty(Assert.IsType<byte[]>(files.Runtime.ReadPolicy("target")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryWritePolicy_RejectsStaleApplicationOrRestorationWithoutChangingAnExternalValue(bool restore)
    {
        using var files = new PolicyFiles();
        var original = PolicyBytes("original", 1);
        var applied = PolicyBytes("applied", 2);
        var foreign = PolicyBytes("foreign", 3);
        Assert.True(files.Runtime.TryWritePolicy("target", null, restore ? applied : original));
        var path = files.Runtime.GetPolicyPath("target");
        File.WriteAllBytes(path, foreign);

        Assert.False(files.Runtime.TryWritePolicy("target", restore ? applied : original, restore ? original : applied));

        Assert.Equal(foreign, File.ReadAllBytes(path));
        Assert.Equal(path, Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!)));
    }

    [Fact]
    public void TryWritePolicy_SameValueIsANoOpEvenOnAReadonlyFile()
    {
        using var files = new PolicyFiles();
        var original = PolicyBytes("original", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", null, original));
        File.SetAttributes(files.Runtime.GetPolicyPath("target"), FileAttributes.ReadOnly);
        Assert.True(files.Runtime.TryWritePolicy("target", original, original));
        Assert.Equal(original, files.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public void ReadAndTryWritePolicy_DoNotConvertAReadErrorToFileAbsence()
    {
        using var files = new PolicyFiles();
        var original = PolicyBytes("original", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", null, original));
        var path = files.Runtime.GetPolicyPath("target");
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => files.Runtime.ReadPolicy("target"));
            Assert.Throws<IOException>(() => files.Runtime.TryWritePolicy("target", null, PolicyBytes("new", 2)));
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(path, Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!)));
    }

    [Fact]
    public void TryWritePolicy_FailedPublicationKeepsTheOriginalAndPreparedFiles()
    {
        using var files = new PolicyFiles();
        var original = PolicyBytes("original", 1);
        var applied = PolicyBytes("applied", 2);
        Assert.True(files.Runtime.TryWritePolicy("target", null, original));
        var path = files.Runtime.GetPolicyPath("target");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Throws<IOException>(() => files.Runtime.TryWritePolicy("target", original, applied));

        Assert.Equal(original, File.ReadAllBytes(path));
        var pending = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Equal(applied, File.ReadAllBytes(pending));
    }

    [Fact]
    public void TryWritePolicy_FailedRestoreKeepsTheAppliedAndPreparedOriginalFiles()
    {
        using var files = new PolicyFiles();
        var original = PolicyBytes("original", 1);
        var applied = PolicyBytes("applied", 2);
        Assert.True(files.Runtime.TryWritePolicy("target", null, applied));
        var path = files.Runtime.GetPolicyPath("target");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Throws<IOException>(() => files.Runtime.TryWritePolicy("target", applied, original));

        Assert.Equal(applied, File.ReadAllBytes(path));
        var pending = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Equal(original, File.ReadAllBytes(pending));
    }

    [Fact]
    public void TryWritePolicy_FailedRestoreToAbsenceDoesNotClaimSuccess()
    {
        using var files = new PolicyFiles();
        var applied = PolicyBytes("applied", 2);
        Assert.True(files.Runtime.TryWritePolicy("target", null, applied));
        var path = files.Runtime.GetPolicyPath("target");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.Throws<IOException>(() => files.Runtime.TryWritePolicy("target", applied, null));
        Assert.Equal(applied, File.ReadAllBytes(path));
    }

    [Fact]
    public void Prepare_FailedPublicationKeepsThePriorPolicy()
    {
        using var files = new PolicyFiles();
        var original = PolicyBytes("original", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", null, original));
        var path = files.Runtime.GetPolicyPath("target");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var result = files.Runtime.Prepare(new("target", "Target", "next", false));

        Assert.False(result.PolicyPrepared);
        Assert.Equal("policy-write-failed", result.TargetResolution);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(original, File.ReadAllBytes(path));
        var pending = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Contains("assignedPositionId=next", File.ReadAllText(pending));
    }

    [Fact]
    public void TryWritePolicy_RestoreToAbsenceImmediatelyUnlinksThePathWhileAReaderRemainsOpen()
    {
        using var files = new PolicyFiles();
        var applied = PolicyBytes("applied", 2);
        Assert.True(files.Runtime.TryWritePolicy("target", null, applied));
        var path = files.Runtime.GetPolicyPath("target");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(files.Runtime.TryWritePolicy("target", applied, null));

        Assert.Null(files.Runtime.ReadPolicy("target"));
        Assert.Throws<FileNotFoundException>(() => File.OpenRead(path));
        var next = PolicyBytes("next", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", null, next));
        Assert.Equal(next, files.Runtime.ReadPolicy("target"));
        using var captured = new MemoryStream();
        reader.CopyTo(captured);
        Assert.Equal(applied, captured.ToArray());
    }

    [Fact]
    public void TryWritePolicy_RestorationDoesNotChangeAnExistingReadersBytes()
    {
        using var files = new PolicyFiles();
        var applied = PolicyBytes("applied", 2);
        var original = PolicyBytes("original", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", null, applied));
        using var reader = new FileStream(files.Runtime.GetPolicyPath("target"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.True(files.Runtime.TryWritePolicy("target", applied, original));

        using var captured = new MemoryStream();
        reader.CopyTo(captured);
        Assert.Equal(applied, captured.ToArray());
        Assert.Equal(original, files.Runtime.ReadPolicy("target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryWritePolicy_DeleteSharingDenialPreservesTheCurrentFile(bool restoreToAbsence)
    {
        using var files = new PolicyFiles();
        var applied = PolicyBytes("applied", 2);
        var original = restoreToAbsence ? null : PolicyBytes("original", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", null, applied));
        var path = files.Runtime.GetPolicyPath("target");
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => files.Runtime.TryWritePolicy("target", applied, original));
        }
        Assert.Equal(applied, files.Runtime.ReadPolicy("target"));
        var pending = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").ToArray();
        if (restoreToAbsence) Assert.Empty(pending);
        else Assert.Equal(original, File.ReadAllBytes(Assert.Single(pending)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAndConditionalRestorationShareOneSerializationDomain(bool restoreToAbsence)
    {
        using var files = new PolicyFiles();
        var expected = PolicyBytes("applied", 2);
        var original = restoreToAbsence ? null : PolicyBytes("original", 1);
        Assert.True(files.Runtime.TryWritePolicy("target", null, expected));
        using var start = new Barrier(3);
        var prepare = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            return files.Runtime.Prepare(new("target", "Target", "prepared", false));
        });
        var restore = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            return files.Runtime.TryWritePolicy("target", expected, original);
        });
        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
        await Task.WhenAll(prepare, restore).WaitAsync(TimeSpan.FromSeconds(15));
        var prepared = await prepare;
        Assert.True(prepared.PolicyPrepared, prepared.ErrorMessage);
        Assert.Contains("assignedPositionId=prepared", File.ReadAllText(prepared.PolicyPath!));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(prepared.PolicyPath!)!, "*.tmp"));
    }

    [Fact]
    public void PolicyTargetsRemainIndependentAndCaseInsensitive()
    {
        using var files = new PolicyFiles();
        var first = PolicyBytes("first", 1);
        var second = PolicyBytes("second", 2);
        Assert.True(files.Runtime.TryWritePolicy("TargetA", null, first));
        Assert.True(files.Runtime.TryWritePolicy("TargetB", null, second));
        Assert.Equal(first, files.Runtime.ReadPolicy("targeta"));
        Assert.True(files.Runtime.TryWritePolicy("targeta", first, null));
        Assert.Null(files.Runtime.ReadPolicy("TargetA"));
        Assert.Equal(second, files.Runtime.ReadPolicy("TargetB"));
    }

    [Fact]
    public async Task ConcurrentWritersUsingTheSameExpectedValueCannotBothSucceed()
    {
        using var files = new PolicyFiles();
        var first = PolicyBytes("first", 1);
        var second = PolicyBytes("second", 2);
        using var start = new Barrier(3);
        var a = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10))); return files.Runtime.TryWritePolicy("target", null, first); });
        var b = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10))); return files.Runtime.TryWritePolicy("target", null, second); });
        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(results[0], results[1]);
        Assert.Equal(results[0] ? first : second, files.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public async Task NativeStyleReadersSeeOnlyCompletePoliciesDuringConcurrentPublication()
    {
        using var files = new PolicyFiles();
        var first = PolicyBytes("first", 1);
        var second = PolicyBytes("second", 2);
        Assert.True(files.Runtime.TryWritePolicy("target", null, first));
        var path = files.Runtime.GetPolicyPath("target");
        using var round = new Barrier(3);
        var observedReads = 0;
        Task Read() => Task.Run(() =>
        {
            for (var i = 0; i < 32; i++)
            {
                Assert.True(round.SignalAndWait(TimeSpan.FromSeconds(10)));
                for (var j = 0; j < 24; j++)
                {
                    using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = new byte[1023];
                    var length = reader.Read(bytes);
                    Assert.True(bytes.AsSpan(0, length).SequenceEqual(first) || bytes.AsSpan(0, length).SequenceEqual(second));
                    Interlocked.Increment(ref observedReads);
                }
                Assert.True(round.SignalAndWait(TimeSpan.FromSeconds(10)));
            }
        });
        var readers = new[] { Read(), Read() };
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 32; i++)
            {
                Assert.True(round.SignalAndWait(TimeSpan.FromSeconds(10)));
                Assert.True(files.Runtime.TryWritePolicy("target", i % 2 == 0 ? first : second, i % 2 == 0 ? second : first));
                Assert.True(round.SignalAndWait(TimeSpan.FromSeconds(10)));
            }
        });
        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1536, observedReads);
        Assert.Equal(first, files.Runtime.ReadPolicy("target"));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    private static byte[] PolicyBytes(string position, uint luid)
        => Encoding.ASCII.GetBytes(D3d11ProxyShimRuntime.BuildPolicyText(
            new("targetLuid", $"0x00000000_0x{luid:x8}", 0, "Test GPU", "exact-dxgi-luid"), position));

    [Fact]
    public void BuildPolicyText_WritesExactTargetLuid()
    {
        var target = new D3d11ProxyShimTarget(
            "targetLuid",
            "0x0000002a_0x000000ff",
            1,
            "Test GPU",
            "exact-dxgi-luid");

        var text = D3d11ProxyShimRuntime.BuildPolicyText(target, "gpu:1");

        Assert.Contains("mode=targetLuid", text);
        Assert.Contains("targetLuid=0x0000002a_0x000000ff", text);
        Assert.Contains("targetAdapterIndex=1", text);
        Assert.Contains("targetResolution=exact-dxgi-luid", text);
    }

    [Theory]
    [InlineData("gpu:0", 0)]
    [InlineData("GPU1", 1)]
    public void TryParseGpuPositionIndex_AcceptsSchedulingPositionIds(string positionId, int expectedIndex)
    {
        var parsed = D3d11ProxyShimRuntime.TryParseGpuPositionIndex(positionId, out var index);

        Assert.True(parsed);
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void ResolveTarget_UsesTheExactAdapterWhenItIsAvailable()
    {
        var target = D3d11ProxyShimRuntime.ResolveTarget(
            new D3d11ProxyShimPreparationRequest("target", "Target", "gpu:1", false),
            [CreateAdapter(0, false, 10), CreateAdapter(1, false, 20)]);

        Assert.Equal("targetLuid", target.Mode);
        Assert.Equal(1, target.AdapterIndex);
        Assert.Equal("exact-dxgi-luid", target.Resolution);
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 20u)]
    public void ResolveTarget_RejectsUnavailableExactAdapterWithoutClassFallback(bool software, uint luidLowPart)
    {
        var exception = Assert.Throws<ExactGpuTargetUnavailableException>(() =>
            D3d11ProxyShimRuntime.ResolveTarget(
                new D3d11ProxyShimPreparationRequest("target", "Target", "GPU1", false),
                [CreateAdapter(1, software, luidLowPart)]));

        Assert.Equal("exact-target-unavailable", exception.Resolution);
        Assert.Equal(1, exception.AdapterIndex);
    }

    [Fact]
    public void ResolveTarget_RejectsMalformedExactAdapterWithoutClassFallback()
    {
        var exception = Assert.Throws<ExactGpuTargetUnavailableException>(() =>
            D3d11ProxyShimRuntime.ResolveTarget(
                new D3d11ProxyShimPreparationRequest("target", "Target", "GPUinvalid", false),
                [CreateAdapter(0, false, 10)]));

        Assert.Equal("exact-target-invalid", exception.Resolution);
        Assert.Null(exception.AdapterIndex);
    }

    private static WindowsGpuAdapter CreateAdapter(int index, bool software, uint luidLowPart)
    {
        return new WindowsGpuAdapter(
            index,
            $"GPU {index}",
            0,
            0,
            0,
            new AdapterLuid { LowPart = luidLowPart, HighPart = 0 },
            software,
            (ulong)(index + 1) * 1024,
            WindowsGpuAdapterKind.Dedicated);
    }

    internal sealed class PolicyFiles : IHostEnvironment, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"gpu-policy-{Guid.NewGuid():N}");
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GpuPolicyTests";
        public string ContentRootPath { get => Root; set => throw new NotSupportedException(); }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public D3d11ProxyShimRuntime Runtime { get; }

        public PolicyFiles()
        {
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT")));
            Directory.CreateDirectory(Root);
            Runtime = new(this);
        }

        public void Dispose()
        {
            var pending = new Queue<string>();
            var directories = new List<string>();
            pending.Enqueue(Root);
            while (pending.TryDequeue(out var directory))
            {
                Assert.True(directories.Count < 40);
                Assert.False(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint));
                directories.Add(directory);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Enqueue(child);
            }
            for (var i = directories.Count - 1; i >= 0; i--) Directory.Delete(directories[i]);
        }
    }
}
