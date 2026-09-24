#pragma once
#include "Controller.h"
#include <tlhelp32.h>
#include <bcrypt.h>
#include <tuple>

namespace ExternalVulkan {
// Vulkan-Loader 1.4.341, matching LunarG PDB A7D4D0DB3B404FE48EB23F68EA71A6E4/1.
// The temporary adapter map is copied at a natural helper return, never reconstructed from ordinals.
class LoaderIdentity {
    HANDLE process;
    Handle file;
    HMODULE image=nullptr;
    using Key=std::tuple<uint64_t,uint64_t,uint64_t,uint64_t>;
    std::map<Key,uint64_t> adapters;
    template<class T> T read(uint64_t address) const {
        T value{};SIZE_T count=0;
        require(address&&ReadProcessMemory(process,reinterpret_cast<void*>(address),&value,sizeof value,&count)
            &&count==sizeof value,"Vulkan loader state read");return value;
    }
    std::vector<uint64_t> array(uint64_t instance,unsigned countOffset,unsigned pointerOffset) const {
        const auto count=read<uint32_t>(instance+countOffset),limit=32u;
        require(count>0&&count<=limit,"Vulkan physical array bound");
        auto pointer=read<uint64_t>(instance+pointerOffset);
        std::vector<uint64_t> result;for(unsigned i=0;i<count;++i)result.push_back(read<uint64_t>(pointer+i*8));
        return result;
    }
    void instancePresent(uint64_t expected) const {
        std::set<uint64_t> seen;auto current=read<uint64_t>(base+0x191408);
        while(current&&seen.size()<16&&seen.insert(current).second) {
            require(read<uint64_t>(current+8)==0x10ADED010110ADEDULL,"Vulkan instance magic");
            if(current==expected)return;
            current=read<uint64_t>(current+0x358);
        }
        throw std::runtime_error("Vulkan instance not owned by loader");
    }
    void icdPresent(uint64_t instance,uint64_t expected) const {
        std::set<uint64_t> seen;auto current=read<uint64_t>(instance+0x368);
        while(current&&seen.size()<16&&seen.insert(current).second) {
            require(read<uint64_t>(current+8)==instance,"Vulkan ICD instance owner");
            if(current==expected)return;
            current=read<uint64_t>(current+0x320);
        }
        throw std::runtime_error("Vulkan ICD not owned by instance");
    }
public:
    uint64_t base=0;
    struct Physical {uint64_t instance,icd,icdInstance,raw;};
    LoaderIdentity(HANDLE target,DWORD pid):process(target) {
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,pid));
        require(snapshot.h!=INVALID_HANDLE_VALUE,"Vulkan loader inventory");
        MODULEENTRY32W module{};module.dwSize=sizeof module;
        for(BOOL ok=Module32FirstW(snapshot.h,&module);ok;ok=Module32NextW(snapshot.h,&module)) {
            if(_wcsicmp(module.szModule,L"vulkan-1.dll"))continue;
            base=reinterpret_cast<uint64_t>(module.modBaseAddr);
            require(module.modBaseSize==0x1aa000,"Vulkan loader image size");
            file.h=CreateFileW(module.szExePath,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
            LARGE_INTEGER size{};require(file.h!=INVALID_HANDLE_VALUE&&GetFileSizeEx(file.h,&size)&&size.QuadPart==1730096,"qualified Vulkan loader file");
            std::vector<unsigned char> bytes(static_cast<size_t>(size.QuadPart));DWORD received=0;
            require(ReadFile(file.h,bytes.data(),static_cast<DWORD>(bytes.size()),&received,nullptr)&&received==bytes.size(),"Vulkan loader bytes");
            unsigned char digest[32]{};char hex[65]{};
            require(BCryptHash(BCRYPT_SHA256_ALG_HANDLE,nullptr,0,bytes.data(),received,digest,sizeof digest)>=0,"Vulkan loader hash");
            for(unsigned i=0;i<32;++i)sprintf(hex+i*2,"%02X",digest[i]);
            require(!strcmp(hex,"CD63989744D15FE13972511D3AFB7BBC171C7C72FE9C479B4909595A2F9D6EFB"),"unqualified Vulkan loader");
            image=LoadLibraryExW(module.szExePath,nullptr,DONT_RESOLVE_DLL_REFERENCES);
            require(image!=nullptr,"Vulkan image map");return;
        }
        throw std::runtime_error("Vulkan loader absent");
    }
    ~LoaderIdentity(){if(image)FreeLibrary(image);}
    LoaderIdentity(const LoaderIdentity&)=delete;
    uint64_t entry(unsigned rva) const {
        require(rva<0x1aa000-32,"Vulkan entry bounds");
        const auto bytes=read<std::array<unsigned char,32>>(base+rva);
        require(!memcmp(bytes.data(),reinterpret_cast<const unsigned char*>(image)+rva,bytes.size()),"Vulkan loader entry changed");
        return base+rva;
    }
    Physical physical(uint64_t handle) const {
        const auto instance=read<uint64_t>(handle+8);instancePresent(instance);
        require(read<uint64_t>(handle+0x10)==0x10ADED020210ADEDULL,"Vulkan physical trampoline magic");
        const auto trampolines=array(instance,0x338,0x340);
        require(std::count(trampolines.begin(),trampolines.end(),handle)==1,"Vulkan trampoline membership");
        const auto terminal=read<uint64_t>(handle+0x18);const auto terminals=array(instance,0x32c,0x330);
        require(std::count(terminals.begin(),terminals.end(),terminal)==1,"Vulkan wrapping layer requires verified translation");
        const auto icd=read<uint64_t>(terminal+8);icdPresent(instance,icd);
        const auto scanned=read<uint64_t>(icd);
        require(read<uint64_t>(scanned+0x38)!=0,"Vulkan ICD has no adapter identity enumeration");
        return {instance,icd,read<uint64_t>(icd+0x18),read<uint64_t>(terminal+0x10)};
    }
    void capture(uint64_t instance,uint64_t countAddress,uint64_t arrayAddress) {
        instancePresent(instance);
        const auto count=read<uint32_t>(countAddress);const auto pointer=read<uint64_t>(arrayAddress);
        require(count>0&&count<=32&&pointer,"Vulkan sorted adapter result bounds");
        std::map<Key,uint64_t> fresh;
        for(unsigned i=0;i<count;++i) {
            const auto item=pointer+i*0x20;const auto devices=read<uint32_t>(item);
            const auto values=read<uint64_t>(item+8),icd=read<uint64_t>(item+0x10),luid=read<uint64_t>(item+0x18);
            require(devices>0&&devices<=32&&values&&luid,"Vulkan sorted device bounds");icdPresent(instance,icd);
            const auto icdInstance=read<uint64_t>(icd+0x18);
            for(unsigned d=0;d<devices;++d) {
                const auto raw=read<uint64_t>(values+d*8);require(raw!=0,"Vulkan raw physical handle");
                const Key key{instance,icd,icdInstance,raw};
                auto inserted=fresh.emplace(key,luid);
                require(inserted.second||inserted.first->second==luid,"ambiguous Vulkan physical adapter");
                printf("{\"kind\":\"vulkanAdapterIdentity\",\"instance\":%llu,\"icd\":%llu,\"raw\":%llu,\"luid\":%llu}\n",instance,icd,raw,luid);
            }
        }
        for(auto it=adapters.begin();it!=adapters.end();)if(std::get<0>(it->first)==instance)it=adapters.erase(it);else ++it;
        adapters.insert(fresh.begin(),fresh.end());
    }
    uint64_t luid(const Physical& value) const {
        const auto it=adapters.find(Key{value.instance,value.icd,value.icdInstance,value.raw});
        require(it!=adapters.end(),"Vulkan physical adapter not observed");return it->second;
    }
    uint64_t luid(uint64_t handle) const {return luid(physical(handle));}
};
}
