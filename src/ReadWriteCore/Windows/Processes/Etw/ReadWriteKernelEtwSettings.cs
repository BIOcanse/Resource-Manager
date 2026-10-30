using System.Diagnostics;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>SDK composition consumes the same build-selected backend limits as the product host.</summary>
internal static class ReadWriteKernelEtwSettings
{
    internal static NativeKernelEtwSessionConfig CreateConfiguration(CpuResidencyCaptureConfiguration cpu)
    {
        cpu.RequirePublished();
        var value = KernelEtwNativeDefaults.SelectedLimits;
        value.Residency.QpcFrequency = Stopwatch.Frequency;
        value.Residency.WindowMilliseconds = checked((uint)cpu.ObservationWindowMilliseconds);
        value.Residency.BucketMilliseconds = checked((uint)cpu.BucketWidthMilliseconds);
        value.Residency.MaximumPairs = checked((uint)cpu.MaximumActivePairs);
        value.Residency.MaximumProcessors = checked((uint)cpu.PhysicalCoreByLogicalProcessor.Count);
        return value;
    }

    internal static NativeKernelEtwSessionRuntimeFactory CreateFactory(Func<CpuResidencyCaptureConfiguration> cpu)
    {
        var limits = KernelEtwNativeDefaults.SelectedLimits;
        return new(new BoundedKernelEtwRegionProvider(checked((nuint)limits.ByteCap)), () => CreateConfiguration(cpu()),
            static () => KernelEtwNativeDefaults.SelectedSessionBuffers);
    }
}
