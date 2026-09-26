#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define RM_ADLX_OK 0
#define RM_ADLX_ERROR_NOT_LOADED -1
#define RM_ADLX_ERROR_INIT_FAILED -2
#define RM_ADLX_ERROR_NO_SYSTEM -3
#define RM_ADLX_ERROR_NO_PERF -4
#define RM_ADLX_BRIDGE_ABI_VERSION 2

#define ADLX_OK 0
#define ADLX_ALREADY_ENABLED 1
#define ADLX_ALREADY_INITIALIZED 2
#define ADLX_BAD_VER 5
#define ADLX_SUCCEEDED(x) ((x) == ADLX_OK || (x) == ADLX_ALREADY_ENABLED || (x) == ADLX_ALREADY_INITIALIZED)

#define ADLX_SDK_FULL_VERSION ((((uint64_t)1) << 48) | (((uint64_t)5) << 32) | (((uint64_t)0) << 16) | ((uint64_t)124))

#define RM_ADLX_METRIC_USAGE 1
#define RM_ADLX_METRIC_CLOCK 2
#define RM_ADLX_METRIC_VRAM_CLOCK 4
#define RM_ADLX_METRIC_TEMPERATURE 8
#define RM_ADLX_METRIC_POWER 16
#define RM_ADLX_METRIC_VOLTAGE 32
#define RM_ADLX_METRIC_VRAM 64
#define RM_ADLX_METRIC_HOTSPOT_TEMPERATURE 128
#define RM_ADLX_METRIC_FAN 256
#define RM_ADLX_METRIC_BOARD_POWER 512
#define RM_ADLX_METRIC_INTAKE_TEMPERATURE 1024

typedef int32_t ADLX_RESULT;
typedef int32_t adlx_int;
typedef uint32_t adlx_uint;
typedef uint8_t adlx_bool;
typedef double adlx_double;
typedef long adlx_long;

typedef struct IADLXInterface IADLXInterface;
typedef struct IADLXSystem IADLXSystem;
typedef struct IADLXGPU IADLXGPU;
typedef struct IADLXGPUList IADLXGPUList;
typedef struct IADLXPerformanceMonitoringServices IADLXPerformanceMonitoringServices;
typedef struct IADLXGPUMetricsSupport IADLXGPUMetricsSupport;
typedef struct IADLXGPUMetrics IADLXGPUMetrics;

typedef ADLX_RESULT (__cdecl *ADLXQueryFullVersionFn)(uint64_t* fullVersion);
typedef ADLX_RESULT (__cdecl *ADLXInitializeFn)(uint64_t version, IADLXSystem** system);
typedef ADLX_RESULT (__cdecl *ADLXInitialize2Fn)(uint64_t version, IADLXSystem** system, void** adlMapping);

typedef struct IADLXGPUVtbl
{
    adlx_long (__stdcall *Acquire)(IADLXGPU* self);
    adlx_long (__stdcall *Release)(IADLXGPU* self);
    ADLX_RESULT (__stdcall *QueryInterface)(IADLXGPU* self, const wchar_t* interfaceId, void** outInterface);
    ADLX_RESULT (__stdcall *VendorId)(IADLXGPU* self, const char** vendorId);
    ADLX_RESULT (__stdcall *ASICFamilyType)(IADLXGPU* self, int* asicFamilyType);
    ADLX_RESULT (__stdcall *Type)(IADLXGPU* self, int* gpuType);
    ADLX_RESULT (__stdcall *IsExternal)(IADLXGPU* self, adlx_bool* isExternal);
    ADLX_RESULT (__stdcall *Name)(IADLXGPU* self, const char** gpuName);
    ADLX_RESULT (__stdcall *DriverPath)(IADLXGPU* self, const char** driverPath);
    ADLX_RESULT (__stdcall *PNPString)(IADLXGPU* self, const char** pnpString);
    ADLX_RESULT (__stdcall *HasDesktops)(IADLXGPU* self, adlx_bool* hasDesktops);
    ADLX_RESULT (__stdcall *TotalVRAM)(IADLXGPU* self, adlx_uint* vramMb);
    ADLX_RESULT (__stdcall *VRAMType)(IADLXGPU* self, const char** type);
    ADLX_RESULT (__stdcall *BIOSInfo)(IADLXGPU* self, const char** partNumber, const char** version, const char** date);
    ADLX_RESULT (__stdcall *DeviceId)(IADLXGPU* self, const char** deviceId);
    ADLX_RESULT (__stdcall *RevisionId)(IADLXGPU* self, const char** revisionId);
    ADLX_RESULT (__stdcall *SubSystemId)(IADLXGPU* self, const char** subSystemId);
    ADLX_RESULT (__stdcall *SubSystemVendorId)(IADLXGPU* self, const char** subSystemVendorId);
    ADLX_RESULT (__stdcall *UniqueId)(IADLXGPU* self, adlx_int* uniqueId);
} IADLXGPUVtbl;

