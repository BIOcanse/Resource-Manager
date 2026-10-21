using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;

namespace Resource_Manager_APP.Tests;

public sealed class NativePdhExternalScratchTests
{
    [Fact]
    public void FailedScratchCloseKeepsSessionValidAndNativeHandleAliveUntilExplicitRetry()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var recorded = new PdhParsingScratchTests.RecordedResource(ledger.CreateScratch(12288));
        var factories = 0;
        var scratch = new PdhParsingScratch(length => ++factories == 1 ? recorded : ledger.CreateScratch(length), ledger.NativePageSize);
        var api = new ExternalApi();
        using var session = new NativePdhSession(api, new(TimeSpan.Zero, TimeSpan.Zero, 0), TimeProvider.System, null, scratch);
        session.Read();
        Assert.Equal(1, api.Creates);
        Assert.Equal(1, api.Samples);
        Assert.Equal(1, recorded.Ends);
        recorded.FailClose = true;
        Assert.Throws<IOException>(() => session.Dispose());
        Assert.Equal(0, api.Destroys);
        Assert.Throws<InvalidOperationException>(() => session.Read());
        Assert.Equal(1, api.Samples);
        session.RetryScratchReturn();
        session.Read();
        Assert.Equal(1, api.Creates);
        Assert.Equal(2, api.Samples);
        Assert.Equal(2, factories);
        session.Dispose(); session.Dispose();
        Assert.Equal(1, api.Destroys);
    }

    [Fact]
    public void RealExternalNativeEntryPointBorrowsAndReturnsProductScratch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ledger = new ResourceManagerLedgerOwner();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var scratch = new PdhParsingScratch(length => { var resource = ledger.CreateScratch(length); resources.Add(resource); return resource; }, ledger.NativePageSize);
        var api = new NativePdhApi();
        var external = (INativePdhExternalScratchApi)api;
        var config = new NativePdhConfig { AbiVersion = NativeCoreAbi.Version, StructSize = (uint)Marshal.SizeOf<NativePdhConfig>() };
        Assert.Equal(NativePdhResultCode.Ok, external.CreateExternal(in config, out var handle));
        try
        {
            for (var sample = 0; sample < 2; sample++)
            {
                var header = new NativePdhFrameHeader { AbiVersion = NativeCoreAbi.Version, StructSize = (uint)Marshal.SizeOf<NativePdhFrameHeader>() };
                scratch.BeginSample();
                try { Assert.Contains(external.SampleExternal(handle, ref header, scratch), new[] { NativePdhResultCode.Ok, NativePdhResultCode.Unavailable, NativePdhResultCode.NoData }); }
                finally { scratch.EndSample(); }
                Assert.NotEmpty(resources);
                Assert.All(resources, resource => Assert.Equal(0, resource.Active));
            }
            var before = new NativePdhDiagnostics { AbiVersion = NativeCoreAbi.Version, StructSize = (uint)Marshal.SizeOf<NativePdhDiagnostics>() };
            Assert.Equal(NativePdhResultCode.Ok, api.GetDiagnostics(handle, ref before));
            Assert.InRange(before.CounterCount, 1U, 9U);
        }
        finally { api.Destroy(handle); }
    }

    private sealed class ExternalApi : INativePdhApi, INativePdhExternalScratchApi
    {
        internal int Creates, Samples, Destroys;
        public uint GetAbiVersion() => NativeCoreAbi.Version;
        public NativePdhResultCode CreateExternal(in NativePdhConfig config, out IntPtr handle)
        { Creates++; handle = new IntPtr(71); return NativePdhResultCode.Ok; }
        public NativePdhResultCode SampleExternal(IntPtr handle, ref NativePdhFrameHeader header, NativePdhScratch scratch)
        {
            Samples++;
            var region = scratch.Supply(8192);
            Marshal.WriteInt32(region.Address, 23);
            Assert.Equal(region.Address, scratch.Supply(4096).Address);
            return NativePdhResultCode.Unavailable;
        }
        public void Destroy(IntPtr handle) => Destroys++;
        public NativePdhResultCode Create(in NativePdhConfig config, out IntPtr handle) => throw new InvalidOperationException("External session cannot create ordinary collector.");
        public NativePdhResultCode Sample(IntPtr handle, ref NativePdhFrameHeader header) => throw new InvalidOperationException("External session cannot sample ordinary collector.");
        public NativePdhResultCode RequestTopologyRefresh(IntPtr handle) => NativePdhResultCode.Ok;
        public NativePdhResultCode CopyFrame(IntPtr handle, ulong expectedSequence, NativePdhGpuEngineRow[] engines, NativePdhGpuMemoryRow[] memory, out NativePdhSystemIo io) => throw new InvalidOperationException("Unavailable sample has no frame.");
        public NativePdhResultCode GetDiagnostics(IntPtr handle, ref NativePdhDiagnostics diagnostics) => throw new InvalidOperationException("Fixture has no diagnostics.");
    }
}
