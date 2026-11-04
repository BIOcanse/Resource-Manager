using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Infrastructure.CpuTopology;

/// <summary>Passes the product capture and current publication to the original shared projection.</summary>
public static class CpuTopologyInformationProjection
{
    public static DeviceInformationSnapshot Create(CpuTopologySnapshot? snapshot, DeviceInformationSnapshot current)
        => snapshot is null ? CpuInformationFieldProjection.ClearValues(current)
            : CpuInformationFieldProjection.Create(snapshot.OsTopologyInformation, snapshot.CapturedAt, current);

    public static DeviceInformationSnapshot ClearValues(DeviceInformationSnapshot current)
        => CpuInformationFieldProjection.ClearValues(current);
}
