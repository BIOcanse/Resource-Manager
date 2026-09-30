/* ADLX tuning ABI follows SDK/Include/IGPUTuning.h and IGPUManual* tuning headers.
   Interfaces are acquired per call under the same lock as telemetry.
   No ResetToFactory: restoring one parameter must preserve other configured values. */
typedef struct RmAdlxTuningInterface RmAdlxTuningInterface;
typedef struct RmAdlxTuningVtbl {
    adlx_long (__stdcall *Acquire)(RmAdlxTuningInterface*);
    adlx_long (__stdcall *Release)(RmAdlxTuningInterface*);
    ADLX_RESULT (__stdcall *QueryInterface)(RmAdlxTuningInterface*, const wchar_t*, void**);
    void* methods[24];
} RmAdlxTuningVtbl;
struct RmAdlxTuningInterface { const RmAdlxTuningVtbl* pVtbl; };
typedef struct RmAdlxIntRange { int minimum; int maximum; int step; } RmAdlxIntRange;
typedef ADLX_RESULT (__stdcall *RmGetTuningServices)(IADLXSystem*, RmAdlxTuningInterface**);
typedef ADLX_RESULT (__stdcall *RmGetTuning)(RmAdlxTuningInterface*, IADLXGPU*, RmAdlxTuningInterface**);
typedef ADLX_RESULT (__stdcall *RmIsSupportedTuning)(RmAdlxTuningInterface*, IADLXGPU*, adlx_bool*);
typedef ADLX_RESULT (__stdcall *RmGetRange)(RmAdlxTuningInterface*, RmAdlxIntRange*);
typedef ADLX_RESULT (__stdcall *RmGetInt)(RmAdlxTuningInterface*, int*);
typedef ADLX_RESULT (__stdcall *RmSetInt)(RmAdlxTuningInterface*, int);
typedef ADLX_RESULT (__stdcall *RmGetBool)(RmAdlxTuningInterface*, adlx_bool*);
typedef ADLX_RESULT (__stdcall *RmSetBool)(RmAdlxTuningInterface*, adlx_bool);

/* Private bridge result values are negative; vendor result values remain intact. */
#define RM_TUNING_UNSUPPORTED -10
#define RM_TUNING_IDENTITY -11
#define RM_TUNING_RANGE -12
#define RM_TUNING_READBACK -13
#define RM_TUNING_END -14
#define RM_TUNING_SEMANTICS -15
#define RM_TUNING_GENERATION -16

/* Known GPU IDs from Infrastructure/Resources/DeviceIds/pci.ids (AMD 1002).
   Do not infer generation from marketing names, ASICFamilyType, or range signs.
   Unknown IDs require an explicit entry before GFX writes can be enabled. */
static int tuning_gfx_mode(IADLXGPU* gpu)
{
    const char* text = NULL;
    char* end = NULL;
    if (!gpu->pVtbl->DeviceId || !ADLX_SUCCEEDED(gpu->pVtbl->DeviceId(gpu, &text)) || !text || !*text) return -1;
    unsigned long id = strtoul(text, &end, 16);
    if (!end || *end || id > 0xffff) return -1;
    if (id == 0x7550 || id == 0x7551 || id == 0x7590) return 1; /* Navi 48 / 44 */
    static const unsigned short absolute_ids[] = {
        0x66a0,0x66a1,0x66a2,0x66a3,0x66a7,0x66af,0x67a0,0x67a1,0x67a2,0x67a8,0x67a9,0x67aa,
        0x67b0,0x67b1,0x67b8,0x67be,0x67c0,0x67c2,0x67c4,0x67c7,0x67ca,0x67cc,0x67cf,0x67d0,
        0x67d4,0x67d7,0x67df,0x67e0,0x67e1,0x67e3,0x67e8,0x67e9,0x67eb,0x67ef,0x67ff,
        0x6860,0x6861,0x6862,0x6863,0x6864,0x6867,0x6868,0x6869,0x686a,0x686b,0x686c,0x686d,
        0x686e,0x687f,0x6929,0x692b,0x692f,0x6930,0x6938,0x6939,0x693b,0x694c,0x694e,0x694f,
        0x6980,0x6986,0x69a0,0x69a1,0x69a2,0x69a3,0x69af,0x6fdf,0x7300,0x7310,0x7312,0x7319,
        0x731b,0x731f,0x7340,0x7341,0x7347,0x734f,0x7360,0x7362,0x73a1,0x73a2,0x73a3,0x73a5,
        0x73ab,0x73ae,0x73af,0x73bf,0x73c3,0x73ce,0x73df,0x73e0,0x73e1,0x73e3,0x73ef,0x73f0,
        0x73ff,0x7421,0x7422,0x7423,0x7424,0x743f,0x7448,0x7449,0x744a,0x744b,0x744c,0x745e,
        0x7460,0x7461,0x7470,0x747e,0x7480,0x7481,0x7483,0x7487,0x7489,0x748b,0x7499,0x749f
    };
    for (size_t i = 0; i < sizeof(absolute_ids) / sizeof(absolute_ids[0]); i++)
        if (id == absolute_ids[i]) return 0;
    return -1;
}

