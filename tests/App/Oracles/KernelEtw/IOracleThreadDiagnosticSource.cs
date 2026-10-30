using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

/// <summary>Only the old owner's OS and clock primitives are replaceable; its state/decisions remain unchanged.</summary>
internal interface IOracleThreadDiagnosticSource
{
    DateTimeOffset UtcNow { get; }
    int LastError { get; }
    IntPtr OpenProcess(uint access, int processId);
    bool TryGetCreationFileTime(IntPtr process, out ulong creation);
    bool IsRunning(IntPtr process);
    bool TryReadModules(IntPtr process, out IReadOnlyList<WindowsProcessLoadedModule> modules);
    bool CloseHandle(IntPtr handle);
    bool TryReadInventory(int processId, long creation, out IReadOnlyList<OracleWindowsSystemCpuThread> rows);
    IntPtr OpenDiagnosticThread(int pid, int tid, long birth, out long creation, out long kernel, out long user, out int error);
    (string? Name, int? Error) ReadDiagnosticName(IntPtr thread);
    ulong? ReadDiagnosticStartAddress(IntPtr thread);
    bool ReadDiagnosticTimes(IntPtr thread, out long creation, out long kernel, out long user, out long exit);
    void CloseDiagnosticThread(IntPtr thread);
}

internal sealed class OracleWindowsThreadDiagnosticSource : IOracleThreadDiagnosticSource
{
    private readonly WindowsProcessDetailsNativeApi process = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public int LastError => Marshal.GetLastWin32Error();
    public IntPtr OpenProcess(uint access, int processId) => process.OpenProcess(access, processId);
    public bool TryGetCreationFileTime(IntPtr handle, out ulong creation) => process.TryGetCreationFileTime(handle, out creation);
    public bool IsRunning(IntPtr handle) => process.IsRunning(handle);
    public bool TryReadModules(IntPtr handle, out IReadOnlyList<WindowsProcessLoadedModule> modules) => process.TryReadModules(handle, out modules);
    public bool CloseHandle(IntPtr handle) => process.CloseHandle(handle);
    public bool TryReadInventory(int pid, long birth, out IReadOnlyList<OracleWindowsSystemCpuThread> rows)
        => OracleWindowsSystemCpuThreadInventory.TryRead(pid, birth, out rows);
    public IntPtr OpenDiagnosticThread(int pid, int tid, long birth, out long creation, out long kernel, out long user, out int error)
        => OracleThreadDiagnosticNativeApi.OpenDiagnosticThread(pid, tid, birth, out creation, out kernel, out user, out error);
    public (string? Name, int? Error) ReadDiagnosticName(IntPtr handle) => OracleThreadDiagnosticNativeApi.ReadDiagnosticName(handle);
    public ulong? ReadDiagnosticStartAddress(IntPtr handle) => OracleThreadDiagnosticNativeApi.ReadDiagnosticStartAddress(handle);
    public bool ReadDiagnosticTimes(IntPtr handle, out long creation, out long kernel, out long user, out long exit)
        => OracleThreadDiagnosticNativeApi.ReadDiagnosticTimes(handle, out creation, out kernel, out user, out exit);
    public void CloseDiagnosticThread(IntPtr handle) => OracleThreadDiagnosticNativeApi.CloseDiagnosticThread(handle);
}
