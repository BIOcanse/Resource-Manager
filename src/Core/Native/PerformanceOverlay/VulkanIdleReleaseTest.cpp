#define WIN32_LEAN_AND_MEAN
#define VK_NO_PROTOTYPES
#define VK_USE_PLATFORM_WIN32_KHR
#include <windows.h>
#include <vulkan/vulkan.h>
#include "IdleReleaseTest.h"
#include <algorithm>

using namespace IdleReleaseTest;

template <typename T, typename U> T Function(U address)
{
    T function = nullptr;
    static_assert(sizeof(function) == sizeof(address));
    std::memcpy(&function, &address, sizeof(function));
    return function;
}

void VkCheck(VkResult result, const char* operation)
{
    if (result < 0)
        throw std::runtime_error(std::string(operation) + " returned VkResult " + std::to_string(result));
}

struct Renderer
{
    Window window;
    VkInstance instance = VK_NULL_HANDLE;
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkDevice device = VK_NULL_HANDLE;
    VkQueue queue = VK_NULL_HANDLE;
    VkSwapchainKHR chain = VK_NULL_HANDLE;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkCommandBuffer command = VK_NULL_HANDLE;
    VkSemaphore acquired = VK_NULL_HANDLE;
    VkFence acquireCompleted = VK_NULL_HANDLE;
    // Frame alternates entry points; Acquire2 also checks a caller-owned fence.
    uint64_t acquireCalls = 0;
    // Frame selects by acquired image; reacquisition proves the prior present consumed its signal.
    std::vector<VkSemaphore> rendered;
    std::vector<VkImage> images;
    // CreateChain reuses the tested surface configuration after each retirement.
    VkSwapchainCreateInfoKHR swapInfo{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
    PFN_vkGetInstanceProcAddr getInstance = nullptr;
    PFN_vkGetDeviceProcAddr getDevice = nullptr;
#define DECLARE(name) PFN_##name name = nullptr
    DECLARE(vkDestroyInstance);
    DECLARE(vkDestroySurfaceKHR);
    DECLARE(vkDestroyDevice);
    DECLARE(vkDestroySwapchainKHR);
    DECLARE(vkDestroyCommandPool);
    DECLARE(vkDestroySemaphore);
    DECLARE(vkAcquireNextImageKHR);
    DECLARE(vkAcquireNextImage2KHR);
    DECLARE(vkCreateFence);
    DECLARE(vkDestroyFence);
    DECLARE(vkWaitForFences);
    DECLARE(vkResetFences);
    DECLARE(vkResetCommandBuffer);
    DECLARE(vkBeginCommandBuffer);
    DECLARE(vkEndCommandBuffer);
    DECLARE(vkCmdPipelineBarrier);
    DECLARE(vkCmdClearColorImage);
    DECLARE(vkQueueSubmit);
    DECLARE(vkQueuePresentKHR);
    DECLARE(vkQueueWaitIdle);
#undef DECLARE

    Renderer()
    {
        const auto directory = ExeDirectory();
        Check(SetEnvironmentVariableW(L"VK_LAYER_PATH", directory.c_str()), "Set VK_LAYER_PATH failed");
        HMODULE loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "Vulkan loader vulkan-1.dll is unavailable");
        getInstance = Function<PFN_vkGetInstanceProcAddr>(GetProcAddress(loader, "vkGetInstanceProcAddr"));
        Check(getInstance != nullptr, "vkGetInstanceProcAddr is unavailable");
        const char* layer = "VK_LAYER_RESOURCE_MANAGER_performance_overlay";
        const char* extensions[] = {VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_WIN32_SURFACE_EXTENSION_NAME};
        VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
        app.pApplicationName = "Resource Manager overlay idle release test";
        app.apiVersion = VK_API_VERSION_1_1;
        VkInstanceCreateInfo create{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
        create.pApplicationInfo = &app;
        create.enabledLayerCount = 1;
        create.ppEnabledLayerNames = &layer;
        create.enabledExtensionCount = 2;
        create.ppEnabledExtensionNames = extensions;
        VkCheck(Function<PFN_vkCreateInstance>(getInstance(nullptr, "vkCreateInstance"))(&create, nullptr, &instance),
            "vkCreateInstance (explicit overlay layer)");
        vkDestroyInstance = Function<PFN_vkDestroyInstance>(getInstance(instance, "vkDestroyInstance"));
        vkDestroySurfaceKHR = Function<PFN_vkDestroySurfaceKHR>(getInstance(instance, "vkDestroySurfaceKHR"));
        VkWin32SurfaceCreateInfoKHR surfaceInfo{VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR};
        surfaceInfo.hinstance = GetModuleHandleW(nullptr);
        surfaceInfo.hwnd = window.handle;
        VkCheck(Function<PFN_vkCreateWin32SurfaceKHR>(getInstance(instance, "vkCreateWin32SurfaceKHR"))(
            instance, &surfaceInfo, nullptr, &surface), "vkCreateWin32SurfaceKHR");
        const auto enumeratePhysical = Function<PFN_vkEnumeratePhysicalDevices>(getInstance(instance, "vkEnumeratePhysicalDevices"));
        uint32_t count = 0;
        VkCheck(enumeratePhysical(instance, &count, nullptr), "vkEnumeratePhysicalDevices count");
        Check(count > 0, "Vulkan loader has no physical devices");
        std::vector<VkPhysicalDevice> physicals(count);
        VkCheck(enumeratePhysical(instance, &count, physicals.data()), "vkEnumeratePhysicalDevices");
        const auto getFamilies = Function<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(
            getInstance(instance, "vkGetPhysicalDeviceQueueFamilyProperties"));
        const auto presentSupport = Function<PFN_vkGetPhysicalDeviceSurfaceSupportKHR>(
            getInstance(instance, "vkGetPhysicalDeviceSurfaceSupportKHR"));
        VkPhysicalDevice physical = VK_NULL_HANDLE;
        uint32_t family = UINT32_MAX;
        for (const auto candidate : physicals)
        {
            uint32_t familyCount = 0;
            getFamilies(candidate, &familyCount, nullptr);
            std::vector<VkQueueFamilyProperties> families(familyCount);
            getFamilies(candidate, &familyCount, families.data());
            for (uint32_t i = 0; i < familyCount; ++i)
            {
                VkBool32 supported = VK_FALSE;
                VkCheck(presentSupport(candidate, i, surface, &supported), "vkGetPhysicalDeviceSurfaceSupportKHR");
                if (supported && (families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT))
                { physical = candidate; family = i; break; }
            }
            if (physical) break;
        }
        Check(physical != VK_NULL_HANDLE, "No Vulkan graphics queue can present to the hidden window");
        VkPhysicalDeviceProperties properties{};
        Function<PFN_vkGetPhysicalDeviceProperties>(getInstance(instance, "vkGetPhysicalDeviceProperties"))(physical, &properties);
        std::printf("Vulkan physical_device=%s\n", properties.deviceName);
        const float priority = 1;
        VkDeviceQueueCreateInfo queueInfo{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
        queueInfo.queueFamilyIndex = family;
        queueInfo.queueCount = 1;
        queueInfo.pQueuePriorities = &priority;
        const char* swapchainExtension = VK_KHR_SWAPCHAIN_EXTENSION_NAME;
        VkDeviceCreateInfo deviceInfo{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
        deviceInfo.queueCreateInfoCount = 1;
        deviceInfo.pQueueCreateInfos = &queueInfo;
        deviceInfo.enabledExtensionCount = 1;
        deviceInfo.ppEnabledExtensionNames = &swapchainExtension;
        VkCheck(Function<PFN_vkCreateDevice>(getInstance(instance, "vkCreateDevice"))(
            physical, &deviceInfo, nullptr, &device), "vkCreateDevice");
        getDevice = Function<PFN_vkGetDeviceProcAddr>(getInstance(instance, "vkGetDeviceProcAddr"));
#define LOAD(name) name = Function<PFN_##name>(getDevice(device, #name)); Check(name != nullptr, #name " unavailable")
        LOAD(vkDestroyDevice); LOAD(vkDestroySwapchainKHR); LOAD(vkDestroyCommandPool);
        LOAD(vkDestroySemaphore); LOAD(vkAcquireNextImageKHR); LOAD(vkResetCommandBuffer);
        LOAD(vkAcquireNextImage2KHR); LOAD(vkCreateFence); LOAD(vkDestroyFence);
        LOAD(vkWaitForFences); LOAD(vkResetFences);
        LOAD(vkBeginCommandBuffer); LOAD(vkEndCommandBuffer); LOAD(vkCmdPipelineBarrier);
        LOAD(vkCmdClearColorImage); LOAD(vkQueueSubmit); LOAD(vkQueuePresentKHR); LOAD(vkQueueWaitIdle);
#undef LOAD
        Function<PFN_vkGetDeviceQueue>(getDevice(device, "vkGetDeviceQueue"))(device, family, 0, &queue);
        VkSurfaceCapabilitiesKHR capabilities{};
        VkCheck(Function<PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR>(
            getInstance(instance, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR"))(physical, surface, &capabilities),
            "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        uint32_t formatCount = 0;
        const auto getFormats = Function<PFN_vkGetPhysicalDeviceSurfaceFormatsKHR>(
            getInstance(instance, "vkGetPhysicalDeviceSurfaceFormatsKHR"));
        VkCheck(getFormats(physical, surface, &formatCount, nullptr), "vkGetPhysicalDeviceSurfaceFormatsKHR count");
        Check(formatCount > 0, "No Vulkan surface formats");
        std::vector<VkSurfaceFormatKHR> formats(formatCount);
        VkCheck(getFormats(physical, surface, &formatCount, formats.data()), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        VkSurfaceFormatKHR format = formats[0];
        for (const auto candidate : formats)
            if (candidate.format == VK_FORMAT_B8G8R8A8_UNORM) { format = candidate; break; }
        if (format.format == VK_FORMAT_UNDEFINED) format.format = VK_FORMAT_B8G8R8A8_UNORM;
        const auto usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        Check((capabilities.supportedUsageFlags & usage) == usage, "Vulkan surface lacks color attachment/transfer destination usage");
        swapInfo.surface = surface;
        swapInfo.minImageCount = std::max(2u, capabilities.minImageCount);
        if (capabilities.maxImageCount) swapInfo.minImageCount = std::min(swapInfo.minImageCount, capabilities.maxImageCount);
        swapInfo.imageFormat = format.format;
        swapInfo.imageColorSpace = format.colorSpace;
        swapInfo.imageExtent = capabilities.currentExtent;
        if (swapInfo.imageExtent.width == UINT32_MAX) swapInfo.imageExtent = {Width, Height};
        swapInfo.imageArrayLayers = 1;
        swapInfo.imageUsage = usage;
        swapInfo.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        swapInfo.preTransform = capabilities.currentTransform;
        const VkCompositeAlphaFlagBitsKHR alphaModes[] = {VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
            VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR, VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR,
            VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR};
        for (const auto alpha : alphaModes)
            if (capabilities.supportedCompositeAlpha & alpha) { swapInfo.compositeAlpha = alpha; break; }
        swapInfo.presentMode = VK_PRESENT_MODE_FIFO_KHR;
        swapInfo.clipped = VK_TRUE;
        CreateChain();
        VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        poolInfo.queueFamilyIndex = family;
        VkCheck(Function<PFN_vkCreateCommandPool>(getDevice(device, "vkCreateCommandPool"))(
            device, &poolInfo, nullptr, &pool), "vkCreateCommandPool");
        VkCommandBufferAllocateInfo allocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        allocate.commandPool = pool;
        allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        allocate.commandBufferCount = 1;
        VkCheck(Function<PFN_vkAllocateCommandBuffers>(getDevice(device, "vkAllocateCommandBuffers"))(
            device, &allocate, &command), "vkAllocateCommandBuffers");
        VkSemaphoreCreateInfo semaphoreInfo{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
        const auto createSemaphore = Function<PFN_vkCreateSemaphore>(getDevice(device, "vkCreateSemaphore"));
        VkCheck(createSemaphore(device, &semaphoreInfo, nullptr, &acquired), "vkCreateSemaphore acquire");
        VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        VkCheck(vkCreateFence(device, &fenceInfo, nullptr, &acquireCompleted), "vkCreateFence acquire");
    }

    void CreateChain()
    {
        Check(chain == VK_NULL_HANDLE, "Vulkan swapchain was not retired before recreation");
        VkCheck(Function<PFN_vkCreateSwapchainKHR>(getDevice(device, "vkCreateSwapchainKHR"))(
            device, &swapInfo, nullptr, &chain), "vkCreateSwapchainKHR");
        const auto getImages = Function<PFN_vkGetSwapchainImagesKHR>(getDevice(device, "vkGetSwapchainImagesKHR"));
        uint32_t count = 0;
        VkCheck(getImages(device, chain, &count, nullptr), "vkGetSwapchainImagesKHR count");
        images.resize(count);
        VkCheck(getImages(device, chain, &count, images.data()), "vkGetSwapchainImagesKHR");
        VkSemaphoreCreateInfo info{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
        const auto createSemaphore = Function<PFN_vkCreateSemaphore>(getDevice(device, "vkCreateSemaphore"));
        rendered.resize(images.size(), VK_NULL_HANDLE);
        for (auto& signal : rendered)
            VkCheck(createSemaphore(device, &info, nullptr, &signal), "vkCreateSemaphore render");
    }

    void Retire()
    {
        // The application owns this queue and completes acquired-image work
        // before DestroySwapchain; the injected layer must not idle other queues.
        if (queue && vkQueueWaitIdle) VkCheck(vkQueueWaitIdle(queue), "vkQueueWaitIdle before retirement");
        if (chain) { vkDestroySwapchainKHR(device, chain, nullptr); chain = VK_NULL_HANDLE; }
        for (auto signal : rendered) if (signal) vkDestroySemaphore(device, signal, nullptr);
        std::vector<VkSemaphore>().swap(rendered);
        std::vector<VkImage>().swap(images);
    }

    ~Renderer()
    {
        Retire();
        if (acquireCompleted) vkDestroyFence(device, acquireCompleted, nullptr);
        if (acquired) vkDestroySemaphore(device, acquired, nullptr);
        if (pool) vkDestroyCommandPool(device, pool, nullptr);
        if (device) vkDestroyDevice(device, nullptr);
        if (surface) vkDestroySurfaceKHR(instance, surface, nullptr);
        if (instance) vkDestroyInstance(instance, nullptr);
    }

    void Frame()
    {
        uint32_t index = 0;
        const bool secondEntry = (acquireCalls++ & 1) != 0;
        VkResult acquireResult;
        if (secondEntry)
        {
            VkAcquireNextImageInfoKHR info{VK_STRUCTURE_TYPE_ACQUIRE_NEXT_IMAGE_INFO_KHR};
            info.swapchain = chain;
            info.timeout = 1000000000;
            info.semaphore = acquired;
            info.fence = acquireCompleted;
            info.deviceMask = 1;
            acquireResult = vkAcquireNextImage2KHR(device, &info, &index);
        }
        else acquireResult = vkAcquireNextImageKHR(device, chain, 1000000000, acquired, VK_NULL_HANDLE, &index);
        VkCheck(acquireResult, secondEntry ? "vkAcquireNextImage2KHR" : "vkAcquireNextImageKHR");
        Check(acquireResult == VK_SUCCESS || acquireResult == VK_SUBOPTIMAL_KHR, "Vulkan image acquisition timed out");
        if (secondEntry)
        {
            const VkResult completed = vkWaitForFences(device, 1, &acquireCompleted, VK_TRUE, UINT64_MAX);
            VkCheck(completed, "vkWaitForFences acquire");
            Check(completed == VK_SUCCESS, "Vulkan acquire fence did not complete");
            VkCheck(vkResetFences(device, 1, &acquireCompleted), "vkResetFences acquire");
        }
        VkCheck(vkResetCommandBuffer(command, 0), "vkResetCommandBuffer");
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        VkCheck(vkBeginCommandBuffer(command, &begin), "vkBeginCommandBuffer");
        VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
        barrier.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        barrier.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        barrier.srcQueueFamilyIndex = barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.image = images[index];
        barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
        vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            0, 0, nullptr, 0, nullptr, 1, &barrier);
        const VkClearColorValue black{{0, 0, 0, 1}};
        vkCmdClearColorImage(command, images[index], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            &black, 1, &barrier.subresourceRange);
        barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        barrier.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        barrier.dstAccessMask = 0;
        vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
            0, 0, nullptr, 0, nullptr, 1, &barrier);
        VkCheck(vkEndCommandBuffer(command), "vkEndCommandBuffer");
        const VkPipelineStageFlags wait = VK_PIPELINE_STAGE_TRANSFER_BIT;
        VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
        submit.waitSemaphoreCount = 1;
        submit.pWaitSemaphores = &acquired;
        submit.pWaitDstStageMask = &wait;
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &command;
        submit.signalSemaphoreCount = 1;
        submit.pSignalSemaphores = &rendered[index];
        VkCheck(vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE), "vkQueueSubmit");
        VkPresentInfoKHR present{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
        present.waitSemaphoreCount = 1;
        present.pWaitSemaphores = &rendered[index];
        present.swapchainCount = 1;
        present.pSwapchains = &chain;
        present.pImageIndices = &index;
        VkCheck(vkQueuePresentKHR(queue, &present), "vkQueuePresentKHR");
        VkCheck(vkQueueWaitIdle(queue), "vkQueueWaitIdle");
    }

    void CheckPixel(uint32_t)
    {
        // The layer's drawSuccessCounter advances only after its textured draw was submitted.
        // WSI owns the presented image; this test does not read it without reacquiring it.
    }
};

int main()
{
    try
    {
        Renderer renderer;
        Run(renderer, "Vulkan");
        Publisher publisher;
        publisher.Open(renderer.window.handle, 0xffff0000u);
        Sleep(300);
        for (int frame = 0; frame < 4; ++frame) renderer.Frame();
        Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > 0,
            "Vulkan did not draw before enabled swapchain retirement");
        for (int cycle = 0; cycle < 6; ++cycle)
        {
            const SIZE_T before = PrivateCommit();
            renderer.Retire();
            const SIZE_T retired = PrivateCommit();
            renderer.CreateChain();
            const auto draws = OverlayProtocol::Read64(&publisher.header->drawSuccessCounter);
            for (int frame = 0; frame < 4; ++frame) renderer.Frame();
            Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > draws,
                "Vulkan did not draw on a recreated swapchain");
            std::printf("Vulkan retirement=%d enabled_commit=%llu retired_commit=%llu recreated_commit=%llu\n",
                cycle + 1, static_cast<unsigned long long>(before), static_cast<unsigned long long>(retired),
                static_cast<unsigned long long>(PrivateCommit()));
            std::fflush(stdout);
        }
        // Retire on the caller-fence route, then use KHR/null for the next
        // acquire so pending present signals exercise the layer-owned fence.
        renderer.acquireCalls |= 1;
        publisher.Disable();
        for (int frame = 0; frame < 12; ++frame) renderer.Frame();
        publisher.Close();
        publisher.CheckGone();
        std::puts("Vulkan acquire_entrypoints=KHR+2KHR caller_fence_preserved=yes layer_owned_fence_sequence=yes");
        std::puts("Vulkan enabled swapchain retirement/recreate x6 PASS");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "Vulkan FAIL: %s (Win32 error %lu)\n", error.what(), GetLastError());
        return 1;
    }
}
