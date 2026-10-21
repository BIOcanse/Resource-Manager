using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeLedger;
using ResourceManager.Adapter.NativeScheduling;

if (args.Length != 3 || args[0] is not ("ledger" or "scheduler")) return 2;
NativeErrorPolicy.SetErrorMode(0x0001 | 0x0002);
using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
var shim = NativeLibrary.Load(Path.GetFullPath(args[1]));
var load = Marshal.GetDelegateForFunctionPointer<LoadActual>(NativeLibrary.GetExport(shim, "rm_test_load_actual"));
if (load(Path.GetFullPath(args[2])) != 1) return 3;
// The fixture's isolated directory contains the shim under the SDK's canonical
// file name, so the production resolver remains in charge of binding.
var calls = Marshal.GetDelegateForFunctionPointer<ReadCalls>(NativeLibrary.GetExport(shim, "rm_test_initialization_calls"));
for (var round = 0; round < 32; round++)
{
    RejectInitialization(args[0]);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}
if (calls() != 32) return 4;
Console.WriteLine($"{args[0]}:32-native-initialization-failures-finalized");
return 0;

[MethodImpl(MethodImplOptions.NoInlining)]
static void RejectInitialization(string kind)
{
    try
    {
        if (kind == "ledger")
        {
            using var session = new NativeAdapterResourceLedgerSession(new(1, 1,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 64, 3, 4));
        }
        else
        {
            using var session = new NativeResourceSchedulerSession(1, 1, 1, 0, Configuration(), 1);
        }
    }
    catch (InvalidOperationException failure) when
        (failure is NativeAdapterResourceLedgerException or NativeResourceSchedulerException
         && failure.Message.Contains("state-initialize", StringComparison.Ordinal)) { return; }
    throw new InvalidOperationException("The native initialization failure was not observed.");
}

static ResourceSchedulerConfig Configuration()
{
    const ulong mib = 1024 * 1024;
    return new(1, mib, 1.0, 2.25, 16.0,
        [1.80, 0.48, 0.88, 1.35, 0.88, 2.20, 0.48, 0.48, 2.20, 1.80, 0.88, 1.35, 0.88],
        [1.45, 1.20, 0.95, 0.72, 0.60], [0.55, 0.80, 0.95, 1.0, 1.10], [4, 3, 2, 1, 0],
        [0.50, 0.75, 1.0, 1.35], 1.0,
        [3.30, 0.32, 0.90, 2.60, 0.90, 4.00, 0.32, 0.32, 4.00, 3.30, 0.90, 2.80, 1.25],
        [2.50, 0.42, 2.50, 1.80, 2.50, 2.50, 0.42, 0.42, 2.50, 1.50, 0.65, 0.65, 1.80],
        [1.80, 1.25, 1.15, 0.95, 1.15, 3.00, 3.00, 1.25, 3.00, 2.20, 0.95, 1.15, 0.95],
        [2.40, 1.30, 2.40, 0.90, 2.40, 2.40, 1.30, 2.40, 2.40, 0.90, 0.90, 0.90, 0.90],
        0.70, 3.30, 255.0, [1.70, 1.22, 1.08], 512 * mib, 192, 3, 0.25, 0.18, 0.15,
        [0.06, 0.12, 0.20], [0.30, 0.30, 0.30], [0.24, 0.18, 0.12],
        [64 * mib, 128 * mib, 256 * mib, 512 * mib], [0.15, 0.30, 0.50, 0.75],
        [81.0, 61.0, 21.0], [0.15, 0.35, 0.65, 1.0], 0, 100, 1, 4, 1, 0.20, 0.10, 0.12, [2, 4, 1, 8, 16]);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
delegate int LoadActual([MarshalAs(UnmanagedType.LPWStr)] string path);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate uint ReadCalls();

static class NativeErrorPolicy
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}
