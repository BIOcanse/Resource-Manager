using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    internal static byte[] CreateRecreationRequest(GpuGraphicsApi api, bool resize, ulong deadlineMilliseconds, ulong target)
    {
        ArgumentOutOfRangeException.ThrowIfZero(deadlineMilliseconds);
        if (api is not (GpuGraphicsApi.D3D11 or GpuGraphicsApi.D3D12 or (GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)) || target == 0)
            throw new ArgumentException("DXGI recreation requires a Direct3D API and an exact adapter.");
        var bytes = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)api);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), resize ? 2u : 1u);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), deadlineMilliseconds);
        // Correlate finish/cancel with this request, including after a delayed remote call.
        RandomNumberGenerator.Fill(bytes.AsSpan(24, 8));
        bytes[24] |= 1;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), target);
        return bytes;
    }

    internal static GpuRecreationResult DecodeRecreationResponse(byte[] request, GpuRemoteCallKind kind, GpuRemoteCallSnapshot call)
    {
        if (!call.Completed || call.ExitCode != 1 || call.Response is not { Length: 56 } response)
            return new(GpuRecreationState.None, 0, call.Completed ? "recreation-not-accepted" : call.Status);
        if (!request.AsSpan(0, 20).SequenceEqual(response.AsSpan(0, 20))
            || !request.AsSpan(24, 16).SequenceEqual(response.AsSpan(24, 16))
            || !request.AsSpan(48, 8).SequenceEqual(response.AsSpan(48, 8)))
            return new(GpuRecreationState.None, 0, "recreation-response-mismatch");
        var state = (GpuRecreationState)BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(20));
        var source = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(40));
        if (!Enum.IsDefined(state) || state == GpuRecreationState.None
            || (kind == GpuRemoteCallKind.ArmRecreation ? state != GpuRecreationState.Armed : state == GpuRecreationState.Armed)
            || (state == GpuRecreationState.Signalled && (source == 0
                || source == BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(32)))))
            return new(GpuRecreationState.None, 0, "recreation-response-invalid");
        return new(state, source, state.ToString().ToLowerInvariant());
    }

    private static async Task<GpuRecreationResult> ExecuteRecreationAsync(ProviderProcess owner,
        GpuRemoteCallKind kind, byte[] request, CancellationToken token)
    {
        var index = kind switch
        {
            GpuRemoteCallKind.ArmRecreation => 0,
            GpuRemoteCallKind.FinishRecreation => 1,
            GpuRemoteCallKind.CancelRecreation => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (owner.RecreationAddresses.Length != 3 || owner.RecreationAddresses[index] == IntPtr.Zero)
            return new(GpuRecreationState.None, 0, "recreation-export-missing");
        var call = await owner.InvokeAsync(kind, owner.RecreationAddresses[index], request, true, token).ConfigureAwait(false);
        return DecodeRecreationResponse(request, kind, call);
    }
}
