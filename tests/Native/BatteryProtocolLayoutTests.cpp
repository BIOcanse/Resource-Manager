#include <windows.h>
#include <winioctl.h>
#include <poclass.h>
#include <cstddef>
static_assert(IOCTL_BATTERY_QUERY_TAG == 0x00294040);
static_assert(IOCTL_BATTERY_QUERY_INFORMATION == 0x00294044);
static_assert(IOCTL_BATTERY_QUERY_STATUS == 0x0029404c);
static_assert(sizeof(BATTERY_QUERY_INFORMATION) == 12);
static_assert(offsetof(BATTERY_QUERY_INFORMATION, AtRate) == 8);
static_assert(sizeof(BATTERY_INFORMATION) == 36);
static_assert(offsetof(BATTERY_INFORMATION, Chemistry) == 8);
static_assert(offsetof(BATTERY_INFORMATION, DesignedCapacity) == 12);
static_assert(offsetof(BATTERY_INFORMATION, FullChargedCapacity) == 16);
static_assert(offsetof(BATTERY_INFORMATION, CycleCount) == 32);
static_assert(sizeof(BATTERY_WAIT_STATUS) == 20);
static_assert(offsetof(BATTERY_WAIT_STATUS, Timeout) == 4);
static_assert(sizeof(BATTERY_STATUS) == 16);
static_assert(offsetof(BATTERY_STATUS, Rate) == 12);
static_assert(BatteryInformation == 0 && BatteryEstimatedTime == 3 && BatteryUniqueID == 7);
static_assert(BATTERY_CAPACITY_RELATIVE == 0x40000000);
static_assert(BATTERY_UNKNOWN_RATE == 0x80000000);
static_assert(BATTERY_UNKNOWN_CAPACITY == 0xffffffff);
static_assert(BATTERY_UNKNOWN_VOLTAGE == 0xffffffff && BATTERY_UNKNOWN_TIME == 0xffffffff);
int main() { return 0; }
