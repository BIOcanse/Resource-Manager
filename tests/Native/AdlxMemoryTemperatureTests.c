/* Test the real bridge extension path with fake ADLX interfaces only. */
#define main unused_fan_fixture
#include "AdlxFanMetricsTests.c"
#undef main
#include <stddef.h>
#include <wchar.h>

static IADLXGPUMetricsSupport1 memory_support;
static IADLXGPUMetrics1 memory_metrics;
static int memory_supported = 1, extension_available = 1, memory_read_result;
static int memory_reads, extension_queries;
static double memory_temperature = 85;
static ADLX_RESULT __stdcall support_extension(IADLXGPUMetricsSupport* self, const wchar_t* iid, void** output) {
    (void)self; assert(wcscmp(iid, L"IADLXGPUMetricsSupport1") == 0); extension_queries++;
    *output = extension_available ? &memory_support : NULL; return extension_available ? 0 : 3;
}
static ADLX_RESULT __stdcall metrics_extension(IADLXGPUMetrics* self, const wchar_t* iid, void** output) {
    (void)self; assert(wcscmp(iid, L"IADLXGPUMetrics1") == 0); extension_queries++; *output = &memory_metrics; return 0;
}
static ADLX_RESULT __stdcall supports_memory(IADLXGPUMetricsSupport1* self, adlx_bool* value) {
    (void)self; *value = (adlx_bool)memory_supported; return 0;
}
static ADLX_RESULT __stdcall read_memory(IADLXGPUMetrics1* self, adlx_double* value) {
    (void)self; memory_reads++; *value = memory_temperature; return memory_read_result;
}
int main(void) {
    assert(offsetof(IADLXGPUMetricsSupport1Vtbl, IsSupportedGPUMemoryTemperature) == 25 * sizeof(void*));
    assert(offsetof(IADLXGPUMetrics1Vtbl, GPUMemoryTemperature) == 15 * sizeof(void*));
    IADLXGPUVtbl gpu_table = {.Release=release_gpu, .Name=text, .VendorId=text, .DeviceId=text, .PNPString=text, .Type=integer, .UniqueId=integer, .TotalVRAM=memory};
    IADLXGPUListVtbl list_table = {.Release=release_list, .Size=size, .At_GPUList=at};
    IADLXGPUMetricsSupportVtbl support_table = {.Release=release_support, .QueryInterface=support_extension};
    IADLXGPUMetricsVtbl metrics_table = {.Release=release_metrics, .QueryInterface=metrics_extension};
    IADLXGPUMetricsSupport1Vtbl memory_support_table = {.base={.Release=release_support}, .IsSupportedGPUMemoryTemperature=supports_memory};
    IADLXGPUMetrics1Vtbl memory_metrics_table = {.base={.Release=release_metrics}, .GPUMemoryTemperature=read_memory};
    IADLXSystemVtbl system_table = {.GetGPUs=get_gpus};
    IADLXPerformanceMonitoringServicesVtbl perf_table = {.GetSupportedGPUMetrics=get_support, .GetCurrentGPUMetrics=get_metrics};
    IADLXSystem system = {&system_table}; IADLXPerformanceMonitoringServices perf = {&perf_table};
    gpu.pVtbl=&gpu_table; list.pVtbl=&list_table; support.pVtbl=&support_table; metrics.pVtbl=&metrics_table;
    memory_support.pVtbl=&memory_support_table; memory_metrics.pVtbl=&memory_metrics_table;
    gSystem=&system; gPerfServices=&perf; gInitAttempted=1; gInitResult=0;
    ResourceManagerAdlxGpuMetrics value = sample(RM_ADLX_METRIC_MEMORY_TEMPERATURE);
    assert(value.memoryTemperatureCelsius == 85 && (value.availableFlags & RM_ADLX_METRIC_MEMORY_TEMPERATURE));
    assert(memory_reads == 1 && releases == 6);
    memory_read_result = 3;
    assert(!(sample(RM_ADLX_METRIC_MEMORY_TEMPERATURE).availableFlags & RM_ADLX_METRIC_MEMORY_TEMPERATURE));
    memory_supported = 0; int before = memory_reads;
    assert(!(sample(RM_ADLX_METRIC_MEMORY_TEMPERATURE).availableFlags & RM_ADLX_METRIC_MEMORY_TEMPERATURE));
    assert(memory_reads == before);
    extension_available = 0;
    assert(!(sample(RM_ADLX_METRIC_MEMORY_TEMPERATURE).availableFlags & RM_ADLX_METRIC_MEMORY_TEMPERATURE));
    int queries_before = extension_queries;
    assert(sample(0).availableFlags == 0 && extension_queries == queries_before);
    puts("ADLX memory temperature: official vtable offsets, requested/support/failed/absent extension and releases passed.");
    return 0;
}
