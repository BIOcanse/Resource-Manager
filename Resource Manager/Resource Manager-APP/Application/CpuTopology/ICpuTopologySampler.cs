using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.CpuTopology;

public interface ICpuTopologySampler
{
    CpuTopologySnapshot CaptureSnapshot();

    CpuTopologySnapshot CaptureTopology();
}
