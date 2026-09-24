#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#define wmain UnusedVulkanPlacementProbeMain
#include "VulkanPlacementProbe.cpp"
#undef wmain
#include <vulkan/vulkan_win32.h>
#include <filesystem>
#include <fstream>
#include <sstream>

namespace {
struct PresentDevice {
    VkInstance instance{}; VkPhysicalDevice physical{}; VkSurfaceKHR surface{};
    VkDevice device{}; PFN_vkGetDeviceProcAddr gdpa{}; VkQueue queue{};
    VkSwapchainKHR swapchain{}; VkCommandPool pool{}; VkCommandBuffer command{};
    VkSemaphore acquired{}, complete{}; VkFence fence{};
    std::vector<VkImage> images;
    void Clear() {
        if(!device) return;
        DP(vkDeviceWaitIdle)(device);
        if(fence) DP(vkDestroyFence)(device,fence,nullptr);
        if(acquired) DP(vkDestroySemaphore)(device,acquired,nullptr);
        if(complete) DP(vkDestroySemaphore)(device,complete,nullptr);
        if(pool) DP(vkDestroyCommandPool)(device,pool,nullptr);
        if(swapchain) DP(vkDestroySwapchainKHR)(device,swapchain,nullptr);
        DP(vkDestroyDevice)(device,nullptr);
        device={}; fence={}; acquired={}; complete={}; pool={}; swapchain={}; images.clear();
    }
    ~PresentDevice() { Clear(); }
    void Create(VkPhysicalDevice selected, HWND window) {
        physical=selected;
        const auto capabilities=CapabilitiesOf(instance,physical);
        uint32_t family=UINT32_MAX;
        for(uint32_t i=0;i<capabilities.queues.size();++i) {
            VkBool32 supported=VK_FALSE;
            Check(IP(vkGetPhysicalDeviceSurfaceSupportKHR)(physical,i,surface,&supported)==VK_SUCCESS,"surface-queue-query");
            if(supported && capabilities.queues[i].queueCount && (capabilities.queues[i].queueFlags&VK_QUEUE_GRAPHICS_BIT)) { family=i; break; }
        }
        Check(family!=UINT32_MAX,"present-capable-queue");
        float priority=1.0f; VkDeviceQueueCreateInfo q{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
        q.queueFamilyIndex=family; q.queueCount=1; q.pQueuePriorities=&priority;
        const char* extension=VK_KHR_SWAPCHAIN_EXTENSION_NAME;
        VkDeviceCreateInfo dc{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO}; dc.queueCreateInfoCount=1; dc.pQueueCreateInfos=&q;
        dc.enabledExtensionCount=1; dc.ppEnabledExtensionNames=&extension;
        Check(IP(vkCreateDevice)(physical,&dc,nullptr,&device)==VK_SUCCESS,"real-present-device");
        gdpa=IP(vkGetDeviceProcAddr); DP(vkGetDeviceQueue)(device,family,0,&queue);
        VkSurfaceCapabilitiesKHR sc{};
        Check(IP(vkGetPhysicalDeviceSurfaceCapabilitiesKHR)(physical,surface,&sc)==VK_SUCCESS,"surface-capabilities");
        Check((sc.supportedUsageFlags&VK_IMAGE_USAGE_TRANSFER_DST_BIT)!=0,"surface-clear-supported");
        uint32_t count=0;
        Check(IP(vkGetPhysicalDeviceSurfaceFormatsKHR)(physical,surface,&count,nullptr)==VK_SUCCESS&&count>0&&count<=128,"surface-format-count");
        std::vector<VkSurfaceFormatKHR> formats(count);
        Check(IP(vkGetPhysicalDeviceSurfaceFormatsKHR)(physical,surface,&count,formats.data())==VK_SUCCESS,"surface-formats");
        auto format=formats[0];
        for(const auto& f:formats) if(f.format==VK_FORMAT_B8G8R8A8_UNORM || f.format==VK_FORMAT_R8G8B8A8_UNORM) { format=f; break; }
        if(format.format==VK_FORMAT_UNDEFINED) format.format=VK_FORMAT_B8G8R8A8_UNORM;
        RECT client{}; Check(GetClientRect(window,&client)!=0,"client-area");
        VkExtent2D extent=sc.currentExtent;
        if(extent.width==UINT32_MAX) {
            extent.width=(std::max)(sc.minImageExtent.width,(std::min)(sc.maxImageExtent.width,uint32_t(client.right)));
            extent.height=(std::max)(sc.minImageExtent.height,(std::min)(sc.maxImageExtent.height,uint32_t(client.bottom)));
        }
        uint32_t imageCount=sc.minImageCount+1;
        if(sc.maxImageCount) imageCount=(std::min)(imageCount,sc.maxImageCount);
        VkCompositeAlphaFlagBitsKHR alpha=VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
        if(!(sc.supportedCompositeAlpha&alpha)) alpha=static_cast<VkCompositeAlphaFlagBitsKHR>(sc.supportedCompositeAlpha&(~sc.supportedCompositeAlpha+1));
        VkSwapchainCreateInfoKHR chain{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
        chain.surface=surface; chain.minImageCount=imageCount; chain.imageFormat=format.format; chain.imageColorSpace=format.colorSpace;
        chain.imageExtent=extent; chain.imageArrayLayers=1; chain.imageUsage=VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        chain.imageSharingMode=VK_SHARING_MODE_EXCLUSIVE; chain.preTransform=sc.currentTransform; chain.compositeAlpha=alpha;
        chain.presentMode=VK_PRESENT_MODE_FIFO_KHR; chain.clipped=VK_TRUE;
        Check(DP(vkCreateSwapchainKHR)(device,&chain,nullptr,&swapchain)==VK_SUCCESS,"swapchain");
        Check(DP(vkGetSwapchainImagesKHR)(device,swapchain,&count,nullptr)==VK_SUCCESS&&count>0&&count<=16,"swapchain-image-count");
        images.resize(count); Check(DP(vkGetSwapchainImagesKHR)(device,swapchain,&count,images.data())==VK_SUCCESS,"swapchain-images");
        VkCommandPoolCreateInfo pc{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO}; pc.queueFamilyIndex=family; pc.flags=VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        Check(DP(vkCreateCommandPool)(device,&pc,nullptr,&pool)==VK_SUCCESS,"command-pool");
        VkCommandBufferAllocateInfo ca{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO}; ca.commandPool=pool; ca.level=VK_COMMAND_BUFFER_LEVEL_PRIMARY; ca.commandBufferCount=1;
        Check(DP(vkAllocateCommandBuffers)(device,&ca,&command)==VK_SUCCESS,"command-buffer");
        VkSemaphoreCreateInfo si{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO}; VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        Check(DP(vkCreateSemaphore)(device,&si,nullptr,&acquired)==VK_SUCCESS&&DP(vkCreateSemaphore)(device,&si,nullptr,&complete)==VK_SUCCESS&&DP(vkCreateFence)(device,&fi,nullptr,&fence)==VK_SUCCESS,"sync-primitives");
    }
    void Present(uint64_t frame) {
        uint32_t image=0;
        if(DP(vkAcquireNextImageKHR)(device,swapchain,2000000000ULL,acquired,VK_NULL_HANDLE,&image)!=VK_SUCCESS) throw std::runtime_error("acquire-image");
        if(DP(vkResetCommandBuffer)(command,0)!=VK_SUCCESS) throw std::runtime_error("reset-command");
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO}; begin.flags=VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        if(DP(vkBeginCommandBuffer)(command,&begin)!=VK_SUCCESS) throw std::runtime_error("begin-command");
        VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
        barrier.oldLayout=VK_IMAGE_LAYOUT_UNDEFINED; barrier.newLayout=VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        barrier.dstAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT; barrier.srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED; barrier.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED;
        barrier.image=images.at(image); barrier.subresourceRange={VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1};
        DP(vkCmdPipelineBarrier)(command,VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,0,nullptr,0,nullptr,1,&barrier);
        VkClearColorValue color{}; color.float32[0]=frame/15%2?0.75f:0.15f; color.float32[1]=0.35f; color.float32[2]=0.15f; color.float32[3]=1.0f;
        DP(vkCmdClearColorImage)(command,images[image],VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,&color,1,&barrier.subresourceRange);
        barrier.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT; barrier.dstAccessMask=0;
        barrier.oldLayout=VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; barrier.newLayout=VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        DP(vkCmdPipelineBarrier)(command,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,0,0,nullptr,0,nullptr,1,&barrier);
        if(DP(vkEndCommandBuffer)(command)!=VK_SUCCESS) throw std::runtime_error("end-command");
        VkPipelineStageFlags stage=VK_PIPELINE_STAGE_TRANSFER_BIT;
        VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO}; submit.waitSemaphoreCount=1; submit.pWaitSemaphores=&acquired; submit.pWaitDstStageMask=&stage;
        submit.commandBufferCount=1; submit.pCommandBuffers=&command; submit.signalSemaphoreCount=1; submit.pSignalSemaphores=&complete;
        if(DP(vkQueueSubmit)(queue,1,&submit,fence)!=VK_SUCCESS || DP(vkWaitForFences)(device,1,&fence,VK_TRUE,3000000000ULL)!=VK_SUCCESS) throw std::runtime_error("submit-fence");
        VkPresentInfoKHR present{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR}; present.waitSemaphoreCount=1; present.pWaitSemaphores=&complete;
        present.swapchainCount=1; present.pSwapchains=&swapchain; present.pImageIndices=&image;
        if(DP(vkQueuePresentKHR)(queue,&present)!=VK_SUCCESS || DP(vkQueueWaitIdle)(queue)!=VK_SUCCESS || DP(vkResetFences)(device,1,&fence)!=VK_SUCCESS) throw std::runtime_error("present");
    }
};
LRESULT CALLBACK ExternalWindow(HWND h,UINT m,WPARAM w,LPARAM l) { return DefWindowProcW(h,m,w,l); }
}

