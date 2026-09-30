/* Executes the actual bridge against recording interfaces, without a GPU driver. */
#include "../../src/Core/Native/AdlxBridge/ResourceManagerAdlxBridge.c"
#include <assert.h>

static int type = 2, writes, releases, ignore_write;
static const char* device_id = "744C";
static adlx_bool service_supported = 1;
static int values[] = {0, 500, 2400, 1000, 1800, 20, 1200, 1};
static IADLXGPU fake_gpu;
static IADLXGPUList fake_list;
static RmAdlxTuningInterface fake_services, fake_gfx, fake_vram, fake_power, fake_fan;
static adlx_long __stdcall release_gpu(IADLXGPU* self) { (void)self; releases++; return 1; }
static adlx_long __stdcall release_list(IADLXGPUList* self) { (void)self; releases++; return 1; }
static adlx_long __stdcall release_tuning(RmAdlxTuningInterface* self) { (void)self; releases++; return 1; }
static ADLX_RESULT __stdcall gpu_type(IADLXGPU* self, int* value) { (void)self; *value = type; return 0; }
static ADLX_RESULT __stdcall gpu_pnp(IADLXGPU* self, const char** value) { (void)self; *value = "PCI\\TEST"; return 0; }
static ADLX_RESULT __stdcall gpu_device(IADLXGPU* self, const char** value) { (void)self; *value = device_id; return 0; }
static adlx_uint __stdcall list_size(IADLXGPUList* self) { (void)self; return 1; }
static ADLX_RESULT __stdcall list_at(IADLXGPUList* self, adlx_uint index, IADLXGPU** value) { (void)self; assert(index == 0); *value = &fake_gpu; return 0; }
static ADLX_RESULT __stdcall get_gpus(IADLXSystem* self, IADLXGPUList** value) { (void)self; *value = &fake_list; return 0; }
static ADLX_RESULT __stdcall get_services(IADLXSystem* self, RmAdlxTuningInterface** value) { (void)self; *value = &fake_services; return 0; }
static ADLX_RESULT __stdcall supports_service(RmAdlxTuningInterface* self, IADLXGPU* gpu, adlx_bool* value) { (void)self; assert(gpu == &fake_gpu); *value = service_supported; return 0; }
static ADLX_RESULT __stdcall query(RmAdlxTuningInterface* self, const wchar_t* id, void** value)
{
    assert((self == &fake_gfx && wcscmp(id, L"IADLXManualGraphicsTuning2") == 0)
        || (self == &fake_vram && wcscmp(id, L"IADLXManualVRAMTuning2") == 0)
        || (self == &fake_power && wcscmp(id, L"IADLXManualPowerTuning") == 0)
        || (self == &fake_fan && wcscmp(id, L"IADLXManualFanTuning") == 0));
    *value = self; return 0;
}
static ADLX_RESULT __stdcall get_power(RmAdlxTuningInterface* self, IADLXGPU* gpu, RmAdlxTuningInterface** value) { (void)self; assert(gpu == &fake_gpu); *value = &fake_power; return 0; }
static ADLX_RESULT __stdcall get_gfx(RmAdlxTuningInterface* self, IADLXGPU* gpu, RmAdlxTuningInterface** value) { (void)self; assert(gpu == &fake_gpu); *value = &fake_gfx; return 0; }
static ADLX_RESULT __stdcall get_vram(RmAdlxTuningInterface* self, IADLXGPU* gpu, RmAdlxTuningInterface** value) { (void)self; assert(gpu == &fake_gpu); *value = &fake_vram; return 0; }
static ADLX_RESULT __stdcall get_fan(RmAdlxTuningInterface* self, IADLXGPU* gpu, RmAdlxTuningInterface** value) { (void)self; assert(gpu == &fake_gpu); *value = &fake_fan; return 0; }
static ADLX_RESULT __stdcall supported(RmAdlxTuningInterface* self, adlx_bool* value) { (void)self; *value = 1; return 0; }
static ADLX_RESULT __stdcall read_zero(RmAdlxTuningInterface* self, adlx_bool* value) { (void)self; *value = (adlx_bool)values[7]; return 0; }
static ADLX_RESULT __stdcall write_zero(RmAdlxTuningInterface* self, adlx_bool value) { (void)self; writes++; if (!ignore_write) values[7]=value; return 0; }
static ADLX_RESULT __stdcall range(RmAdlxTuningInterface* self, RmAdlxIntRange* value)
{ *value = self == &fake_power ? (RmAdlxIntRange){-10,14,2} : (RmAdlxIntRange){100,3000,1}; return 0; }
#define VALUE_METHODS(name, field) \
static ADLX_RESULT __stdcall read_##name(RmAdlxTuningInterface* self, int* value) { (void)self; *value = values[field]; return 0; } \
static ADLX_RESULT __stdcall write_##name(RmAdlxTuningInterface* self, int value) { (void)self; writes++; if (!ignore_write) values[field] = value; return 0; }
VALUE_METHODS(power,0)
VALUE_METHODS(min,1)
VALUE_METHODS(max,2)
VALUE_METHODS(voltage,3)
VALUE_METHODS(vram,4)
VALUE_METHODS(fan_min,5)
VALUE_METHODS(fan_target,6)
static int request(int field, int operation, int value)
{ int actual, minimum, maximum, step, mode; return ResourceManagerAdlxTuning("PCI\\TEST", field, operation, value, &actual, &minimum, &maximum, &step, &mode); }
int main(void)
{
    IADLXGPUVtbl gpu_vtable = {.Release=release_gpu, .Type=gpu_type, .PNPString=gpu_pnp, .DeviceId=gpu_device};
    IADLXGPUListVtbl list_vtable = {.Release=release_list, .Size=list_size, .At_GPUList=list_at};
    IADLXSystemVtbl system_vtable = {.GetGPUs=get_gpus, .GetGPUTuningServices=get_services};
    IADLXSystem system = {&system_vtable};
    RmAdlxTuningVtbl services_vtable = {.Release=release_tuning, .methods={
        [5]=supports_service,[6]=supports_service,[7]=supports_service,[8]=supports_service,
        [11]=get_gfx,[12]=get_vram,[13]=get_fan,[14]=get_power}};
    RmAdlxTuningVtbl gfx_vtable = {.Release=release_tuning, .QueryInterface=query,
        .methods={range,read_min,write_min,range,read_max,write_max,range,read_voltage,write_voltage}};
    RmAdlxTuningVtbl power_vtable = {.Release=release_tuning, .QueryInterface=query, .methods={range,read_power,write_power}};
    RmAdlxTuningVtbl vram_vtable = {.Release=release_tuning, .QueryInterface=query, .methods={[4]=range,[5]=read_vram,[6]=write_vram}};
    RmAdlxTuningVtbl fan_vtable = {.Release=release_tuning, .QueryInterface=query, .methods={
        [5]=supported,[6]=read_zero,[7]=write_zero,[12]=supported,[13]=range,[14]=read_fan_min,[15]=write_fan_min,
        [16]=supported,[17]=range,[18]=read_fan_target,[19]=write_fan_target}};
    fake_gpu.pVtbl=&gpu_vtable; fake_list.pVtbl=&list_vtable; fake_services.pVtbl=&services_vtable;
    fake_gfx.pVtbl=&gfx_vtable; fake_power.pVtbl=&power_vtable; fake_vram.pVtbl=&vram_vtable;
    fake_fan.pVtbl=&fan_vtable;
    gSystem=&system; gInitAttempted=1; gInitResult=0;
    for (int field=0; field<8; field++) assert(request(field,0,0)==0);
    assert(writes==0);
    assert(request(0,1,3)==RM_TUNING_RANGE); assert(writes==0);
    assert(request(1,1,2500)==RM_TUNING_RANGE); assert(writes==0);
    assert(request(2,1,400)==RM_TUNING_RANGE); assert(writes==0);
    assert(request(0,1,6)==0); assert(values[0]==6 && values[2]==2400);
    assert(request(4,1,1900)==0); assert(values[4]==1900 && values[2]==2400);
    assert(request(5,1,200)==0); assert(values[5]==200 && values[6]==1200);
    assert(request(6,1,1500)==0); assert(values[6]==1500);
    assert(request(7,1,0)==0); assert(values[7]==0);
    ignore_write=1; assert(request(0,1,8)==RM_TUNING_READBACK);
    ignore_write=0;
    int before_semantics=writes;
    assert(request(8,1,100)==RM_TUNING_SEMANTICS);
    device_id="7550";
    assert(request(2,1,200)==RM_TUNING_SEMANTICS);
    assert(request(3,1,200)==RM_TUNING_SEMANTICS);
    assert(writes==before_semantics);
    /* Positive offset range is still an offset. No absolute minimum comparison. */
    assert(request(8,1,200)==0); assert(values[2]==200);
    assert(request(1,1,600)==0); assert(values[1]==600);
    assert(request(9,1,100)==0); assert(values[3]==100);
    device_id="FFFF"; before_semantics=writes;
    assert(request(2,1,200)==RM_TUNING_GENERATION);
    assert(request(8,1,200)==RM_TUNING_GENERATION);
    assert(request(1,1,600)==RM_TUNING_GENERATION);
    assert(writes==before_semantics);
    assert(request(0,0,0)==0);
    device_id="ab40"; assert(request(8,1,200)==RM_TUNING_GENERATION);
    before_semantics=writes; service_supported=0;
    assert(request(0,1,6)==RM_TUNING_UNSUPPORTED); assert(writes==before_semantics);
    service_supported=1;
    int count=writes; type=1; assert(request(0,1,6)==RM_TUNING_IDENTITY); assert(writes==count);
    assert(releases>0);
    puts("ADLX native tuning: range, discovery, siblings, readback, identity, generation semantics and releases passed.");
    return 0;
}
