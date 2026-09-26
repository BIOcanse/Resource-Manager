using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class AdapterSchedulingStateDispatcherTests
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Export_RequiresAndReturnsExactIdentityPayloadAndGrades()
    {
        var payload = new byte[] { 1, 4, 9, 16, 25 };
        var digest = Digest(payload);
        var handler = new ScriptedHttpMessageHandler(async (request, cancellationToken) =>
        {
            var document = await ReadRequestAsync(request, cancellationToken);
            var root = document.RootElement;
            Assert.Equal("scheduling-state-export-v1", root.GetProperty("operation").GetString());
            Assert.Equal("adapter:test", root.GetProperty("softwareId").GetString());
            Assert.Equal("adapter:test.adapter", root.GetProperty("adapterId").GetString());
            Assert.Equal("adapter:test.app", root.GetProperty("applicationId").GetString());
            Assert.Equal(64, root.GetProperty("maximumPayloadBytes").GetInt32());
            Assert.Equal(Deadline, root.GetProperty("deadline").GetDateTimeOffset());

            return JsonResponse(new
            {
                operation = "scheduling-state-export-v1",
                requestId = root.GetProperty("requestId").GetGuid(),
                status = (byte)AdapterSchedulingStateExportStatus.Exported,
                identity = IdentityWire(),
                payloadVersion = 7U,
                payloadLength = payload.Length,
                payloadDigestSha256 = digest,
                payload,
                currentCpuGrade = "optimize",
                currentGpuGrade = "normal",
                observedAt = ObservedAt,
                message = "exported"
            });
        });

        var result = await CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
            "adapter:test",
            64,
            Deadline,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateExportStatus.Exported, result.Status);
        Assert.Equal(new AdapterInstanceId(0x11, 0x22), result.Identity!.AdapterInstanceId);
        Assert.Equal(new AdapterInstanceLeaseId(0x33, 0x44), result.Identity.LeaseId);
        Assert.Equal(5UL, result.Identity.LeaseGeneration);
        Assert.Equal("adapter:test", result.Identity.SoftwareId);
        Assert.Equal(7U, result.Payload!.Version);
        Assert.Equal(digest, result.Payload.Sha256Digest);
        Assert.Equal(payload, result.Payload.Bytes);
        Assert.Equal(AdapterCpuSchedulingGrade.Optimize, result.CurrentCpuGrade);
        Assert.Equal(AdapterGpuSchedulingGrade.Normal, result.CurrentGpuGrade);
        Assert.Equal(ObservedAt, result.ObservedAt);
    }

    [Fact]
    public async Task Export_DigestMismatchFailsClosedAsConflict()
    {
        var payload = new byte[] { 2, 3, 5, 7 };
        var handler = new ScriptedHttpMessageHandler(async (request, cancellationToken) =>
        {
            var root = (await ReadRequestAsync(request, cancellationToken)).RootElement;
            return JsonResponse(new
            {
                operation = "scheduling-state-export-v1",
                requestId = root.GetProperty("requestId").GetGuid(),
                status = (byte)AdapterSchedulingStateExportStatus.Exported,
                identity = IdentityWire(),
                payloadVersion = 1U,
                payloadLength = payload.Length,
                payloadDigestSha256 = new string('0', 64),
                payload,
                currentCpuGrade = "normal",
                currentGpuGrade = "normal",
                observedAt = ObservedAt,
                message = "exported"
            });
        });

        var result = await CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
            "adapter:test",
            64,
            Deadline,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateExportStatus.Conflict, result.Status);
        Assert.Null(result.Identity);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task Export_MismatchedSoftwareIdentityFailsClosedAsConflict()
    {
        var payload = new byte[] { 1, 2, 3 };
        var handler = new ScriptedHttpMessageHandler(async (request, cancellationToken) =>
        {
            var root = (await ReadRequestAsync(request, cancellationToken)).RootElement;
            return JsonResponse(new
            {
                operation = "scheduling-state-export-v1",
                requestId = root.GetProperty("requestId").GetGuid(),
                status = (byte)AdapterSchedulingStateExportStatus.Exported,
                identity = new
                {
                    adapterInstanceIdHigh = "0000000000000011",
                    adapterInstanceIdLow = "0000000000000022",
                    leaseIdHigh = "0000000000000033",
                    leaseIdLow = "0000000000000044",
                    leaseGeneration = "0000000000000005",
                    softwareId = "adapter:other",
                    adapterId = "adapter:test.adapter",
                    applicationId = "adapter:test.app"
                },
                payloadVersion = 1U,
                payloadLength = payload.Length,
                payloadDigestSha256 = Digest(payload),
                payload,
                currentCpuGrade = "normal",
                currentGpuGrade = "normal",
                observedAt = ObservedAt,
                message = "exported"
            });
        });

        var result = await CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
            "adapter:test",
            64,
            Deadline,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateExportStatus.Conflict, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task Export_UnknownWireMemberIsRejected()
    {
        var payload = new byte[] { 1, 2, 3 };
        var handler = new ScriptedHttpMessageHandler(async (request, cancellationToken) =>
        {
            var root = (await ReadRequestAsync(request, cancellationToken)).RootElement;
            return JsonResponse(new
            {
                operation = "scheduling-state-export-v1",
                requestId = root.GetProperty("requestId").GetGuid(),
                status = (byte)AdapterSchedulingStateExportStatus.Exported,
                identity = IdentityWire(),
                payloadVersion = 1U,
                payloadLength = payload.Length,
                payloadDigestSha256 = Digest(payload),
                payload,
                currentCpuGrade = "normal",
                currentGpuGrade = "normal",
                observedAt = ObservedAt,
                message = "exported",
                compatibilityPayload = "must-not-be-accepted"
            });
        });

        var result = await CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
            "adapter:test",
            64,
            Deadline,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateExportStatus.Conflict, result.Status);
        Assert.Null(result.Payload);
        Assert.Contains("wire", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented, AdapterSchedulingStateExportStatus.Unsupported)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AdapterSchedulingStateExportStatus.Unavailable)]
    [InlineData(HttpStatusCode.Conflict, AdapterSchedulingStateExportStatus.Conflict)]
    public async Task Export_HttpFailuresRemainExplicitAndCarryNoState(
        HttpStatusCode statusCode,
        AdapterSchedulingStateExportStatus expected)
    {
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));

        var result = await CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
            "adapter:test",
            64,
            Deadline,
            CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Identity);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task Export_ExplicitDeadlineCancelsBlockedHttpWithoutCallerCancellation()
    {
        var handler = new ScriptedHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Deadline cancellation must stop the blocked request.");
        });
        using var callerCancellation = new CancellationTokenSource();

        var result = await CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
            "adapter:test",
            64,
            DateTimeOffset.UtcNow.AddMilliseconds(100),
            callerCancellation.Token);

        Assert.Equal(AdapterSchedulingStateExportStatus.Unavailable, result.Status);
        Assert.False(callerCancellation.IsCancellationRequested);
        Assert.Contains("截止时间", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_CallerCancellationPropagatesInsteadOfBecomingDeadline()
    {
        var handler = new ScriptedHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Caller cancellation must stop the blocked request.");
        });
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
                "adapter:test",
                64,
                DateTimeOffset.UtcNow.AddMinutes(5),
                callerCancellation.Token));
    }

    [Fact]
    public async Task Export_SimultaneousCallerAndDeadlineCancellationPrefersCaller()
    {
        var handler = new ScriptedHttpMessageHandler(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                throw;
            }

            throw new InvalidOperationException("Cancellation must stop the blocked request.");
        });
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateDispatcher(handler).ExportSoftwareSchedulingStateAsync(
                "adapter:test",
                64,
                DateTimeOffset.UtcNow.AddMilliseconds(75),
                callerCancellation.Token));
    }

    [Fact]
    public async Task Restore_SubmitsOriginalExportAndRequiresExactObservedOwnership()
    {
        var command = CreateRestoreCommand();
        var handler = new ScriptedHttpMessageHandler(async (request, cancellationToken) =>
        {
            var root = (await ReadRequestAsync(request, cancellationToken)).RootElement;
            Assert.Equal("scheduling-state-restore-v1", root.GetProperty("operation").GetString());
            Assert.Equal(command.Payload.Version, root.GetProperty("payloadVersion").GetUInt32());
            Assert.Equal(command.Payload.Bytes.Length, root.GetProperty("payloadLength").GetInt32());
            Assert.Equal(command.Payload.Sha256Digest, root.GetProperty("payloadDigestSha256").GetString());
            Assert.Equal(command.Payload.Bytes, root.GetProperty("payload").GetBytesFromBase64());
            Assert.Equal("0000000000000011", root.GetProperty("identity").GetProperty("adapterInstanceIdHigh").GetString());
            Assert.Equal(command.MaximumPayloadBytes, root.GetProperty("maximumPayloadBytes").GetInt32());
            Assert.Equal(command.Deadline, root.GetProperty("deadline").GetDateTimeOffset());

            return JsonResponse(new
            {
                operation = "scheduling-state-restore-v1",
                requestId = root.GetProperty("requestId").GetGuid(),
                status = (byte)AdapterSchedulingStateRestoreStatus.Restored,
                ownershipResult = (byte)AdapterSchedulingStateOwnershipResult.Restored,
                identity = IdentityWire(),
                payloadVersion = command.Payload.Version,
                payloadLength = command.Payload.Bytes.Length,
                payloadDigestSha256 = command.Payload.Sha256Digest,
                observedCpuGrade = "optimize",
                observedGpuGrade = "normal",
                observedAt = ObservedAt,
                message = "restored"
            });
        });

        var result = await CreateDispatcher(handler).RestoreSoftwareSchedulingStateAsync(
            "adapter:test",
            command,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateRestoreStatus.Restored, result.Status);
        Assert.Equal(AdapterSchedulingStateOwnershipResult.Restored, result.OwnershipResult);
        Assert.Equal(command.Identity, result.Identity);
        Assert.Equal(command.ExpectedCpuGrade, result.ObservedCpuGrade);
        Assert.Equal(command.ExpectedGpuGrade, result.ObservedGpuGrade);
    }

    [Fact]
    public async Task Restore_TamperedPayloadNeverReachesAdapter()
    {
        var command = CreateRestoreCommand();
        command.Payload.Bytes[0] ^= 0xff;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Invalid restore input must not reach HTTP."));

        var result = await CreateDispatcher(handler).RestoreSoftwareSchedulingStateAsync(
            "adapter:test",
            command,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateRestoreStatus.Conflict, result.Status);
        Assert.Equal(AdapterSchedulingStateOwnershipResult.NotEvaluated, result.OwnershipResult);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Restore_ExplicitDeadlineCancelsBlockedHttp()
    {
        var command = CreateRestoreCommand() with
        {
            Deadline = DateTimeOffset.UtcNow.AddMilliseconds(100)
        };
        var handler = new ScriptedHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Deadline cancellation must stop the blocked request.");
        });

        var result = await CreateDispatcher(handler).RestoreSoftwareSchedulingStateAsync(
            "adapter:test",
            command,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateRestoreStatus.Unavailable, result.Status);
        Assert.Equal(AdapterSchedulingStateOwnershipResult.NotEvaluated, result.OwnershipResult);
        Assert.Contains("截止时间", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_OrdinaryApplyShapeCannotMasqueradeAsExactRestore()
    {
        var command = CreateRestoreCommand();
        var handler = new ScriptedHttpMessageHandler(async (request, cancellationToken) =>
        {
            _ = await ReadRequestAsync(request, cancellationToken);
            return JsonResponse(new
            {
                policyId = "ordinary-apply",
                accepted = true,
                appliedCpuGrade = "optimize",
                appliedGpuGrade = "normal"
            });
        });

        var result = await CreateDispatcher(handler).RestoreSoftwareSchedulingStateAsync(
            "adapter:test",
            command,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateRestoreStatus.Conflict, result.Status);
        Assert.Equal(AdapterSchedulingStateOwnershipResult.OwnershipLost, result.OwnershipResult);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task Restore_ChangedObservedGradeFailsClosedAsOwnershipConflict()
    {
        var command = CreateRestoreCommand();
        var handler = new ScriptedHttpMessageHandler(async (request, cancellationToken) =>
        {
            var root = (await ReadRequestAsync(request, cancellationToken)).RootElement;
            return JsonResponse(new
            {
                operation = "scheduling-state-restore-v1",
                requestId = root.GetProperty("requestId").GetGuid(),
                status = (byte)AdapterSchedulingStateRestoreStatus.Restored,
                ownershipResult = (byte)AdapterSchedulingStateOwnershipResult.Restored,
                identity = IdentityWire(),
                payloadVersion = command.Payload.Version,
                payloadLength = command.Payload.Bytes.Length,
                payloadDigestSha256 = command.Payload.Sha256Digest,
                observedCpuGrade = "normal",
                observedGpuGrade = "normal",
                observedAt = ObservedAt,
                message = "restored"
            });
        });

        var result = await CreateDispatcher(handler).RestoreSoftwareSchedulingStateAsync(
            "adapter:test",
            command,
            CancellationToken.None);

        Assert.Equal(AdapterSchedulingStateRestoreStatus.Conflict, result.Status);
        Assert.Equal(AdapterSchedulingStateOwnershipResult.OwnershipLost, result.OwnershipResult);
        Assert.Null(result.Identity);
    }

    private static HttpAdapterPolicyDispatcher CreateDispatcher(HttpMessageHandler handler)
    {
        return new HttpAdapterPolicyDispatcher(
            new HttpClient(handler),
            new StaticAdapterSoftwareRegistry([CreateAdapterRegistration()]),
            new ResourceManagerSelfSchedulingControl());
    }

    private static AdapterSoftwareRegistration CreateAdapterRegistration()
    {
        var capabilities = new AdapterSoftwareSchedulingCapabilities(
            new AdapterCpuSchedulingCapabilities(AdapterCpuSchedulingGrades.All),
            new AdapterGpuSchedulingCapabilities(AdapterGpuSchedulingGrades.All));
        return new AdapterSoftwareRegistration(
            "adapter:test",
            AdapterRegistrationSchemaVersions.Current,
            "adapter:test.adapter",
            "adapter:test.app",
            "Test Adapter",
            null,
            [@"D:\Apps\TestAdapter"],
            [],
            [],
            new AdapterResourceMarkerEndpoint(
                AdapterResourceMarkerTransports.LoopbackHttp,
                "http://127.0.0.1:9322/ledger"),
            new AdapterResourceMarkerProbeResult(
                AdapterResourceMarkerStates.Online,
                DateTimeOffset.UtcNow,
                200,
                "ok"),
            "registered",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            capabilities);
    }

    private static AdapterSchedulingStateRestoreCommand CreateRestoreCommand()
    {
        var payload = new byte[] { 8, 13, 21, 34 };
        return new AdapterSchedulingStateRestoreCommand(
            new AdapterSchedulingStateIdentity(
                new AdapterInstanceId(0x11, 0x22),
                new AdapterInstanceLeaseId(0x33, 0x44),
                5,
                "adapter:test",
                "adapter:test.adapter",
                "adapter:test.app"),
            new AdapterSchedulingStatePayload(7, Digest(payload), payload),
            AdapterCpuSchedulingGrade.Optimize,
            AdapterGpuSchedulingGrade.Normal,
            64,
            Deadline);
    }

    private static object IdentityWire()
    {
        return new
        {
            adapterInstanceIdHigh = "0000000000000011",
            adapterInstanceIdLow = "0000000000000022",
            leaseIdHigh = "0000000000000033",
            leaseIdLow = "0000000000000044",
            leaseGeneration = "0000000000000005",
            softwareId = "adapter:test",
            adapterId = "adapter:test.adapter",
            applicationId = "adapter:test.app"
        };
    }

    private static string Digest(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static async Task<JsonDocument> ReadRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:9322/ledger", request.RequestUri!.AbsoluteUri);
        return await JsonDocument.ParseAsync(
            await request.Content!.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private static HttpResponseMessage JsonResponse(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class ScriptedHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(request, cancellationToken);
        }
    }

    private sealed class StaticAdapterSoftwareRegistry(IReadOnlyList<AdapterSoftwareRegistration> registrations)
        : IAdapterSoftwareRegistry
    {
        public Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult(registrations);

        public Task<AdapterRegistrationResult> RegisterAsync(
            AdapterSoftwareRegistrationRequest request,
            AdapterResourceMarkerProbeResult markerProbe,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
