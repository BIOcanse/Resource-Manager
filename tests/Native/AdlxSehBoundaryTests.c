/* The production bridge and real Windows SEH, with no installed vendor SDK or hardware. */
#include "../../src/Core/Native/AdlxBridge/ResourceManagerAdlxBridge.c"
#include <assert.h>

static IADLXSystem system_fixture;
static IADLXGPUList list_fixture;
static int get_calls, release_calls, fault_get, fault_release;
static ADLX_RESULT __stdcall get_gpus(IADLXSystem* self, IADLXGPUList** result)
{
    (void)self; get_calls++;
    if (fault_get) RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, NULL);
    *result = &list_fixture; return ADLX_OK;
}
static adlx_uint __stdcall list_size(IADLXGPUList* self) { (void)self; return 0; }
static adlx_long __stdcall release_list(IADLXGPUList* self)
{
    (void)self; release_calls++;
    if (fault_release) RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, NULL);
    return 1;
}
static void reset_fixture(void)
{
    static IADLXSystemVtbl system_vtable;
    static IADLXGPUListVtbl list_vtable;
    system_vtable.GetGPUs = get_gpus;
    list_vtable.Size = list_size; list_vtable.Release = release_list;
    system_fixture.pVtbl = &system_vtable; list_fixture.pVtbl = &list_vtable;
    gSystem = &system_fixture; gInitAttempted = 1; gInitResult = RM_ADLX_OK;
    gAdlxFaulted = 0; gAdlxExceptionCode = 0;
    get_calls = release_calls = fault_get = fault_release = 0;
}
static int call_entry(int entry)
{
    ResourceManagerAdlxGpuMetrics metrics[1] = {{0}};
    ResourceManagerAdlxGpuDeclaration declarations[1] = {{0}};
    ResourceManagerAdlxGpuSelection selection = {0};
    selection.uniqueId = 1; strcpy(selection.pnpString, "PCI\\VEN_1002\\FIXTURE"); selection.requestedFlags = 1;
    char text[512] = {0}; int value, min, max, step, mode;
    switch (entry) {
    case 0: return ResourceManagerAdlxReadGpuMetrics(metrics, 1, 0, text, sizeof(text));
    case 1: return ResourceManagerAdlxReadSelectedGpuMetrics(metrics, 1, &selection, 1, text, sizeof(text));
    case 2: return ResourceManagerAdlxReadGpuDeclarations(declarations, 1, &selection, 1, text, sizeof(text));
    case 3: return ResourceManagerAdlxGetTuningGpuPnp(0, text, sizeof(text));
    default: return ResourceManagerAdlxTuning(selection.pnpString, 0, 0, 0, &value, &min, &max, &step, &mode);
    }
}
int main(void)
{
    for (int entry = 0; entry < 5; entry++) {
        for (int cleanup = 0; cleanup < 2; cleanup++) {
            reset_fixture(); fault_get = !cleanup; fault_release = cleanup;
            assert(call_entry(entry) == RM_ADLX_ERROR_FAULTED);
            assert(gAdlxFaulted == 1 && (DWORD)gAdlxExceptionCode == EXCEPTION_ACCESS_VIOLATION);
            assert(TryAcquireSRWLockExclusive(&gLock));
            ReleaseSRWLockExclusive(&gLock);
            int calls = get_calls, releases = release_calls;
            for (int later = 0; later < 5; later++) assert(call_entry(later) == RM_ADLX_ERROR_FAULTED);
            assert(get_calls == calls && release_calls == releases);
        }
    }
    reset_fixture(); AcquireSRWLockExclusive(&gLock);
    for (int entry = 0; entry < 5; entry++) assert(call_entry(entry) == RM_ADLX_ERROR_BUSY);
    assert(get_calls == 0); ReleaseSRWLockExclusive(&gLock);
    assert(call_entry(0) == 0 && get_calls == 1 && release_calls == 1);
    assert(TryAcquireSRWLockExclusive(&gLock)); ReleaseSRWLockExclusive(&gLock);
    puts("ADLX Windows SEH: five public entries, call/cleanup faults, permanent invalidation, lock release and busy admission passed.");
    return 0;
}