int wmain(int argc,wchar_t** argv) {
    SetErrorMode(32771); setvbuf(stdout,nullptr,_IONBF,0);
    std::filesystem::path directory; bool cached=false;
    for(int i=1;i<argc;++i) {
        if(std::wstring(argv[i])==L"--external-control"&&i+1<argc) directory=argv[++i];
        else if(std::wstring(argv[i])==L"--cached-selection") cached=true;
    }
    HWND window=nullptr; VkSurfaceKHR surface{}; HMODULE loader=nullptr;
    try {
        Check(!directory.empty()&&std::filesystem::is_directory(directory)&&!std::filesystem::exists(directory/"baseline.json"),"fresh-fixture-directory");
        Check(SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)!=0,"fixture-dpi-context");
        loader=LoadLibraryExW(L"vulkan-1.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32); Check(loader!=nullptr,"system-vulkan-loader");
        auto address=GetProcAddress(loader,"vkGetInstanceProcAddr"); std::memcpy(&gipa,&address,sizeof(gipa));
        OwnedInstance owner;
        VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO}; app.apiVersion=VK_API_VERSION_1_1;
        const char* extensions[]={VK_KHR_SURFACE_EXTENSION_NAME,VK_KHR_WIN32_SURFACE_EXTENSION_NAME};
        VkInstanceCreateInfo ci{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO}; ci.pApplicationInfo=&app; ci.enabledExtensionCount=2; ci.ppEnabledExtensionNames=extensions;
        Check(I<PFN_vkCreateInstance>(nullptr,"vkCreateInstance")(&ci,nullptr,&owner.value)==VK_SUCCESS,"instance-no-explicit-layers");
        auto instance=owner.value;
        struct SurfaceOwner {
            VkInstance instance; VkSurfaceKHR& value;
            ~SurfaceOwner() { if(value) I<PFN_vkDestroySurfaceKHR>(instance,"vkDestroySurfaceKHR")(instance,value,nullptr); }
        } surfaceOwner{instance,surface};
        WNDCLASSW wc{}; wc.hInstance=GetModuleHandleW(nullptr); wc.lpfnWndProc=ExternalWindow; wc.lpszClassName=L"RmExternalVulkanFixture";
        Check(RegisterClassW(&wc)!=0,"window-class");
        window=CreateWindowExW(WS_EX_NOACTIVATE,wc.lpszClassName,L"Vulkan external selection fixture",WS_OVERLAPPEDWINDOW,40,40,640,400,nullptr,nullptr,wc.hInstance,nullptr);
        Check(window!=nullptr,"owned-window"); ShowWindow(window,SW_SHOWNOACTIVATE);
        VkWin32SurfaceCreateInfoKHR wi{VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR}; wi.hinstance=wc.hInstance; wi.hwnd=window;
        Check(IP(vkCreateWin32SurfaceKHR)(instance,&wi,nullptr,&surface)==VK_SUCCESS,"win32-surface");
        {
            auto original=Enumerate(instance); Check(original.size()>=2&&original.size()<=16,"real-adapters");
            PresentDevice renderer; renderer.instance=instance; renderer.surface=surface; renderer.Create(original[0],window);
            uint64_t frames=0,detachedAt=0; unsigned recreations=0; bool baseline=false,partial=false;
            auto publish=[&](const char* name) {
                FILETIME birth{},e{},k{},u{}; Check(GetProcessTimes(GetCurrentProcess(),&birth,&e,&k,&u)!=0,"native-birth");
                BOOL debugger=TRUE; Check(CheckRemoteDebuggerPresent(GetCurrentProcess(),&debugger)!=0,"debugger-state");
                auto list=Enumerate(instance); auto entry=IP(vkEnumeratePhysicalDevices);
                std::ostringstream json;
                json << "{\"pid\":" << GetCurrentProcessId() << ",\"birth\":\"" << ((uint64_t(birth.dwHighDateTime)<<32)|birth.dwLowDateTime)
                    << "\",\"luid\":\"" << Identity(instance,renderer.physical) << "\",\"entry\":\"" << reinterpret_cast<uintptr_t>(entry)
                    << "\",\"hwnd\":\"" << reinterpret_cast<uintptr_t>(window) << "\",\"frames\":" << frames << ",\"recreations\":" << recreations
                    << ",\"debugger\":" << (debugger?"true":"false") << ",\"cachedSelection\":" << (cached?"true":"false") << ",\"adapters\":[";
                for(size_t i=0;i<list.size();++i) {
                    if(i) json << ',';
                    json << "{\"luid\":\"" << Identity(instance,list[i]) << "\",\"handle\":\"" << reinterpret_cast<uintptr_t>(list[i]) << "\"}";
                }
                json << "]}";
                auto pending=directory/(std::string(name)+".pending");
                { std::ofstream file(pending); file << json.str(); file.flush(); Check(bool(file),"write-state"); }
                std::filesystem::rename(pending,directory/name); std::puts(json.str().c_str());
            };
            const auto end=GetTickCount64()+60000;
            while(GetTickCount64()<end) {
                MSG message{}; while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
                renderer.Present(frames++); Sleep(16);
                if(!baseline&&frames>=20) { publish("baseline.json"); baseline=true; }
                if(!partial&&std::filesystem::exists(directory/"partial.request")) {
                    uint32_t count=1; VkPhysicalDevice item{};
                    auto result=IP(vkEnumeratePhysicalDevices)(instance,&count,&item);
                    Check(result==VK_INCOMPLETE&&count==1&&item==original[0],"partial-enumeration-preserved");
                    publish("partial.json"); partial=true;
                }
                if(!recreations&&std::filesystem::exists(directory/"recreate.request")) {
                    renderer.Clear(); auto physical=cached?original[0]:Enumerate(instance)[0]; renderer.Create(physical,window);
                    ++recreations; renderer.Present(frames++); publish("recreated.json");
                }
                if(!detachedAt&&std::filesystem::exists(directory/"detach.complete")) {
                    BOOL debugger=TRUE; Check(CheckRemoteDebuggerPresent(GetCurrentProcess(),&debugger)&&!debugger,"fully-detached");
                    detachedAt=frames; publish("detached.json");
                }
                if(detachedAt&&frames-detachedAt>=120) { publish("after-detach.json"); break; }
                if(std::filesystem::exists(directory/"exit.request")) break;
            }
            Check(std::filesystem::exists(directory/"after-detach.json"),"post-detach-present-completed");
        }
        IP(vkDestroySurfaceKHR)(instance,surface,nullptr); surface={};
        Check(DestroyWindow(window)!=0,"window-released"); window=nullptr;
        std::puts("{\"kind\":\"fixtureComplete\",\"passed\":true,\"api\":\"Vulkan\"}");
        return 0;
    } catch(const std::exception& e) { fprintf(stderr,"%s\n",e.what()); if(window) DestroyWindow(window); return 1; }
}
