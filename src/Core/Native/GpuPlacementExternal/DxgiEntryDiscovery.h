#pragma once
#include "Controller.h"
#include <d3d11.h>
#include <d3d12.h>
#include <tlhelp32.h>

namespace ExternalDxgi {
enum class Renderer { D3D11, D3D12 };
enum class EntryRole { Strict, QualifiedRenderer };
struct Entries {
    uint64_t present=0, present1=0;
    uint64_t createDevice=0, adapterLuid=0, createQueue=0, createSwapChain=0, releaseSwapChain=0;
    uint64_t executeCommandLists=0;
    std::array<unsigned char,32> submissionEntryBytes{};
    std::array<unsigned char,32> presentBytes{},present1Bytes{};
};

struct Window {
    HWND value=nullptr;
    Window() {
        // A hidden controller-owned window is sufficient to build real COM vtables.
        value=CreateWindowExW(0,L"STATIC",L"",WS_POPUP,0,0,64,64,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        require(value!=nullptr,"calibration window");
    }
    ~Window() { if(value) DestroyWindow(value); }
    Window(const Window&)=delete;
    Window& operator=(const Window&)=delete;
};

inline bool ObservedEntryJump(const unsigned char* local,const unsigned char* remote,size_t length,EntryRole role) {
    return role==EntryRole::QualifiedRenderer&&length==32&&remote[0]==0xe9
        &&memcmp(local+5,remote+5,length-5)==0;
}

struct ReadOnlyImageView {
    void* base=nullptr;
    ~ReadOnlyImageView(){if(base) UnmapViewOfFile(base);}
};

inline uint64_t Resolve(HANDLE process,DWORD pid,uint64_t address,EntryRole role=EntryRole::Strict,
    std::array<unsigned char,32>* validatedBytes=nullptr) {
    HMODULE local=nullptr; wchar_t path[32768]{};
    require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(address),&local),"calibration entry module");
    const DWORD length=GetModuleFileNameW(local,path,32768);
    require(length>0&&length<32768,"calibration module path");
    const auto* dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(local);
    require(dos->e_magic==IMAGE_DOS_SIGNATURE&&dos->e_lfanew>0&&dos->e_lfanew<1048576,"calibration DOS header");
    const auto* pe=reinterpret_cast<const IMAGE_NT_HEADERS64*>(reinterpret_cast<const char*>(local)+dos->e_lfanew);
    require(pe->Signature==IMAGE_NT_SIGNATURE&&pe->OptionalHeader.Magic==IMAGE_NT_OPTIONAL_HDR64_MAGIC,"calibration PE header");
    const auto offset=address-reinterpret_cast<uint64_t>(local);
    require(offset<pe->OptionalHeader.SizeOfImage&&pe->OptionalHeader.SizeOfImage-offset>=32,"calibration entry bounds");
    // Calibration can itself have an overlay hook. Compare with the image file,
    // mapped without executable permission or loader initialization.
    Handle file(CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
    require(file.h!=INVALID_HANDLE_VALUE,"calibration image file");
    Handle mapping(CreateFileMappingW(file.h,nullptr,PAGE_READONLY|SEC_IMAGE_NO_EXECUTE,0,0,nullptr));
    require(mapping.h!=nullptr,"calibration readonly image mapping");
    ReadOnlyImageView view{MapViewOfFile(mapping.h,FILE_MAP_READ,0,0,0)};
    require(view.base!=nullptr,"calibration readonly image view");
    const auto* diskDos=static_cast<const IMAGE_DOS_HEADER*>(view.base);
    require(diskDos->e_magic==IMAGE_DOS_SIGNATURE&&diskDos->e_lfanew==dos->e_lfanew,"calibration disk DOS header");
    const auto* diskPe=reinterpret_cast<const IMAGE_NT_HEADERS64*>(static_cast<const char*>(view.base)+diskDos->e_lfanew);
    require(diskPe->Signature==IMAGE_NT_SIGNATURE&&diskPe->OptionalHeader.Magic==IMAGE_NT_OPTIONAL_HDR64_MAGIC
        &&diskPe->OptionalHeader.SizeOfImage==pe->OptionalHeader.SizeOfImage
        &&diskPe->FileHeader.TimeDateStamp==pe->FileHeader.TimeDateStamp,"calibration loaded image identity");
    const auto* reference=static_cast<const unsigned char*>(view.base)+offset;
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,pid));
    require(snapshot.h!=INVALID_HANDLE_VALUE,"renderer module inventory");
    MODULEENTRY32W item{}; item.dwSize=sizeof item;
    for(BOOL ok=Module32FirstW(snapshot.h,&item);ok;ok=Module32NextW(snapshot.h,&item)) {
        if(_wcsicmp(item.szExePath,path)!=0) continue;
        require(item.modBaseSize==pe->OptionalHeader.SizeOfImage,"renderer module size");
        const auto remote=reinterpret_cast<uint64_t>(item.modBaseAddr)+offset;
        MEMORY_BASIC_INFORMATION memory{};
        require(VirtualQueryEx(process,reinterpret_cast<void*>(remote),&memory,sizeof memory)==sizeof memory
            &&memory.Type==MEM_IMAGE&&memory.State==MEM_COMMIT
            &&!(memory.Protect&PAGE_GUARD)
            &&(memory.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)),"renderer entry executable image");
        unsigned char bytes[32]{}; SIZE_T read=0;
        require(ReadProcessMemory(process,reinterpret_cast<void*>(remote),bytes,sizeof bytes,&read)
            &&read==sizeof bytes,"renderer entry read");
        if(memcmp(bytes,reference,sizeof bytes)!=0) {
            // Stop at the original COM entry before the jump. Present returns to
            // its original caller; never follow, emulate, or modify the detour.
            if(ObservedEntryJump(reference,bytes,sizeof bytes,role)) {
                int32_t displacement=0;memcpy(&displacement,bytes+1,sizeof displacement);
                const auto destination=remote+5+static_cast<int64_t>(displacement);
                MEMORY_BASIC_INFORMATION branch{};
                require(destination!=remote&&VirtualQueryEx(process,reinterpret_cast<void*>(destination),&branch,sizeof branch)==sizeof branch
                    &&branch.State==MEM_COMMIT&&!(branch.Protect&PAGE_GUARD)
                    &&(branch.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)),
                    "renderer entry detour destination");
                printf("{\"kind\":\"rendererEntryDetour\",\"rva\":%llu,\"remote\":%llu,\"destination\":%llu,\"role\":\"%s\"}\n",
                    static_cast<unsigned long long>(offset),static_cast<unsigned long long>(remote),static_cast<unsigned long long>(destination),
                    "qualified-renderer");
                if(validatedBytes) std::copy(std::begin(bytes),std::end(bytes),validatedBytes->begin());
                return remote;
            }
            printf("{\"kind\":\"rendererEntryMismatch\",\"rva\":%llu,\"localAddress\":%llu,\"remoteAddress\":%llu,\"canonicalFileBytes\":\"",
                static_cast<unsigned long long>(offset),static_cast<unsigned long long>(address),static_cast<unsigned long long>(remote));
            for(unsigned i=0;i<sizeof bytes;++i) printf("%02x",reference[i]);
            printf("\",\"remoteBytes\":\""); for(auto byte:bytes) printf("%02x",byte); puts("\"}");
            throw std::runtime_error("renderer entry byte identity");
        }
        printf("{\"kind\":\"rendererEntry\",\"rva\":%llu,\"remote\":%llu,\"imageSize\":%lu,\"targetMetadata\":false}\n",
            static_cast<unsigned long long>(offset),static_cast<unsigned long long>(remote),item.modBaseSize);
        if(validatedBytes) std::copy(std::begin(bytes),std::end(bytes),validatedBytes->begin());
        return remote;
    }
    throw std::runtime_error("Calibrated renderer module not present in target");
}

