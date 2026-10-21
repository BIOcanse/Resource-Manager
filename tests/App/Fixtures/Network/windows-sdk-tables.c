#include <winsock2.h>
#include <windows.h>
#include <ws2ipdef.h>
#include <ifdef.h>
#include <nldef.h>
#include <ntddndis.h>
#define __IPHLPAPI_H__
#if defined(RESOURCE_MANAGER_SDK_NETIO_HEADERS)
#define IPHLPAPI_DLL_LINKAGE DECLSPEC_IMPORT
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
    const size_t interfacesSize = offsetof(MIB_IF_TABLE2, Table) + 2 * sizeof(MIB_IF_ROW2);
    const size_t addressesSize = offsetof(MIB_UNICASTIPADDRESS_TABLE, Table) + 2 * sizeof(MIB_UNICASTIPADDRESS_ROW);
    MIB_IF_TABLE2 *interfaces = (MIB_IF_TABLE2 *)calloc(1, interfacesSize);
    MIB_UNICASTIPADDRESS_TABLE *addresses = (MIB_UNICASTIPADDRESS_TABLE *)calloc(1, addressesSize);
    if (!interfaces || !addresses) { free(interfaces); free(addresses); return 1; }
    interfaces->NumEntries = addresses->NumEntries = 2;
    MIB_IF_ROW2 *interfaceRows = (MIB_IF_ROW2 *)((unsigned char *)interfaces + offsetof(MIB_IF_TABLE2, Table));
    MIB_UNICASTIPADDRESS_ROW *addressRows = (MIB_UNICASTIPADDRESS_ROW *)((unsigned char *)addresses + offsetof(MIB_UNICASTIPADDRESS_TABLE, Table));
    interfaceRows[0].InterfaceLuid.Value = 0xFEDCBA9876543210ULL;
    interfaceRows[0].InterfaceIndex = 0xF0000001UL;
    interfaceRows[0].InterfaceGuid = (GUID){0x12345678, 0xABCD, 0x1234, {0x80,0x01,0x02,0x03,0x04,0x05,0x06,0x07}};
    interfaceRows[1].InterfaceLuid.Value = 0x0000000100000001ULL;
    interfaceRows[1].InterfaceIndex = 23;
    interfaceRows[1].InterfaceGuid = (GUID){0xFEDCBA98, 0xDCBA, 0x4321, {0x81,0x01,0x02,0x03,0x04,0x05,0x06,0x08}};
    interfaceRows[0].InOctets = 0ULL;
    interfaceRows[1].InOctets = 0ULL;
    interfaceRows[0].InUcastPkts = 1ULL;
    interfaceRows[1].InUcastPkts = 0ULL;
    interfaceRows[0].InNUcastPkts = 9007199254740993ULL;
    interfaceRows[1].InNUcastPkts = 4503599627370496ULL;
    interfaceRows[0].InDiscards = 18446744073709551615ULL;
    interfaceRows[1].InDiscards = 9223372036854775807ULL;
    interfaceRows[0].InErrors = 104ULL;
    interfaceRows[1].InErrors = 52ULL;
    interfaceRows[0].InUnknownProtos = 105ULL;
    interfaceRows[1].InUnknownProtos = 52ULL;
    interfaceRows[0].InUcastOctets = 106ULL;
    interfaceRows[1].InUcastOctets = 53ULL;
    interfaceRows[0].InMulticastOctets = 107ULL;
    interfaceRows[1].InMulticastOctets = 53ULL;
    interfaceRows[0].InBroadcastOctets = 108ULL;
    interfaceRows[1].InBroadcastOctets = 54ULL;
    interfaceRows[0].OutOctets = 109ULL;
    interfaceRows[1].OutOctets = 54ULL;
    interfaceRows[0].OutUcastPkts = 110ULL;
    interfaceRows[1].OutUcastPkts = 55ULL;
    interfaceRows[0].OutNUcastPkts = 111ULL;
    interfaceRows[1].OutNUcastPkts = 55ULL;
    interfaceRows[0].OutDiscards = 112ULL;
    interfaceRows[1].OutDiscards = 56ULL;
    interfaceRows[0].OutErrors = 113ULL;
    interfaceRows[1].OutErrors = 56ULL;
    interfaceRows[0].OutUcastOctets = 114ULL;
    interfaceRows[1].OutUcastOctets = 57ULL;
    interfaceRows[0].OutMulticastOctets = 115ULL;
    interfaceRows[1].OutMulticastOctets = 57ULL;
    interfaceRows[0].OutBroadcastOctets = 116ULL;
    interfaceRows[1].OutBroadcastOctets = 58ULL;
    for (int i = 0; i < 2; i++) {
        addressRows[i].InterfaceLuid = interfaceRows[0].InterfaceLuid;
        addressRows[i].InterfaceIndex = interfaceRows[0].InterfaceIndex;
    }
    MIB_UNICASTIPADDRESS_ROW *v4 = &addressRows[0], *v6 = &addressRows[1];
    const unsigned char v4bytes[4] = {198,51,100,7};
    v4->Address.Ipv4.sin_family = AF_INET;
    memcpy(&v4->Address.Ipv4.sin_addr, v4bytes, sizeof(v4bytes));
    v4->PrefixOrigin = (NL_PREFIX_ORIGIN)4;
    v4->SuffixOrigin = (NL_SUFFIX_ORIGIN)5;
    v4->ValidLifetime = 0xFFFFFFFFUL;
    v4->PreferredLifetime = 17;
    v4->OnLinkPrefixLength = 24;
    v4->SkipAsSource = TRUE;
    v4->DadState = (NL_DAD_STATE)4;
    v4->CreationTimeStamp.QuadPart = 0x7FFFFFFFFFFFFFF2LL;
    const unsigned char v6bytes[16] = {0xFE,0x80,0,0,0,0,0,0,0,0,0,0,0,0,0x12,0x34};
    v6->Address.Ipv6.sin6_family = AF_INET6;
    memcpy(&v6->Address.Ipv6.sin6_addr, v6bytes, sizeof(v6bytes));
    v6->Address.Ipv6.sin6_scope_id = 55;
    v6->PrefixOrigin = (NL_PREFIX_ORIGIN)41;
    v6->SuffixOrigin = (NL_SUFFIX_ORIGIN)43;
    v6->DadState = (NL_DAD_STATE)49;
    v6->OnLinkPrefixLength = 64;
    v6->ScopeId.Value = 0xFFFFFFFFUL;
    v6->CreationTimeStamp.QuadPart = -1;
    printf("{\"pointerSize\":%zu,\"interfaceRowSize\":%zu,\"interfaceTableOffset\":%zu,\"interfaceGuidOffset\":%zu,", sizeof(void *),sizeof(MIB_IF_ROW2),offsetof(MIB_IF_TABLE2,Table),offsetof(MIB_IF_ROW2,InterfaceGuid));
    printf("\"addressRowSize\":%zu,\"addressTableOffset\":%zu,\"luidOffset\":%zu,\"creationOffset\":%zu,\"scopeOffset\":%zu,\"socketV4Offset\":%zu,\"socketV6Offset\":%zu,\"socketScopeOffset\":%zu,",sizeof(MIB_UNICASTIPADDRESS_ROW),offsetof(MIB_UNICASTIPADDRESS_TABLE,Table),offsetof(MIB_UNICASTIPADDRESS_ROW,InterfaceLuid),offsetof(MIB_UNICASTIPADDRESS_ROW,CreationTimeStamp),offsetof(MIB_UNICASTIPADDRESS_ROW,ScopeId),offsetof(SOCKADDR_IN,sin_addr),offsetof(SOCKADDR_IN6,sin6_addr),offsetof(SOCKADDR_IN6,sin6_scope_id));
    printf("\"counterOffsets\":{");
    printf("\"InOctets\":%zu",offsetof(MIB_IF_ROW2,InOctets));
    printf(",\"InUcastPkts\":%zu",offsetof(MIB_IF_ROW2,InUcastPkts));
    printf(",\"InNUcastPkts\":%zu",offsetof(MIB_IF_ROW2,InNUcastPkts));
    printf(",\"InDiscards\":%zu",offsetof(MIB_IF_ROW2,InDiscards));
    printf(",\"InErrors\":%zu",offsetof(MIB_IF_ROW2,InErrors));
    printf(",\"InUnknownProtos\":%zu",offsetof(MIB_IF_ROW2,InUnknownProtos));
    printf(",\"InUcastOctets\":%zu",offsetof(MIB_IF_ROW2,InUcastOctets));
    printf(",\"InMulticastOctets\":%zu",offsetof(MIB_IF_ROW2,InMulticastOctets));
    printf(",\"InBroadcastOctets\":%zu",offsetof(MIB_IF_ROW2,InBroadcastOctets));
    printf(",\"OutOctets\":%zu",offsetof(MIB_IF_ROW2,OutOctets));
    printf(",\"OutUcastPkts\":%zu",offsetof(MIB_IF_ROW2,OutUcastPkts));
    printf(",\"OutNUcastPkts\":%zu",offsetof(MIB_IF_ROW2,OutNUcastPkts));
    printf(",\"OutDiscards\":%zu",offsetof(MIB_IF_ROW2,OutDiscards));
    printf(",\"OutErrors\":%zu",offsetof(MIB_IF_ROW2,OutErrors));
    printf(",\"OutUcastOctets\":%zu",offsetof(MIB_IF_ROW2,OutUcastOctets));
    printf(",\"OutMulticastOctets\":%zu",offsetof(MIB_IF_ROW2,OutMulticastOctets));
    printf(",\"OutBroadcastOctets\":%zu",offsetof(MIB_IF_ROW2,OutBroadcastOctets));
    printf("},");
    printf("\"interfaceTableHex\":"); hex(interfaces, interfacesSize);
    printf(",\"addressTableHex\":"); hex(addresses, addressesSize);
    puts("}");
    free(interfaces); free(addresses);
    return 0;
}
