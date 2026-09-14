using System.Security.Cryptography;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private sealed class LongSoakSampleShardWriter : IDisposable
    {
        private readonly string root;
        private readonly string samplesRoot;
        private readonly int maximumShardCount;
        private readonly List<LongSoakShardIdentity> completedShards = [];
        private FileStream? currentStream;
        private string? currentPath;
        private int currentCount;
        private int currentFirstSequence;
        private int currentLastSequence;
        private LongSoakCompactSample? currentFirstSample;
        private LongSoakCompactSample? currentLastSample;
        private int totalCount;
        private long totalBytes;
        private bool sealedWriter;
        private Exception? failure;

        internal LongSoakSampleShardWriter(string evidenceRoot, int maximumShardCount)
        {
            root = ValidateScoreOnlyPerformanceEvidenceRoot(evidenceRoot);
            if (maximumShardCount is < 1 or > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumShardCount));
            }
            this.maximumShardCount = maximumShardCount;
            samplesRoot = Path.Combine(root, "samples");
            if (Directory.Exists(samplesRoot))
            {
                throw new InvalidDataException(
                    "The long-soak samples directory must not exist before capture.");
            }
            Directory.CreateDirectory(samplesRoot);
            var info = new DirectoryInfo(samplesRoot);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "The long-soak samples directory cannot be a reparse point.");
            }
        }

        internal int RecordCount => totalCount;

        internal LongSoakShardIndex Seal(string evidenceRunId, string profile)
        {
            ThrowIfFaulted();
            if (sealedWriter)
            {
                throw new InvalidOperationException("The long-soak shard writer is already sealed.");
            }
            if (totalCount == 0)
            {
                throw new InvalidDataException("The long-soak shard writer cannot seal an empty corpus.");
            }

            FinalizeCurrentShard();
            sealedWriter = true;
            if (completedShards.Count is < 1 || completedShards.Count > maximumShardCount)
            {
                throw new InvalidDataException("The long-soak shard count is outside its bound.");
            }
            return new LongSoakShardIndex(
                1,
                LongSoakShardIndexContract,
                evidenceRunId,
                profile,
                LongSoakRecordsPerShard,
                totalCount,
                totalBytes,
                [.. completedShards],
                Sealed: true);
        }

        internal void Append(LongSoakCompactSample sample)
        {
            try
            {
                ThrowIfFaulted();
                if (sealedWriter)
                {
                    throw new InvalidOperationException(
                        "The long-soak shard writer cannot append after seal.");
                }
                if (sample.Sequence != checked(totalCount + 1))
                {
                    throw new InvalidDataException(
                        "The long-soak sample sequence is not exact and contiguous.");
                }
                if (currentStream is null || currentCount == LongSoakRecordsPerShard)
                {
                    FinalizeCurrentShard();
                    OpenNextShard();
                }

                var image = JsonSerializer.SerializeToUtf8Bytes(
                    sample,
                    LongSoakCompactJsonOptions);
                if (image.Length is <= 0 or > LongSoakMaximumLineBytes)
                {
                    throw new InvalidDataException(
                        "A long-soak compact sample exceeded its line-size bound.");
                }
                var lineBytes = checked(image.LongLength + 1);
                if (checked(totalBytes + lineBytes) > LongSoakMaximumTotalSampleBytes)
                {
                    throw new InvalidDataException(
                        "The long-soak compact corpus exceeded its total byte bound.");
                }

                currentStream!.Write(image);
                currentStream.WriteByte((byte)'\n');
                currentStream.Flush(flushToDisk: true);
                currentCount++;
                totalCount++;
                totalBytes += lineBytes;
                currentFirstSequence = currentFirstSequence == 0
                    ? sample.Sequence
                    : currentFirstSequence;
                currentLastSequence = sample.Sequence;
                currentFirstSample ??= sample;
                currentLastSample = sample;
            }
            catch (Exception exception)
            {
                failure ??= exception;
                throw;
            }
        }

        private void OpenNextShard()
        {
            if (completedShards.Count >= maximumShardCount)
            {
                throw new InvalidDataException(
                    "The long-soak compact corpus exceeded its shard-count bound.");
            }
            currentPath = Path.Combine(
                samplesRoot,
                $"shard-{completedShards.Count:D4}.jsonl");
            currentStream = new FileStream(
                currentPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            currentCount = 0;
            currentFirstSequence = 0;
            currentLastSequence = 0;
            currentFirstSample = null;
            currentLastSample = null;
        }

        private void FinalizeCurrentShard()
        {
            if (currentStream is null)
            {
                return;
            }
            currentStream.Flush(flushToDisk: true);
            currentStream.Dispose();
            currentStream = null;
            var path = currentPath
                ?? throw new InvalidOperationException("The current shard path is unavailable.");
            var file = new FileInfo(path);
            if (!file.Exists
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || file.Length <= 0)
            {
                throw new InvalidDataException("A long-soak shard was not sealed as a regular file.");
            }
            var first = currentFirstSample
                ?? throw new InvalidDataException("A sealed long-soak shard has no first sample.");
            var last = currentLastSample
                ?? throw new InvalidDataException("A sealed long-soak shard has no last sample.");
            if (!string.Equals(
                    first.ProducerInstanceId,
                    last.ProducerInstanceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    first.OpportunityLedgerId,
                    last.OpportunityLedgerId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    first.WriterInstanceId,
                    last.WriterInstanceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    first.TestHostInstanceId,
                    last.TestHostInstanceId,
                    StringComparison.Ordinal)
                || first.TestHostProcessId != last.TestHostProcessId
                || first.TestHostStartUtcTicks != last.TestHostStartUtcTicks
                || !string.Equals(
                    first.BootIdentitySha256,
                    last.BootIdentitySha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A long-soak shard crossed a producer, ledger, writer, process, or boot identity.");
            }
            var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            completedShards.Add(new LongSoakShardIdentity(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                currentCount,
                file.Length,
                sha256,
                currentFirstSequence,
                currentLastSequence,
                completedShards.Count == 0 ? string.Empty : completedShards[^1].Sha256,
                first.OpportunityActiveTime100Nanoseconds,
                last.OpportunityActiveTime100Nanoseconds,
                first.OpportunityQpcTicks,
                last.OpportunityQpcTicks,
                first.OpportunityUtcTicks,
                last.OpportunityUtcTicks,
                first.ProducerInstanceId,
                first.OpportunityLedgerId,
                first.WriterInstanceId,
                first.TestHostInstanceId,
                first.TestHostProcessId,
                first.TestHostStartUtcTicks,
                first.BootIdentitySha256));
            currentPath = null;
            currentCount = 0;
            currentFirstSequence = 0;
            currentLastSequence = 0;
            currentFirstSample = null;
            currentLastSample = null;
        }

        private void ThrowIfFaulted()
        {
            if (failure is not null)
            {
                throw new InvalidDataException(
                    "The long-soak shard writer has a sticky failure.",
                    failure);
            }
        }

        public void Dispose()
        {
            currentStream?.Dispose();
            currentStream = null;
        }
    }
}
