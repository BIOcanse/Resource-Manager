/* Pure IGCL substitute compiled against the pinned official header. No driver or device IO. */
#include <string.h>
#include "igcl_api.h"

static ctl_oc_telemetry_item_t number(double value, ctl_units_t unit) {
    ctl_oc_telemetry_item_t item = {0};
    item.bSupported = true; item.units = unit; item.type = CTL_DATA_TYPE_DOUBLE;
    item.value.datadouble = value; return item;
}
static ctl_oc_telemetry_item_t counter(uint64_t value) {
    ctl_oc_telemetry_item_t item = {0};
    item.bSupported = true; item.units = CTL_UNITS_MEMORY_BYTES; item.type = CTL_DATA_TYPE_UINT64;
    item.value.datau64 = value; return item;
}

CTL_APIEXPORT ctl_result_t CTL_APICALL ctlInit(ctl_init_args_t *args, ctl_api_handle_t *api) {
    const ctl_application_id_t zero = {0};
    if (args->Size != sizeof(*args) || args->Version != 0 || args->AppVersion != CTL_MAKE_VERSION(1,0)
        || args->flags != CTL_INIT_FLAG_USE_LEVEL_ZERO || memcmp(&args->ApplicationUID, &zero, sizeof(zero))) return (ctl_result_t)1;
    *api = (ctl_api_handle_t)(uintptr_t)42; args->SupportedVersion = CTL_MAKE_VERSION(1,1);
    return CTL_RESULT_SUCCESS;
}
CTL_APIEXPORT ctl_result_t CTL_APICALL ctlClose(ctl_api_handle_t api) {
    return (uintptr_t)api == 42 ? CTL_RESULT_SUCCESS : (ctl_result_t)1;
}
CTL_APIEXPORT ctl_result_t CTL_APICALL ctlEnumerateDevices(ctl_api_handle_t api, uint32_t *count, ctl_device_adapter_handle_t *devices) {
    if ((uintptr_t)api != 42) return (ctl_result_t)1;
    if (*count == 0) { *count = 2; return CTL_RESULT_SUCCESS; }
    if (*count != 2 || !devices) return (ctl_result_t)1;
    devices[0] = (ctl_device_adapter_handle_t)(uintptr_t)2;
    devices[1] = (ctl_device_adapter_handle_t)(uintptr_t)1;
    return CTL_RESULT_SUCCESS;
}
CTL_APIEXPORT ctl_result_t CTL_APICALL ctlGetDeviceProperties(ctl_device_adapter_handle_t device, ctl_device_adapter_properties_t *props) {
    if (props->Size != sizeof(*props) || props->Version != 0 || props->device_id_size != sizeof(uint64_t) || !props->pDeviceID)
        return (ctl_result_t)1;
    *(uint64_t*)props->pDeviceID = (uintptr_t)device == 1 ? UINT64_C(0xFEDCBA98000000A1) : UINT64_C(0x00000002000000B1);
    props->device_type = CTL_DEVICE_TYPE_GRAPHICS; props->pci_vendor_id = 0x8086; props->pci_device_id = 0x56A0;
    return CTL_RESULT_SUCCESS;
}
CTL_APIEXPORT ctl_result_t CTL_APICALL ctlPowerTelemetryGet(ctl_device_adapter_handle_t device, ctl_power_telemetry_t *value) {
    if ((uintptr_t)device != 1 || value->Size != sizeof(*value) || value->Version != 0) return (ctl_result_t)1;
    memset(value, 0, sizeof(*value)); value->Size = sizeof(*value);
    value->timeStamp = number(13, CTL_UNITS_TIME_SECONDS); value->gpuEnergyCounter = number(5, CTL_UNITS_ENERGY_JOULES);
    value->gpuCurrentClockFrequency = number(1222.5, CTL_UNITS_FREQUENCY_MHZ);
    value->vramCurrentClockFrequency = number(900.75, CTL_UNITS_FREQUENCY_MHZ);
    value->gpuVoltage = number(1.1, CTL_UNITS_VOLTAGE_VOLTS); value->gpuCurrentTemperature = number(-1, CTL_UNITS_TEMPERATURE_CELSIUS);
    value->globalActivityCounter = number(17, CTL_UNITS_TIME_SECONDS); value->renderComputeActivityCounter = number(19, CTL_UNITS_TIME_SECONDS);
    value->mediaActivityCounter = number(23, CTL_UNITS_TIME_SECONDS);
    value->gpuPowerLimited = false; value->gpuTemperatureLimited = true; value->gpuCurrentLimited = false;
    value->gpuVoltageLimited = true; value->gpuUtilizationLimited = false;
    value->vramEnergyCounter = number(7, CTL_UNITS_ENERGY_JOULES); value->vramVoltage = number(1.2, CTL_UNITS_VOLTAGE_VOLTS);
    value->vramReadBandwidthCounter = counter(UINT64_C(9007199254740993)); value->vramWriteBandwidthCounter = counter(UINT64_MAX);
    value->vramCurrentTemperature = number(-3, CTL_UNITS_TEMPERATURE_CELSIUS); value->totalCardEnergyCounter = number(11, CTL_UNITS_ENERGY_JOULES);
    return CTL_RESULT_SUCCESS;
}

CTL_APIEXPORT ctl_result_t CTL_APICALL ctlPciGetState(ctl_device_adapter_handle_t device, ctl_pci_state_t *state) {
    if ((uintptr_t)device != 1 || state->Size != sizeof(*state) || state->Version != 0
        || state->speed.Size != sizeof(state->speed) || state->speed.Version != 0) return (ctl_result_t)1;
    state->speed.gen = 4; state->speed.width = 8; state->speed.maxBandwidth = -1;
    return CTL_RESULT_SUCCESS;
}

CTL_APIEXPORT ctl_result_t CTL_APICALL ctlPciGetProperties(ctl_device_adapter_handle_t device, ctl_pci_properties_t *props) {
    if ((uintptr_t)device != 1 || props->Size != sizeof(*props) || props->Version != 0
        || props->address.Size != sizeof(props->address) || props->address.Version != 0
        || props->maxSpeed.Size != sizeof(props->maxSpeed) || props->maxSpeed.Version != 0) return (ctl_result_t)1;
    props->maxSpeed.gen = 5; props->maxSpeed.width = 16; props->maxSpeed.maxBandwidth = -1;
    props->address.domain = 1; props->address.bus = 23; props->address.device = 4; props->address.function = 2;
    props->resizable_bar_supported = true; props->resizable_bar_enabled = false;
    return CTL_RESULT_SUCCESS;
}
