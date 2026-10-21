/* Real bridge fan sampling against fake interfaces; never loads a driver. */
#include "../../src/Core/Native/AdlxBridge/ResourceManagerAdlxBridge.c"
#include <assert.h>

static IADLXGPU gpu;
static IADLXGPUList list;
static IADLXGPUMetricsSupport support;
static IADLXGPUMetrics metrics;
static adlx_bool fan_supported = 1;
static int rpm = 2400, reads, releases, read_result;
static adlx_long __stdcall release_gpu(IADLXGPU* value) { (void)value; releases++; return 1; }
static adlx_long __stdcall release_list(IADLXGPUList* value) { (void)value; releases++; return 1; }
static adlx_long __stdcall release_support(IADLXGPUMetricsSupport* value) { (void)value; releases++; return 1; }
static adlx_long __stdcall release_metrics(IADLXGPUMetrics* value) { (void)value; releases++; return 1; }
static ADLX_RESULT __stdcall text(IADLXGPU* value, const char** output) { (void)value; *output = "fixture"; return 0; }
static ADLX_RESULT __stdcall integer(IADLXGPU* value, int* output) { (void)value; *output = 2; return 0; }
static ADLX_RESULT __stdcall memory(IADLXGPU* value, unsigned int* output) { (void)value; *output = 8192; return 0; }
static adlx_uint __stdcall size(IADLXGPUList* value) { (void)value; return 1; }
static ADLX_RESULT __stdcall at(IADLXGPUList* value, adlx_uint index, IADLXGPU** output) { (void)value; assert(index == 0); *output = &gpu; return 0; }
static ADLX_RESULT __stdcall get_gpus(IADLXSystem* value, IADLXGPUList** output) { (void)value; *output = &list; return 0; }
static ADLX_RESULT __stdcall get_support(IADLXPerformanceMonitoringServices* value, IADLXGPU* device, IADLXGPUMetricsSupport** output) { (void)value; assert(device == &gpu); *output = &support; return 0; }
static ADLX_RESULT __stdcall get_metrics(IADLXPerformanceMonitoringServices* value, IADLXGPU* device, IADLXGPUMetrics** output) { (void)value; assert(device == &gpu); *output = &metrics; return 0; }
static ADLX_RESULT __stdcall supported(IADLXGPUMetricsSupport* value, adlx_bool* output) { (void)value; *output = fan_supported; return 0; }
static ADLX_RESULT __stdcall speed(IADLXGPUMetrics* value, int* output) { (void)value; reads++; *output = rpm; return read_result; }
static ResourceManagerAdlxGpuMetrics sample(int flags)
{
    ResourceManagerAdlxGpuMetrics output;
    assert(ResourceManagerAdlxReadGpuMetrics(&output, 1, flags, NULL, 0) == 1);
    assert(output.abiVersion == RM_ADLX_BRIDGE_ABI_VERSION && output.structSize == sizeof(output));
    return output;
}
int main(void)
{
    IADLXGPUVtbl gpu_table = {.Release=release_gpu, .Name=text, .VendorId=text, .DeviceId=text, .PNPString=text, .Type=integer, .UniqueId=integer, .TotalVRAM=memory};
    IADLXGPUListVtbl list_table = {.Release=release_list, .Size=size, .At_GPUList=at};
    IADLXGPUMetricsSupportVtbl support_table = {.Release=release_support, .IsSupportedGPUFanSpeed=supported};
    IADLXGPUMetricsVtbl metrics_table = {.Release=release_metrics, .GPUFanSpeed=speed};
    IADLXSystemVtbl system_table = {.GetGPUs=get_gpus};
    IADLXPerformanceMonitoringServicesVtbl perf_table = {.GetSupportedGPUMetrics=get_support, .GetCurrentGPUMetrics=get_metrics};
    IADLXSystem system = {&system_table}; IADLXPerformanceMonitoringServices perf = {&perf_table};
    gpu.pVtbl=&gpu_table; list.pVtbl=&list_table; support.pVtbl=&support_table; metrics.pVtbl=&metrics_table;
    gSystem=&system; gPerfServices=&perf; gInitAttempted=1; gInitResult=0;
    int examples[] = {0, 40, 2400};
    for (unsigned int i=0; i<sizeof(examples)/sizeof(examples[0]); i++) {
        rpm=examples[i]; ResourceManagerAdlxGpuMetrics output=sample(RM_ADLX_METRIC_FAN);
        assert(output.fanSpeedRpm == rpm && (output.availableFlags & RM_ADLX_METRIC_FAN));
    }
    rpm=-1; assert(!(sample(RM_ADLX_METRIC_FAN).availableFlags & RM_ADLX_METRIC_FAN));
    read_result=-1; assert(!(sample(RM_ADLX_METRIC_FAN).availableFlags & RM_ADLX_METRIC_FAN));
    int before=reads; fan_supported=0;
    assert(!(sample(RM_ADLX_METRIC_FAN).availableFlags & RM_ADLX_METRIC_FAN)); assert(reads == before);
    assert(sample(0).availableFlags == 0); assert(reads == before); assert(releases == 26);
    puts("ADLX fan sampling: RPM, zero, invalid/failed/unsupported/unrequested values and interface releases passed.");
    return 0;
}
