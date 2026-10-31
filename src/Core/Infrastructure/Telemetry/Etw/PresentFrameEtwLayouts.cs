using Pf = ResourceManager.App.Infrastructure.Telemetry.Etw.PresentFrameField;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Payload fields PresentFrameEtwSource reads, named as in the provider manifests.
internal enum PresentFrameField
{
    ProcessID, CreateTime, pIDXGISwapChain, Flags, Result, pSwapchain, MMIOFlip, VidPnSourceId,
    SubmitSequence, hContext, PacketType, bPresent, FlipSubmitSequence, FlipFenceId,
    CompositionSurfaceLuid, PresentCount, BindId, NewState, Width, Height, NewWidth, NewHeight, ReturnValue, DestWidth, DestHeight,
    hProcessId, pDxgAdapter, hDevice, NodeOrdinal, QueueSubmitSequence, ParentDxgHwQueue
}

// Fixed layouts of the Present session's events, selected by provider, event id and header version.
// Unknown payload versions and short payloads are rejected per event by EtwFixedLayoutGate;
// the diagnostic includes provider/id/version/reason and leaves supported events available.
// Offsets are (bytes, pointers); ETW packs manifest fields without padding and win:Boolean is 4 bytes.
// Kernel-Process templates end in variable strings, so their End is the fixed prefix before the first one;
// the DXGI swap chain templates are long, so their End is the prefix through the last field read.
internal static class PresentFrameEtwLayouts
{
    internal static readonly EtwFixedLayoutCatalog Catalog = Create();

