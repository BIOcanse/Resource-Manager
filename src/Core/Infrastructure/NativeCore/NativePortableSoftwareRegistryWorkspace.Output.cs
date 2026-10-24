using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativePortableSoftwareRegistryWorkspace
{
    private readonly IComputeScratch scratch;
    private readonly ulong registrationsOffset;
    private readonly ulong pathsOffset;
    internal int OperationCapacity { get; }
    internal int RegistrationCapacity { get; }
    internal int PathCapacity { get; }

    internal NativePortableSoftwareRegistryStatus PlanPersistence(
        in NativePortableSoftwarePlanPersistenceInput input,
        out NativePortableSoftwarePersistenceOperation[] operations,
        out NativePortableSoftwarePersistencePlanOutput output)
    {
        operations = [];
        using var use = scratch.Acquire();
        using var view = new ComputeScratchView(use);
        var rows = view.Column<NativePortableSoftwarePersistenceOperation>(0, OperationCapacity);
        rows.Clear();
        var status = Session.PlanPersistence(in input, rows, out output);
        if (status != NativePortableSoftwareRegistryStatus.Ok) return status;
        if (output.OperationCount > rows.Length)
            throw new InvalidDataException("Portable registry persistence count exceeds output capacity.");
        operations = rows[..checked((int)output.OperationCount)].ToArray();
        use.Complete();
        return status;
    }

    internal NativePortableSoftwareRegistryStatus ReadSnapshot(
        in NativePortableSoftwareSnapshotInput input,
        out NativePortableSoftwareRegistrationSnapshot[] registrations,
        out NativePortableSoftwarePathSnapshot[] paths,
        out NativePortableSoftwareSnapshotOutput output)
    {
        registrations = [];
        paths = [];
        using var use = scratch.Acquire();
        using var view = new ComputeScratchView(use);
        var registrationRows = view.Column<NativePortableSoftwareRegistrationSnapshot>(registrationsOffset, RegistrationCapacity);
        var pathRows = view.Column<NativePortableSoftwarePathSnapshot>(pathsOffset, PathCapacity);
        registrationRows.Clear();
        pathRows.Clear();
        var status = Session.Snapshot(in input, registrationRows, pathRows, out output);
        if (status != NativePortableSoftwareRegistryStatus.Ok) return status;
        if (output.RegistrationOutputCount > registrationRows.Length || output.PathOutputCount > pathRows.Length)
            throw new InvalidDataException("Portable registry snapshot count exceeds output capacity.");
        registrations = registrationRows[..checked((int)output.RegistrationOutputCount)].ToArray();
        paths = pathRows[..checked((int)output.PathOutputCount)].ToArray();
        use.Complete();
        return status;
    }
}
