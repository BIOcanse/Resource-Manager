#include <winsock2.h>
#include <windows.h>
#include <ws2ipdef.h>
#include <ifdef.h>
#include <nldef.h>
#include <ntddndis.h>
#define __IPHLPAPI_H__
#if defined(RESOURCE_MANAGER_SDK_NETIO_HEADERS)
#include "resource_manager_sdk_netio.h"
#else
#include <netioapi.h>
#endif
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void hex(const void *data, size_t size) {
    const unsigned char *bytes = (const unsigned char *)data;
    putchar('"');
    for (size_t i = 0; i < size; i++) printf("%02X", bytes[i]);
    putchar('"');
}

int main(void) {
    const size_t size = offsetof(MIB_IPFORWARD_TABLE2, Table) + 4 * sizeof(MIB_IPFORWARD_ROW2);
    MIB_IPFORWARD_TABLE2 *table = (MIB_IPFORWARD_TABLE2 *)calloc(1, size);
    if (!table) return 1;
    table->NumEntries = 4;
    MIB_IPFORWARD_ROW2 *rows = (MIB_IPFORWARD_ROW2 *)((unsigned char *)table + offsetof(MIB_IPFORWARD_TABLE2, Table));
    for (int i = 0; i < 4; i++) {
        rows[i].InterfaceLuid.Value = i == 3 ? 0x0000000100000001ULL : 0xFEDCBA9876543210ULL;
        rows[i].InterfaceIndex = i == 3 ? 23 : 0xF0000001UL;
    }
    rows[0].DestinationPrefix.Prefix.si_family = AF_INET;
    rows[0].DestinationPrefix.PrefixLength = 0;
    rows[0].NextHop.si_family = AF_INET;
    const unsigned char gateway4[] = {198,51,100,1};
    memcpy(&rows[0].NextHop.Ipv4.sin_addr, gateway4, 4);
    rows[0].SitePrefixLength = 255;
    rows[0].ValidLifetime = 0xffffffffUL;
    rows[0].PreferredLifetime = 17;
    rows[0].Metric = 0xffffffffUL;
    rows[0].Protocol = (NL_ROUTE_PROTOCOL)0xf0000041UL;
    rows[0].Loopback = TRUE;
    rows[0].AutoconfigureAddress = FALSE;
    rows[0].Publish = TRUE;
    rows[0].Immortal = FALSE;
    rows[0].Age = 0xffffffffUL;
    rows[0].Origin = (NL_ROUTE_ORIGIN)0xf0000043UL;
    rows[1].DestinationPrefix.Prefix.si_family = AF_INET6;
    rows[1].DestinationPrefix.PrefixLength = 64;
    const unsigned char prefix6[] = {0xfe,0x80,0,0,0,0,0,0,0,0,0,0,0,0,0,0};
    memcpy(&rows[1].DestinationPrefix.Prefix.Ipv6.sin6_addr, prefix6, 16);
    rows[1].DestinationPrefix.Prefix.Ipv6.sin6_scope_id = 55;
    rows[1].NextHop.si_family = AF_INET6;
    const unsigned char gateway6[] = {0xfe,0x80,0,0,0,0,0,0,0,0,0,0,0,0,0,1};
    memcpy(&rows[1].NextHop.Ipv6.sin6_addr, gateway6, 16);
    rows[1].NextHop.Ipv6.sin6_scope_id = 77;
    rows[1].SitePrefixLength = 64;
    rows[1].ValidLifetime = 0;
    rows[1].PreferredLifetime = 0xffffffffUL;
    rows[1].Metric = 31;
    rows[1].Protocol = (NL_ROUTE_PROTOCOL)3;
    rows[1].Loopback = FALSE;
    rows[1].AutoconfigureAddress = TRUE;
    rows[1].Publish = FALSE;
    rows[1].Immortal = TRUE;
    rows[1].Age = 9;
    rows[1].Origin = (NL_ROUTE_ORIGIN)2;
    rows[2].DestinationPrefix.Prefix.si_family = AF_INET;
    rows[2].DestinationPrefix.PrefixLength = 24;
    const unsigned char prefix4[] = {198,51,100,0};
    memcpy(&rows[2].DestinationPrefix.Prefix.Ipv4.sin_addr, prefix4, 4);
    rows[2].NextHop.si_family = AF_INET;
    rows[2].SitePrefixLength = 24;
    rows[3].DestinationPrefix.Prefix.si_family = AF_INET6;
    rows[3].NextHop.si_family = AF_INET6;
    rows[3].ValidLifetime = rows[3].PreferredLifetime = 0xffffffffUL;
    printf("{\"pointerSize\":%zu,\"rowSize\":%zu,\"tableOffset\":%zu,\"prefixSize\":%zu,", sizeof(void *), sizeof(MIB_IPFORWARD_ROW2), offsetof(MIB_IPFORWARD_TABLE2, Table), sizeof(IP_ADDRESS_PREFIX));
#define OFFSET(type, member, name) printf("\"" name "\":%zu,", offsetof(type, member))
    OFFSET(MIB_IPFORWARD_ROW2, InterfaceLuid, "luidOffset");
    OFFSET(MIB_IPFORWARD_ROW2, InterfaceIndex, "indexOffset");
    OFFSET(MIB_IPFORWARD_ROW2, DestinationPrefix, "destinationOffset");
    OFFSET(IP_ADDRESS_PREFIX, PrefixLength, "prefixLengthOffset");
    OFFSET(MIB_IPFORWARD_ROW2, NextHop, "nextHopOffset");
    OFFSET(MIB_IPFORWARD_ROW2, SitePrefixLength, "sitePrefixOffset");
    OFFSET(MIB_IPFORWARD_ROW2, ValidLifetime, "validLifetimeOffset");
    OFFSET(MIB_IPFORWARD_ROW2, PreferredLifetime, "preferredLifetimeOffset");
    OFFSET(MIB_IPFORWARD_ROW2, Metric, "metricOffset");
    OFFSET(MIB_IPFORWARD_ROW2, Protocol, "protocolOffset");
    OFFSET(MIB_IPFORWARD_ROW2, Loopback, "loopbackOffset");
    OFFSET(MIB_IPFORWARD_ROW2, AutoconfigureAddress, "autoconfigureOffset");
    OFFSET(MIB_IPFORWARD_ROW2, Publish, "publishOffset");
    OFFSET(MIB_IPFORWARD_ROW2, Immortal, "immortalOffset");
    OFFSET(MIB_IPFORWARD_ROW2, Age, "ageOffset");
    OFFSET(MIB_IPFORWARD_ROW2, Origin, "originOffset");
    OFFSET(SOCKADDR_IN, sin_addr, "socketV4Offset");
    OFFSET(SOCKADDR_IN6, sin6_addr, "socketV6Offset");
    OFFSET(SOCKADDR_IN6, sin6_scope_id, "socketScopeOffset");
    printf("\"routeTableHex\":"); hex(table,size); printf("}\n");
    free(table);
    return 0;
}
