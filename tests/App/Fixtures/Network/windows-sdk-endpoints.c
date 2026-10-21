#include <winsock2.h>
#include <windows.h>
#if defined(RESOURCE_MANAGER_SDK_ENDPOINT_HEADERS)
#include "resource_manager_sdk_endpoints.h"
#else
#include <iphlpapi.h>
#endif
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void hex(const void *data, size_t size) {
    const unsigned char *bytes=(const unsigned char *)data;
    putchar('"'); for(size_t i=0;i<size;i++) printf("%02X",bytes[i]); putchar('"');
}
static DWORD port(unsigned short value) {
    const unsigned char bytes[4]={(unsigned char)(value>>8),(unsigned char)value,0xa5,0x5a};
    DWORD result; memcpy(&result,bytes,4); return result;
}
static DWORD address(unsigned char a,unsigned char b,unsigned char c,unsigned char d) {
    const unsigned char bytes[4]={a,b,c,d}; DWORD result; memcpy(&result,bytes,4); return result;
}
int main(void) {
    const size_t t4Size=offsetof(MIB_TCPTABLE_OWNER_PID,table)+3*sizeof(MIB_TCPROW_OWNER_PID);
    const size_t t6Size=offsetof(MIB_TCP6TABLE_OWNER_PID,table)+3*sizeof(MIB_TCP6ROW_OWNER_PID);
    const size_t u4Size=offsetof(MIB_UDPTABLE_OWNER_PID,table)+3*sizeof(MIB_UDPROW_OWNER_PID);
    const size_t u6Size=offsetof(MIB_UDP6TABLE_OWNER_PID,table)+3*sizeof(MIB_UDP6ROW_OWNER_PID);
    MIB_TCPTABLE_OWNER_PID *t4=(MIB_TCPTABLE_OWNER_PID *)calloc(1,t4Size);
    MIB_TCP6TABLE_OWNER_PID *t6=(MIB_TCP6TABLE_OWNER_PID *)calloc(1,t6Size);
    MIB_UDPTABLE_OWNER_PID *u4=(MIB_UDPTABLE_OWNER_PID *)calloc(1,u4Size);
    MIB_UDP6TABLE_OWNER_PID *u6=(MIB_UDP6TABLE_OWNER_PID *)calloc(1,u6Size);
    if(!t4||!t6||!u4||!u6) { free(t4);free(t6);free(u4);free(u6);return 1; }
    t4->dwNumEntries=t6->dwNumEntries=u4->dwNumEntries=u6->dwNumEntries=3;
    MIB_TCPROW_OWNER_PID *a=t4->table;
    a[0].dwState=2; a[0].dwLocalPort=port(55535); a[0].dwRemoteAddr=address(203,0,113,4);a[0].dwRemotePort=port(443);a[0].dwOwningPid=101;
    a[1].dwState=5;a[1].dwLocalAddr=address(198,51,100,7);a[1].dwLocalPort=port(50000);a[1].dwRemoteAddr=address(203,0,113,9);a[1].dwRemotePort=port(443);a[1].dwOwningPid=101;
    a[2].dwState=0xfffffffeUL;a[2].dwLocalAddr=address(127,0,0,1);a[2].dwLocalPort=port(1);a[2].dwRemotePort=port(0);a[2].dwOwningPid=0xffffffffUL;
    const unsigned char link[16]={0xfe,0x80,0,0,0,0,0,0,0,0,0,0,0,0,0,1};
    const unsigned char remote[16]={0x20,0x01,0x0d,0xb8,0,0,0,0,0,0,0,0,0,0,0,9};
    const unsigned char loop[16]={0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1};
    MIB_TCP6ROW_OWNER_PID *b=t6->table;
    b[0].dwState=2;b[0].dwLocalPort=port(5353);memcpy(b[0].ucRemoteAddr,remote,16);b[0].dwRemoteScopeId=55;b[0].dwRemotePort=port(443);b[0].dwOwningPid=101;
    b[1].dwState=5;memcpy(b[1].ucLocalAddr,link,16);b[1].dwLocalScopeId=0x11223344UL;b[1].dwLocalPort=port(65535);memcpy(b[1].ucRemoteAddr,remote,16);b[1].dwRemoteScopeId=0xf1234567UL;b[1].dwRemotePort=port(443);b[1].dwOwningPid=202;
    b[2].dwState=11;memcpy(b[2].ucLocalAddr,loop,16);b[2].dwLocalPort=port(0);b[2].dwRemotePort=port(0);
    MIB_UDPROW_OWNER_PID *c=u4->table;
    c[0].dwLocalPort=port(5353);c[0].dwOwningPid=101;
    c[1].dwLocalAddr=address(127,0,0,1);c[1].dwLocalPort=port(65535);c[1].dwOwningPid=303;
    c[2].dwLocalAddr=address(198,51,100,7);c[2].dwLocalPort=port(0);c[2].dwOwningPid=0xffffffffUL;
    MIB_UDP6ROW_OWNER_PID *d=u6->table;
    d[0].dwLocalPort=port(5353);d[0].dwOwningPid=101;
    memcpy(d[1].ucLocalAddr,link,16);d[1].dwLocalScopeId=0x11223344UL;d[1].dwLocalPort=port(65535);d[1].dwOwningPid=202;
    memcpy(d[2].ucLocalAddr,loop,16);d[2].dwLocalScopeId=0xffffffffUL;d[2].dwLocalPort=port(0);d[2].dwOwningPid=303;
    printf("{\"pointerSize\":%zu,\"tcpTableClass\":%u,\"udpTableClass\":%u,",sizeof(void *),TCP_TABLE_OWNER_PID_ALL,UDP_TABLE_OWNER_PID);
    printf("\"tcp4RowSize\":%zu,\"tcp4TableOffset\":%zu,",sizeof(MIB_TCPROW_OWNER_PID),offsetof(MIB_TCPTABLE_OWNER_PID,table));
    printf("\"tcp6RowSize\":%zu,\"tcp6TableOffset\":%zu,",sizeof(MIB_TCP6ROW_OWNER_PID),offsetof(MIB_TCP6TABLE_OWNER_PID,table));
    printf("\"udp4RowSize\":%zu,\"udp4TableOffset\":%zu,",sizeof(MIB_UDPROW_OWNER_PID),offsetof(MIB_UDPTABLE_OWNER_PID,table));
    printf("\"udp6RowSize\":%zu,\"udp6TableOffset\":%zu,",sizeof(MIB_UDP6ROW_OWNER_PID),offsetof(MIB_UDP6TABLE_OWNER_PID,table));
#define OFFSET(type,member,name) printf("\"" name "\":%zu,",offsetof(type,member))
    OFFSET(MIB_TCPROW_OWNER_PID,dwState,"tcp4StateOffset");OFFSET(MIB_TCPROW_OWNER_PID,dwLocalAddr,"tcp4LocalOffset");
    OFFSET(MIB_TCPROW_OWNER_PID,dwLocalPort,"tcp4LocalPortOffset");OFFSET(MIB_TCPROW_OWNER_PID,dwRemoteAddr,"tcp4RemoteOffset");
    OFFSET(MIB_TCPROW_OWNER_PID,dwRemotePort,"tcp4RemotePortOffset");OFFSET(MIB_TCPROW_OWNER_PID,dwOwningPid,"tcp4PidOffset");
    OFFSET(MIB_TCP6ROW_OWNER_PID,ucLocalAddr,"tcp6LocalOffset");OFFSET(MIB_TCP6ROW_OWNER_PID,dwLocalScopeId,"tcp6LocalScopeOffset");
    OFFSET(MIB_TCP6ROW_OWNER_PID,dwLocalPort,"tcp6LocalPortOffset");OFFSET(MIB_TCP6ROW_OWNER_PID,ucRemoteAddr,"tcp6RemoteOffset");
    OFFSET(MIB_TCP6ROW_OWNER_PID,dwRemoteScopeId,"tcp6RemoteScopeOffset");OFFSET(MIB_TCP6ROW_OWNER_PID,dwRemotePort,"tcp6RemotePortOffset");
    OFFSET(MIB_TCP6ROW_OWNER_PID,dwState,"tcp6StateOffset");OFFSET(MIB_TCP6ROW_OWNER_PID,dwOwningPid,"tcp6PidOffset");
    OFFSET(MIB_UDPROW_OWNER_PID,dwLocalAddr,"udp4LocalOffset");OFFSET(MIB_UDPROW_OWNER_PID,dwLocalPort,"udp4LocalPortOffset");
    OFFSET(MIB_UDPROW_OWNER_PID,dwOwningPid,"udp4PidOffset");OFFSET(MIB_UDP6ROW_OWNER_PID,ucLocalAddr,"udp6LocalOffset");
    OFFSET(MIB_UDP6ROW_OWNER_PID,dwLocalScopeId,"udp6LocalScopeOffset");OFFSET(MIB_UDP6ROW_OWNER_PID,dwLocalPort,"udp6LocalPortOffset");
    OFFSET(MIB_UDP6ROW_OWNER_PID,dwOwningPid,"udp6PidOffset");
    printf("\"tcp4Hex\":");hex(t4,t4Size);printf(",\"tcp6Hex\":");hex(t6,t6Size);
    printf(",\"udp4Hex\":");hex(u4,u4Size);printf(",\"udp6Hex\":");hex(u6,u6Size);printf("}\n");
    free(t4);free(t6);free(u4);free(u6);return 0;
}
