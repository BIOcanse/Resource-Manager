using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Infrastructure.NativeCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.Diagnostics;

public sealed class JsonDebugDiagnosticLogWriter(
    IHostEnvironment environment,
    IRuntimePlanProvider runtimePlanProvider,
    ILogger<JsonDebugDiagnosticLogWriter> logger,
    IBusinessBlockDiagnostics? blockDiagnostics = null) : BackgroundService, IDebugDiagnosticLogWriter
{
    private const long MaxLogFileBytes = 32L * 1024 * 1024;
    internal const int QueueCapacity = 1024;
    private readonly Channel<DebugDiagnosticLogRecord> channel = Channel.CreateBounded<DebugDiagnosticLogRecord>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly string logPath = DebugDiagnosticLogStorage.ResolvePath(environment.ContentRootPath);
    private readonly TimeProvider clock = new NativeFoundationTimeProvider();
    private readonly object admissionSync = new();
    private long acceptedRecordCount;
    private long writtenRecordCount;
    private long droppedRecordCount;
    private long failedRecordCount;
    private long rejectedRecordCount;
    private int sealedForEvidence;
    private bool hasPendingRecords;

    internal bool HasPendingRecords => Volatile.Read(ref hasPendingRecords);
    internal event Action? PendingRecordsChanged;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public bool TryWrite(DebugDiagnosticLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (admissionSync)
        {
            if (!runtimePlanProvider.Current.Diagnostics.DebugLogEnabled ||
                Volatile.Read(ref sealedForEvidence) != 0)
            {
                Interlocked.Increment(ref rejectedRecordCount);
                return false;
            }

            if (!channel.Writer.TryWrite(record))
            {
                Interlocked.Increment(ref droppedRecordCount);
                return false;
            }

            Interlocked.Increment(ref acceptedRecordCount);
            PublishPendingRecordsUnderLock();
            return true;
        }
    }

    internal JsonDebugDiagnosticLogWriterStatus CaptureStatus()
        => new(
            AcceptedRecordCount: Volatile.Read(ref acceptedRecordCount),
            WrittenRecordCount: Volatile.Read(ref writtenRecordCount),
            DroppedRecordCount: Volatile.Read(ref droppedRecordCount),
            FailedRecordCount: Volatile.Read(ref failedRecordCount),
            RejectedRecordCount: Volatile.Read(ref rejectedRecordCount),
            Sealed: Volatile.Read(ref sealedForEvidence) != 0);

    internal async Task<JsonDebugDiagnosticLogSealReceipt> SealForEvidenceAsync(
        long expectedAcceptedRecordCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedAcceptedRecordCount);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        var sealStartedAtQpcTicks = clock.GetTimestamp();
        lock (admissionSync)
        {
            if (Volatile.Read(ref sealedForEvidence) != 0)
            {
                throw new InvalidOperationException("The debug diagnostic log writer is already sealed.");
            }
            Volatile.Write(ref sealedForEvidence, 1);
            if (!channel.Writer.TryComplete())
            {
                throw new InvalidOperationException(
                    "The debug diagnostic log writer channel was already completed.");
            }
        }
        var admissionClosedAtQpcTicks = clock.GetTimestamp();

        var drainStartedAtQpcTicks = clock.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = CaptureStatus();
            if (status.AcceptedRecordCount != expectedAcceptedRecordCount)
            {
                throw new InvalidDataException(
                    $"The debug diagnostic log writer accepted {status.AcceptedRecordCount} records; " +
                    $"{expectedAcceptedRecordCount} were expected.");
            }
            if (checked(status.WrittenRecordCount + status.FailedRecordCount) ==
                status.AcceptedRecordCount)
            {
                break;
            }
            if (clock.GetElapsedTime(drainStartedAtQpcTicks) >= timeout)
            {
                throw new TimeoutException(
                    "The debug diagnostic log writer did not drain before its evidence deadline.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
        var drainCompletedAtQpcTicks = clock.GetTimestamp();

        string? sha256 = null;
        long length = 0;
        long? flushStartedAtQpcTicks = null;
        long? flushCompletedAtQpcTicks = null;
        long? hashCompletedAtQpcTicks = null;
        if (File.Exists(logPath))
        {
            flushStartedAtQpcTicks = clock.GetTimestamp();
            using (var flushStream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read))
            {
                flushStream.Flush(flushToDisk: true);
                length = flushStream.Length;
            }
            flushCompletedAtQpcTicks = clock.GetTimestamp();
            using var hashStream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            sha256 = Convert.ToHexString(SHA256.HashData(hashStream));
            hashCompletedAtQpcTicks = clock.GetTimestamp();
        }

        var finalStatus = CaptureStatus();
        var sealCompletedAtQpcTicks = clock.GetTimestamp();
        return new JsonDebugDiagnosticLogSealReceipt(
            ExpectedAcceptedRecordCount: expectedAcceptedRecordCount,
            finalStatus.AcceptedRecordCount,
            finalStatus.WrittenRecordCount,
            finalStatus.DroppedRecordCount,
            finalStatus.FailedRecordCount,
            finalStatus.RejectedRecordCount,
            LogPath: logPath,
            LogLength: length,
            LogSha256: sha256,
            QpcFrequency: clock.TimestampFrequency,
            SealStartedAtQpcTicks: sealStartedAtQpcTicks,
            AdmissionClosedAtQpcTicks: admissionClosedAtQpcTicks,
            DrainCompletedAtQpcTicks: drainCompletedAtQpcTicks,
            FlushStartedAtQpcTicks: flushStartedAtQpcTicks,
            FlushCompletedAtQpcTicks: flushCompletedAtQpcTicks,
            HashCompletedAtQpcTicks: hashCompletedAtQpcTicks,
            SealCompletedAtQpcTicks: sealCompletedAtQpcTicks);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(stoppingToken))
            {
                bool written;
                try
                {
                    written = await WriteRecordAsync(record, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    lock (admissionSync)
                    {
                        Interlocked.Increment(ref failedRecordCount);
                        PublishPendingRecordsUnderLock();
                    }
                    throw;
                }
                lock (admissionSync)
                {
                    if (written) Interlocked.Increment(ref writtenRecordCount);
                    else Interlocked.Increment(ref failedRecordCount);
                    PublishPendingRecordsUnderLock();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            blockDiagnostics?.ReportFailure(BlockUids.JsonDebugDiagnosticLogWriter, ex, "diagnostic-writer-loop");
            logger.LogDebug(ex, "Debug diagnostic log writer stopped unexpectedly.");
            throw;
        }
    }

    private Task<bool> WriteRecordAsync(
        DebugDiagnosticLogRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            string line;
            using (blockDiagnostics?.MeasureSegment(BlockUids.JsonDebugDiagnosticLogWriter))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? AppContext.BaseDirectory);
                NativeFoundation.RotateLog(logPath,
                    Path.Combine(Path.GetDirectoryName(logPath)!, "debug-log.1.jsonl"), MaxLogFileBytes);
                line = JsonSerializer.Serialize(record, JsonOptions);
            }
            cancellationToken.ThrowIfCancellationRequested();
            NativeFoundation.AppendLog(logPath,
                Path.Combine(Path.GetDirectoryName(logPath)!, "debug-log.1.jsonl"),
                MaxLogFileBytes, Encoding.UTF8.GetBytes(line + Environment.NewLine));
            return Task.FromResult(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            blockDiagnostics?.ReportFailure(BlockUids.JsonDebugDiagnosticLogWriter, ex, "diagnostic-record-write");
            logger.LogDebug(ex, "Failed to write debug diagnostic log record.");
            return Task.FromResult(false);
        }
    }

    private void PublishPendingRecordsUnderLock()
    {
        var pending = acceptedRecordCount > writtenRecordCount + failedRecordCount;
        if (hasPendingRecords == pending) return;
        Volatile.Write(ref hasPendingRecords, pending);
        PendingRecordsChanged?.Invoke();
    }
}

