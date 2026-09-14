using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Tests;

public sealed class ResourceManagerSelfSchedulingTransactionTests
{
    private const string PolicyId = "host-manager-smart-coordinator";
    private const int PayloadLimit = 4096;
    private const int EnvelopeLimit = 8192;

    [Fact]
    public async Task Capture_UsesTheActualSelfOwnerWithoutHttp()
    {
        var owner = new ResourceManagerSelfSchedulingControl();
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);

        var captured = await Capture(transaction);

        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.Captured, captured.Status);
        Assert.Equal(RuntimeAttributionIds.ResourceManagerSelf, captured.CapturedState!.Identity.SoftwareId);
        Assert.Equal(ResourceManagerSelfCpuGrade.Normal, owner.GetSchedulingSnapshot().CpuGrade);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Transaction_ReachesConsumersAndRestoresBothDimensions(bool cpu, bool gpu)
    {
        var zone = new RecordingZone();
        var owner = new ResourceManagerSelfSchedulingControl(standaloneComputeZones: [zone]);
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        var captured = await Capture(transaction);
        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.Captured, captured.Status);

        var applied = await Apply(transaction, captured, cpu, gpu);

        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Applied, applied.Status);
        Assert.Equal(cpu ? ResourceManagerSelfCpuGrade.Optimize : ResourceManagerSelfCpuGrade.Normal,
            owner.GetSchedulingSnapshot().CpuGrade);
        Assert.Equal(gpu ? ResourceManagerSelfGpuGrade.Optimize : ResourceManagerSelfGpuGrade.Normal,
            owner.GetSchedulingSnapshot().GpuGrade);
        Assert.Equal(cpu ? ResourceManagerComputeZoneMode.LowPower : ResourceManagerComputeZoneMode.Normal,
            zone.CurrentMode);

        var restored = await Restore(transaction, captured);
        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.Restored, restored.Status);
        Assert.Equal(ResourceManagerSelfCpuGrade.Normal, owner.GetSchedulingSnapshot().CpuGrade);
        Assert.Equal(ResourceManagerSelfGpuGrade.Normal, owner.GetSchedulingSnapshot().GpuGrade);
        Assert.Empty(owner.GetSchedulingSnapshot().Sources);
        Assert.Equal(ResourceManagerComputeZoneMode.Normal, zone.CurrentMode);
        Assert.Equal(cpu ? 2 : 0, zone.Calls);
        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.AlreadyRestored,
            (await Restore(transaction, captured)).Status);
    }

    [Fact]
    public async Task Restore_PreservesTheOtherCallersSource()
    {
        var owner = new ResourceManagerSelfSchedulingControl();
        owner.ApplyScheduling(ForeignGpu(AdapterGpuSchedulingGrade.Optimize));
        var foreign = Assert.Single(owner.GetSchedulingSnapshot().Sources);
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        var captured = await Capture(transaction);
        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.Captured, captured.Status);
        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Applied,
            (await Apply(transaction, captured, true, false)).Status);

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.Restored,
            (await Restore(transaction, captured)).Status);

        Assert.Equal(foreign, Assert.Single(owner.GetSchedulingSnapshot().Sources));
        Assert.Equal(ResourceManagerSelfGpuGrade.Optimize, owner.GetSchedulingSnapshot().GpuGrade);
        Assert.Equal(ResourceManagerSelfCpuGrade.Normal, owner.GetSchedulingSnapshot().CpuGrade);
    }

    [Fact]
    public async Task Restore_RejectsChangedForeignStateWithoutMutatingIt()
    {
        var owner = new ResourceManagerSelfSchedulingControl();
        owner.ApplyScheduling(ForeignGpu(AdapterGpuSchedulingGrade.Optimize));
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        var captured = await Capture(transaction);
        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.Captured, captured.Status);
        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Applied,
            (await Apply(transaction, captured, true, false)).Status);
        owner.ApplyScheduling(ForeignGpu(AdapterGpuSchedulingGrade.Normal));
        var before = owner.GetSchedulingSnapshot();

        var restored = await Restore(transaction, captured);

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.Conflict, restored.Status);
        Assert.Equal(before.CpuGrade, owner.GetSchedulingSnapshot().CpuGrade);
        Assert.Equal(before.GpuGrade, owner.GetSchedulingSnapshot().GpuGrade);
        Assert.Equal(before.Sources, owner.GetSchedulingSnapshot().Sources);
    }

    [Fact]
    public async Task Restore_ReturnsTheCapturedCoordinatorSourcesInsteadOfResettingAllToNormal()
    {
        var owner = new ResourceManagerSelfSchedulingControl();
        owner.ApplyScheduling(ForeignGpu(AdapterGpuSchedulingGrade.Optimize) with
        {
            PolicyId = PolicyId,
            TargetId = "older-coordinator-source"
        });
        var original = Assert.Single(owner.GetSchedulingSnapshot().Sources);
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        var captured = await Capture(transaction);
        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Applied,
            (await Apply(transaction, captured, true, false)).Status);

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.Restored,
            (await Restore(transaction, captured)).Status);

        Assert.Equal(original, Assert.Single(owner.GetSchedulingSnapshot().Sources));
        Assert.Equal(ResourceManagerSelfCpuGrade.Normal, owner.GetSchedulingSnapshot().CpuGrade);
        Assert.Equal(ResourceManagerSelfGpuGrade.Optimize, owner.GetSchedulingSnapshot().GpuGrade);
    }

    [Fact]
    public async Task Restore_RejectsAnotherOwnerInstanceBeforeAnyChange()
    {
        using var http = new HttpClient(new RejectHttp());
        var captured = await Capture(CreateTransaction(new ResourceManagerSelfSchedulingControl(), http));
        var newOwner = new ResourceManagerSelfSchedulingControl();
        newOwner.ApplyScheduling(ForeignGpu(AdapterGpuSchedulingGrade.Optimize));
        var before = newOwner.GetSchedulingSnapshot();

        var result = await Restore(CreateTransaction(newOwner, http), captured);

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.OwnershipLost, result.Status);
        Assert.Equal(before.Sources, newOwner.GetSchedulingSnapshot().Sources);
        Assert.Equal(before.GpuGrade, newOwner.GetSchedulingSnapshot().GpuGrade);
    }

    [Fact]
    public async Task Restore_RejectsACapturedSlotTakenByAnotherCaller()
    {
        var owner = new ResourceManagerSelfSchedulingControl();
        var envelope = ForeignGpu(AdapterGpuSchedulingGrade.Optimize) with { PolicyId = PolicyId };
        owner.ApplyScheduling(envelope);
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        var captured = await Capture(transaction);
        owner.ApplyScheduling(envelope with { PolicyId = "manual-takeover" });
        var before = owner.GetSchedulingSnapshot();

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.OwnershipLost,
            (await Restore(transaction, captured)).Status);

        Assert.Equal(before.Sources, owner.GetSchedulingSnapshot().Sources);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("limit")]
    [InlineData("deadline")]
    [InlineData("grade")]
    [InlineData("foreign-source")]
    [InlineData("duplicate-source")]
    [InlineData("expected-grade")]
    [InlineData("null-sources")]
    [InlineData("missing-grade")]
    public void Restore_RejectsInvalidCapturedStateWithoutChangingSources(string mutation)
    {
        var owner = new ResourceManagerSelfSchedulingControl();
        owner.ApplyScheduling(ForeignGpu(AdapterGpuSchedulingGrade.Optimize) with { PolicyId = PolicyId });
        var capture = owner.ExportCoordinatorSchedulingState(PayloadLimit, DateTimeOffset.UtcNow.AddMinutes(1));
        var command = new AdapterSchedulingStateRestoreCommand(capture.Identity!, capture.Payload!,
            capture.CurrentCpuGrade, capture.CurrentGpuGrade, PayloadLimit, DateTimeOffset.UtcNow.AddMinutes(1));
        switch (mutation)
        {
            case "version": command = command with { Payload = command.Payload with { Version = 2 } }; break;
            case "digest": command = command with { Payload = command.Payload with { Sha256Digest = new('a', 64) } }; break;
            case "limit": command = command with { MaximumPayloadBytes = command.Payload.Bytes.Length - 1 }; break;
            case "deadline": command = command with { Deadline = DateTimeOffset.UtcNow.AddMinutes(-1) }; break;
            case "expected-grade": command = command with { ExpectedGpuGrade = AdapterGpuSchedulingGrade.Normal }; break;
            default:
                var json = JsonNode.Parse(command.Payload.Bytes)!.AsObject();
                if (mutation == "grade") json["CpuGrade"] = 255;
                if (mutation == "foreign-source") json["Sources"]![0]!["PolicyId"] = "another-caller";
                if (mutation == "duplicate-source") json["Sources"]!.AsArray().Add(json["Sources"]![0]!.DeepClone());
                if (mutation == "null-sources") json["Sources"] = null;
                if (mutation == "missing-grade") json.Remove("CpuGrade");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
                command = command with { Payload = command.Payload with
                {
                    Bytes = bytes,
                    Sha256Digest = Convert.ToHexStringLower(SHA256.HashData(bytes))
                } };
                break;
        }
        var before = owner.GetSchedulingSnapshot();

        var restored = owner.RestoreCoordinatorSchedulingState(command);

        Assert.Equal(AdapterSchedulingStateRestoreStatus.Conflict, restored.Status);
        Assert.Equal(before.Sources, owner.GetSchedulingSnapshot().Sources);
    }

    [Fact]
    public void Capture_RespectsTheExplicitPayloadLimit()
    {
        var owner = new ResourceManagerSelfSchedulingControl();
        Assert.Equal(AdapterSchedulingStateExportStatus.Conflict,
            owner.ExportCoordinatorSchedulingState(1, DateTimeOffset.UtcNow.AddMinutes(1)).Status);
        Assert.Empty(owner.GetSchedulingSnapshot().Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_DoesNotPublishSuccessWhenAnActualConsumerDoesNotApply(bool silent)
    {
        var zone = new RecordingZone();
        var owner = new ResourceManagerSelfSchedulingControl(standaloneComputeZones: [zone]);
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        var captured = await Capture(transaction);
        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Applied,
            (await Apply(transaction, captured, true, false)).Status);
        zone.ThrowOnNormal = !silent;
        zone.IgnoreNormal = silent;

        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.StateUncertain,
            (await Restore(transaction, captured)).Status);
        Assert.Equal(ResourceManagerSelfCpuGrade.Optimize, owner.GetSchedulingSnapshot().CpuGrade);
        Assert.Equal(ResourceManagerComputeZoneMode.LowPower, zone.CurrentMode);
        Assert.NotEmpty(owner.GetSchedulingSnapshot().Sources);

        zone.ThrowOnNormal = false;
        zone.IgnoreNormal = false;
        Assert.Equal(HostManagerAdapterSchedulingRestoreStatus.Restored,
            (await Restore(transaction, captured)).Status);
        Assert.Equal(ResourceManagerComputeZoneMode.Normal, zone.CurrentMode);
    }

    [Fact]
    public async Task Capture_RejectsLogicalStateAfterActualConsumerApplicationFailed()
    {
        var zone = new RecordingZone();
        var owner = new ResourceManagerSelfSchedulingControl(standaloneComputeZones: [zone]);
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        var baseline = await Capture(transaction);
        await Apply(transaction, baseline, true, false);
        var captured = await Capture(transaction);
        zone.ThrowOnNormal = true;
        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.StateUncertain,
            (await Apply(transaction, captured, true, false, AdapterCpuSchedulingGrade.Normal)).Status);

        Assert.Equal(HostManagerAdapterSchedulingCaptureStatus.Unavailable, (await Capture(transaction)).Status);
        Assert.Equal(ResourceManagerComputeZoneMode.LowPower, zone.CurrentMode);
    }

    [Fact]
    public async Task Apply_ExplicitRetryReconcilesTheActualConsumerInsteadOfClaimingAlreadyApplied()
    {
        var zone = new RecordingZone();
        var owner = new ResourceManagerSelfSchedulingControl(standaloneComputeZones: [zone]);
        using var http = new HttpClient(new RejectHttp());
        var transaction = CreateTransaction(owner, http);
        await Apply(transaction, await Capture(transaction), true, false);
        var captured = await Capture(transaction);
        zone.ThrowOnNormal = true;
        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.StateUncertain,
            (await Apply(transaction, captured, true, false, AdapterCpuSchedulingGrade.Normal)).Status);
        zone.ThrowOnNormal = false;

        var reapplied = await Apply(transaction, captured, true, false, AdapterCpuSchedulingGrade.Normal);

        Assert.Equal(HostManagerAdapterSchedulingApplyStatus.Applied, reapplied.Status);
        Assert.Equal(ResourceManagerComputeZoneMode.Normal, zone.CurrentMode);
    }

    private static HostManagerAdapterSchedulingTransaction CreateTransaction(
        ResourceManagerSelfSchedulingControl owner, HttpClient http) =>
        new(new HttpAdapterPolicyDispatcher(http, new RejectRegistry(), owner), TimeProvider.System);

    private static Task<HostManagerAdapterSchedulingCaptureResult> Capture(
        HostManagerAdapterSchedulingTransaction transaction) => transaction.CaptureAsync(
        RuntimeAttributionIds.ResourceManagerSelf, PayloadLimit, EnvelopeLimit,
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private static Task<HostManagerAdapterSchedulingApplyResult> Apply(
        HostManagerAdapterSchedulingTransaction transaction,
        HostManagerAdapterSchedulingCaptureResult captured, bool cpu, bool gpu,
        AdapterCpuSchedulingGrade cpuGrade = AdapterCpuSchedulingGrade.Optimize) =>
        transaction.ApplyAsync(new(captured.CapturedState!.Envelope, PayloadLimit, EnvelopeLimit,
            PolicyId, DateTimeOffset.UtcNow, "process:123:456", "Resource Manager",
            cpu ? cpuGrade : null,
            gpu ? AdapterGpuSchedulingGrade.Optimize : null,
            10, 10, "actual self transaction", DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

    private static Task<HostManagerAdapterSchedulingRestoreResult> Restore(
        HostManagerAdapterSchedulingTransaction transaction,
        HostManagerAdapterSchedulingCaptureResult captured) =>
        transaction.RestoreAsync(new(captured.CapturedState!.Envelope, PayloadLimit, EnvelopeLimit,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

    private static AdapterSoftwareSchedulingEnvelope ForeignGpu(AdapterGpuSchedulingGrade grade) =>
        new("manual-gpu", DateTimeOffset.UtcNow, "manual-source", "Manual source",
            RuntimeAttributionIds.ResourceManagerSelf, null, grade, 0, 0, "manual");

    private sealed class RecordingZone : IResourceManagerSelfComputeZone
    {
        public ulong ZoneKey => 17;
        public string DisplayName => "Recording CPU consumer";
        public ResourceManagerComputeZoneMode CurrentMode { get; private set; }
        public int Calls { get; private set; }
        public bool ThrowOnNormal { get; set; }
        public bool IgnoreNormal { get; set; }
        public void ApplyMode(ResourceManagerComputeZoneMode mode)
        {
            if (ThrowOnNormal && mode == ResourceManagerComputeZoneMode.Normal)
            {
                throw new InvalidOperationException("Actual CPU consumer refused restoration.");
            }
            if (IgnoreNormal && mode == ResourceManagerComputeZoneMode.Normal)
            {
                return;
            }
            CurrentMode = mode;
            Calls++;
        }
    }

    private sealed class RejectHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Self scheduling must not use HTTP.");
    }

    private sealed class RejectRegistry : IAdapterSoftwareRegistry
    {
        public Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Self scheduling has no external registration.");
        public Task<AdapterRegistrationResult> RegisterAsync(AdapterSoftwareRegistrationRequest request,
            AdapterResourceMarkerProbeResult markerProbe, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