struct IADLXGPU { const IADLXGPUVtbl* pVtbl; };

typedef struct IADLXGPUListVtbl
{
    adlx_long (__stdcall *Acquire)(IADLXGPUList* self);
    adlx_long (__stdcall *Release)(IADLXGPUList* self);
    ADLX_RESULT (__stdcall *QueryInterface)(IADLXGPUList* self, const wchar_t* interfaceId, void** outInterface);
    adlx_uint (__stdcall *Size)(IADLXGPUList* self);
    adlx_bool (__stdcall *Empty)(IADLXGPUList* self);
    adlx_uint (__stdcall *Begin)(IADLXGPUList* self);
    adlx_uint (__stdcall *End)(IADLXGPUList* self);
    ADLX_RESULT (__stdcall *At)(IADLXGPUList* self, const adlx_uint location, IADLXInterface** item);
    ADLX_RESULT (__stdcall *Clear)(IADLXGPUList* self);
    ADLX_RESULT (__stdcall *Remove_Back)(IADLXGPUList* self);
    ADLX_RESULT (__stdcall *Add_Back)(IADLXGPUList* self, IADLXInterface* item);
    ADLX_RESULT (__stdcall *At_GPUList)(IADLXGPUList* self, const adlx_uint location, IADLXGPU** item);
    ADLX_RESULT (__stdcall *Add_Back_GPUList)(IADLXGPUList* self, IADLXGPU* item);
} IADLXGPUListVtbl;

struct IADLXGPUList { const IADLXGPUListVtbl* pVtbl; };

typedef struct IADLXSystemVtbl
{
    ADLX_RESULT (__stdcall *GetHybridGraphicsType)(IADLXSystem* self, int* hgType);
    ADLX_RESULT (__stdcall *GetGPUs)(IADLXSystem* self, IADLXGPUList** gpus);
    ADLX_RESULT (__stdcall *QueryInterface)(IADLXSystem* self, const wchar_t* interfaceId, void** outInterface);
    void* GetDisplaysServices;
    void* GetDesktopsServices;
    void* GetGPUsChangedHandling;
    void* EnableLog;
    void* Get3DSettingsServices;
    void* GetGPUTuningServices;
    ADLX_RESULT (__stdcall *GetPerformanceMonitoringServices)(IADLXSystem* self, IADLXPerformanceMonitoringServices** perfServices);
    void* TotalSystemRAM;
    void* GetI2C;
} IADLXSystemVtbl;

struct IADLXSystem { const IADLXSystemVtbl* pVtbl; };

typedef struct IADLXPerformanceMonitoringServicesVtbl
{
    adlx_long (__stdcall *Acquire)(IADLXPerformanceMonitoringServices* self);
    adlx_long (__stdcall *Release)(IADLXPerformanceMonitoringServices* self);
    ADLX_RESULT (__stdcall *QueryInterface)(IADLXPerformanceMonitoringServices* self, const wchar_t* interfaceId, void** outInterface);
    void* GetSamplingIntervalRange;
    void* SetSamplingInterval;
    void* GetSamplingInterval;
    void* GetMaxPerformanceMetricsHistorySizeRange;
    void* SetMaxPerformanceMetricsHistorySize;
    void* GetMaxPerformanceMetricsHistorySize;
    void* ClearPerformanceMetricsHistory;
    void* GetCurrentPerformanceMetricsHistorySize;
    void* StartPerformanceMetricsTracking;
    void* StopPerformanceMetricsTracking;
    void* GetAllMetricsHistory;
    void* GetGPUMetricsHistory;
    void* GetSystemMetricsHistory;
    void* GetFPSHistory;
    void* GetCurrentAllMetrics;
    ADLX_RESULT (__stdcall *GetCurrentGPUMetrics)(IADLXPerformanceMonitoringServices* self, IADLXGPU* gpu, IADLXGPUMetrics** metrics);
    void* GetCurrentSystemMetrics;
    void* GetCurrentFPS;
    ADLX_RESULT (__stdcall *GetSupportedGPUMetrics)(IADLXPerformanceMonitoringServices* self, IADLXGPU* gpu, IADLXGPUMetricsSupport** supported);
    void* GetSupportedSystemMetrics;
} IADLXPerformanceMonitoringServicesVtbl;

