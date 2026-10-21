using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    internal static string D3dVideoDecoderConfigurationIdentity(WindowsD3dAdapterIdentity adapter,
        WindowsD3dVideoDecoderDescriptor descriptor)
        => $"windows-d3d-video-decoder|d3d11|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}"
            + $"|profile:{descriptor.ProfileGuid:D}|sample-size:{descriptor.SampleWidth}x{descriptor.SampleHeight}"
            + $"|output-format:{(uint)descriptor.OutputFormat}";

    internal static bool IsRequestedD3dVideoDecoderConfigurationIdentity(
        WindowsD3dCapabilityScope scope, WindowsD3dAdapterIdentity adapter, string identity)
        => (scope.Fields & WindowsD3dCapabilityFields.DecoderConfigurations) != 0
            && scope.DecoderConfigurationDescriptors.Any(descriptor => string.Equals(identity,
                D3dVideoDecoderConfigurationIdentity(adapter, descriptor), StringComparison.Ordinal));

    private static void MergeD3dVideoDecoderConfigurations(
        Dictionary<string, DeviceInformationObject> objects,
        WindowsD3dCapabilityScope scope,
        WindowsD3dCapabilityFields selected,
        IReadOnlyDictionary<WindowsD3dAdapterIdentity, WindowsD3dAdapterCapabilityRead> rows,
        DateTimeOffset observedAt)
    {
        var selectedConfigurations = (selected & WindowsD3dCapabilityFields.DecoderConfigurations) != 0;
        ulong completeConfigurationQueries = 0;
        foreach (var adapter in scope.Adapters)
        {
            var row = rows[adapter];
            var results = row.DecoderConfigurations.IsDefault
                ? ImmutableArray<WindowsD3dVideoDecoderConfigurationsRead>.Empty
                : row.DecoderConfigurations;
            if (!selectedConfigurations)
            {
                if (!results.IsEmpty)
                    throw new InvalidOperationException("The D3D reader returned unrequested decoder configurations.");
                continue;
            }

            var byDescriptor = new Dictionary<WindowsD3dVideoDecoderDescriptor, WindowsD3dVideoDecoderConfigurationsRead>();
            foreach (var result in results)
            {
                if (result is null || !byDescriptor.TryAdd(result.Descriptor, result))
                    throw new InvalidOperationException("The D3D reader returned a null or duplicate decoder-description result.");
                ValidateD3dVideoDecoderConfigurations(scope, result);
            }
            if (byDescriptor.Count != scope.DecoderConfigurationDescriptors.Length
                || scope.DecoderConfigurationDescriptors.Any(descriptor => !byDescriptor.ContainsKey(descriptor)))
                throw new InvalidOperationException("The D3D reader did not return exactly the selected decoder descriptions.");

            foreach (var descriptor in scope.DecoderConfigurationDescriptors)
            {
                var result = byDescriptor[descriptor];
                JsonElement? value = null;
                if (result.ConfigurationsComplete == true)
                {
                    completeConfigurationQueries = checked(completeConfigurationQueries
                        + 1UL + result.ConfigurationCount!.Value);
                    if (completeConfigurationQueries > (ulong)scope.MaximumVideoDecoderConfigurationQueryCount)
                        throw new InvalidOperationException("The D3D reader exceeded the total decoder configuration query budget.");
                    var configurations = result.Configurations.IsDefault
                        ? ImmutableArray<WindowsD3dVideoDecoderIndexedConfigurationRead>.Empty
                        : result.Configurations;
                    value = JsonSerializer.SerializeToElement(new
                    {
                        descriptor = new
                        {
                            profileGuid = descriptor.ProfileGuid.ToString("D"),
                            sampleWidth = descriptor.SampleWidth,
                            sampleHeight = descriptor.SampleHeight,
                            outputFormat = (uint)descriptor.OutputFormat
                        },
                        configurationCount = result.ConfigurationCount!.Value,
                        configurations = configurations.Select(static indexed => new
                        {
                            index = indexed.Index,
                            configuration = indexed.Configuration
                        }).ToArray()
                    }, D3dJsonOptions);
                }
                SetStructuredField(objects, D3dVideoDecoderConfigurationIdentity(adapter, descriptor),
                    "gpu_adapter_decoder_configuration", DeviceInformationFieldRegistry.GpuAllVideoDecoderConfiguration,
                    value, observedAt);
            }
        }
    }

    private static void ValidateD3dVideoDecoderConfigurations(WindowsD3dCapabilityScope scope,
        WindowsD3dVideoDecoderConfigurationsRead result)
    {
        if (!scope.DecoderConfigurationDescriptors.Contains(result.Descriptor))
            throw new InvalidOperationException("The D3D reader returned a decoder description outside the explicit scope.");
        var configurations = result.Configurations.IsDefault
            ? ImmutableArray<WindowsD3dVideoDecoderIndexedConfigurationRead>.Empty : result.Configurations;
        if (result.ConfigurationsComplete is null)
            throw new InvalidOperationException("The D3D reader omitted decoder-configuration query status.");
        if (result.ConfigurationsComplete == false)
        {
            if (!configurations.IsEmpty)
                throw new InvalidOperationException("An incomplete decoder configuration result must not contain a partial list.");
            return;
        }

        if (result.ConfigurationCount is not { } count
            || count > (uint)scope.MaximumVideoDecoderConfigurationCount
            || configurations.Length != count)
            throw new InvalidOperationException("The D3D reader returned an invalid or over-bound configuration count.");
        for (var index = 0; index < configurations.Length; index++)
        {
            var configuration = configurations[index];
            if (configuration is null || configuration.Configuration is null || configuration.Index != (uint)index)
                throw new InvalidOperationException("The D3D reader returned a missing, reordered, or null indexed configuration.");
        }
    }
}
