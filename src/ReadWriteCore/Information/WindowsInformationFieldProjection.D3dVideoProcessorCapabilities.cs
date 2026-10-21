using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    internal static string D3dVideoProcessorIdentity(WindowsD3dAdapterIdentity adapter,
        WindowsD3dVideoProcessorDescriptor descriptor)
        => FormattableString.Invariant($"windows-d3d-video-processor|d3d11|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}|input-frame-format:{(uint)descriptor.InputFrameFormat}|input-rate:{descriptor.InputFrameRate.Numerator}/{descriptor.InputFrameRate.Denominator}|input-size:{descriptor.InputWidth}x{descriptor.InputHeight}|output-rate:{descriptor.OutputFrameRate.Numerator}/{descriptor.OutputFrameRate.Denominator}|output-size:{descriptor.OutputWidth}x{descriptor.OutputHeight}|usage:{(uint)descriptor.Usage}");

    internal static bool IsRequestedD3dVideoProcessorIdentity(WindowsD3dCapabilityScope scope,
        string identity, DeviceInformationDefinition definition)
    {
        var requested = ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps)
            ? (scope.Fields & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0
            : ReferenceEquals(definition, DeviceInformationFieldRegistry.GpuAllVideoProcessorRateConversionCaps)
                && (scope.Fields & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0;
        if (!requested) return false;
        foreach (var adapter in scope.Adapters)
            foreach (var descriptor in scope.VideoProcessorDescriptors)
                if (string.Equals(identity, D3dVideoProcessorIdentity(adapter, descriptor), StringComparison.Ordinal))
                    return true;
        return false;
    }

    private static void MergeD3dVideoProcessorCapabilities(
        Dictionary<string, DeviceInformationObject> objects,
        WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selected,
        IReadOnlyDictionary<WindowsD3dAdapterIdentity, WindowsD3dAdapterCapabilityRead> rows,
        DateTimeOffset observedAt)
    {
        var capsSelected = (selected & WindowsD3dCapabilityFields.VideoProcessorCaps) != 0;
        var rateCapsSelected = (selected & WindowsD3dCapabilityFields.VideoProcessorRateConversionCaps) != 0;
        ulong completeRateConversionQueries = 0;
        foreach (var adapter in scope.Adapters)
        {
            var row = rows[adapter];
            var capsResults = row.VideoProcessorCaps.IsDefault
                ? ImmutableArray<WindowsD3dVideoProcessorCapsRead>.Empty : row.VideoProcessorCaps;
            var rateResults = row.VideoProcessorRateConversionCaps.IsDefault
                ? ImmutableArray<WindowsD3dVideoProcessorRateConversionCapsRead>.Empty : row.VideoProcessorRateConversionCaps;
            if (!capsSelected && !capsResults.IsEmpty)
                throw new InvalidOperationException("The D3D reader returned unrequested video processor caps.");
            if (!rateCapsSelected && !rateResults.IsEmpty)
                throw new InvalidOperationException("The D3D reader returned unrequested rate-conversion caps.");

            if (capsSelected)
            {
                var byDescriptor = new Dictionary<WindowsD3dVideoProcessorDescriptor, WindowsD3dVideoProcessorCapsRead>();
                foreach (var result in capsResults)
                {
                    if (result is null || !byDescriptor.TryAdd(result.Descriptor, result))
                        throw new InvalidOperationException("The D3D reader returned a null or duplicate processor caps result.");
                    ValidateD3dVideoProcessorCaps(scope, result);
                }
                RequireExactVideoProcessorDescriptors(scope, byDescriptor.Keys);
                foreach (var descriptor in scope.VideoProcessorDescriptors)
                {
                    var result = byDescriptor[descriptor];
                    JsonElement? value = result.Complete == true
                        ? JsonSerializer.SerializeToElement(new
                        {
                            contentDescriptor = ContentDescriptorValue(descriptor),
                            caps = result.Caps
                        }, D3dJsonOptions)
                        : null;
                    SetStructuredField(objects, D3dVideoProcessorIdentity(adapter, descriptor),
                        "gpu_adapter_video_processor", DeviceInformationFieldRegistry.GpuAllVideoProcessorCaps,
                        value, observedAt);
                }
            }

            if (rateCapsSelected)
            {
                var byDescriptor = new Dictionary<WindowsD3dVideoProcessorDescriptor,
                    WindowsD3dVideoProcessorRateConversionCapsRead>();
                foreach (var result in rateResults)
                {
                    if (result is null || !byDescriptor.TryAdd(result.Descriptor, result))
                        throw new InvalidOperationException("The D3D reader returned a null or duplicate rate-conversion result.");
                    ValidateD3dVideoProcessorRateConversionCaps(scope, result);
                    if (result.Complete == true)
                        completeRateConversionQueries = checked(completeRateConversionQueries
                            + result.RateConversionCapsCount!.Value);
                }
                RequireExactVideoProcessorDescriptors(scope, byDescriptor.Keys);
                if (completeRateConversionQueries > (ulong)scope.MaximumVideoProcessorRateConversionCapsQueryCount)
                    throw new InvalidOperationException("The D3D reader exceeded the total rate-conversion caps query bound.");
                foreach (var descriptor in scope.VideoProcessorDescriptors)
                {
                    var result = byDescriptor[descriptor];
                    var rateCaps = result.RateConversionCaps.IsDefault
                        ? ImmutableArray<WindowsD3dIndexedRateConversionCapsRead>.Empty : result.RateConversionCaps;
                    JsonElement? value = result.Complete == true
                        ? JsonSerializer.SerializeToElement(new
                        {
                            contentDescriptor = ContentDescriptorValue(descriptor),
                            rateConversionCapsCount = result.RateConversionCapsCount!.Value,
                            rateConversionCaps = rateCaps.Select(static indexed => new
                            {
                                index = indexed.Index,
                                caps = indexed.Caps
                            }).ToArray()
                        }, D3dJsonOptions)
                        : null;
                    SetStructuredField(objects, D3dVideoProcessorIdentity(adapter, descriptor),
                        "gpu_adapter_video_processor", DeviceInformationFieldRegistry.GpuAllVideoProcessorRateConversionCaps,
                        value, observedAt);
                }
            }
        }
    }

    private static void RequireExactVideoProcessorDescriptors(WindowsD3dCapabilityScope scope,
        IEnumerable<WindowsD3dVideoProcessorDescriptor> descriptors)
    {
        var set = descriptors.ToHashSet();
        if (set.Count != scope.VideoProcessorDescriptors.Length
            || scope.VideoProcessorDescriptors.Any(descriptor => !set.Contains(descriptor)))
            throw new InvalidOperationException("The D3D reader did not return exactly the selected processor content descriptors.");
    }

    private static void ValidateD3dVideoProcessorCaps(WindowsD3dCapabilityScope scope,
        WindowsD3dVideoProcessorCapsRead result)
    {
        if (!scope.VideoProcessorDescriptors.Contains(result.Descriptor) || result.Complete is null)
            throw new InvalidOperationException("The D3D reader returned an out-of-scope descriptor or omitted processor caps status.");
        if (result.Complete == true && result.Caps is null)
            throw new InvalidOperationException("A complete processor caps result must contain all native members.");
        if (result.Complete == false && result.Caps is not null)
            throw new InvalidOperationException("An incomplete processor caps result must not contain partial members.");
    }

    private static void ValidateD3dVideoProcessorRateConversionCaps(WindowsD3dCapabilityScope scope,
        WindowsD3dVideoProcessorRateConversionCapsRead result)
    {
        var caps = result.RateConversionCaps.IsDefault
            ? ImmutableArray<WindowsD3dIndexedRateConversionCapsRead>.Empty : result.RateConversionCaps;
        if (!scope.VideoProcessorDescriptors.Contains(result.Descriptor) || result.Complete is null)
            throw new InvalidOperationException("The D3D reader returned an out-of-scope descriptor or omitted rate-conversion status.");
        if (result.Complete == false)
        {
            if (!caps.IsEmpty) throw new InvalidOperationException("An incomplete rate-conversion result must not contain partial groups.");
            return;
        }
        if (result.RateConversionCapsCount is not { } count
            || count > (uint)scope.MaximumVideoProcessorRateConversionCapsQueryCount
            || caps.Length != count)
            throw new InvalidOperationException("The D3D reader returned an invalid or over-bound rate-conversion count.");
        for (var index = 0; index < caps.Length; index++)
        {
            var indexed = caps[index];
            if (indexed is null || indexed.Caps is null || indexed.Index != (uint)index)
                throw new InvalidOperationException("The D3D reader returned a missing, reordered, or null rate-conversion group.");
        }
    }

    private static object ContentDescriptorValue(WindowsD3dVideoProcessorDescriptor descriptor) => new
    {
        inputFrameFormat = (uint)descriptor.InputFrameFormat,
        inputFrameRate = new { numerator = descriptor.InputFrameRate.Numerator, denominator = descriptor.InputFrameRate.Denominator },
        inputWidth = descriptor.InputWidth,
        inputHeight = descriptor.InputHeight,
        outputFrameRate = new { numerator = descriptor.OutputFrameRate.Numerator, denominator = descriptor.OutputFrameRate.Denominator },
        outputWidth = descriptor.OutputWidth,
        outputHeight = descriptor.OutputHeight,
        usage = (uint)descriptor.Usage
    };
}
