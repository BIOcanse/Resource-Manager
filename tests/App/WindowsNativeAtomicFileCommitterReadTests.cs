using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsNativeAtomicFileCommitterReadTests
{
    // A reader that shared only read made CommitReplace fail with a sharing violation, and a reader that
    // shared read and delete failed while the committer's handle still held the renamed file for writing.
    [Fact]
    public async Task CommittedReadersNeverBlockReplacementAndAlwaysSeeOneWholeVersion()
    {
        var root = Directory.CreateTempSubdirectory("rm-committed-read-").FullName;
        try
        {
            var target = Path.Combine(root, "store.bin");
            File.WriteAllBytes(target, new byte[4096]);
            const int commits = 250;
            var readFailures = 0;
            var tornReads = 0;
            var reads = 0;
            using var done = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(() =>
            {
                while (!done.IsCancellationRequested)
                {
                    started.TrySetResult();
                    try
                    {
                        var content = WindowsNativeAtomicFileCommitter.ReadCommittedBytes(target);
                        if (content.Length != 4096 || content.AsSpan().ContainsAnyExcept(content[0])) tornReads++;
                        reads++;
                    }
                    catch (IOException) { readFailures++; }
                }
            });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                for (var index = 1; index <= commits; index++)
                {
                    var temporary = Path.Combine(root, $"store.{index}.tmp");
                    var payload = new byte[4096];
                    Array.Fill(payload, (byte)index);
                    File.WriteAllBytes(temporary, payload);
                    WindowsNativeAtomicFileCommitter.CommitReplace(temporary, target);
                }
            }
            finally
            {
                await done.CancelAsync();
                await reader;
            }

            Assert.True(reads > 0);
            Assert.Equal(0, readFailures);
            Assert.Equal(0, tornReads);
            Assert.All(File.ReadAllBytes(target), value => Assert.Equal((byte)commits, value));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