struct IADLXPerformanceMonitoringServices { const IADLXPerformanceMonitoringServicesVtbl* pVtbl; };

typedef struct IADLXGPUMetricsSupportVtbl
{
    adlx_long (__stdcall *Acquire)(IADLXGPUMetricsSupport* self);
    adlx_long (__stdcall *Release)(IADLXGPUMetricsSupport* self);
    ADLX_RESULT (__stdcall *QueryInterface)(IADLXGPUMetricsSupport* self, const wchar_t* interfaceId, void** outInterface);
    ADLX_RESULT (__stdcall *IsSupportedGPUUsage)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUClockSpeed)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUVRAMClockSpeed)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUTemperature)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUHotspotTemperature)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUPower)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUTotalBoardPower)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUFanSpeed)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUVRAM)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *IsSupportedGPUVoltage)(IADLXGPUMetricsSupport* self, adlx_bool* supported);
    ADLX_RESULT (__stdcall *GetGPUUsageRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUClockSpeedRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUVRAMClockSpeedRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUTemperatureRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUHotspotTemperatureRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUPowerRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUFanSpeedRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUVRAMRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUVoltageRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
    ADLX_RESULT (__stdcall *GetGPUTotalBoardPowerRange)(IADLXGPUMetricsSupport* self, adlx_int* minValue, adlx_int* maxValue);
} IADLXGPUMetricsSupportVtbl;

struct IADLXGPUMetricsSupport { const IADLXGPUMetricsSupportVtbl* pVtbl; };

typedef struct IADLXGPUMetricsVtbl
{
    adlx_long (__stdcall *Acquire)(IADLXGPUMetrics* self);
    adlx_long (__stdcall *Release)(IADLXGPUMetrics* self);
    ADLX_RESULT (__stdcall *QueryInterface)(IADLXGPUMetrics* self, const wchar_t* interfaceId, void** outInterface);
    ADLX_RESULT (__stdcall *TimeStamp)(IADLXGPUMetrics* self, int64_t* ms);
    ADLX_RESULT (__stdcall *GPUUsage)(IADLXGPUMetrics* self, adlx_double* data);
    ADLX_RESULT (__stdcall *GPUClockSpeed)(IADLXGPUMetrics* self, adlx_int* data);
    ADLX_RESULT (__stdcall *GPUVRAMClockSpeed)(IADLXGPUMetrics* self, adlx_int* data);
    ADLX_RESULT (__stdcall *GPUTemperature)(IADLXGPUMetrics* self, adlx_double* data);
    ADLX_RESULT (__stdcall *GPUHotspotTemperature)(IADLXGPUMetrics* self, adlx_double* data);
    ADLX_RESULT (__stdcall *GPUPower)(IADLXGPUMetrics* self, adlx_double* data);
    ADLX_RESULT (__stdcall *GPUTotalBoardPower)(IADLXGPUMetrics* self, adlx_double* data);
    ADLX_RESULT (__stdcall *GPUFanSpeed)(IADLXGPUMetrics* self, adlx_int* data);
    ADLX_RESULT (__stdcall *GPUVRAM)(IADLXGPUMetrics* self, adlx_int* data);
    ADLX_RESULT (__stdcall *GPUVoltage)(IADLXGPUMetrics* self, adlx_int* data);
    ADLX_RESULT (__stdcall *GPUIntakeTemperature)(IADLXGPUMetrics* self, adlx_double* data);
} IADLXGPUMetricsVtbl;

struct IADLXGPUMetrics { const IADLXGPUMetricsVtbl* pVtbl; };

typedef struct ResourceManagerAdlxGpuMetrics
{
    int32_t structSize;
    int32_t abiVersion;
    int32_t adlxIndex;
    int32_t gpuType;
    int32_t uniqueId;
    uint32_t totalVramMb;
    char name[128];
    char vendorId[32];
    char deviceId[32];
    char pnpString[512];
    int32_t supportedFlags;
    int32_t availableFlags;
    double usagePercent;
    int32_t graphicsClockMhz;
    int32_t maxGraphicsClockMhz;
    int32_t memoryClockMhz;
    int32_t maxMemoryClockMhz;
    double temperatureCelsius;
    double powerWatts;
    int32_t voltageMillivolts;
    int32_t vramUsedMb;
    double hotspotTemperatureCelsius;
    double boardPowerWatts;
    int32_t fanSpeedPercent;
    double intakeTemperatureCelsius;
} ResourceManagerAdlxGpuMetrics;

