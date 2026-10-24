using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeTransactionJournalCommitterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostCommitVerificationRejectsWrongLengthAndWrongContent(bool sameLength)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-journal-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var temporary = Path.Combine(root, "temporary.bin");
            var canonical = Path.Combine(root, "journal.bin");
            // Span more than one verification chunk to exercise the bounded read loop.
            var expected = new byte[8193];
            var actual = new byte[sameLength ? expected.Length : expected.Length - 1];
            actual[^1] = 1;
            await File.WriteAllBytesAsync(temporary, actual);
            var failure = await Assert.ThrowsAsync<NativeTransactionJournalCommitException>(
                async () => await WindowsNativeTransactionJournalFileCommitter.Instance.CommitAsync(
                    temporary, canonical, expected, CancellationToken.None));
            Assert.Equal(NativeTransactionJournalCommitOutcome.CommitAmbiguous, failure.Outcome);
            Assert.IsType<InvalidDataException>(failure.InnerException);
            Assert.Equal(actual, await File.ReadAllBytesAsync(canonical));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