inline Entries Discover(HANDLE process,DWORD pid,Renderer renderer,bool observeSubmission=false) {
    Window window;
    Com<IDXGIFactory2> factory;
    require(SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory2),reinterpret_cast<void**>(factory.out()))),"calibration factory");
    Com<ID3D11Device> device11; Com<ID3D11DeviceContext> context11;
    Com<ID3D12Device> device12; Com<ID3D12CommandQueue> queue;
    IUnknown* source=nullptr;
    if(renderer==Renderer::D3D11) {
        require(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,
            device11.out(),nullptr,context11.out())),"calibration D3D11 device");
        source=device11.p;
    } else {
        require(SUCCEEDED(D3D12CreateDevice(nullptr,D3D_FEATURE_LEVEL_11_0,__uuidof(ID3D12Device),
            reinterpret_cast<void**>(device12.out()))),"calibration D3D12 device");
        D3D12_COMMAND_QUEUE_DESC description{}; description.Type=D3D12_COMMAND_LIST_TYPE_DIRECT;
        require(SUCCEEDED(device12->CreateCommandQueue(&description,__uuidof(ID3D12CommandQueue),
            reinterpret_cast<void**>(queue.out()))),"calibration D3D12 queue");
        source=queue.p;
    }
    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width=64; description.Height=64; description.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count=1; description.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount=2; description.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
    Com<IDXGISwapChain1> chain;
    require(SUCCEEDED(factory->CreateSwapChainForHwnd(source,window.value,&description,nullptr,nullptr,chain.out())),"calibration swap chain");
    auto table=*reinterpret_cast<void***>(chain.p);
    Entries entries;
    const auto role=observeSubmission?EntryRole::QualifiedRenderer:EntryRole::Strict;
    entries.present=Resolve(process,pid,reinterpret_cast<uint64_t>(table[8]),role,&entries.presentBytes);
    entries.present1=Resolve(process,pid,reinterpret_cast<uint64_t>(table[22]),role,&entries.present1Bytes);
    if(renderer==Renderer::D3D12) {
        const auto module=GetModuleHandleW(L"d3d12.dll");
        const auto creation=GetProcAddress(module,"D3D12CreateDevice");
        require(creation!=nullptr,"D3D12 creation entry");
        const auto deviceTable=*reinterpret_cast<void***>(device12.p);
        const auto factoryTable=*reinterpret_cast<void***>(factory.p);
        entries.createDevice=Resolve(process,pid,reinterpret_cast<uint64_t>(creation),role);
        // Published COM ABI: ID3D12Device slots 8/43; IDXGIFactory2 slot 15.
        entries.createQueue=Resolve(process,pid,reinterpret_cast<uint64_t>(deviceTable[8]),role);
        entries.adapterLuid=Resolve(process,pid,reinterpret_cast<uint64_t>(deviceTable[43]),role);
        entries.createSwapChain=Resolve(process,pid,reinterpret_cast<uint64_t>(factoryTable[15]),role);
        entries.releaseSwapChain=Resolve(process,pid,reinterpret_cast<uint64_t>(table[2]),role);
        if(observeSubmission) {
            entries.executeCommandLists=Resolve(process,pid,reinterpret_cast<uint64_t>((*reinterpret_cast<void***>(queue.p))[10]),
                role,&entries.submissionEntryBytes);
        }
    }
    return entries;
}
}
