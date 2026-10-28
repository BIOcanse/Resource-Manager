using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ControlDecisionAcceptance;

// Owns one unique Job and only adds this probe process. No process is launched or terminated.
internal sealed class ProbeJob : IDisposable
{
    private readonly SafeFileHandle handle;
    internal string Name { get; } = $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}";
    internal ProbeJob()
    {
        handle = CreateJobObjectW(0, Name);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = Process.GetCurrentProcess();
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The private probe Job could not own the probe process.");
        }
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}

