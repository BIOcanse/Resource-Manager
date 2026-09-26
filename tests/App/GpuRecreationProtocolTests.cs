using System.Buffers.Binary;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuRecreationProtocolTests
{
    [Theory]
    [InlineData(GpuGraphicsApi.D3D11, false, 1u)]
    [InlineData(GpuGraphicsApi.D3D12, true, 2u)]
    [InlineData(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, false, 1u)]
    public void RequestPreservesApiMethodDeadlineAndTarget(GpuGraphicsApi api, bool resize, uint method)
    {
        var request = WindowsGpuPlacementInjector.CreateRecreationRequest(api, resize, 723, 0x8123456789abcdef);
        Assert.Equal(56, request.Length);
        Assert.Equal((uint)api, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8)));
        Assert.Equal(method, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12)));
        Assert.Equal(723UL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(48)));
        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24)));
        Assert.Equal(0x8123456789abcdefUL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(32)));
    }

    [Theory]
    [InlineData(GpuRecreationState.Armed, GpuRemoteCallKind.ArmRecreation)]
    [InlineData(GpuRecreationState.Signalled, GpuRemoteCallKind.FinishRecreation)]
    [InlineData(GpuRecreationState.Expired, GpuRemoteCallKind.FinishRecreation)]
    [InlineData(GpuRecreationState.Cancelled, GpuRemoteCallKind.CancelRecreation)]
    public void DeliveryIsDistinctFromPreparationAndExpiry(GpuRecreationState state, GpuRemoteCallKind kind)
    {
        var request = WindowsGpuPlacementInjector.CreateRecreationRequest(GpuGraphicsApi.D3D11, false, 500, 2);
        var response = (byte[])request.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(20), (uint)state);
        if (state == GpuRecreationState.Signalled) BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(40), 1);
        var result = WindowsGpuPlacementInjector.DecodeRecreationResponse(request, kind, Completed(response));
        Assert.Equal(state, result.State);
        Assert.Equal(state == GpuRecreationState.Signalled, result.Signalled);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("api")]
    [InlineData("target")]
    [InlineData("empty-source")]
    [InlineData("same-source")]
    [InlineData("unknown-state")]
    [InlineData("still-armed")]
    [InlineData("outer-zero")]
    [InlineData("unreleased")]
    public void InvalidOrIncompleteResponseNeverClaimsDelivery(string mutation)
    {
        var request = WindowsGpuPlacementInjector.CreateRecreationRequest(GpuGraphicsApi.D3D11, false, 500, 2);
        var response = (byte[])request.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(20), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(40), 1);
        switch (mutation)
        {
            case "identity": response[24] ^= 4; break;
            case "api": response[8] = 2; break;
            case "target": response[32] = 3; break;
            case "empty-source": response[40] = 0; break;
            case "same-source": response[40] = 2; break;
            case "unknown-state": response[20] = 99; break;
            case "still-armed": response[20] = 1; break;
        }
        var call = Completed(response) with
        {
            ExitCode = mutation == "outer-zero" ? 0u : 1u,
            ResourcesReleased = mutation != "unreleased"
        };
        Assert.False(WindowsGpuPlacementInjector.DecodeRecreationResponse(request, GpuRemoteCallKind.FinishRecreation, call).Signalled);
    }

    private static GpuRemoteCallSnapshot Completed(byte[] response) => new(1, 2, 3, 1, response, true, "completed", null);
}