static IADLXGPU* tuning_gpu_at(IADLXGPUList* list, unsigned int index)
{
    IADLXGPU* gpu = NULL;
    if (!ADLX_SUCCEEDED(list->pVtbl->At_GPUList(list, index, &gpu))) return NULL;
    return gpu;
}

__declspec(dllexport) int ResourceManagerAdlxGetTuningGpuPnp(int index, char* pnp, int capacity)
{
    IADLXGPUList* list = NULL;
    IADLXGPU* gpu = NULL;
    int result;
    AcquireSRWLockExclusive(&gLock);
    result = load_adlx(NULL, 0);
    if (result != 0) goto done;
    result = gSystem->pVtbl->GetGPUs(gSystem, &list);
    if (!ADLX_SUCCEEDED(result)) goto done;
    if (!list) { result = RM_TUNING_IDENTITY; goto done; }
    result = RM_TUNING_IDENTITY;
    if (index < 0 || (unsigned int)index >= list->pVtbl->Size(list) || index >= 128) { result = RM_TUNING_END; goto done; }
    gpu = tuning_gpu_at(list, (unsigned int)index);
    if (!gpu) goto done;
    const char* identity = NULL;
    result = gpu->pVtbl->PNPString(gpu, &identity);
    if (ADLX_SUCCEEDED(result) && identity && capacity > (int)strlen(identity)) copy_text(pnp, (size_t)capacity, identity);
    else result = RM_TUNING_IDENTITY;
done:
    if (gpu) gpu->pVtbl->Release(gpu);
    if (list) list->pVtbl->Release(list);
    ReleaseSRWLockExclusive(&gLock);
    return result;
}

/* field: 0=power %, 1=min GFX MHz, 2=max GFX MHz, 3=voltage mV, 4=max VRAM MHz,
   5=minimum fan RPM, 6=target fan RPM, 7=Zero RPM, 8=max GFX offset MHz, 9=voltage offset mV.
   operation: 0=read only (also range discovery), 1=write and read back. */
