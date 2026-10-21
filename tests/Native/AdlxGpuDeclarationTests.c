/* Actual bridge declaration path, fake GPU interfaces only. */
#define main unused_fan_fixture
#include "AdlxFanMetricsTests.c"
#undef main
#include <stddef.h>

static char version[129], part[129], date[129], vram[129];
static int bios_calls, vram_calls, bios_result, vram_result, destroy_strings;
static const char* version_pointer=version;
static ADLX_RESULT __stdcall bios(IADLXGPU* self,const char** out_part,const char** out_version,const char** out_date)
{ (void)self; bios_calls++; *out_part=part; *out_version=version_pointer; *out_date=date; return bios_result; }
static ADLX_RESULT __stdcall memory_type(IADLXGPU* self,const char** output)
{ (void)self; vram_calls++; *output=vram; return vram_result; }
static adlx_long __stdcall drop_declaration(IADLXGPU* self)
{ (void)self; releases++; if(destroy_strings) version[0]=part[0]=date[0]=vram[0]=0; return 1; }
static void reset_text(void)
{ strcpy(version,"020.003.000.030"); strcpy(part,"113-TEST-001"); strcpy(date,"04/31/2024"); strcpy(vram,"GDDR6"); }
static ResourceManagerAdlxGpuDeclaration declaration(int flags)
{
    reset_text(); ResourceManagerAdlxGpuSelection selected={.uniqueId=2,.requestedFlags=flags};
    strcpy(selected.pnpString,"fixture"); ResourceManagerAdlxGpuDeclaration result;
    assert(ResourceManagerAdlxReadGpuDeclarations(&result,1,&selected,1,NULL,0)==1);
    assert(result.structSize==1040 && result.abiVersion==1 && result.uniqueId==2);
    return result;
}
int main(void)
{
    assert(offsetof(IADLXGPUVtbl,VRAMType)==12*sizeof(void*) && offsetof(IADLXGPUVtbl,BIOSInfo)==13*sizeof(void*));
    assert(ResourceManagerAdlxGetAbiVersion()==5 && ResourceManagerAdlxGetDeclarationAbiVersion()==1);
    assert(ResourceManagerAdlxGetGpuDeclarationSize()==1040 && offsetof(ResourceManagerAdlxGpuDeclaration,vbiosVersion)==528);
    IADLXGPUVtbl gpu_table={.Release=drop_declaration,.Name=text,.VendorId=text,.DeviceId=text,.PNPString=text,.Type=integer,.UniqueId=integer,.TotalVRAM=memory,.BIOSInfo=bios,.VRAMType=memory_type};
    IADLXGPUListVtbl list_table={.Release=release_list,.Size=size,.At_GPUList=at};
    IADLXSystemVtbl system_table={.GetGPUs=get_gpus}; IADLXSystem system={&system_table};
    gpu.pVtbl=&gpu_table; list.pVtbl=&list_table; gSystem=&system; gPerfServices=NULL;
    gInitAttempted=1; gInitResult=RM_ADLX_ERROR_NO_PERF;
    ResourceManagerAdlxGpuMetrics identity;
    assert(ResourceManagerAdlxReadGpuMetrics(&identity,1,0,NULL,0)==1 && identity.identityValid==1);
    assert(ResourceManagerAdlxReadGpuMetrics(&identity,1,RM_ADLX_METRIC_USAGE,NULL,0)==RM_ADLX_ERROR_NO_PERF);
    assert(bios_calls==0 && vram_calls==0);
    destroy_strings=1; int before=releases; ResourceManagerAdlxGpuDeclaration value=declaration(15);
    assert(value.availableFlags==15 && strcmp(value.vbiosVersion,"020.003.000.030")==0 && strcmp(value.vbiosPartNumber,"113-TEST-001")==0);
    assert(strcmp(value.vbiosDateRaw,"04/31/2024")==0 && strcmp(value.vramType,"GDDR6")==0);
    assert(version[0]==0 && vram[0]==0 && releases-before==2 && bios_calls==1 && vram_calls==1);
    int b=bios_calls,v=vram_calls; value=declaration(2);
    assert(value.availableFlags==2 && bios_calls==b && vram_calls==v+1);
    b=bios_calls;v=vram_calls;value=declaration(1);
    assert(value.availableFlags==1 && bios_calls==b+1 && vram_calls==v);
    bios_result=3; assert(declaration(15).availableFlags==2); bios_result=0;
    vram_result=3; assert(declaration(15).availableFlags==13); vram_result=0;
    version_pointer=NULL; assert(declaration(15).availableFlags==14); version_pointer=version;
    reset_text(); memset(version,'x',128); version[128]=0;
    ResourceManagerAdlxGpuSelection selected={.uniqueId=2,.requestedFlags=15}; strcpy(selected.pnpString,"fixture");
    assert(ResourceManagerAdlxReadGpuDeclarations(&value,1,&selected,1,NULL,0)==1 && value.availableFlags==14);
    gpu_table.BIOSInfo=NULL; assert(declaration(15).availableFlags==2); gpu_table.BIOSInfo=bios;
    gpu_table.VRAMType=NULL; assert(declaration(15).availableFlags==13); gpu_table.VRAMType=memory_type;
    b=bios_calls;v=vram_calls;selected.uniqueId=3;
    assert(ResourceManagerAdlxReadGpuDeclarations(&value,1,&selected,1,NULL,0)==0 && bios_calls==b && vram_calls==v);
    selected.uniqueId=2;strcpy(selected.pnpString,"changed");
    assert(ResourceManagerAdlxReadGpuDeclarations(&value,1,&selected,1,NULL,0)==0 && bios_calls==b && vram_calls==v);
    strcpy(selected.pnpString,"fixture");selected.requestedFlags=16;
    assert(ResourceManagerAdlxReadGpuDeclarations(&value,1,&selected,1,NULL,0)==0);
    selected.requestedFlags=15;ResourceManagerAdlxGpuSelection duplicate[2]={selected,selected};ResourceManagerAdlxGpuDeclaration outputs[2];
    assert(ResourceManagerAdlxReadGpuDeclarations(outputs,2,duplicate,2,NULL,0)==0);
    assert(ResourceManagerAdlxReadGpuDeclarations(NULL,1,&selected,1,NULL,0)==0);
    assert(ResourceManagerAdlxReadGpuDeclarations(outputs,2,NULL,1,NULL,0)==0);
    puts("ADLX GPU declarations: official offsets, ABI/size, no performance dependency, identity, selection, strings copied before Release, partial failures and releases passed.");
    return 0;
}
