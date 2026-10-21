using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.CpuTopology;

public interface ICpuTopologySampler
{
    CpuTopologySnapshot CaptureSnapshot();

    CpuTopologySnapshot CaptureTopology();

    /// <summary>Ends the current usage interval when sampling demand or a capture is lost.</summary>
    void ResetUsageBaseline();
}
