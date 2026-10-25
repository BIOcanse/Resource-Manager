using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

internal sealed record RendererProcessIdentity(int ProcessId, ulong StartKey, string ImagePath, uint SessionId);
internal sealed record RendererModuleName(string Name, IntPtr Address);
internal readonly record struct RendererArgumentsRead(string[]? Value);

internal interface IRendererQualificationNativeApi
{
    IntPtr Open(int processId);
    void Close(IntPtr process);
    RendererProcessIdentity? ReadIdentity(IntPtr process, int processId);
    RendererArgumentsRead ReadArguments(IntPtr process);
    int? ReadParentId(IntPtr process);
    bool? IsDebugged(IntPtr process);
    bool IsAmd64(string path);
    IReadOnlyList<RendererModuleName>? ReadModuleNames(IntPtr process);
    string? ReadModulePath(IntPtr process, RendererModuleName module);
    int? ReadModuleSize(IntPtr process, RendererModuleName module);
    Version ReadVersion(string path);
    string HashFile(string path);
    byte[]? ReadMemory(IntPtr process, IntPtr address, int length);
}

/// <summary>Borrowed module addresses and owned process handles for one preparation pass only.</summary>
internal sealed class RendererQualificationReadCycle(IRendererQualificationNativeApi api) : IDisposable
{
    private readonly Dictionary<int, RendererProcessRead?> processes = [];
    private readonly Dictionary<(int, ulong), RendererAbsenceCache.Observation> absenceObservations = [];
    private bool disposed;
    internal int ReadPlanCount { get; set; }
    internal int ConfirmedApiCount { get; set; }
    internal int NegativeHits { get; set; }
    internal int ModuleReads { get; private set; }
    internal RendererAbsenceCache.Observation RememberAbsenceObservation(GpuPlacementProcessInstance process,
        RendererAbsenceCache.Observation observation)
    {
        var key = (process.ProcessId, process.ProcessStartKey);
        if (!absenceObservations.TryGetValue(key, out var first)) absenceObservations.Add(key, first = observation);
        return first;
    }

    internal RendererProcessRead? Get(GpuPlacementProcessInstance expected)
    {
        if (expected.ProcessId <= 4 || expected.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(expected.ExecutablePath)) return null;
        var process = Get(expected.ProcessId);
        return process is not null && process.Identity.StartKey == expected.ProcessStartKey
            && StringComparer.OrdinalIgnoreCase.Equals(process.Identity.ImagePath, expected.ExecutablePath) ? process : null;
    }

    internal RendererProcessRead? Get(int processId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (processId <= 4) return null;
        if (processes.TryGetValue(processId, out var current)) return current;
        processes.Add(processId, null);
        var handle = api.Open(processId);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        try
        {
            var identity = api.ReadIdentity(handle, processId);
            if (identity is null || identity.ProcessId != processId || identity.StartKey == 0
                || string.IsNullOrWhiteSpace(identity.ImagePath)) return null;
            current = new RendererProcessRead(api, handle, identity, () => ModuleReads++);
            processes[processId] = current;
            handle = IntPtr.Zero;
            return current;
        }
        finally { if (handle != IntPtr.Zero) api.Close(handle); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Exception? failure = null;
        foreach (var process in processes.Values)
        {
            try { process?.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        processes.Clear();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal sealed class RendererProcessRead(
    IRendererQualificationNativeApi api, IntPtr handle, RendererProcessIdentity identity, Action moduleRead) : IDisposable
{
    private bool disposed;
    private bool argumentsRead, modulesRead;
    private RendererArgumentsRead arguments;
    private IReadOnlyList<RendererModuleName>? modules;
    private readonly Dictionary<IntPtr, string?> paths = [];
    internal RendererProcessIdentity Identity { get; } = identity;
    internal IntPtr Handle => !disposed ? handle : throw new ObjectDisposedException(nameof(RendererProcessRead));
    internal RendererArgumentsRead Arguments()
    {
        if (!argumentsRead) { argumentsRead = true; arguments = api.ReadArguments(Handle); }
        return arguments;
    }
    internal IReadOnlyList<RendererModuleName>? Modules()
    {
        if (!modulesRead) { modulesRead = true; moduleRead(); modules = api.ReadModuleNames(Handle); }
        return modules;
    }
    internal string? ModulePath(RendererModuleName module)
    {
        if (!paths.TryGetValue(module.Address, out var path)) paths.Add(module.Address, path = api.ReadModulePath(Handle, module));
        return path;
    }
    internal int? ParentId() => api.ReadParentId(Handle);
    internal bool? IsDebugged() => api.IsDebugged(Handle);
    internal bool IsAmd64() => api.IsAmd64(Identity.ImagePath);
    internal int? ModuleSize(RendererModuleName module) => api.ReadModuleSize(Handle, module);
    internal Version Version(RendererModuleName module)
        => api.ReadVersion(ModulePath(module) ?? throw new InvalidOperationException("Module path is unavailable."));
    internal string Hash(RendererModuleName module)
        => api.HashFile(ModulePath(module) ?? throw new InvalidOperationException("Module path is unavailable."));
    internal byte[]? ReadMemory(IntPtr address, int length) => api.ReadMemory(Handle, address, length);
    public void Dispose() { if (!disposed) { disposed = true; api.Close(handle); } }
}