    private static EtwFixedLayoutCatalog Create()
    {
        const EtwFieldType P = EtwFieldType.Pointer, U32 = EtwFieldType.UInt32, U64 = EtwFieldType.UInt64,
            B = EtwFieldType.Boolean, FT = EtwFieldType.FileTime;
        var dxgi = PresentFrameEtwSource.DxgiProvider;
        var d3d9 = PresentFrameEtwSource.Direct3D9Provider;
        var dxgKrnl = PresentFrameEtwSource.GraphicsKernelProvider;
        var win32k = PresentFrameEtwSource.Win32kProvider;
        var kernelProcess = PresentFrameEtwSource.KernelProcessProvider;
        return new EtwFixedLayoutCatalog(Enum.GetValues<PresentFrameField>().Length)
            // Microsoft-Windows-Kernel-Process ProcessStart (1) and ProcessStop (2).
            .Add(kernelProcess, [1], 0, new(20), F(Pf.ProcessID, U32, 0), F(Pf.CreateTime, FT, 4))
            .Add(kernelProcess, [1], 1, new(24), F(Pf.ProcessID, U32, 0), F(Pf.CreateTime, FT, 4))
            .Add(kernelProcess, [1], 2, new(24), F(Pf.ProcessID, U32, 0), F(Pf.CreateTime, FT, 4))
            .Add(kernelProcess, [1], 3, new(48), F(Pf.ProcessID, U32, 0), F(Pf.CreateTime, FT, 12))
            .Add(kernelProcess, [1], 4, new(48), F(Pf.ProcessID, U32, 0), F(Pf.CreateTime, FT, 12))
            .Add(kernelProcess, [2], 0, new(48), F(Pf.ProcessID, U32, 0))
            .Add(kernelProcess, [2], 1, new(76), F(Pf.ProcessID, U32, 0))
            .Add(kernelProcess, [2], 2, new(84), F(Pf.ProcessID, U32, 0))
            // Microsoft-Windows-DXGI Present and PresentMultiplaneOverlay start/stop.
            .Add(dxgi, [42, 55], 0, new(16, 1), F(Pf.pIDXGISwapChain, P, 0), F(Pf.Flags, U32, 0, 1))
            .Add(dxgi, [43, 56], 0, new(4), F(Pf.Result, U32, 0))
            // SwapChain_Start (10) / DC_Start (12): pIDXGISwapChain, pIDXGIFactory, pIDXGIDevice, pIDXGIOutput,
            // UserBackbufferCount u8, BackbufferCount u8, ppBackBuffers, pPrimary, pProxyPrimary, Width, Height, ...
            .Add(dxgi, [10, 12], 0, new(10, 7), F(Pf.pIDXGISwapChain, P, 0), F(Pf.Width, U32, 2, 7), F(Pf.Height, U32, 6, 7))
            // ResizeBuffers_Stop (46): pIDXGISwapChain, ReturnValue, NewUserBackbufferCount u8, NewBackbufferCount u8,
            // ppNewBackBuffers, pNewPrimary, pNewProxyPrimary, NewWidth, NewHeight, ...
            .Add(dxgi, [46], 0, new(14, 4), F(Pf.pIDXGISwapChain, P, 0), F(Pf.ReturnValue, U32, 0, 1),
                F(Pf.NewWidth, U32, 6, 4), F(Pf.NewHeight, U32, 10, 4))
            // Microsoft-Windows-D3D9 Present start/stop.
            .Add(d3d9, [1], 0, new(4, 1), F(Pf.pSwapchain, P, 0))
            .Add(d3d9, [2], 0, new(4), F(Pf.Result, U32, 0))
            // Microsoft-Windows-DxgKrnl display events. Present (184) is read from the header only.
            .Add(dxgKrnl, [17], 0, new(40, 2), F(Pf.FlipFenceId, U64, 32, 2))
            .Add(dxgKrnl, [116], 0, new(32, 2), F(Pf.FlipSubmitSequence, U32, 4, 1), F(Pf.Flags, U32, 28, 2))
            .Add(dxgKrnl, [168], 0, new(16, 2), F(Pf.MMIOFlip, B, 12, 2))
            .Add(dxgKrnl, [178], 1, new(36, 3),
                F(Pf.hContext, P, 0), F(Pf.PacketType, U32, 0, 1), F(Pf.SubmitSequence, U32, 4, 1), F(Pf.bPresent, B, 24, 1))
            // GPU tier; versioned manifest templates. DCStart carries object owner in Device.hProcessId,
            // not the tracing thread's header. Context DCStart is bound through that device.
            .Add(dxgKrnl, [27, 28, 29], 2, new(4, 3), F(Pf.hProcessId, P, 0),
                F(Pf.pDxgAdapter, P, 0, 1), F(Pf.hDevice, P, 4, 2))
            .Add(dxgKrnl, [30, 31, 32], 0, new(32, 2), F(Pf.hDevice, P, 0),
                F(Pf.NodeOrdinal, U32, 0, 1), F(Pf.hContext, P, 32, 1))
            .Add(dxgKrnl, [175], 1, new(16, 2), F(Pf.hContext, P, 0), F(Pf.QueueSubmitSequence, U32, 12, 2))
            .Add(dxgKrnl, [177], 1, new(16, 1), F(Pf.hContext, P, 0), F(Pf.QueueSubmitSequence, U32, 12, 1))
            .Add(dxgKrnl, [180], 1, new(16, 2), F(Pf.hContext, P, 0), F(Pf.SubmitSequence, U32, 4, 1))
            .Add(dxgKrnl, [422, 423, 424], 0, new(0, 3), F(Pf.hContext, P, 0), F(Pf.ParentDxgHwQueue, P, 0, 2))
            .HeaderOnly(dxgKrnl, 450).HeaderOnly(dxgKrnl, 451)
            .HeaderOnly(dxgKrnl, 184)
            .Add(dxgKrnl, [252], 0, new(88, 1), F(Pf.VidPnSourceId, U32, 0))
            // Microsoft-Windows-Win32k composition token events.
            // pToken, pCompositionSurfaceObject, SwapChainIndex, PresentCount, CompositionSurfaceLuid, BindId,
            // FlipInterval, DestWidth, DestHeight.
            .Add(win32k, [201], 1, new(40, 2),
                F(Pf.PresentCount, U64, 4, 2), F(Pf.CompositionSurfaceLuid, U64, 12, 2), F(Pf.BindId, U64, 20, 2),
                F(Pf.DestWidth, U32, 32, 2), F(Pf.DestHeight, U32, 36, 2))
            .Add(win32k, [301], 1, new(48, 1),
                F(Pf.PresentCount, U32, 4, 1), F(Pf.NewState, U32, 16, 1), F(Pf.CompositionSurfaceLuid, U64, 28, 1), F(Pf.BindId, U64, 36, 1));

        static (int Field, EtwFixedField At) F(Pf field, EtwFieldType type, int bytes, int pointers = 0)
            => ((int)field, new EtwFixedField(type, bytes, pointers));
    }
}
