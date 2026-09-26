#pragma once
#include "DxgiEntryDiscovery.h"
#include <bcrypt.h>

namespace ExternalDxgi {
// Reads existing renderer state at natural calls; no function is invoked in the target.
class QtD3D12Admission {
    struct Image {
        HMODULE local=nullptr;
        uint64_t remote=0;
        DWORD size=0;
        Image(DWORD pid,const wchar_t* name,const char* expectedHash) {
            Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,pid));
            require(snapshot.h!=INVALID_HANDLE_VALUE,"Qt module inventory");
            MODULEENTRY32W module{}; module.dwSize=sizeof module;
            for(BOOL ok=Module32FirstW(snapshot.h,&module);ok;ok=Module32NextW(snapshot.h,&module)) {
                if(_wcsicmp(module.szModule,name)) continue;
                Handle file(CreateFileW(module.szExePath,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
                LARGE_INTEGER length{};
                require(file.h!=INVALID_HANDLE_VALUE&&GetFileSizeEx(file.h,&length)&&length.QuadPart>0&&length.QuadPart<=128*1024*1024,"Qt qualification image file");
                std::vector<unsigned char> content(static_cast<size_t>(length.QuadPart));DWORD count=0;
                require(ReadFile(file.h,content.data(),static_cast<DWORD>(content.size()),&count,nullptr)&&count==content.size(),"Qt qualification image read");
                unsigned char hash[32]{};
                require(BCryptHash(BCRYPT_SHA256_ALG_HANDLE,nullptr,0,content.data(),count,hash,sizeof hash)>=0,"Qt qualification image hash");
                char hex[65]{};
                for(unsigned i=0;i<32;++i) sprintf(hex+i*2,"%02X",hash[i]);
                require(strcmp(hex,expectedHash)==0,"Qt or CRT build has not been qualified");
                local=LoadLibraryExW(module.szExePath,nullptr,DONT_RESOLVE_DLL_REFERENCES);
                require(local!=nullptr,"map Qt image without executing initialization");
                remote=reinterpret_cast<uint64_t>(module.modBaseAddr); size=module.modBaseSize;
                return;
            }
            throw std::runtime_error("Qt renderer module missing");
        }
        ~Image(){if(local) FreeLibrary(local);}
        Image(const Image&)=delete;
        uint64_t entry(HANDLE process,const char* name) const {
            auto address=GetProcAddress(local,name);
            require(address!=nullptr,"Qt renderer export missing");
            const auto offset=reinterpret_cast<uint64_t>(address)-reinterpret_cast<uint64_t>(local);
            require(offset<size&&size-offset>=32,"Qt export bounds");
            unsigned char bytes[32]{}; SIZE_T count=0;
            require(ReadProcessMemory(process,reinterpret_cast<void*>(remote+offset),bytes,sizeof bytes,&count)
                &&count==sizeof bytes&&memcmp(bytes,reinterpret_cast<void*>(address),sizeof bytes)==0,"Qt export byte identity");
            return remote+offset;
        }
    };
    struct Frame {uint64_t window=0,rhi=0,chain=0,returnAddress=0,returnStack=0;bool ending=false,submitted=false;};
    HANDLE process;
    Image quick,gui,core,crt;
    uint64_t beforeFrame,endFrame;
    uint64_t environmentSlots=0;
    std::map<DWORD,Frame> frames;
    template<class T> T read(uint64_t address) const {
        T value{}; SIZE_T count=0;
        require(address&&ReadProcessMemory(process,reinterpret_cast<void*>(address),&value,sizeof value,&count)
            &&count==sizeof value,"Qt renderer state read");
        return value;
    }
    void match(const Image& image,const char* name,std::initializer_list<unsigned char> expected) const {
        const auto address=image.entry(process,name);
        std::vector<unsigned char> bytes(expected.size()); SIZE_T count=0;
        require(ReadProcessMemory(process,reinterpret_cast<void*>(address),bytes.data(),bytes.size(),&count)
            &&count==bytes.size()&&std::equal(bytes.begin(),bytes.end(),expected.begin()),"Qt private layout not verified");
    }
    Frame inspect(uint64_t window) const {
        const auto data=read<uint64_t>(window+8);
        const auto device=read<uint64_t>(data+0x2d8);
        const auto configuration=read<uint64_t>(data+0x2e0);
        require(read<uint32_t>(device+4)==0,"Qt explicit adapter or imported device is not movable");
        require(!(read<uint32_t>(configuration+0x20)&8),"Qt software-device preference is not movable");
        Frame frame{window,read<uint64_t>(data+0x378),read<uint64_t>(data+0x380)};
        require(frame.rhi&&frame.chain,"Qt has no active RHI swapchain");
        return frame;
    }
    void checkEnvironment(uint64_t deadline,const char* adapterVariable) const {
        // Qualified UCRT dual-state narrow environments are the data Qt's getenv_s reads.
        require(read<uint64_t>(core.remote+0x5ab890)==0,"Qt environment mutation in progress");
        const auto lock=read<RTL_CRITICAL_SECTION>(crt.remote+0x139a78);
        require(lock.LockCount==-1&&lock.RecursionCount==0&&lock.OwningThread==nullptr,"CRT environment mutation in progress");
        std::set<uint64_t> vectors;
        for(unsigned state=0;state<2;++state) {
            const auto vector=read<uint64_t>(environmentSlots+state*8);
            require(vector!=0,"CRT narrow environment is not initialized");
            if(!vectors.insert(vector).second) continue;
            bool terminated=false;
            for(unsigned index=0;index<4096;++index) {
                require(GetTickCount64()<deadline,"CRT environment read deadline");
                const auto address=read<uint64_t>(vector+index*8);
                if(!address){terminated=true;break;}
                // Longer names cannot match either override; never scan their values.
                char prefix[sizeof("QSG_RHI_PREFER_SOFTWARE_RENDERER")]{};size_t offset=0;bool separator=false;std::string name;
                while(offset<sizeof prefix&&!separator) {
                    const auto count=std::min(sizeof prefix-offset,size_t(4096-((address+offset)&4095)));
                    SIZE_T received=0;
                    require(ReadProcessMemory(process,reinterpret_cast<void*>(address+offset),prefix+offset,count,&received)
                        &&received==count,"CRT environment prefix read");
                    for(size_t i=offset;i<offset+count;++i) {
                        const auto byte=prefix[i];
                        if(!byte||byte=='='){separator=true;break;}
                        name.push_back(byte>='a'&&byte<='z'?static_cast<char>(byte-'a'+'A'):byte);
                    }
                    offset+=count;
                }
                require(!separator||(name!=adapterVariable&&name!="QSG_RHI_PREFER_SOFTWARE_RENDERER"),"Qt adapter environment override is not movable");
            }
            require(terminated,"CRT environment vector limit");
        }
    }
public:
    // This private-layout qualification is limited to the independently inspected binary profile.
    QtD3D12Admission(HANDLE target,DWORD pid):process(target),
        quick(pid,L"Qt6Quick.dll","2454BAE3EE29FA76B3ABE0F9D6CE6FC55630A01B400892F79511ADD3EBE78601"),
        gui(pid,L"Qt6Gui.dll","428E11074A3764184F06BD437779F483F5C754C8C8DE5A6CDA94D9ED108A4789"),
        core(pid,L"Qt6Core.dll","6965720757C87788C1701B236B8157270F3EA9B7D5EAF2D1297B21803FC3B000"),
        crt(pid,L"ucrtbase.dll","5C52E3A303BAAAC0E0AF8BD9B96134993DA34BC9D834A31EF37E1D2CDC7FE192") {
        match(quick,"?graphicsDevice@QQuickWindow@@QEBA?AVQQuickGraphicsDevice@@XZ",
            {0x40,0x53,0x48,0x83,0xec,0x20,0x48,0x8b,0xda,0x48,0x8b,0x51,0x08,0x48,0x81,0xc2,0xd8,0x02,0,0});
        match(quick,"?graphicsConfiguration@QQuickWindow@@QEBA?AVQQuickGraphicsConfiguration@@XZ",
            {0x40,0x53,0x48,0x83,0xec,0x20,0x48,0x8b,0xda,0x48,0x8b,0x51,0x08,0x48,0x81,0xc2,0xe0,0x02,0,0});
        match(quick,"?isNull@QQuickGraphicsDevice@@QEBA_NXZ",{0x48,0x8b,0x01,0x83,0x78,0x04,0,0x0f,0x94,0xc0,0xc3});
        match(quick,"?prefersSoftwareDevice@QQuickGraphicsConfiguration@@QEBA_NXZ",{0x48,0x8b,0x01,0x8b,0x40,0x20,0xc1,0xe8,0x03,0x24,0x01,0xc3});
        match(quick,"?rhi@QQuickWindow@@QEBAPEAVQRhi@@XZ",{0x48,0x8b,0x41,0x08,0x48,0x8b,0x80,0x78,0x03,0,0,0xc3});
        match(quick,"?swapChain@QQuickWindow@@QEBAPEAVQRhiSwapChain@@XZ",{0x48,0x8b,0x41,0x08,0x48,0x8b,0x80,0x80,0x03,0,0,0xc3});
        beforeFrame=quick.entry(process,"?beforeFrameBegin@QQuickWindow@@QEAAXXZ");
        endFrame=gui.entry(process,"?endFrame@QRhi@@QEAA?AW4FrameOpResult@1@PEAVQRhiSwapChain@@V?$QFlags@W4EndFrameFlag@QRhi@@@@@Z");
        match(crt,"__p__environ",{0x48,0x83,0xec,0x28,0xe8,0x83,0x5f,0xf7,0xff,0x48,0x8d,0x0d,0xc0,0x64,0x0a,0,0x48,0x8d,0x04,0xc1,0x48,0x83,0xc4,0x28,0xc3});
        require(crt.entry(process,"__p__environ")==crt.remote+0x93370,"CRT environment layout");
        require(read<uint64_t>(core.remote+0x3a79e0)==crt.entry(process,"getenv_s"),"Qt environment provider identity");
        environmentSlots=crt.remote+0x139840;
    }
    std::array<uint64_t,4> table() const {return {beforeFrame,endFrame,0,0};}
    bool observe(DWORD tid,const CONTEXT& context,bool& ready) {
        ready=false;
        if(context.Rip==beforeFrame) {frames[tid]=inspect(context.Rcx);return true;}
        if(context.Rip!=endFrame) return false;
        auto it=frames.find(tid);
        if(it!=frames.end()&&it->second.rhi==context.Rcx&&it->second.chain==context.Rdx) {
            validate(tid);
            it->second.returnAddress=read<uint64_t>(context.Rsp);
            it->second.returnStack=context.Rsp+sizeof(uint64_t);
            require(it->second.returnAddress!=0,"Qt endFrame return address");
            it->second.ending=true;ready=true;
            printf("{\"kind\":\"qtDefaultDeviceObserved\",\"tid\":%lu,\"window\":%llu}\n",tid,static_cast<unsigned long long>(it->second.window));
        }
        return true;
    }
    void submitted(DWORD tid) {
        auto it=frames.find(tid);
        if(it!=frames.end()&&it->second.ending) it->second.submitted=true;
    }
    bool canPresent(DWORD tid) const {
        auto it=frames.find(tid);
        return it!=frames.end()&&it->second.ending&&it->second.submitted;
    }
    uint64_t frameReturn(DWORD tid) const {return frames.at(tid).returnAddress;}
    uint64_t window(DWORD tid) const {return frames.at(tid).window;}
    bool frameReturned(DWORD tid,const CONTEXT& context) {
        auto it=frames.find(tid);
        if(it==frames.end()||!it->second.ending||context.Rip!=it->second.returnAddress||context.Rsp!=it->second.returnStack) return false;
        frames.erase(it);return true;
    }
    void validate(DWORD tid) const {
        const auto& prior=frames.at(tid);const auto now=inspect(prior.window);
        require(now.rhi==prior.rhi&&now.chain==prior.chain,"Qt frame device changed before loss authorization");
    }
    void authorizeLoss(DWORD tid,uint64_t deadline=UINT64_MAX,const char* adapterVariable="QT_D3D_ADAPTER_INDEX") const {
        require(canPresent(tid),"Qt frame has no current submission");
        validate(tid);checkEnvironment(std::min(deadline,GetTickCount64()+250),adapterVariable);
    }
    void threadExited(DWORD tid){frames.erase(tid);}
};
}
