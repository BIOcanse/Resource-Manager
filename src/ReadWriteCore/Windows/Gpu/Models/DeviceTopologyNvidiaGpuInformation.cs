namespace ResourceManager.App.Domain.DeviceTopology;

public sealed record DeviceTopologyNvidiaGpuInformation(
    string PnpInstanceId, string? VbiosVersion, string? CudaComputeCapability);
