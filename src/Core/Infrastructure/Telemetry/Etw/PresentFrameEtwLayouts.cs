using Pf = ResourceManager.App.Infrastructure.Telemetry.Etw.PresentFrameField;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Payload fields PresentFrameEtwSource reads, named as in the provider manifests.
internal enum PresentFrameField
{
    ProcessID, CreateTime, pIDXGISwapChain, Flags, Result, pSwapchain, MMIOFlip, VidPnSourceId,
    SubmitSequence, hContext, PacketType, bPresent, FlipSubmitSequence, FlipFenceId,
    CompositionSurfaceLuid, PresentCount, BindId, NewState
}

// Fixed layouts of the Present session's events on Windows 10.0.26300 (EtwFixedLayout.cs).
// Offsets are (bytes, pointers). Kernel-Process templates end in variable strings, so
// their End is the fixed prefix before the first one.
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
            // Microsoft-Windows-D3D9 Present start/stop.
            .Add(d3d9, [1], 0, new(4, 1), F(Pf.pSwapchain, P, 0))
            .Add(d3d9, [2], 0, new(4), F(Pf.Result, U32, 0))
            // Microsoft-Windows-DxgKrnl display events. Present (184) is read from the header only.
            .Add(dxgKrnl, [17], 0, new(40, 2), F(Pf.FlipFenceId, U64, 32, 2))
            .Add(dxgKrnl, [116], 0, new(32, 2), F(Pf.FlipSubmitSequence, U32, 4, 1), F(Pf.Flags, U32, 28, 2))
            .Add(dxgKrnl, [168], 0, new(16, 2), F(Pf.MMIOFlip, B, 12, 2))
            .Add(dxgKrnl, [178], 1, new(36, 3),
                F(Pf.hContext, P, 0), F(Pf.PacketType, U32, 0, 1), F(Pf.SubmitSequence, U32, 4, 1), F(Pf.bPresent, B, 24, 1))
            .HeaderOnly(dxgKrnl, 184)
            .Add(dxgKrnl, [252], 0, new(88, 1), F(Pf.VidPnSourceId, U32, 0))
            // Microsoft-Windows-Win32k composition token events.
            .Add(win32k, [201], 1, new(40, 2),
                F(Pf.PresentCount, U64, 4, 2), F(Pf.CompositionSurfaceLuid, U64, 12, 2), F(Pf.BindId, U64, 20, 2))
            .Add(win32k, [301], 1, new(48, 1),
                F(Pf.PresentCount, U32, 4, 1), F(Pf.NewState, U32, 16, 1), F(Pf.CompositionSurfaceLuid, U64, 28, 1), F(Pf.BindId, U64, 36, 1));

        static (int Field, EtwFixedField At) F(Pf field, EtwFieldType type, int bytes, int pointers = 0)
            => ((int)field, new EtwFixedField(type, bytes, pointers));
    }
}
