// Compile with Windows SDK winioctl.h/nvme.h on the include path.
#include <windows.h>
#include <winioctl.h>
#include <nvme.h>
#include <cstddef>
static_assert(IOCTL_STORAGE_QUERY_PROPERTY == 0x002D1400);
static_assert(StorageAdapterProtocolSpecificProperty == 49);
static_assert(StorageDeviceProtocolSpecificProperty == 50);
static_assert(ProtocolTypeNvme == 3 && NVMeDataTypeLogPage == 2);
static_assert(offsetof(STORAGE_PROPERTY_QUERY, AdditionalParameters) == 8);
static_assert(sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA) == 40);
static_assert(sizeof(STORAGE_PROTOCOL_DATA_DESCRIPTOR) == 48);
static_assert(offsetof(STORAGE_PROTOCOL_DATA_DESCRIPTOR, ProtocolSpecificData) == 8);
static_assert(offsetof(STORAGE_PROTOCOL_SPECIFIC_DATA, ProtocolDataOffset) == 16);
static_assert(offsetof(STORAGE_PROTOCOL_SPECIFIC_DATA, ProtocolDataLength) == 20);
static_assert(offsetof(STORAGE_DEVICE_DESCRIPTOR, SerialNumberOffset) == 24);
static_assert(offsetof(STORAGE_DEVICE_DESCRIPTOR, BusType) == 28);
static_assert(sizeof(NVME_HEALTH_INFO_LOG) == 512);
static_assert(ProtocolTypeAta == 2 && AtaDataTypeIdentify == 1 && NVMeDataTypeIdentify == 1);
static_assert(sizeof(NVME_IDENTIFY_CONTROLLER_DATA) == 4096);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, SN) == 4);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, MN) == 24);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, FR) == 64);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, IEEE) == 73);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, CNTLID) == 78);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, VER) == 80);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, CTRATT) == 96);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, OACS) == 256);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, TNVMCAP) == 280);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, UNVMCAP) == 296);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, NN) == 516);
static_assert(offsetof(NVME_IDENTIFY_CONTROLLER_DATA, ONCS) == 520);
static_assert(offsetof(NVME_HEALTH_INFO_LOG, Temperature) == 1);
static_assert(offsetof(NVME_HEALTH_INFO_LOG, PercentageUsed) == 5);
static_assert(offsetof(NVME_HEALTH_INFO_LOG, DataUnitRead) == 32);
static_assert(offsetof(NVME_HEALTH_INFO_LOG, MediaErrors) == 160);
static_assert(offsetof(NVME_HEALTH_INFO_LOG, WarningCompositeTemperatureTime) == 192);
static_assert(offsetof(NVME_HEALTH_INFO_LOG, TemperatureSensor1) == 200);
static_assert(offsetof(NVME_HEALTH_INFO_LOG, TemperatureSensor8) == 214);
int main() { return 0; }