__declspec(dllexport) uint32_t ResourceManagerAdlxGetAbiVersion(void)
{
    return RM_ADLX_BRIDGE_ABI_VERSION;
}

__declspec(dllexport) uint32_t ResourceManagerAdlxGetGpuMetricsSize(void)
{
    return (uint32_t)sizeof(ResourceManagerAdlxGpuMetrics);
}

static HMODULE gAdlxModule;
static IADLXSystem* gSystem;
static IADLXPerformanceMonitoringServices* gPerfServices;
static int gInitAttempted;
static int gInitResult;
static SRWLOCK gLock = SRWLOCK_INIT;

static void set_message(char* message, int capacity, const char* value)
{
    if (!message || capacity <= 0)
        return;
    if (!value)
        value = "";
    snprintf(message, (size_t)capacity, "%s", value);
}

static void copy_text(char* destination, size_t capacity, const char* source)
{
    if (!destination || capacity == 0)
        return;
    if (!source)
        source = "";
    snprintf(destination, capacity, "%s", source);
}

static void debug_trace(const char* value)
{
#ifdef RM_ADLX_DEBUG
    fprintf(stderr, "[adlx-bridge] %s\n", value ? value : "");
    fflush(stderr);
#else
    (void)value;
#endif
}

static int load_adlx(char* message, int messageCapacity)
{
    if (gInitAttempted)
        return gInitResult;

    gInitAttempted = 1;
    gAdlxModule = LoadLibraryA("amdadlx64.dll");
    if (!gAdlxModule)
    {
        set_message(message, messageCapacity, "amdadlx64.dll could not be loaded.");
        gInitResult = RM_ADLX_ERROR_NOT_LOADED;
        return gInitResult;
    }
    debug_trace("loaded amdadlx64.dll");

    ADLXQueryFullVersionFn queryFullVersion = (ADLXQueryFullVersionFn)GetProcAddress(gAdlxModule, "ADLXQueryFullVersion");
    ADLXInitialize2Fn initialize2 = (ADLXInitialize2Fn)GetProcAddress(gAdlxModule, "ADLXInitialize2");
    ADLXInitialize2Fn initialize2Incompatible = (ADLXInitialize2Fn)GetProcAddress(gAdlxModule, "ADLXInitializeWithIncompatibleDriver2");
    ADLXInitializeFn initialize = (ADLXInitializeFn)GetProcAddress(gAdlxModule, "ADLXInitialize");
    ADLXInitializeFn initializeIncompatible = (ADLXInitializeFn)GetProcAddress(gAdlxModule, "ADLXInitializeWithIncompatibleDriver");
    if (!initialize2 && !initialize)
    {
        set_message(message, messageCapacity, "ADLX initialization export was not found.");
        gInitResult = RM_ADLX_ERROR_INIT_FAILED;
        return gInitResult;
    }

    uint64_t version = ADLX_SDK_FULL_VERSION;
    if (queryFullVersion)
    {
        uint64_t runtimeVersion = 0;
        debug_trace("calling ADLXQueryFullVersion");
        if (ADLX_SUCCEEDED(queryFullVersion(&runtimeVersion)) && runtimeVersion != 0)
            version = runtimeVersion;
        debug_trace("returned ADLXQueryFullVersion");
    }

    void* adlMapping = NULL;
    debug_trace("calling ADLXInitialize");
    ADLX_RESULT result = initialize2
        ? initialize2(version, &gSystem, &adlMapping)
        : initialize(version, &gSystem);
    debug_trace("returned ADLXInitialize");

    if (result == ADLX_BAD_VER && version != ADLX_SDK_FULL_VERSION)
    {
        gSystem = NULL;
        adlMapping = NULL;
        result = initialize2
            ? initialize2(ADLX_SDK_FULL_VERSION, &gSystem, &adlMapping)
            : initialize(ADLX_SDK_FULL_VERSION, &gSystem);
    }

    if (!ADLX_SUCCEEDED(result) && (initialize2Incompatible || initializeIncompatible))
    {
        gSystem = NULL;
        adlMapping = NULL;
        result = initialize2Incompatible
            ? initialize2Incompatible(version, &gSystem, &adlMapping)
            : initializeIncompatible(version, &gSystem);
    }

    if (!ADLX_SUCCEEDED(result) || !gSystem)
    {
        char buffer[128];
        snprintf(buffer, sizeof(buffer), "ADLXInitialize failed with result %d.", result);
        set_message(message, messageCapacity, buffer);
        gInitResult = RM_ADLX_ERROR_INIT_FAILED;
        return gInitResult;
    }

    if (!gSystem->pVtbl || !gSystem->pVtbl->GetPerformanceMonitoringServices)
    {
        set_message(message, messageCapacity, "ADLX system interface is missing performance services.");
        gInitResult = RM_ADLX_ERROR_NO_SYSTEM;
        return gInitResult;
    }

    result = gSystem->pVtbl->GetPerformanceMonitoringServices(gSystem, &gPerfServices);
    debug_trace("returned GetPerformanceMonitoringServices");
    if (!ADLX_SUCCEEDED(result) || !gPerfServices)
    {
        char buffer[128];
        snprintf(buffer, sizeof(buffer), "GetPerformanceMonitoringServices failed with result %d.", result);
        set_message(message, messageCapacity, buffer);
        gInitResult = RM_ADLX_ERROR_NO_PERF;
        return gInitResult;
    }

    gInitResult = RM_ADLX_OK;
    return RM_ADLX_OK;
}

