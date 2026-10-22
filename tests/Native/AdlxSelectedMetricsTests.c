/* Exercise the production bridge against fake SDK interfaces, never hardware. */
#include "../../src/Core/Native/AdlxBridge/ResourceManagerAdlxBridge.c"
#include <assert.h>
#include <math.h>
#include <stddef.h>

static IADLXGPU devices[3];
static IADLXGPUList list;
static IADLXGPUMetricsSupport support;
static IADLXGPUMetrics metrics;
static int metadata_count=3, releases, support_reads[3], metric_reads[3];
static int usage_reads, voltage_reads, timestamp_reads, support_result, metrics_result, identity_result;
static int usage_result, voltage_result, timestamp_result;
static int total_result, total_reads[3], clock_result, memory_clock_result, used_result;
static int clock_value, memory_clock_value, used_value;
static adlx_bool memory_supported=1;
static IADLXGPUMetricsSupport1 support_extension;
static IADLXGPUMetrics1 metrics_extension;
static int support_extension_queries, metrics_extension_queries;
static int activity_support_reads, frequency_support_reads, activity_metric_reads, frequency_metric_reads;
static int npu_call_order[8], npu_call_order_count;
static int activity_support_result, frequency_support_result, activity_metric_result, frequency_metric_result;
static adlx_bool activity_supported=1, frequency_supported=1;
static int activity_level=0, npu_frequency=0;
static adlx_bool usage_supported=1, voltage_supported=1;
static double usage=0;
static int voltage=0;
static int64_t timestamp=0;
static const char* pnp[3]={"PCI\\VEN_1002&DEV_744C\\A","PCI\\VEN_1002&DEV_744C\\B","PCI\\VEN_1002&DEV_744C\\C"};
static adlx_long __stdcall drop_gpu(IADLXGPU* self) { (void)self; releases++; return 1; }
static adlx_long __stdcall drop_list(IADLXGPUList* self) { (void)self; releases++; return 1; }
static adlx_long __stdcall drop_support(IADLXGPUMetricsSupport* self) { (void)self; releases++; return 1; }
static adlx_long __stdcall drop_metrics(IADLXGPUMetrics* self) { (void)self; releases++; return 1; }
static ADLX_RESULT __stdcall text(IADLXGPU* self,const char** out) { (void)self; *out="AMD fixture"; return 0; }
static ADLX_RESULT __stdcall instance(IADLXGPU* self,const char** out) { *out=pnp[self-devices]; return identity_result; }
static ADLX_RESULT __stdcall uid(IADLXGPU* self,int* out) { *out=10+(int)(self-devices); return identity_result; }
static ADLX_RESULT __stdcall type(IADLXGPU* self,int* out) { (void)self; *out=2; return 0; }
static ADLX_RESULT __stdcall vram(IADLXGPU* self,unsigned int* out) { total_reads[self-devices]++; *out=8192; return total_result; }
static adlx_uint __stdcall size(IADLXGPUList* self) { (void)self; return (adlx_uint)metadata_count; }
static ADLX_RESULT __stdcall at(IADLXGPUList* self,adlx_uint index,IADLXGPU** out) { (void)self; assert(index<3); *out=&devices[index]; return 0; }
static ADLX_RESULT __stdcall get_list(IADLXSystem* self,IADLXGPUList** out) { (void)self; *out=&list; return 0; }
static ADLX_RESULT __stdcall get_support(IADLXPerformanceMonitoringServices* self,IADLXGPU* gpu,IADLXGPUMetricsSupport** out)
{ (void)self; support_reads[gpu-devices]++; *out=&support; return support_result; }
static ADLX_RESULT __stdcall get_metrics(IADLXPerformanceMonitoringServices* self,IADLXGPU* gpu,IADLXGPUMetrics** out)
{ (void)self; metric_reads[gpu-devices]++; *out=&metrics; return metrics_result; }
static ADLX_RESULT __stdcall supports_usage(IADLXGPUMetricsSupport* self,adlx_bool* out) { (void)self; *out=usage_supported; return 0; }
static ADLX_RESULT __stdcall supports_voltage(IADLXGPUMetricsSupport* self,adlx_bool* out) { (void)self; *out=voltage_supported; return 0; }
static ADLX_RESULT __stdcall read_usage(IADLXGPUMetrics* self,double* out) { (void)self; usage_reads++; *out=usage; return usage_result; }
static ADLX_RESULT __stdcall read_voltage(IADLXGPUMetrics* self,int* out) { (void)self; voltage_reads++; *out=voltage; return voltage_result; }
static ADLX_RESULT __stdcall read_timestamp(IADLXGPUMetrics* self,int64_t* out) { (void)self; timestamp_reads++; *out=timestamp; return timestamp_result; }
static ADLX_RESULT __stdcall supports_memory(IADLXGPUMetricsSupport* self,adlx_bool* out) { (void)self; *out=memory_supported; return 0; }
static ADLX_RESULT __stdcall query_support_extension(IADLXGPUMetricsSupport* self,const wchar_t* iid,void** out)
{ (void)self; (void)iid; support_extension_queries++; *out=&support_extension; return 0; }
static ADLX_RESULT __stdcall query_metrics_extension(IADLXGPUMetrics* self,const wchar_t* iid,void** out)
{ (void)self; (void)iid; metrics_extension_queries++; *out=&metrics_extension; return 0; }
static ADLX_RESULT __stdcall supports_npu_activity(IADLXGPUMetricsSupport1* self,adlx_bool* out)
{ (void)self; if(npu_call_order_count<8) npu_call_order[npu_call_order_count]=1; npu_call_order_count++; activity_support_reads++; *out=activity_supported; return activity_support_result; }
static ADLX_RESULT __stdcall supports_npu_frequency(IADLXGPUMetricsSupport1* self,adlx_bool* out)
{ (void)self; if(npu_call_order_count<8) npu_call_order[npu_call_order_count]=3; npu_call_order_count++; frequency_support_reads++; *out=frequency_supported; return frequency_support_result; }
static ADLX_RESULT __stdcall read_npu_activity(IADLXGPUMetrics1* self,adlx_int* out)
{ (void)self; if(npu_call_order_count<8) npu_call_order[npu_call_order_count]=2; npu_call_order_count++; activity_metric_reads++; *out=activity_level; return activity_metric_result; }
static ADLX_RESULT __stdcall read_npu_frequency(IADLXGPUMetrics1* self,adlx_int* out)
{ (void)self; if(npu_call_order_count<8) npu_call_order[npu_call_order_count]=4; npu_call_order_count++; frequency_metric_reads++; *out=npu_frequency; return frequency_metric_result; }
static ADLX_RESULT __stdcall npu_range(IADLXGPUMetricsSupport1* self,adlx_int* min,adlx_int* max)
{ (void)self; *min=0; *max=100; return 0; }
static ADLX_RESULT __stdcall clock_range(IADLXGPUMetricsSupport* self,int* min,int* max) { (void)self; *min=0; *max=2000; return 0; }
static ADLX_RESULT __stdcall read_clock(IADLXGPUMetrics* self,int* out) { (void)self; *out=clock_value; return clock_result; }
static ADLX_RESULT __stdcall read_memory_clock(IADLXGPUMetrics* self,int* out) { (void)self; *out=memory_clock_value; return memory_clock_result; }
static ADLX_RESULT __stdcall read_used(IADLXGPUMetrics* self,int* out) { (void)self; *out=used_value; return used_result; }
static ResourceManagerAdlxGpuMetrics sample(int flags)
{
    ResourceManagerAdlxGpuSelection selected={.uniqueId=11,.requestedFlags=flags};
    strcpy(selected.pnpString,pnp[1]); ResourceManagerAdlxGpuMetrics output;
    assert(ResourceManagerAdlxReadSelectedGpuMetrics(&output,1,&selected,1,NULL,0)==1);
    assert(output.uniqueId==11 && output.identityValid==1);
    return output;
}
int main(void)
{
    IADLXGPUVtbl gpu_table={.Release=drop_gpu,.Name=text,.VendorId=text,.DeviceId=text,.PNPString=instance,.Type=type,.UniqueId=uid,.TotalVRAM=vram};
    IADLXGPUListVtbl list_table={.Release=drop_list,.Size=size,.At_GPUList=at};
    IADLXGPUMetricsSupportVtbl support_table={.Release=drop_support,.QueryInterface=query_support_extension,.IsSupportedGPUUsage=supports_usage,.IsSupportedGPUVoltage=supports_voltage,
        .IsSupportedGPUClockSpeed=supports_memory,.GetGPUClockSpeedRange=clock_range,
        .IsSupportedGPUVRAMClockSpeed=supports_memory,.GetGPUVRAMClockSpeedRange=clock_range,.IsSupportedGPUVRAM=supports_memory};
    IADLXGPUMetricsVtbl metrics_table={.Release=drop_metrics,.QueryInterface=query_metrics_extension,.TimeStamp=read_timestamp,.GPUUsage=read_usage,.GPUVoltage=read_voltage,
        .GPUClockSpeed=read_clock,.GPUVRAMClockSpeed=read_memory_clock,.GPUVRAM=read_used};
    IADLXGPUMetricsSupport1Vtbl support1_table={0};
    support1_table.base=support_table; support1_table.GetGPUMemoryTemperatureRange=npu_range;
    support1_table.IsSupportedNPUFrequency=supports_npu_frequency; support1_table.GetNPUFrequencyRange=npu_range;
    support1_table.IsSupportedNPUActivityLevel=supports_npu_activity; support1_table.GetNPUActivityLevelRange=npu_range;
    IADLXGPUMetrics1Vtbl metrics1_table={0};
    metrics1_table.base=metrics_table; metrics1_table.NPUFrequency=read_npu_frequency; metrics1_table.NPUActivityLevel=read_npu_activity;
    IADLXSystemVtbl system_table={.GetGPUs=get_list};
    IADLXPerformanceMonitoringServicesVtbl perf_table={.GetSupportedGPUMetrics=get_support,.GetCurrentGPUMetrics=get_metrics};
    IADLXSystem system={&system_table}; IADLXPerformanceMonitoringServices perf={&perf_table};
    for(int i=0;i<3;i++) devices[i].pVtbl=&gpu_table;
    list.pVtbl=&list_table; support.pVtbl=&support_table; metrics.pVtbl=&metrics_table;
    support_extension.pVtbl=&support1_table; metrics_extension.pVtbl=&metrics1_table;
    gSystem=&system; gPerfServices=&perf; gInitAttempted=1; gInitResult=0;
    assert(ResourceManagerAdlxGetAbiVersion()==6);
    assert(ResourceManagerAdlxGetGpuMetricsSize()==848 && ResourceManagerAdlxGetGpuSelectionSize()==520);
    assert(offsetof(ResourceManagerAdlxGpuMetrics,identityValid)==824 && offsetof(ResourceManagerAdlxGpuMetrics,sampleTimestampMilliseconds)==832);
    assert(offsetof(ResourceManagerAdlxGpuMetrics,npuActivityLevelPercent)==840 && offsetof(ResourceManagerAdlxGpuMetrics,npuFrequencyMhz)==844);
    ResourceManagerAdlxGpuMetrics probe[3];
    assert(ResourceManagerAdlxReadGpuMetrics(probe,3,0,NULL,0)==3);
    assert(releases==4 && support_reads[0]+support_reads[1]+support_reads[2]==0);
    assert(ResourceManagerAdlxReadGpuMetrics(probe,2,0,NULL,0)==0);
    const int all=RM_ADLX_METRIC_USAGE|RM_ADLX_METRIC_VOLTAGE|RM_ADLX_METRIC_TIMESTAMP;
    int before=releases; ResourceManagerAdlxGpuMetrics value=sample(all);
    assert(value.availableFlags==all && value.usagePercent==0 && value.voltageMillivolts==0 && value.sampleTimestampMilliseconds==0);
    assert(releases-before==5); /* Two inspected GPUs, support, metrics, list. */
    assert(metric_reads[0]==0 && metric_reads[2]==0 && support_reads[0]==0 && support_reads[2]==0);
    assert(metric_reads[1]==1 && usage_reads==1 && voltage_reads==1 && timestamp_reads==1);
    int u=usage_reads,t=timestamp_reads; sample(RM_ADLX_METRIC_VOLTAGE);
    assert(usage_reads==u && timestamp_reads==t && voltage_reads==2);
    ResourceManagerAdlxGpuSelection distinct[2]={{.uniqueId=10,.requestedFlags=RM_ADLX_METRIC_USAGE},{.uniqueId=11,.requestedFlags=RM_ADLX_METRIC_VOLTAGE}};
    strcpy(distinct[0].pnpString,pnp[0]); strcpy(distinct[1].pnpString,pnp[1]);
    u=usage_reads; int previous_voltage=voltage_reads; t=timestamp_reads;
    assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,distinct,2,NULL,0)==2);
    assert(probe[0].availableFlags==RM_ADLX_METRIC_USAGE && probe[1].availableFlags==RM_ADLX_METRIC_VOLTAGE);
    assert(usage_reads==u+1 && voltage_reads==previous_voltage+1 && timestamp_reads==t && metric_reads[2]==0);
    usage=100; voltage=1250; timestamp=9007199254740993LL; value=sample(all);
    assert(value.availableFlags==all && value.sampleTimestampMilliseconds==9007199254740993LL);
    usage=100.1; assert(!(sample(all).availableFlags & RM_ADLX_METRIC_USAGE));
    usage=NAN; assert(!(sample(all).availableFlags & RM_ADLX_METRIC_USAGE));
    usage=INFINITY; assert(!(sample(all).availableFlags & RM_ADLX_METRIC_USAGE));
    usage=-1; voltage=-1; timestamp=-1; assert(sample(all).availableFlags==0);
    usage=50; voltage=1200; timestamp=0;
    usage_supported=0; voltage_supported=0; u=usage_reads; int v=voltage_reads;
    assert(sample(all).availableFlags==RM_ADLX_METRIC_TIMESTAMP && usage_reads==u && voltage_reads==v);
    usage_supported=1; voltage_supported=1; usage_result=voltage_result=timestamp_result=3;
    assert(sample(all).availableFlags==0); usage_result=voltage_result=timestamp_result=0;
    metrics_result=3; u=usage_reads; v=voltage_reads; t=timestamp_reads; before=releases;
    assert(sample(all).availableFlags==0 && usage_reads==u && voltage_reads==v && timestamp_reads==t && releases-before==5);
    metrics_result=0; support_result=3; before=releases;
    assert(ResourceManagerAdlxReadGpuMetrics(probe,3,all,NULL,0)==0 && releases-before==7);
    support_result=0;
    ResourceManagerAdlxGpuSelection selected={.uniqueId=11,.requestedFlags=all}; strcpy(selected.pnpString,pnp[1]);
    pnp[1]="changed"; u=metric_reads[1];
    assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,&selected,1,NULL,0)==0 && metric_reads[1]==u);
    pnp[1]=selected.pnpString; identity_result=3;
    assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,&selected,1,NULL,0)==0 && metric_reads[1]==u);
    identity_result=0; ResourceManagerAdlxGpuSelection duplicate[2]={selected,selected};
    assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,duplicate,2,NULL,0)==0);
    selected.requestedFlags=0; assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,&selected,1,NULL,0)==0);
    selected.requestedFlags=32768; assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,&selected,1,NULL,0)==0);
    selected.requestedFlags=all; memset(selected.pnpString,'x',512);
    assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,&selected,1,NULL,0)==0);
    assert(ResourceManagerAdlxReadSelectedGpuMetrics(probe,3,NULL,1,NULL,0)==0);
    pnp[1]="PCI\\VEN_1002&DEV_744C\\B";
    assert(total_reads[0]+total_reads[1]+total_reads[2]==0); /* Metadata and non-memory demands never query capacity. */
    const int memory_flags=RM_ADLX_METRIC_CLOCK|RM_ADLX_METRIC_VRAM_CLOCK|RM_ADLX_METRIC_VRAM;
    value=sample(memory_flags);
    assert(value.availableFlags==memory_flags && value.totalVramMb==8192 && value.vramUsedMb==0 && value.graphicsClockMhz==0 && value.memoryClockMhz==0);
    assert(total_reads[0]==0 && total_reads[1]==1 && total_reads[2]==0);
    clock_value=memory_clock_value=used_value=-1;
    assert(sample(memory_flags).availableFlags==0);
    clock_value=1200; memory_clock_value=1800; used_value=128;
    total_result=3; value=sample(memory_flags);
    assert(value.availableFlags==memory_flags && value.totalVramMb==0 && value.vramUsedMb==128); /* Failed dirty output discarded. */
    total_result=0; used_result=3; value=sample(memory_flags);
    assert(value.availableFlags==(RM_ADLX_METRIC_CLOCK|RM_ADLX_METRIC_VRAM_CLOCK) && value.totalVramMb==8192);
    used_result=0; clock_result=memory_clock_result=3; value=sample(memory_flags);
    assert(value.availableFlags==RM_ADLX_METRIC_VRAM);
    clock_result=memory_clock_result=0; memory_supported=0;
    const int npu_flags=RM_ADLX_METRIC_NPU_ACTIVITY_LEVEL|RM_ADLX_METRIC_NPU_FREQUENCY;
    before=releases; npu_call_order_count=0; value=sample(npu_flags);
    assert(value.availableFlags==npu_flags && value.npuActivityLevelPercent==0 && value.npuFrequencyMhz==0);
    assert(releases-before==7 && support_extension_queries>0 && metrics_extension_queries>0);
    assert(npu_call_order_count==4 && npu_call_order[0]==1 && npu_call_order[1]==2
        && npu_call_order[2]==3 && npu_call_order[3]==4);
    int activity_before=activity_metric_reads, frequency_before=frequency_metric_reads;
    activity_level=47; assert(sample(RM_ADLX_METRIC_NPU_ACTIVITY_LEVEL).availableFlags==RM_ADLX_METRIC_NPU_ACTIVITY_LEVEL);
    assert(activity_metric_reads==activity_before+1 && frequency_metric_reads==frequency_before);
    npu_frequency=1800; assert(sample(RM_ADLX_METRIC_NPU_FREQUENCY).availableFlags==RM_ADLX_METRIC_NPU_FREQUENCY);
    assert(activity_metric_reads==activity_before+1 && frequency_metric_reads==frequency_before+1);
    activity_supported=0; frequency_supported=0; u=activity_metric_reads; v=frequency_metric_reads;
    assert(sample(npu_flags).availableFlags==0 && activity_metric_reads==u && frequency_metric_reads==v);
    activity_supported=1; frequency_supported=1; activity_metric_result=frequency_metric_result=3;
    assert(sample(npu_flags).availableFlags==0); activity_metric_result=frequency_metric_result=0;
    activity_level=101; npu_frequency=-1;
    assert(sample(npu_flags).availableFlags==0); activity_level=0; npu_frequency=0;
    activity_support_result=3; u=activity_metric_reads;
    assert(!(sample(RM_ADLX_METRIC_NPU_ACTIVITY_LEVEL).availableFlags & RM_ADLX_METRIC_NPU_ACTIVITY_LEVEL)
        && activity_metric_reads==u);
    activity_support_result=0;
    frequency_support_result=3; v=frequency_metric_reads;
    assert(!(sample(RM_ADLX_METRIC_NPU_FREQUENCY).availableFlags & RM_ADLX_METRIC_NPU_FREQUENCY)
        && frequency_metric_reads==v);
    frequency_support_result=0;
    assert(sample(memory_flags).availableFlags==0);
    memory_supported=1; gpu_table.TotalVRAM=NULL; value=sample(memory_flags);
    assert(value.availableFlags==memory_flags && value.totalVramMb==0);
    gpu_table.TotalVRAM=vram;
    puts("ADLX selected metrics: ABI6, exact selected IO, NPU support and getter checks, NPU zero and failures, timestamp, memory/clock zero, dirty capacity rejection, identity and releases passed.");
    return 0;
}
