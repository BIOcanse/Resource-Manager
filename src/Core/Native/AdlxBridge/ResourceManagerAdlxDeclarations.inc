/* Passive GPU declarations use the same SDK instance and lock as the metric bridge. */
typedef struct ResourceManagerAdlxGpuDeclaration
{
    int32_t structSize;
    uint32_t abiVersion;
    int32_t uniqueId;
    char pnpString[512];
    int32_t availableFlags;
    char vbiosVersion[128];
    char vramType[128];
    char vbiosPartNumber[128];
    char vbiosDateRaw[128];
} ResourceManagerAdlxGpuDeclaration;

__declspec(dllexport) uint32_t __cdecl ResourceManagerAdlxGetDeclarationAbiVersion(void) { return 1; }
__declspec(dllexport) int __cdecl ResourceManagerAdlxGetGpuDeclarationSize(void) { return sizeof(ResourceManagerAdlxGpuDeclaration); }

static int copy_declaration_text(char* target, size_t capacity, const char* source)
{
    if (!source) return 0;
    size_t length = 0;
    while (length < capacity && source[length]) length++;
    if (length == 0 || length >= capacity) return 0;
    memcpy(target, source, length + 1);
    return 1;
}

__declspec(dllexport) int __cdecl ResourceManagerAdlxReadGpuDeclarations(
    ResourceManagerAdlxGpuDeclaration* outputs, int capacity,
    const ResourceManagerAdlxGpuSelection* selections, int selectionCount,
    char* message, int messageCapacity)
{
    if (!outputs || !selections || capacity <= 0 || selectionCount <= 0 || selectionCount > capacity || selectionCount > 32) return 0;
    for (int index = 0; index < selectionCount; index++) {
        if (!selections[index].pnpString[0] || !memchr(selections[index].pnpString, 0, sizeof(selections[index].pnpString))
            || selections[index].requestedFlags <= 0 || (selections[index].requestedFlags & ~15)) return 0;
        for (int previous = 0; previous < index; previous++)
            if (selections[index].uniqueId == selections[previous].uniqueId) return 0;
    }
    AcquireSRWLockExclusive(&gLock);
    int init = load_adlx(message, messageCapacity);
    if (!gpu_system_loaded(init)) { ReleaseSRWLockExclusive(&gLock); return 0; }
    IADLXGPUList* list = NULL;
    if (!ADLX_SUCCEEDED(gSystem->pVtbl->GetGPUs(gSystem, &list)) || !list) {
        if (list) list->pVtbl->Release(list);
        ReleaseSRWLockExclusive(&gLock); return 0;
    }
    adlx_uint size = list->pVtbl->Size(list);
    if (size > 32) { list->pVtbl->Release(list); ReleaseSRWLockExclusive(&gLock); return 0; }
    int written = 0;
    for (adlx_uint index = 0; index < size && written < capacity; index++) {
        IADLXGPU* gpu = NULL;
        if (!ADLX_SUCCEEDED(list->pVtbl->At_GPUList(list, index, &gpu)) || !gpu) {
            if (gpu) gpu->pVtbl->Release(gpu);
            continue;
        }
        const char* pnp = NULL; int uid = 0;
        char currentPnp[512] = {0};
        int valid = ADLX_SUCCEEDED(gpu->pVtbl->PNPString(gpu, &pnp)) && copy_declaration_text(currentPnp, sizeof(currentPnp), pnp)
            && ADLX_SUCCEEDED(gpu->pVtbl->UniqueId(gpu, &uid));
        int flags = 0, matches = 0;
        if (valid) for (int selected = 0; selected < selectionCount; selected++)
            if (uid == selections[selected].uniqueId && strcmp(currentPnp, selections[selected].pnpString) == 0) {
                flags = selections[selected].requestedFlags; matches++;
            }
        if (matches != 1) { gpu->pVtbl->Release(gpu); continue; }
        ResourceManagerAdlxGpuDeclaration* output = &outputs[written++];
        memset(output, 0, sizeof(*output));
        output->structSize = sizeof(*output); output->abiVersion = 1; output->uniqueId = uid;
        memcpy(output->pnpString, currentPnp, sizeof(currentPnp));
        if ((flags & 13) && gpu->pVtbl->BIOSInfo) {
            const char* part = NULL; const char* version = NULL; const char* date = NULL;
            if (ADLX_SUCCEEDED(gpu->pVtbl->BIOSInfo(gpu, &part, &version, &date))) {
                if ((flags & 1) && copy_declaration_text(output->vbiosVersion, sizeof(output->vbiosVersion), version)) output->availableFlags |= 1;
                if ((flags & 4) && copy_declaration_text(output->vbiosPartNumber, sizeof(output->vbiosPartNumber), part)) output->availableFlags |= 4;
                if ((flags & 8) && copy_declaration_text(output->vbiosDateRaw, sizeof(output->vbiosDateRaw), date)) output->availableFlags |= 8;
            }
        }
        if ((flags & 2) && gpu->pVtbl->VRAMType) {
            const char* type = NULL;
            if (ADLX_SUCCEEDED(gpu->pVtbl->VRAMType(gpu, &type)) && copy_declaration_text(output->vramType, sizeof(output->vramType), type)) output->availableFlags |= 2;
        }
        gpu->pVtbl->Release(gpu);
    }
    list->pVtbl->Release(list); ReleaseSRWLockExclusive(&gLock);
    return written;
}