static int get_range_max(ADLX_RESULT (__stdcall *rangeFn)(IADLXGPUMetricsSupport*, adlx_int*, adlx_int*), IADLXGPUMetricsSupport* support)
{
    if (!rangeFn || !support)
        return 0;

    adlx_int minValue = 0;
    adlx_int maxValue = 0;
    return ADLX_SUCCEEDED(rangeFn(support, &minValue, &maxValue)) ? maxValue : 0;
}

static void set_supported(ResourceManagerAdlxGpuMetrics* output, int flag, adlx_bool supported)
{
    if (supported)
        output->supportedFlags |= flag;
}

static void set_available(ResourceManagerAdlxGpuMetrics* output, int flag)
{
    output->availableFlags |= flag;
}

__declspec(dllexport) int ResourceManagerAdlxReadGpuMetrics(
    ResourceManagerAdlxGpuMetrics* outputs,
    int capacity,
    int requestedFlags,
    char* message,
    int messageCapacity)
{
    if (!outputs || capacity <= 0)
    {
        set_message(message, messageCapacity, "Invalid output buffer.");
        return 0;
    }

    AcquireSRWLockExclusive(&gLock);
    int init = load_adlx(message, messageCapacity);
    if (init != RM_ADLX_OK)
    {
        ReleaseSRWLockExclusive(&gLock);
        return init;
    }

    IADLXGPUList* gpuList = NULL;
    debug_trace("calling GetGPUs");
    ADLX_RESULT result = gSystem->pVtbl->GetGPUs(gSystem, &gpuList);
    debug_trace("returned GetGPUs");
    if (!ADLX_SUCCEEDED(result) || !gpuList)
    {
        set_message(message, messageCapacity, "GetGPUs failed.");
        ReleaseSRWLockExclusive(&gLock);
        return 0;
    }

    adlx_uint size = gpuList->pVtbl->Size(gpuList);
    debug_trace("returned GPUList Size");
    int written = 0;
    for (adlx_uint index = 0; index < size && written < capacity; index++)
    {
        IADLXGPU* gpu = NULL;
        debug_trace("calling At_GPUList");
        result = gpuList->pVtbl->At_GPUList(gpuList, index, &gpu);
        debug_trace("returned At_GPUList");
        if (!ADLX_SUCCEEDED(result) || !gpu)
            continue;

        ResourceManagerAdlxGpuMetrics* output = &outputs[written];
        memset(output, 0, sizeof(ResourceManagerAdlxGpuMetrics));
        output->structSize = (int32_t)sizeof(ResourceManagerAdlxGpuMetrics);
        output->abiVersion = RM_ADLX_BRIDGE_ABI_VERSION;
        output->adlxIndex = (int32_t)index;

        const char* text = NULL;
        debug_trace("reading GPU identity");
        if (ADLX_SUCCEEDED(gpu->pVtbl->Name(gpu, &text)))
            copy_text(output->name, sizeof(output->name), text);
        if (ADLX_SUCCEEDED(gpu->pVtbl->VendorId(gpu, &text)))
            copy_text(output->vendorId, sizeof(output->vendorId), text);
        if (ADLX_SUCCEEDED(gpu->pVtbl->DeviceId(gpu, &text)))
            copy_text(output->deviceId, sizeof(output->deviceId), text);
        if (ADLX_SUCCEEDED(gpu->pVtbl->PNPString(gpu, &text)))
            copy_text(output->pnpString, sizeof(output->pnpString), text);
        gpu->pVtbl->Type(gpu, &output->gpuType);
        gpu->pVtbl->UniqueId(gpu, &output->uniqueId);
        gpu->pVtbl->TotalVRAM(gpu, &output->totalVramMb);

        IADLXGPUMetricsSupport* support = NULL;
        debug_trace("calling GetSupportedGPUMetrics");
        result = gPerfServices->pVtbl->GetSupportedGPUMetrics(gPerfServices, gpu, &support);
        debug_trace("returned GetSupportedGPUMetrics");
        if (!ADLX_SUCCEEDED(result) || !support)
        {
            gpu->pVtbl->Release(gpu);
            continue;
        }

        IADLXGPUMetrics* metrics = NULL;
        debug_trace("calling GetCurrentGPUMetrics");
        if (requestedFlags != 0)
            gPerfServices->pVtbl->GetCurrentGPUMetrics(gPerfServices, gpu, &metrics);
        debug_trace("returned GetCurrentGPUMetrics");

        adlx_bool supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_USAGE) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUUsage(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_USAGE, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUUsage(metrics, &output->usagePercent)))
                set_available(output, RM_ADLX_METRIC_USAGE);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_CLOCK) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUClockSpeed(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_CLOCK, supported);
            output->maxGraphicsClockMhz = get_range_max(support->pVtbl->GetGPUClockSpeedRange, support);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUClockSpeed(metrics, &output->graphicsClockMhz)))
                set_available(output, RM_ADLX_METRIC_CLOCK);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_VRAM_CLOCK) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUVRAMClockSpeed(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_VRAM_CLOCK, supported);
            output->maxMemoryClockMhz = get_range_max(support->pVtbl->GetGPUVRAMClockSpeedRange, support);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUVRAMClockSpeed(metrics, &output->memoryClockMhz)))
                set_available(output, RM_ADLX_METRIC_VRAM_CLOCK);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_TEMPERATURE) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUTemperature(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_TEMPERATURE, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUTemperature(metrics, &output->temperatureCelsius)))
                set_available(output, RM_ADLX_METRIC_TEMPERATURE);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_HOTSPOT_TEMPERATURE) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUHotspotTemperature(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_HOTSPOT_TEMPERATURE, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUHotspotTemperature(metrics, &output->hotspotTemperatureCelsius)))
                set_available(output, RM_ADLX_METRIC_HOTSPOT_TEMPERATURE);
        }

        if ((requestedFlags & RM_ADLX_METRIC_INTAKE_TEMPERATURE) && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUIntakeTemperature(metrics, &output->intakeTemperatureCelsius)))
            set_available(output, RM_ADLX_METRIC_INTAKE_TEMPERATURE);

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_POWER) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUPower(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_POWER, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUPower(metrics, &output->powerWatts)))
                set_available(output, RM_ADLX_METRIC_POWER);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_BOARD_POWER) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUTotalBoardPower(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_BOARD_POWER, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUTotalBoardPower(metrics, &output->boardPowerWatts)))
                set_available(output, RM_ADLX_METRIC_BOARD_POWER);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_FAN) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUFanSpeed(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_FAN, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUFanSpeed(metrics, &output->fanSpeedPercent)))
                set_available(output, RM_ADLX_METRIC_FAN);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_VOLTAGE) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUVoltage(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_VOLTAGE, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUVoltage(metrics, &output->voltageMillivolts)))
                set_available(output, RM_ADLX_METRIC_VOLTAGE);
        }

        supported = 0;
        if ((requestedFlags & RM_ADLX_METRIC_VRAM) && ADLX_SUCCEEDED(support->pVtbl->IsSupportedGPUVRAM(support, &supported)))
        {
            set_supported(output, RM_ADLX_METRIC_VRAM, supported);
            if (supported && metrics && ADLX_SUCCEEDED(metrics->pVtbl->GPUVRAM(metrics, &output->vramUsedMb)))
                set_available(output, RM_ADLX_METRIC_VRAM);
        }

        if (metrics)
            metrics->pVtbl->Release(metrics);
        support->pVtbl->Release(support);
        gpu->pVtbl->Release(gpu);
        written++;
    }

    gpuList->pVtbl->Release(gpuList);
    set_message(message, messageCapacity, "ADLX read completed.");
    ReleaseSRWLockExclusive(&gLock);
    return written;
}
