using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessInvocationContractTests
{
    private const string ApiKey = "rm_sk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task RequestAndResponseFramesRoundTripUnicodeAndTypedStatuses()
    {
        var request = new TrustedAccessInvocationRequest(
            TrustedAccessInvocationContract.GpuResolveOperation,
            ApiKey,
            "D:\\软件\\台式机.exe");
        using var requestStream = new MemoryStream();
        await TrustedAccessInvocationContract.WriteRequestAsync(requestStream, request, CancellationToken.None);
        requestStream.Position = 0;
        Assert.Equal(request, await TrustedAccessInvocationContract.ReadRequestAsync(requestStream, CancellationToken.None));

        using var responseStream = new MemoryStream();
        await TrustedAccessInvocationContract.WriteResponseAsync(responseStream,
            new(TrustedAccessInvocationContract.Success, "status=probe-resolved\nmessage=设备就绪"), CancellationToken.None);
        responseStream.Position = 0;
        Assert.Equal(new TrustedAccessInvocationResponse(
            TrustedAccessInvocationContract.Success,
            "status=probe-resolved\nmessage=设备就绪"),
            await TrustedAccessInvocationContract.ReadResponseAsync(responseStream, CancellationToken.None));

        using var deniedStream = new MemoryStream();
        await TrustedAccessInvocationContract.WriteResponseAsync(deniedStream,
            new(TrustedAccessInvocationContract.Forbidden, null), CancellationToken.None);
        deniedStream.Position = 0;
        Assert.Equal(new TrustedAccessInvocationResponse(TrustedAccessInvocationContract.Forbidden, null),
            await TrustedAccessInvocationContract.ReadResponseAsync(deniedStream, CancellationToken.None));
    }

    [Fact]
    public async Task RequestAllowsAnEmptyKeyButRejectsANullKey()
    {
        using var stream = new MemoryStream();
        await TrustedAccessInvocationContract.WriteRequestAsync(stream,
            new("gpu.resolve", string.Empty, @"D:\owned.exe"), CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(new TrustedAccessInvocationRequest("gpu.resolve", string.Empty, @"D:\owned.exe"),
            await TrustedAccessInvocationContract.ReadRequestAsync(stream, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => TrustedAccessInvocationContract.WriteRequestAsync(
            new MemoryStream(), new("gpu.resolve", null!, @"D:\owned.exe"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadJsonRequestAsync(
            "{\"operation\":\"gpu.resolve\",\"key\":null,\"payload\":\"path\"}"));
    }

    [Fact]
    public async Task RequestUsesOnlyTheGenericOperationKeyAndPayloadFields()
    {
        using var stream = new MemoryStream();
        await TrustedAccessInvocationContract.WriteRequestAsync(stream,
            new("gpu.report", ApiKey, "outcome=launch-failed"), CancellationToken.None);
        var bytes = stream.ToArray();
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        Assert.Equal(bytes.Length - sizeof(int), length);
        using var json = JsonDocument.Parse(bytes.AsMemory(sizeof(int)));
        Assert.Equal("gpu.report", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal(ApiKey, json.RootElement.GetProperty("key").GetString());
        Assert.Equal("outcome=launch-failed", json.RootElement.GetProperty("payload").GetString());
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.False(json.RootElement.TryGetProperty("sessionId", out _));
        Assert.False(json.RootElement.TryGetProperty("executablePath", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(131073)]
    public async Task FrameLengthIsRejectedBeforeBodyAllocation(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TrustedAccessInvocationContract.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RequestRejectsInvalidUtf8UnknownFieldsAndOversizedKey()
    {
        using var invalidUtf8 = new MemoryStream([1, 0, 0, 0, 255]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TrustedAccessInvocationContract.ReadRequestAsync(invalidUtf8, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadJsonRequestAsync(
            "{\"operation\":\"gpu.resolve\",\"key\":\"" + ApiKey
            + "\",\"payload\":\"path\",\"sessionId\":3}"));

        var oversizedKey = new string('a', TrustedAccessInvocationContract.MaximumKeyBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => TrustedAccessInvocationContract.WriteRequestAsync(
            new MemoryStream(), new("gpu.resolve", oversizedKey, "path"), CancellationToken.None));
    }

    [Fact]
    public async Task ResponseRejectsUnknownStatusesAndInvalidFrameSizes()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => WriteJsonResponseAsync(
            "{\"status\":\"accepted\",\"payload\":null}"));

        using var stream = new MemoryStream(BitConverter.GetBytes(TrustedAccessInvocationContract.MaximumFrameBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TrustedAccessInvocationContract.ReadResponseAsync(stream, CancellationToken.None));
    }

    private static async Task ReadJsonRequestAsync(string json)
    {
        using var stream = Frame(json);
        _ = await TrustedAccessInvocationContract.ReadRequestAsync(stream, CancellationToken.None);
    }

    private static async Task WriteJsonResponseAsync(string json)
    {
        using var stream = Frame(json);
        _ = await TrustedAccessInvocationContract.ReadResponseAsync(stream, CancellationToken.None);
    }

    private static MemoryStream Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }
}
