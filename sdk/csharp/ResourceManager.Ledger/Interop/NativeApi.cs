using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.Ledger.Interop;

internal sealed unsafe class NativeApi
{
    private readonly nint _library;

    internal readonly delegate* unmanaged[Cdecl]<NativeDescriptor*, int> QueryDescriptor;
    internal readonly delegate* unmanaged[Cdecl]<void*, ulong, void**, int> Create;
    internal readonly delegate* unmanaged[Cdecl]<void*, uint, void*, ulong, int> Destroy;
    internal readonly delegate* unmanaged[Cdecl]<void*, byte*, ulong, NativeBorrow*, int> Use;
    internal readonly delegate* unmanaged[Cdecl]<void*, uint, NativeBorrow*, int> UseFixed;
    internal readonly delegate* unmanaged[Cdecl]<void*, NativeTarget*, byte*, ulong, uint, ulong, void*, ulong, NativeBorrow*, int> Push;
    internal readonly delegate* unmanaged[Cdecl]<void*, NativeTarget*, byte*, ulong, uint, NativeAttributes*, void*, ulong, NativePartitionResult*, int> PushPartition;
    internal readonly delegate* unmanaged[Cdecl]<void*, NativeLease*, uint, int> EndUse;
    internal readonly delegate* unmanaged[Cdecl]<void*, byte*, ulong, NativeUpdate*, int> UpdateUid;
    internal readonly delegate* unmanaged[Cdecl]<void*, NativeLease*, NativeUpdate*, NativeBorrow*, int> UpdateLease;
    internal readonly delegate* unmanaged[Cdecl]<void*, byte*, ulong, void*, ulong, int> Close;
    internal readonly delegate* unmanaged[Cdecl]<void*, byte*, ulong, void*, ulong, int> ForceRelease;
    internal readonly delegate* unmanaged[Cdecl]<void*, uint, uint, int> SetMode;
    internal readonly delegate* unmanaged[Cdecl]<void*, uint, NativeTableView*, int> GetTableView;
    internal readonly delegate* unmanaged[Cdecl]<void*, uint, NativeDevelopmentStatistics*, int> GetDevelopmentStatistics;
    internal readonly delegate* unmanaged[Cdecl]<void*, NativeLeaseAnalysisRecord*, ulong, NativeLeaseAnalysisSummary*, int> GetLeaseAnalysis;
    internal readonly delegate* unmanaged[Cdecl]<void*, void*, ulong, NativeTickResult*, int> Tick;
    internal readonly delegate* unmanaged[Cdecl]<void*, uint, delegate* unmanaged[Cdecl]<void*, byte*, ulong, ulong, void>, void*, NativeSubscriptionHandle*, int> Subscribe;
    internal readonly delegate* unmanaged[Cdecl]<void*, NativeSubscriptionHandle*, int> Unsubscribe;

    private NativeApi(nint library)
    {
        _library = library;
        QueryDescriptor = (delegate* unmanaged[Cdecl]<NativeDescriptor*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_query_descriptor");
        Create = (delegate* unmanaged[Cdecl]<void*, ulong, void**, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_create");
        Destroy = (delegate* unmanaged[Cdecl]<void*, uint, void*, ulong, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_destroy");
        Use = (delegate* unmanaged[Cdecl]<void*, byte*, ulong, NativeBorrow*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_use");
        UseFixed = (delegate* unmanaged[Cdecl]<void*, uint, NativeBorrow*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_use_fixed");
        Push = (delegate* unmanaged[Cdecl]<void*, NativeTarget*, byte*, ulong, uint, ulong, void*, ulong, NativeBorrow*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_push");
        PushPartition = (delegate* unmanaged[Cdecl]<void*, NativeTarget*, byte*, ulong, uint, NativeAttributes*, void*, ulong, NativePartitionResult*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_push_partition");
        EndUse = (delegate* unmanaged[Cdecl]<void*, NativeLease*, uint, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_end_use");
        UpdateUid = (delegate* unmanaged[Cdecl]<void*, byte*, ulong, NativeUpdate*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_update_uid");
        UpdateLease = (delegate* unmanaged[Cdecl]<void*, NativeLease*, NativeUpdate*, NativeBorrow*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_update_lease");
        Close = (delegate* unmanaged[Cdecl]<void*, byte*, ulong, void*, ulong, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_close");
        ForceRelease = (delegate* unmanaged[Cdecl]<void*, byte*, ulong, void*, ulong, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_force_release");
        SetMode = (delegate* unmanaged[Cdecl]<void*, uint, uint, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_set_mode");
        GetTableView = (delegate* unmanaged[Cdecl]<void*, uint, NativeTableView*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_get_table_view");
        if (NativeLibrary.TryGetExport(library, "rm_ledger_get_development_statistics", out nint statistics))
            GetDevelopmentStatistics = (delegate* unmanaged[Cdecl]<void*, uint, NativeDevelopmentStatistics*, int>)(void*)statistics;
        bool hasAnalysis = NativeLibrary.TryGetExport(library, "rm_ledger_get_lease_analysis", out nint analysis);
        if (hasAnalysis)
        {
            if (sizeof(NativeLeaseAnalysisRecord) != 128 || sizeof(NativeLeaseAnalysisSummary) != 48)
                throw new InvalidOperationException("Managed lease-analysis record layout is invalid.");
            GetLeaseAnalysis = (delegate* unmanaged[Cdecl]<void*, NativeLeaseAnalysisRecord*, ulong, NativeLeaseAnalysisSummary*, int>)(void*)analysis;
        }
        Tick = (delegate* unmanaged[Cdecl]<void*, void*, ulong, NativeTickResult*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_tick");
        Subscribe = (delegate* unmanaged[Cdecl]<void*, uint, delegate* unmanaged[Cdecl]<void*, byte*, ulong, ulong, void>, void*, NativeSubscriptionHandle*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_subscribe");
        Unsubscribe = (delegate* unmanaged[Cdecl]<void*, NativeSubscriptionHandle*, int>)(void*)NativeLibrary.GetExport(library, "rm_ledger_unsubscribe");
    }

    internal static NativeApi Load(string nativeLibraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeLibraryPath);
        string fullPath = Path.GetFullPath(nativeLibraryPath);
        nint library = NativeLibrary.Load(fullPath);
        try
        {
            return new NativeApi(library);
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    internal void Free() => NativeLibrary.Free(_library);
}
