namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>A retained, pinned region owned by the host's allocation authority.</summary>
public interface IKernelEtwRegionProvider
{
    IKernelEtwRegionLease Allocate(string owner, nuint minimumLength);
}

public interface IKernelEtwRegionLease : IDisposable
{
    nint Pointer { get; }
    nuint Length { get; }
    void Complete();
}

/// <summary>Output-scoped demand allocation; the cap is not a reservation.</summary>
public interface IKernelEtwAllocationProvider
{
    NativeKernelEtwHostAllocator CreateAllocator(string sessionName, ulong byteCap);
}

/// <summary>Thread attribution for a held resource, which has no business-block node.</summary>
public interface IKernelEtwConsumerThreadObserver
{
    IDisposable RegisterCurrentThread(string sessionName);
    void ReportFailure(Exception error);
}
