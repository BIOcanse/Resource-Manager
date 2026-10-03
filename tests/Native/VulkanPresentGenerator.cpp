// Fixed-rate Vulkan WSI presentation probe for the explicit overlay layer.
// Build: g++ -std=c++17 -O2 -static -I../../src/Core/Native/GpuPlacementShim/third_party/vulkan-headers/include VulkanPresentGenerator.cpp -o VulkanPresentGenerator.exe -lgdi32 -luser32 -lwinmm -ladvapi32
// Usage: VulkanPresentGenerator.exe <fps> <seconds> <layer-manifest-directory>
#define VK_NO_PROTOTYPES
#define VK_USE_PLATFORM_WIN32_KHR
#include <windows.h>
#include <vulkan/vulkan.h>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <algorithm>
#include <vector>
#include "OverlayTestProducer.h"

static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

static double Seconds(const LARGE_INTEGER& start, const LARGE_INTEGER& frequency)
{
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    return double(now.QuadPart - start.QuadPart) / double(frequency.QuadPart);
}

int main(int argc, char** argv)
{
    if (argc != 4) return 2;
    const double fps = std::atof(argv[1]);
    const double seconds = std::atof(argv[2]);
    if (fps <= 0 || fps > 1000 || seconds <= 0) return 2;
    SetEnvironmentVariableA("VK_LAYER_PATH", argv[3]);

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"RmVulkanPresentGenerator";
    RegisterClassW(&windowClass);
    HWND window = CreateWindowExW(0, windowClass.lpszClassName, L"Vulkan present generator",
        WS_OVERLAPPEDWINDOW, 100, 100, 640, 360, nullptr, nullptr, windowClass.hInstance, nullptr);
    ShowWindow(window, SW_SHOWNOACTIVATE);
    OverlayTestProducer producer;
    if (!producer.Start(window)) return 3;

    HMODULE loader = LoadLibraryW(L"vulkan-1.dll");
    if (!loader) return 4;
    auto gipa = reinterpret_cast<PFN_vkGetInstanceProcAddr>(GetProcAddress(loader, "vkGetInstanceProcAddr"));
    if (!gipa) return 4;
#define GLOBAL(name) auto name = reinterpret_cast<PFN_##name>(gipa(nullptr, #name))
    GLOBAL(vkCreateInstance);
    if (!vkCreateInstance) return 4;
    const char* extensions[] = { VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_WIN32_SURFACE_EXTENSION_NAME };
    const char* layer = "VK_LAYER_RESOURCE_MANAGER_performance_overlay";
    VkApplicationInfo application{ VK_STRUCTURE_TYPE_APPLICATION_INFO };
    application.apiVersion = VK_API_VERSION_1_1;
    VkInstanceCreateInfo instanceInfo{ VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    instanceInfo.pApplicationInfo = &application;
    instanceInfo.enabledLayerCount = 1;
    instanceInfo.ppEnabledLayerNames = &layer;
    instanceInfo.enabledExtensionCount = 2;
    instanceInfo.ppEnabledExtensionNames = extensions;
    VkInstance instance = VK_NULL_HANDLE;
    VkResult result = vkCreateInstance(&instanceInfo, nullptr, &instance);
    if (result != VK_SUCCESS) { std::fprintf(stderr, "vkCreateInstance=%d\n", result); return 5; }
#define INSTANCE(name) auto name = reinterpret_cast<PFN_##name>(gipa(instance, #name))
    INSTANCE(vkCreateWin32SurfaceKHR);
    INSTANCE(vkEnumeratePhysicalDevices);
    INSTANCE(vkGetPhysicalDeviceQueueFamilyProperties);
    INSTANCE(vkGetPhysicalDeviceSurfaceSupportKHR);
    INSTANCE(vkGetPhysicalDeviceSurfaceCapabilitiesKHR);
    INSTANCE(vkGetPhysicalDeviceSurfaceFormatsKHR);
    INSTANCE(vkCreateDevice);
    INSTANCE(vkGetDeviceProcAddr);
    INSTANCE(vkDestroySurfaceKHR);
    INSTANCE(vkDestroyInstance);
    if (!vkCreateWin32SurfaceKHR || !vkCreateDevice || !vkGetDeviceProcAddr) return 5;
    VkWin32SurfaceCreateInfoKHR surfaceInfo{ VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR };
    surfaceInfo.hinstance = windowClass.hInstance;
    surfaceInfo.hwnd = window;
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    if (vkCreateWin32SurfaceKHR(instance, &surfaceInfo, nullptr, &surface) != VK_SUCCESS) return 6;
    uint32_t physicalCount = 0;
    vkEnumeratePhysicalDevices(instance, &physicalCount, nullptr);
    if (!physicalCount) return 6;
    std::vector<VkPhysicalDevice> physicals(physicalCount);
    vkEnumeratePhysicalDevices(instance, &physicalCount, physicals.data());
    VkPhysicalDevice physical = VK_NULL_HANDLE;
    uint32_t queueFamily = 0;
    for (VkPhysicalDevice candidate : physicals)
    {
        uint32_t count = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(candidate, &count, nullptr);
        std::vector<VkQueueFamilyProperties> families(count);
        vkGetPhysicalDeviceQueueFamilyProperties(candidate, &count, families.data());
        for (uint32_t index = 0; index < count; ++index)
        {
            VkBool32 supported = VK_FALSE;
            vkGetPhysicalDeviceSurfaceSupportKHR(candidate, index, surface, &supported);
            if ((families[index].queueFlags & VK_QUEUE_GRAPHICS_BIT) && supported)
            { physical = candidate; queueFamily = index; break; }
        }
        if (physical) break;
    }
    if (!physical) return 6;
    VkSurfaceCapabilitiesKHR capabilities{};
    if (vkGetPhysicalDeviceSurfaceCapabilitiesKHR(physical, surface, &capabilities) != VK_SUCCESS) return 6;
    uint32_t formatCount = 0;
    vkGetPhysicalDeviceSurfaceFormatsKHR(physical, surface, &formatCount, nullptr);
    if (!formatCount) return 6;
    std::vector<VkSurfaceFormatKHR> formats(formatCount);
    vkGetPhysicalDeviceSurfaceFormatsKHR(physical, surface, &formatCount, formats.data());
    VkSurfaceFormatKHR format = formats[0];
    for (const auto& candidate : formats)
        if (candidate.format == VK_FORMAT_B8G8R8A8_UNORM) { format = candidate; break; }
    const float priority = 1.0f;
    VkDeviceQueueCreateInfo queueInfo{ VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
    queueInfo.queueFamilyIndex = queueFamily;
    queueInfo.queueCount = 1;
    queueInfo.pQueuePriorities = &priority;
    const char* swapchainExtension = VK_KHR_SWAPCHAIN_EXTENSION_NAME;
    VkDeviceCreateInfo deviceInfo{ VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
    deviceInfo.queueCreateInfoCount = 1;
    deviceInfo.pQueueCreateInfos = &queueInfo;
    deviceInfo.enabledExtensionCount = 1;
    deviceInfo.ppEnabledExtensionNames = &swapchainExtension;
    VkDevice device = VK_NULL_HANDLE;
    result = vkCreateDevice(physical, &deviceInfo, nullptr, &device);
    if (result != VK_SUCCESS) { std::fprintf(stderr, "vkCreateDevice=%d\n", result); return 7; }
#define DEVICE(name) auto name = reinterpret_cast<PFN_##name>(vkGetDeviceProcAddr(device, #name))
    DEVICE(vkGetDeviceQueue);
    DEVICE(vkCreateSwapchainKHR);
    DEVICE(vkGetSwapchainImagesKHR);
    DEVICE(vkCreateCommandPool);
    DEVICE(vkAllocateCommandBuffers);
    DEVICE(vkCreateFence);
    DEVICE(vkDestroyFence);
    DEVICE(vkCreateSemaphore);
    DEVICE(vkDestroySemaphore);
    DEVICE(vkDestroyCommandPool);
    DEVICE(vkDestroySwapchainKHR);
    DEVICE(vkDestroyDevice);
    DEVICE(vkWaitForFences);
    DEVICE(vkResetFences);
    DEVICE(vkAcquireNextImageKHR);
    DEVICE(vkResetCommandBuffer);
    DEVICE(vkBeginCommandBuffer);
    DEVICE(vkCmdPipelineBarrier);
    DEVICE(vkEndCommandBuffer);
    DEVICE(vkQueueSubmit);
    DEVICE(vkQueueWaitIdle);
    DEVICE(vkQueuePresentKHR);
    if (!vkCreateSwapchainKHR || !vkQueuePresentKHR) return 7;
    VkQueue queue = VK_NULL_HANDLE;
    vkGetDeviceQueue(device, queueFamily, 0, &queue);
    RECT bounds{};
    GetClientRect(window, &bounds);
    VkExtent2D extent{ static_cast<uint32_t>(bounds.right), static_cast<uint32_t>(bounds.bottom) };
    if (capabilities.currentExtent.width != UINT32_MAX) extent = capabilities.currentExtent;
    uint32_t imageCount = (std::max)(2u, capabilities.minImageCount);
    if (capabilities.maxImageCount) imageCount = (std::min)(imageCount, capabilities.maxImageCount);
    VkSwapchainCreateInfoKHR swapInfo{ VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR };
    swapInfo.surface = surface;
    swapInfo.minImageCount = imageCount;
    swapInfo.imageFormat = format.format;
    swapInfo.imageColorSpace = format.colorSpace;
    swapInfo.imageExtent = extent;
    swapInfo.imageArrayLayers = 1;
    swapInfo.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    swapInfo.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    swapInfo.preTransform = capabilities.currentTransform;
    swapInfo.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    swapInfo.presentMode = VK_PRESENT_MODE_FIFO_KHR;
    swapInfo.clipped = VK_TRUE;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    result = vkCreateSwapchainKHR(device, &swapInfo, nullptr, &swapchain);
    if (result != VK_SUCCESS) { std::fprintf(stderr, "vkCreateSwapchainKHR=%d\n", result); return 8; }
    vkGetSwapchainImagesKHR(device, swapchain, &imageCount, nullptr);
    std::vector<VkImage> images(imageCount);
    vkGetSwapchainImagesKHR(device, swapchain, &imageCount, images.data());
    VkCommandPoolCreateInfo poolInfo{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    poolInfo.queueFamilyIndex = queueFamily;
    VkCommandPool pool = VK_NULL_HANDLE;
    if (vkCreateCommandPool(device, &poolInfo, nullptr, &pool) != VK_SUCCESS) return 8;
    VkCommandBufferAllocateInfo allocate{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    allocate.commandPool = pool;
    allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocate.commandBufferCount = 1;
    VkCommandBuffer command = VK_NULL_HANDLE;
    if (vkAllocateCommandBuffers(device, &allocate, &command) != VK_SUCCESS) return 8;
    VkFenceCreateInfo fenceInfo{ VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    VkFence acquireFence = VK_NULL_HANDLE;
    if (vkCreateFence(device, &fenceInfo, nullptr, &acquireFence) != VK_SUCCESS) return 8;
    VkSemaphoreCreateInfo semaphoreInfo{ VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    VkSemaphore drawReady = VK_NULL_HANDLE;
    if (vkCreateSemaphore(device, &semaphoreInfo, nullptr, &drawReady) != VK_SUCCESS) return 8;

    LARGE_INTEGER frequency{}, start{};
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&start);
    long long frame = 0;
    bool changed = false, disabled = false;
    timeBeginPeriod(1);
    for (;;)
    {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        double elapsed = Seconds(start, frequency);
        if (elapsed >= seconds) break;
        const double due = frame / fps;
        while (elapsed < due)
        {
            if (due - elapsed > 0.002) Sleep(1);
            elapsed = Seconds(start, frequency);
        }
        if (!changed && elapsed >= seconds / 2) { producer.ChangeBitmap(); changed = true; }
        if (!disabled && elapsed >= seconds * 0.75) { producer.Disable(); disabled = true; }
        uint32_t imageIndex = 0;
        result = vkAcquireNextImageKHR(device, swapchain, UINT64_MAX, VK_NULL_HANDLE, acquireFence, &imageIndex);
        if (result != VK_SUCCESS && result != VK_SUBOPTIMAL_KHR) return 9;
        if (vkWaitForFences(device, 1, &acquireFence, VK_TRUE, UINT64_MAX) != VK_SUCCESS) return 9;
        vkResetFences(device, 1, &acquireFence);
        vkResetCommandBuffer(command, 0);
        VkCommandBufferBeginInfo begin{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        vkBeginCommandBuffer(command, &begin);
        VkImageMemoryBarrier barrier{ VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
        barrier.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        barrier.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.image = images[imageIndex];
        barrier.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        barrier.subresourceRange.levelCount = 1;
        barrier.subresourceRange.layerCount = 1;
        vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
            VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
        vkEndCommandBuffer(command);
        VkSubmitInfo submit{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &command;
        submit.signalSemaphoreCount = 1;
        submit.pSignalSemaphores = &drawReady;
        if (vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE) != VK_SUCCESS) return 9;
        VkPresentInfoKHR present{ VK_STRUCTURE_TYPE_PRESENT_INFO_KHR };
        present.waitSemaphoreCount = 1;
        present.pWaitSemaphores = &drawReady;
        present.swapchainCount = 1;
        present.pSwapchains = &swapchain;
        present.pImageIndices = &imageIndex;
        result = vkQueuePresentKHR(queue, &present);
        if (result != VK_SUCCESS && result != VK_SUBOPTIMAL_KHR) return 9;
        if (vkQueueWaitIdle(queue) != VK_SUCCESS) return 9;
        ++frame;
    }
    timeEndPeriod(1);
    std::printf("%lld\n", frame);
    const bool passed = std::fabs(double(frame) - fps * seconds) <= 2.0 && producer.Check(frame);
    vkDestroySemaphore(device, drawReady, nullptr);
    vkDestroyFence(device, acquireFence, nullptr);
    vkDestroyCommandPool(device, pool, nullptr);
    vkDestroySwapchainKHR(device, swapchain, nullptr);
    vkDestroyDevice(device, nullptr);
    vkDestroySurfaceKHR(instance, surface, nullptr);
    vkDestroyInstance(instance, nullptr);
    DestroyWindow(window);
    FreeLibrary(loader);
    return passed ? 0 : 10;
}
