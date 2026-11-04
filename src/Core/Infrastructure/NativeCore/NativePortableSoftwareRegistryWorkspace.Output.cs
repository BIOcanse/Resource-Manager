using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Each plan or snapshot call creates its output rows at the rows the session
// holds right now (88 B per dirty path, 80 B per registration and per path) and
// closes them before returning, so the registry holds no scratch between calls.
// The caller's maximum counts stay upper bounds; a call lends no more rows than
// the session holds, and the caller's existing paging covers rows added between
// the row read and the call.
internal sealed partial class NativePortableSoftwareRegistryWorkspace
{
    internal const string ScratchWorkArea = "portable-registry";
    private readonly Func<ulong, IComputeScratch> createScratch;
    internal int OperationCapacity { get; }
    internal int RegistrationCapacity { get; }
    internal int PathCapacity { get; }

    internal NativePortableSoftwareRegistryStatus PlanPersistence(
        in NativePortableSoftwarePlanPersistenceInput input,
        out NativePortableSoftwarePersistenceOperation[] operations,
        out NativePortableSoftwarePersistencePlanOutput output)
    {
        operations = [];
        if (input.MaximumOperationCount == 0) return Session.PlanPersistence(in input, [], out output); // Native rejects it.
        var live = Session.WorkspaceRows;
        var bounded = input;
        bounded.MaximumOperationCount = LentRows(live.DirtyPathCount, input.MaximumOperationCount);
        var length = checked((ulong)bounded.MaximumOperationCount * RowBytes<NativePortableSoftwarePersistenceOperation>());
        using var scratch = CreateScratch(length,
            $"operations={bounded.MaximumOperationCount}/live={live.DirtyPathCount}");
        using var use = scratch.Acquire();
        using var view = new ComputeScratchView(use);
        var rows = view.Column<NativePortableSoftwarePersistenceOperation>(0, checked((int)bounded.MaximumOperationCount));
        rows.Clear();
        var status = Session.PlanPersistence(in bounded, rows, out output);
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
        if (input.MaximumRegistrationCount == 0 || input.MaximumPathCount == 0)
            return Session.Snapshot(in input, [], [], out output); // Native rejects it.
        var live = Session.WorkspaceRows;
        var bounded = input;
        bounded.MaximumRegistrationCount = LentRows(live.RegistrationCount, input.MaximumRegistrationCount);
        bounded.MaximumPathCount = LentRows(live.PathCount, input.MaximumPathCount);
        var pathsOffset = checked((ulong)bounded.MaximumRegistrationCount * RowBytes<NativePortableSoftwareRegistrationSnapshot>());
        var length = checked(pathsOffset + (ulong)bounded.MaximumPathCount * RowBytes<NativePortableSoftwarePathSnapshot>());
        using var scratch = CreateScratch(length,
            $"registrations={bounded.MaximumRegistrationCount}/live={live.RegistrationCount}, paths={bounded.MaximumPathCount}/live={live.PathCount}");
        using var use = scratch.Acquire();
        using var view = new ComputeScratchView(use);
        var registrationRows = view.Column<NativePortableSoftwareRegistrationSnapshot>(0, checked((int)bounded.MaximumRegistrationCount));
        var pathRows = view.Column<NativePortableSoftwarePathSnapshot>(pathsOffset, checked((int)bounded.MaximumPathCount));
        registrationRows.Clear();
        pathRows.Clear();
        var status = Session.Snapshot(in bounded, registrationRows, pathRows, out output);
        if (status != NativePortableSoftwareRegistryStatus.Ok) return status;
        if (output.RegistrationOutputCount > registrationRows.Length || output.PathOutputCount > pathRows.Length)
            throw new InvalidDataException("Portable registry snapshot count exceeds output capacity.");
        registrations = registrationRows[..checked((int)output.RegistrationOutputCount)].ToArray();
        paths = pathRows[..checked((int)output.PathOutputCount)].ToArray();
        use.Complete();
        return status;
    }

    // Native rejects a zero maximum, so an empty registry still lends one row.
    private static uint LentRows(uint live, uint maximum) => Math.Clamp(live, 1u, maximum);

    private static ulong RowBytes<T>() where T : unmanaged => (ulong)Unsafe.SizeOf<T>();

    private IComputeScratch CreateScratch(ulong length, string rows)
    {
        var created = createScratch(length)
            ?? throw new InvalidOperationException("Portable registry scratch factory returned no resource.");
        if (created.Length != length)
        {
            created.Dispose();
            throw new InvalidOperationException("Portable registry output scratch length mismatch.");
        }
        ComputeScratchNames.Name(created, ScratchWorkArea, rows);
        return created;
    }
}
