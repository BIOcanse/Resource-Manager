// Independent SDK check for the Zig MIB_IF_ROW2 declaration.
// Build/run: g++ -std=c++20 NetworkInterfaceLayoutTests.cpp -o <owned-test.exe>
#include <winsock2.h>
#include <windows.h>
#include <netioapi.h>
#include <cstddef>
#include <cstring>

static_assert(sizeof(MIB_IF_ROW2) == 1352);
static_assert(offsetof(MIB_IF_ROW2, InterfaceAndOperStatusFlags) == 1152);
static_assert(offsetof(MIB_IF_ROW2, OperStatus) == 1156);
static_assert(offsetof(MIB_IF_ROW2, TransmitLinkSpeed) == 1192);
static_assert(offsetof(MIB_IF_ROW2, ReceiveLinkSpeed) == 1200);
static_assert(offsetof(MIB_IF_ROW2, InOctets) == 1208);
static_assert(offsetof(MIB_IF_ROW2, OutOctets) == 1280);
static_assert(offsetof(MIB_IF_TABLE2, Table) == 8);

int main() {
    MIB_IF_ROW2 row{};
    row.InterfaceAndOperStatusFlags.HardwareInterface = 1;
    row.InterfaceAndOperStatusFlags.FilterInterface = 1;
    row.InterfaceAndOperStatusFlags.EndPointInterface = 1;
    unsigned char flags = 0;
    std::memcpy(&flags, &row.InterfaceAndOperStatusFlags, 1);
    return flags == 0x83 ? 0 : 1;
}
