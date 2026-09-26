using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Diagnostics;

namespace Resource_Manager_APP.Tests;

public sealed class JsonDebugDiagnosticLogWriterTests
{
    [Fact]
    public async Task DisabledLoggingDoesNotQueueOrTouchTheFileSystem()
    {
        var root = CreateRoot();
        try
        {
            var plans = new TestRuntimePlanProvider(
                CreatePlan(debugLogEnabled: false));
            using var writer = new JsonDebugDiagnosticLogWriter(
                new TestHostEnvironment(root),
                plans,
                NullLogger<JsonDebugDiagnosticLogWriter>.Instance);

            await writer.StartAsync(CancellationToken.None);
            Assert.False(writer.TryWrite(CreateRecord()));
            await writer.StopAsync(CancellationToken.None);

            Assert.False(Directory.Exists(
                Path.Combine(root, "Config", "Diagnostics", "DebugLogs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnabledLoggingWritesAndDisablingClosesTheAdmissionGate()
    {
        var root = CreateRoot();
        try
        {
            var plans = new TestRuntimePlanProvider(
                CreatePlan(debugLogEnabled: true));
            using var writer = new JsonDebugDiagnosticLogWriter(
                new TestHostEnvironment(root),
                plans,
                NullLogger<JsonDebugDiagnosticLogWriter>.Instance);

            await writer.StartAsync(CancellationToken.None);
            Assert.True(writer.TryWrite(CreateRecord()));
            var logPath = Path.Combine(
                root,
                "Config",
                "Diagnostics",
                "DebugLogs",
                "debug-log.jsonl");
            await WaitForFileAsync(logPath);

            plans.Publish(CreatePlan(debugLogEnabled: false));
            Assert.False(writer.TryWrite(CreateRecord()));
            await writer.StopAsync(CancellationToken.None);

            Assert.Single(File.ReadAllLines(logPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BoundedQueueReportsCapacityDropBeforeConsumerStarts()
    {
        var root = CreateRoot();
        try
        {
            var plans = new TestRuntimePlanProvider(
                CreatePlan(debugLogEnabled: true));
            using var writer = new JsonDebugDiagnosticLogWriter(
                new TestHostEnvironment(root),
                plans,
                NullLogger<JsonDebugDiagnosticLogWriter>.Instance);

            for (var index = 0; index < JsonDebugDiagnosticLogWriter.QueueCapacity; index++)
            {
                Assert.True(writer.TryWrite(CreateRecord()));
            }
            Assert.False(writer.TryWrite(CreateRecord()));

            var status = writer.CaptureStatus();
            Assert.Equal(JsonDebugDiagnosticLogWriter.QueueCapacity, status.AcceptedRecordCount);
            Assert.Equal(1, status.DroppedRecordCount);
            Assert.Equal(0, status.WrittenRecordCount);
            Assert.Equal(0, status.FailedRecordCount);
            Assert.Equal(0, status.RejectedRecordCount);
            Assert.False(status.Sealed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvidenceSealDrainsExactAcceptedRecordsAndClosesAdmission()
    {
        const int recordCount = 128;
        var root = CreateRoot();
        try
        {
            var plans = new TestRuntimePlanProvider(
                CreatePlan(debugLogEnabled: true));
            using var writer = new JsonDebugDiagnosticLogWriter(
                new TestHostEnvironment(root),
                plans,
                NullLogger<JsonDebugDiagnosticLogWriter>.Instance);

            await writer.StartAsync(CancellationToken.None);
            for (var index = 0; index < recordCount; index++)
            {
                Assert.True(writer.TryWrite(CreateRecord()));
            }

            var receipt = await writer.SealForEvidenceAsync(
                recordCount,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await writer.StopAsync(CancellationToken.None);

            Assert.True(receipt.Satisfied);
            Assert.Equal(recordCount, receipt.ExpectedAcceptedRecordCount);
            Assert.Equal(recordCount, receipt.AcceptedRecordCount);
            Assert.Equal(recordCount, receipt.WrittenRecordCount);
            Assert.Equal(0, receipt.DroppedRecordCount);
            Assert.Equal(0, receipt.FailedRecordCount);
            Assert.Equal(0, receipt.RejectedRecordCount);
            Assert.Equal(recordCount, File.ReadAllLines(receipt.LogPath).Length);
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(receipt.LogPath))),
                receipt.LogSha256);
            Assert.True(receipt.QpcFrequency > 0);
            Assert.InRange(
                receipt.AdmissionClosedAtQpcTicks,
                receipt.SealStartedAtQpcTicks,
                long.MaxValue);
            Assert.InRange(
                receipt.DrainCompletedAtQpcTicks,
                receipt.AdmissionClosedAtQpcTicks,
                long.MaxValue);
            Assert.NotNull(receipt.FlushStartedAtQpcTicks);
            Assert.NotNull(receipt.FlushCompletedAtQpcTicks);
            Assert.NotNull(receipt.HashCompletedAtQpcTicks);
            Assert.InRange(
                receipt.FlushStartedAtQpcTicks!.Value,
                receipt.DrainCompletedAtQpcTicks,
                long.MaxValue);
            Assert.InRange(
                receipt.FlushCompletedAtQpcTicks!.Value,
                receipt.FlushStartedAtQpcTicks.Value,
                long.MaxValue);
            Assert.InRange(
                receipt.HashCompletedAtQpcTicks!.Value,
                receipt.FlushCompletedAtQpcTicks.Value,
                long.MaxValue);
            Assert.InRange(
                receipt.SealCompletedAtQpcTicks,
                receipt.HashCompletedAtQpcTicks.Value,
                long.MaxValue);
            Assert.False(writer.TryWrite(CreateRecord()));
            var status = writer.CaptureStatus();
            Assert.True(status.Sealed);
            Assert.Equal(1, status.RejectedRecordCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentAdmissionCannotAdvanceAfterSealTransitionCompletes()
    {
        const int admissionAttemptCount = 512;
        var root = CreateRoot();
        try
        {
            var plans = new TestRuntimePlanProvider(
                CreatePlan(debugLogEnabled: true));
            using var writer = new JsonDebugDiagnosticLogWriter(
                new TestHostEnvironment(root),
                plans,
                NullLogger<JsonDebugDiagnosticLogWriter>.Instance);

            await writer.StartAsync(CancellationToken.None);
            var start = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var admissions = Enumerable.Range(0, admissionAttemptCount)
                .Select(async _ =>
                {
                    await start.Task;
                    return writer.TryWrite(CreateRecord());
                })
                .ToArray();
            var seal = Task.Run(async () =>
            {
                await start.Task;
                return await Assert.ThrowsAsync<InvalidDataException>(
                    () => writer.SealForEvidenceAsync(
                        long.MaxValue,
                        TimeSpan.FromSeconds(5),
                        CancellationToken.None));
            });

            start.SetResult(true);
            _ = await seal;
            var acceptedWhenSealCompleted = writer.CaptureStatus().AcceptedRecordCount;
            var admissionResults = await Task.WhenAll(admissions);
            await Task.Delay(50);
            var finalStatus = writer.CaptureStatus();

            Assert.True(finalStatus.Sealed);
            Assert.Equal(acceptedWhenSealCompleted, finalStatus.AcceptedRecordCount);
            Assert.Equal(
                admissionResults.LongCount(static accepted => accepted),
                finalStatus.AcceptedRecordCount);
            Assert.Equal(
                admissionAttemptCount,
                finalStatus.AcceptedRecordCount +
                finalStatus.DroppedRecordCount +
                finalStatus.RejectedRecordCount);

            using var shutdownCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await writer.StopAsync(shutdownCancellation.Token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CompiledRuntimePlan CreatePlan(bool debugLogEnabled)
        => CompiledRuntimePlan.Default with
        {
            Diagnostics = new CompiledDiagnosticsPlan(
                DebugModeEnabled: debugLogEnabled,
                DebugLogEnabled: debugLogEnabled,
                HostManagerSmartCoordinatorScoreOnlyEnabled: false,
                HostManagerSmartCoordinatorPerformanceLogEnabled: false)
        };

    private static DebugDiagnosticLogRecord CreateRecord()
        => new(
            DateTimeOffset.UtcNow,
            "test",
            "write",
            1,
            new Dictionary<string, object?>());

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"debug-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!File.Exists(path))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The enabled debug log was not written.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class TestRuntimePlanProvider(
        CompiledRuntimePlan initialPlan) : IRuntimePlanProvider
    {
        private CompiledRuntimePlan current = initialPlan;

        public CompiledRuntimePlan Current => Volatile.Read(ref current);

        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 1);

        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
        {
            Volatile.Write(ref current, plan);
            return new RuntimePlanPublicationResult(plan, 1, []);
        }
    }

    private sealed class TestHostEnvironment(
        string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