__declspec(dllexport) int ResourceManagerAdlxTuning(const char* pnp, int field, int operation,
    int requested, int* current, int* minimum, int* maximum, int* step, int* gfxMode)
{
    IADLXGPUList* list = NULL;
    IADLXGPU* gpu = NULL;
    RmAdlxTuningInterface* services = NULL;
    RmAdlxTuningInterface* unknown = NULL;
    RmAdlxTuningInterface* tuning = NULL;
    RmAdlxIntRange range = {0};
    int result, rangeSlot, getSlot, setSlot, serviceSlot;
    const wchar_t* interfaceId;
    if (!pnp || !*pnp || !current || !minimum || !maximum || !step || !gfxMode || field < 0 || field > 9 || operation < 0 || operation > 1) return RM_TUNING_RANGE;
    *gfxMode = -1;
    AcquireSRWLockExclusive(&gLock);
    result = load_adlx(NULL, 0);
    if (result != 0) goto done;
    result = gSystem->pVtbl->GetGPUs(gSystem, &list);
    if (!ADLX_SUCCEEDED(result)) goto done;
    if (!list) { result = RM_TUNING_IDENTITY; goto done; }
    for (unsigned int i = 0, count = list->pVtbl->Size(list); i < count && i < 128; i++) {
        IADLXGPU* candidate = tuning_gpu_at(list, i);
        if (!candidate) continue;
        const char* identity = NULL;
        int type = 0;
        if (ADLX_SUCCEEDED(candidate->pVtbl->PNPString(candidate, &identity)) && identity && _stricmp(identity, pnp) == 0
            && ADLX_SUCCEEDED(candidate->pVtbl->Type(candidate, &type)) && type == 2) {
            if (gpu) { candidate->pVtbl->Release(candidate); result = RM_TUNING_IDENTITY; goto done; }
            gpu = candidate;
        } else candidate->pVtbl->Release(candidate);
    }
    result = RM_TUNING_IDENTITY;
    if (!gpu) goto done;
    *gfxMode = tuning_gfx_mode(gpu);
    if ((field >= 1 && field <= 3) || field >= 8) {
        if (*gfxMode < 0) { result = RM_TUNING_GENERATION; goto done; }
        if (((field == 2 || field == 3) && *gfxMode != 0) || (field >= 8 && *gfxMode != 1)) {
            result = RM_TUNING_SEMANTICS; goto done;
        }
    }
    if (!gSystem->pVtbl->GetGPUTuningServices) { result = RM_TUNING_UNSUPPORTED; goto done; }
    result = ((RmGetTuningServices)gSystem->pVtbl->GetGPUTuningServices)(gSystem, &services);
    if (!ADLX_SUCCEEDED(result)) goto done;
    if (!services) { result = RM_TUNING_UNSUPPORTED; goto done; }
    /* Slots after the IADLXInterface prefix: GetManualPower=14, GFX=11, VRAM=12. */
    if (field == 0) {
        serviceSlot = 14; interfaceId = L"IADLXManualPowerTuning"; rangeSlot = 0; getSlot = 1; setSlot = 2;
    } else if (field == 4) {
        serviceSlot = 12; interfaceId = L"IADLXManualVRAMTuning2"; rangeSlot = 4; getSlot = 5; setSlot = 6;
    } else if (field < 5 || field >= 8) {
        int gfxField = field == 8 ? 2 : field == 9 ? 3 : field;
        serviceSlot = 11; interfaceId = L"IADLXManualGraphicsTuning2"; rangeSlot = (gfxField - 1) * 3; getSlot = rangeSlot + 1; setSlot = rangeSlot + 2;
    } else {
        serviceSlot = 13; interfaceId = L"IADLXManualFanTuning";
        rangeSlot = field == 5 ? 13 : 17; getSlot = rangeSlot + 1; setSlot = rangeSlot + 2;
    }
    adlx_bool serviceSupported = 0;
    result = ((RmIsSupportedTuning)services->pVtbl->methods[serviceSlot - 6])(services, gpu, &serviceSupported);
    if (!ADLX_SUCCEEDED(result) || !serviceSupported) { result = RM_TUNING_UNSUPPORTED; goto done; }
    result = ((RmGetTuning)services->pVtbl->methods[serviceSlot])(services, gpu, &unknown);
    if (!ADLX_SUCCEEDED(result)) goto done;
    if (!unknown) { result = RM_TUNING_UNSUPPORTED; goto done; }
    result = unknown->pVtbl->QueryInterface(unknown, interfaceId, (void**)&tuning);
    if (!ADLX_SUCCEEDED(result) || !tuning) { result = RM_TUNING_UNSUPPORTED; goto done; }
    if (field >= 5 && field <= 7) {
        adlx_bool supported = 0;
        result = ((RmGetBool)tuning->pVtbl->methods[field == 5 ? 12 : field == 6 ? 16 : 5])(tuning, &supported);
        if (!ADLX_SUCCEEDED(result) || !supported) { result = RM_TUNING_UNSUPPORTED; goto done; }
    }
    if (field == 7) {
        adlx_bool enabled = 0;
        *minimum = 0; *maximum = 1; *step = 1;
        result = ((RmGetBool)tuning->pVtbl->methods[6])(tuning, &enabled);
        *current = enabled ? 1 : 0;
        if (!ADLX_SUCCEEDED(result) || operation == 0) goto done;
        if (requested != 0 && requested != 1) { result = RM_TUNING_RANGE; goto done; }
        result = ((RmSetBool)tuning->pVtbl->methods[7])(tuning, (adlx_bool)requested);
        if (!ADLX_SUCCEEDED(result)) goto done;
        result = ((RmGetBool)tuning->pVtbl->methods[6])(tuning, &enabled);
        *current = enabled ? 1 : 0;
        if (ADLX_SUCCEEDED(result) && *current != requested) result = RM_TUNING_READBACK;
        goto done;
    }
    result = ((RmGetRange)tuning->pVtbl->methods[rangeSlot])(tuning, &range);
    if (!ADLX_SUCCEEDED(result)) goto done;
    if (range.minimum > range.maximum || range.step <= 0) { result = RM_TUNING_RANGE; goto done; }
    *minimum = range.minimum; *maximum = range.maximum; *step = range.step;
    result = ((RmGetInt)tuning->pVtbl->methods[getSlot])(tuning, current);
    if (!ADLX_SUCCEEDED(result) || operation == 0) goto done;
    if (requested < range.minimum || requested > range.maximum || ((int64_t)requested - range.minimum) % range.step != 0) { result = RM_TUNING_RANGE; goto done; }
    if ((field == 1 || field == 2) && *gfxMode == 0) {
        int sibling;
        result = ((RmGetInt)tuning->pVtbl->methods[field == 1 ? 4 : 1])(tuning, &sibling);
        if (!ADLX_SUCCEEDED(result)) goto done;
        if ((field == 1 && requested > sibling) || (field == 2 && requested < sibling)) { result = RM_TUNING_RANGE; goto done; }
    }
    result = ((RmSetInt)tuning->pVtbl->methods[setSlot])(tuning, requested);
    if (!ADLX_SUCCEEDED(result)) goto done;
    result = ((RmGetInt)tuning->pVtbl->methods[getSlot])(tuning, current);
    if (ADLX_SUCCEEDED(result) && *current != requested) result = RM_TUNING_READBACK;
done:
    if (tuning) tuning->pVtbl->Release(tuning);
    if (unknown) unknown->pVtbl->Release(unknown);
    if (services) services->pVtbl->Release(services);
    if (gpu) gpu->pVtbl->Release(gpu);
    if (list) list->pVtbl->Release(list);
    ReleaseSRWLockExclusive(&gLock);
    return result;
}