internal sealed record JsonDebugDiagnosticLogWriterStatus(
    long AcceptedRecordCount,
    long WrittenRecordCount,
    long DroppedRecordCount,
    long FailedRecordCount,
    long RejectedRecordCount,
    bool Sealed);

internal sealed record JsonDebugDiagnosticLogSealReceipt(
    long ExpectedAcceptedRecordCount,
    long AcceptedRecordCount,
    long WrittenRecordCount,
    long DroppedRecordCount,
    long FailedRecordCount,
    long RejectedRecordCount,
    string LogPath,
    long LogLength,
    string? LogSha256,
    long QpcFrequency,
    long SealStartedAtQpcTicks,
    long AdmissionClosedAtQpcTicks,
    long DrainCompletedAtQpcTicks,
    long? FlushStartedAtQpcTicks,
    long? FlushCompletedAtQpcTicks,
    long? HashCompletedAtQpcTicks,
    long SealCompletedAtQpcTicks)
{
    public bool Satisfied =>
        AcceptedRecordCount == ExpectedAcceptedRecordCount &&
        WrittenRecordCount == ExpectedAcceptedRecordCount &&
        DroppedRecordCount == 0 &&
        FailedRecordCount == 0 &&
        RejectedRecordCount == 0 &&
        LogLength > 0 &&
        !string.IsNullOrWhiteSpace(LogSha256);
}
