using System.Security.Cryptography;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Tests;

public sealed class HostManagerAdapterSchedulingTransactionTests
{
    private const int MaximumOpaquePayloadBytes = 1024;
    private const int MaximumEnvelopeBytes = 4096;
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddHours(1);

    [Fact]
    public async Task Capture_ExportsExactIdentityAndOwnsEnvelopeBytes()
    {
        var export = CreateExport();
        var dispatcher = new StrictFakeDispatcher { ExportResult = export };
        var transaction = CreateTransaction(dispatcher);

        var result = await transaction.CaptureAsync(
            export.Identity!.SoftwareId,
            MaximumOpaquePayloadBytes,
            MaximumEnvelopeBytes,
            Deadline,
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.Captured, result.Status);
        Assert.Equal(export.Identity, result.CapturedState!.Identity);
        var first = result.CapturedState.Envelope;
        first.AsSpan().Fill(0xff);
        Assert.NotEqual(first, result.CapturedState.Envelope);
        Assert.Equal(MaximumOpaquePayloadBytes, dispatcher.LastExportMaximumPayloadBytes);
        Assert.Equal(Deadline, dispatcher.LastExportDeadline);
    }

    [Fact]
    public async Task Capture_RejectsIdentityDriftAndOversizedOpaquePayload()
    {
        var dispatcher = new StrictFakeDispatcher
        {
            ExportResult = CreateExport() with
            {
                Identity = CreateIdentity() with { SoftwareId = "different" }
            }
        };
        var transaction = CreateTransaction(dispatcher);

        var identityDrift = await transaction.CaptureAsync(
            "software.alpha",
            MaximumOpaquePayloadBytes,
            MaximumEnvelopeBytes,
            Deadline,
            CancellationToken.None);

        dispatcher.ExportResult = CreateExport(new byte[MaximumOpaquePayloadBytes + 1]);
        var oversized = await transaction.CaptureAsync(
            "software.alpha",
            MaximumOpaquePayloadBytes,
            MaximumEnvelopeBytes * 2,
            Deadline,
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.Conflict, identityDrift.Status);
        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.InvalidPayload, oversized.Status);
    }

    [Fact]
    public async Task Capture_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateTransaction(new StrictFakeDispatcher()).CaptureAsync(
                "software.alpha",
                MaximumOpaquePayloadBytes,
                MaximumEnvelopeBytes,
                Deadline,
                cancellation.Token));
    }

    [Theory]
    [InlineData(true, false, (byte)HostManagerAdapterSchedulingApplyStatus.Applied)]
    [InlineData(false, false, (byte)HostManagerAdapterSchedulingApplyStatus.AlreadyApplied)]
    public async Task Apply_RequiresExactReadbackAndClassifiesChange(
        bool cpuChanged,
        bool gpuChanged,
        byte expectedStatus)
    {
        var identity = CreateIdentity();
        var dispatcher = new StrictFakeDispatcher
        {
            ApplyResult = CreateApplyResult(cpuChanged, gpuChanged),
            ExportResult = CreateExport(
                identity: identity,
                cpuGrade: AdapterCpuSchedulingGrade.Extreme,
                gpuGrade: AdapterGpuSchedulingGrade.Optimize)
        };

        var result = await CreateTransaction(dispatcher).ApplyAsync(
            CreateApplyCommand(Encode(CreateExport(identity: identity))),
            CancellationToken.None);

        Assert.Equal((HostManagerAdapterSchedulingApplyStatus)expectedStatus, result.Status);
        Assert.Equal(identity, result.ObservedIdentity);
        Assert.Equal(AdapterCpuSchedulingGrade.Extreme, result.ObservedCpuGrade);
        Assert.Equal(AdapterGpuSchedulingGrade.Optimize, result.ObservedGpuGrade);
        Assert.Equal(identity.SoftwareId, dispatcher.LastApplySoftwareId);
        Assert.Equal(Deadline, dispatcher.LastExportDeadline);
    }

    [Fact]
    public async Task Apply_MapsExplicitRejectionWithoutInventingReadbackSuccess()
    {
        var dispatcher = new StrictFakeDispatcher
        {
            ApplyResult = CreateApplyResult(false, false) with { Accepted = false }
        };

        var result = await CreateTransaction(dispatcher).ApplyAsync(
            CreateApplyCommand(Encode(CreateExport())),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Rejected, result.Status);
        Assert.Equal(0, dispatcher.ExportCallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Apply_SingleDimensionRequiresTheOtherCapturedValue(bool cpuOnly, bool otherChanged)
    {
        var original = CreateExport();
        var command = CreateApplyCommand(Encode(original),
            cpuGrade: cpuOnly ? AdapterCpuSchedulingGrade.Extreme : null,
            gpuGrade: cpuOnly ? null : AdapterGpuSchedulingGrade.Optimize);
        var dispatcher = new StrictFakeDispatcher
        {
            ApplyResult = CreateApplyResult(cpuOnly, !cpuOnly) with
            {
                AppliedCpuGrade = command.TargetCpuGrade,
                AppliedGpuGrade = command.TargetGpuGrade
            },
            ExportResult = CreateExport(
                cpuGrade: cpuOnly ? AdapterCpuSchedulingGrade.Extreme
                    : otherChanged ? AdapterCpuSchedulingGrade.Normal : original.CurrentCpuGrade,
                gpuGrade: !cpuOnly ? AdapterGpuSchedulingGrade.Optimize
                    : otherChanged ? AdapterGpuSchedulingGrade.Optimize : original.CurrentGpuGrade)
        };

        var result = await CreateTransaction(dispatcher).ApplyAsync(command, CancellationToken.None);

        Assert.Equal(otherChanged ? HostManagerAdapterSchedulingApplyStatus.StateUncertain
            : HostManagerAdapterSchedulingApplyStatus.Applied, result.Status);
    }

    [Fact]
    public async Task Apply_MapsReadbackIdentityDriftToOwnershipLost()
    {
        var dispatcher = new StrictFakeDispatcher
        {
            ApplyResult = CreateApplyResult(true, true),
            ExportResult = CreateExport(
                identity: CreateIdentity() with { LeaseGeneration = 99 },
                cpuGrade: AdapterCpuSchedulingGrade.Extreme,
                gpuGrade: AdapterGpuSchedulingGrade.Optimize)
        };

        var result = await CreateTransaction(dispatcher).ApplyAsync(
            CreateApplyCommand(Encode(CreateExport())),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.OwnershipLost, result.Status);
    }

    [Fact]
    public async Task Apply_MapsGradeOrReceiptDriftToStateUncertain()
    {
        var dispatcher = new StrictFakeDispatcher
        {
            ApplyResult = CreateApplyResult(true, true) with { PolicyId = "wrong-policy" },
            ExportResult = CreateExport(
                cpuGrade: AdapterCpuSchedulingGrade.Normal,
                gpuGrade: AdapterGpuSchedulingGrade.Optimize)
        };

        var result = await CreateTransaction(dispatcher).ApplyAsync(
            CreateApplyCommand(Encode(CreateExport())),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.StateUncertain, result.Status);
        Assert.Equal(AdapterCpuSchedulingGrade.Normal, result.ObservedCpuGrade);
    }

    [Fact]
    public async Task Apply_MapsNonCallerCancellationToStateUncertain()
    {
        var dispatcher = new StrictFakeDispatcher
        {
            ApplyException = new OperationCanceledException("transport cancellation")
        };

        var result = await CreateTransaction(dispatcher).ApplyAsync(
            CreateApplyCommand(Encode(CreateExport())),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.StateUncertain, result.Status);
    }

    [Fact]
    public async Task Apply_RejectsCorruptOrOverCapacityCapturedEnvelope()
    {
        var envelope = Encode(CreateExport());
        envelope[^1] ^= 0x01;
        var transaction = CreateTransaction(new StrictFakeDispatcher());

        var corrupt = await transaction.ApplyAsync(
            CreateApplyCommand(envelope),
            CancellationToken.None);
        var overOpaqueLimit = await transaction.ApplyAsync(
            CreateApplyCommand(Encode(CreateExport()), maximumOpaquePayloadBytes: 1),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Rejected, corrupt.Status);
        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Rejected, overOpaqueLimit.Status);
    }

    [Theory]
    [InlineData(AdapterSchedulingStateOwnershipResult.Restored, (byte)HostManagerAdapterSchedulingRestoreStatus.Restored)]
    [InlineData(AdapterSchedulingStateOwnershipResult.AlreadyRestored, (byte)HostManagerAdapterSchedulingRestoreStatus.AlreadyRestored)]
    public async Task Restore_MapsExactTerminalOwnership(
        AdapterSchedulingStateOwnershipResult ownership,
        byte expectedStatus)
    {
        var identity = CreateIdentity();
        var dispatcher = new StrictFakeDispatcher
        {
            RestoreResult = new AdapterSchedulingStateRestoreResult(
                AdapterSchedulingStateRestoreStatus.Restored,
                ownership,
                identity,
                AdapterCpuSchedulingGrade.Optimize,
                AdapterGpuSchedulingGrade.Normal,
                DateTimeOffset.UtcNow,
                "restored")
        };

        var result = await CreateTransaction(dispatcher).RestoreAsync(
            CreateRestoreCommand(Encode(CreateExport(identity: identity))),
            CancellationToken.None);

        Assert.Equal((HostManagerAdapterSchedulingRestoreStatus)expectedStatus, result.Status);
        Assert.Equal(identity, dispatcher.LastRestoreCommand!.Identity);
        Assert.Equal(AdapterCpuSchedulingGrade.Optimize, dispatcher.LastRestoreCommand.ExpectedCpuGrade);
        Assert.Equal(AdapterGpuSchedulingGrade.Normal, dispatcher.LastRestoreCommand.ExpectedGpuGrade);
        Assert.Equal(Deadline, dispatcher.LastRestoreCommand.Deadline);
    }

    [Theory]
    [InlineData(AdapterSchedulingStateRestoreStatus.Conflict, AdapterSchedulingStateOwnershipResult.OwnershipLost, (byte)HostManagerAdapterSchedulingRestoreStatus.OwnershipLost)]
    [InlineData(AdapterSchedulingStateRestoreStatus.Unsupported, AdapterSchedulingStateOwnershipResult.NotEvaluated, (byte)HostManagerAdapterSchedulingRestoreStatus.Unavailable)]
    [InlineData(AdapterSchedulingStateRestoreStatus.Unavailable, AdapterSchedulingStateOwnershipResult.NotEvaluated, (byte)HostManagerAdapterSchedulingRestoreStatus.Unavailable)]
    [InlineData(AdapterSchedulingStateRestoreStatus.Conflict, AdapterSchedulingStateOwnershipResult.NotEvaluated, (byte)HostManagerAdapterSchedulingRestoreStatus.Conflict)]
    public async Task Restore_MapsExplicitNonSuccessWithoutInventingSuccess(
        AdapterSchedulingStateRestoreStatus adapterStatus,
        AdapterSchedulingStateOwnershipResult ownership,
        byte expectedStatus)
    {
        var dispatcher = new StrictFakeDispatcher
        {
            RestoreResult = new AdapterSchedulingStateRestoreResult(
                adapterStatus,
                ownership,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                "not restored")
        };

        var result = await CreateTransaction(dispatcher).RestoreAsync(
            CreateRestoreCommand(Encode(CreateExport())),
            CancellationToken.None);

        Assert.Equal((HostManagerAdapterSchedulingRestoreStatus)expectedStatus, result.Status);
    }

    [Fact]
    public async Task Restore_RejectsInvalidPayloadBeforeCallingDispatcher()
    {
        var envelope = Encode(CreateExport());
        envelope[0] ^= 0xff;
        var dispatcher = new StrictFakeDispatcher();

        var result = await CreateTransaction(dispatcher).RestoreAsync(
            CreateRestoreCommand(envelope),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.InvalidPayload, result.Status);
        Assert.Equal(0, dispatcher.RestoreCallCount);
    }

    [Fact]
    public async Task Restore_MapsNonCallerCancellationToStateUncertain()
    {
        var dispatcher = new StrictFakeDispatcher
        {
            RestoreException = new OperationCanceledException("transport cancellation")
        };

        var result = await CreateTransaction(dispatcher).RestoreAsync(
            CreateRestoreCommand(Encode(CreateExport())),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.StateUncertain, result.Status);
    }

    [Fact]
    public async Task Restore_DoesNotTreatNonTerminalOwnershipAsAlreadyRestored()
    {
        var dispatcher = new StrictFakeDispatcher
        {
            RestoreResult = new AdapterSchedulingStateRestoreResult(
                AdapterSchedulingStateRestoreStatus.Restored,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                CreateIdentity(),
                AdapterCpuSchedulingGrade.Optimize,
                AdapterGpuSchedulingGrade.Normal,
                DateTimeOffset.UtcNow,
                "ambiguous")
        };

        var result = await CreateTransaction(dispatcher).RestoreAsync(
            CreateRestoreCommand(Encode(CreateExport())),
            CancellationToken.None);

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.StateUncertain, result.Status);
    }

    [Fact]
    public async Task Restore_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateTransaction(new StrictFakeDispatcher()).RestoreAsync(
                CreateRestoreCommand(Encode(CreateExport())),
                cancellation.Token));
    }

    private static HostManagerAdapterSchedulingTransaction CreateTransaction(
        StrictFakeDispatcher dispatcher) => new(dispatcher, TimeProvider.System);

    private static HostManagerAdapterSchedulingApplyCommand CreateApplyCommand(
        byte[] envelope,
        int maximumOpaquePayloadBytes = MaximumOpaquePayloadBytes,
        AdapterCpuSchedulingGrade? cpuGrade = AdapterCpuSchedulingGrade.Extreme,
        AdapterGpuSchedulingGrade? gpuGrade = AdapterGpuSchedulingGrade.Optimize) => new(
        envelope,
        maximumOpaquePayloadBytes,
        MaximumEnvelopeBytes,
        "policy-1",
        DateTimeOffset.UtcNow,
        "target-1",
        "Target 1",
        cpuGrade,
        gpuGrade,
        12.5,
        34.5,
        "explicit-test",
        Deadline);

    private static HostManagerAdapterSchedulingRestoreCommand CreateRestoreCommand(byte[] envelope) => new(
        envelope,
        MaximumOpaquePayloadBytes,
        MaximumEnvelopeBytes,
        Deadline);

    private static AdapterSchedulingStateIdentity CreateIdentity() => new(
        new AdapterInstanceId(1, 2),
        new AdapterInstanceLeaseId(3, 4),
        5,
        "software.alpha",
        "adapter.beta",
        "application.gamma");

    private static AdapterSchedulingStateExportResult CreateExport(
        byte[]? bytes = null,
        AdapterSchedulingStateIdentity? identity = null,
        AdapterCpuSchedulingGrade? cpuGrade = AdapterCpuSchedulingGrade.Optimize,
        AdapterGpuSchedulingGrade? gpuGrade = AdapterGpuSchedulingGrade.Normal)
    {
        bytes ??= [1, 2, 3, 4];
        return new AdapterSchedulingStateExportResult(
            AdapterSchedulingStateExportStatus.Exported,
            identity ?? CreateIdentity(),
            new AdapterSchedulingStatePayload(1, Digest(bytes), bytes),
            cpuGrade,
            gpuGrade,
            DateTimeOffset.UtcNow,
            "exported");
    }

    private static AdapterSoftwareSchedulingResult CreateApplyResult(bool cpuChanged, bool gpuChanged) => new(
        "policy-1",
        true,
        AdapterCpuSchedulingGrade.Extreme,
        AdapterGpuSchedulingGrade.Optimize,
        cpuChanged,
        gpuChanged,
        "applied",
        DateTimeOffset.UtcNow);

    private static byte[] Encode(AdapterSchedulingStateExportResult export)
    {
        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            MaximumEnvelopeBytes,
            out var envelope));
        return envelope;
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class StrictFakeDispatcher : IAdapterPolicyDispatcher
    {
        internal AdapterSchedulingStateExportResult ExportResult { get; set; } = CreateExport();

        internal AdapterSoftwareSchedulingResult ApplyResult { get; set; } = CreateApplyResult(true, true);

        internal AdapterSchedulingStateRestoreResult RestoreResult { get; set; } = new(
            AdapterSchedulingStateRestoreStatus.Restored,
            AdapterSchedulingStateOwnershipResult.Restored,
            CreateIdentity(),
            AdapterCpuSchedulingGrade.Optimize,
            AdapterGpuSchedulingGrade.Normal,
            DateTimeOffset.UtcNow,
            "restored");

        internal Exception? ApplyException { get; set; }

        internal Exception? RestoreException { get; set; }

        internal int ExportCallCount { get; private set; }

        internal int RestoreCallCount { get; private set; }

        internal string? LastApplySoftwareId { get; private set; }

        internal int LastExportMaximumPayloadBytes { get; private set; }

        internal DateTimeOffset LastExportDeadline { get; private set; }

        internal AdapterSchedulingStateRestoreCommand? LastRestoreCommand { get; private set; }

        public Task<AdapterSoftwareSchedulingResult> ApplySoftwareSchedulingAsync(
            string softwareId,
            AdapterSoftwareSchedulingEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastApplySoftwareId = softwareId;
            if (ApplyException is not null)
            {
                return Task.FromException<AdapterSoftwareSchedulingResult>(ApplyException);
            }
            return Task.FromResult(ApplyResult);
        }

        public Task<AdapterSchedulingStateExportResult> ExportSoftwareSchedulingStateAsync(
            string softwareId,
            int maximumPayloadBytes,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportCallCount++;
            LastExportMaximumPayloadBytes = maximumPayloadBytes;
            LastExportDeadline = deadline;
            return Task.FromResult(ExportResult);
        }

        public Task<AdapterSchedulingStateRestoreResult> RestoreSoftwareSchedulingStateAsync(
            string softwareId,
            AdapterSchedulingStateRestoreCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCallCount++;
            LastRestoreCommand = command;
            if (RestoreException is not null)
            {
                return Task.FromException<AdapterSchedulingStateRestoreResult>(RestoreException);
            }
            return Task.FromResult(RestoreResult);
        }
    }
}
