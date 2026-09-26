#define WIN32_LEAN_AND_MEAN
#define VK_NO_PROTOTYPES
#include <windows.h>
#include <d3d9.h>
#include <d3d11.h>
#include <d3d12.h>
#include <wrl/client.h>
#include <vulkan/vulkan_core.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <vector>

namespace {
template<typename T> T Function(HMODULE module, const char* name) {
    auto address = GetProcAddress(module, name);
    if (!address) throw std::runtime_error(name);
    T typed = nullptr;
    static_assert(sizeof(typed) == sizeof(address));
    std::memcpy(&typed, &address, sizeof(typed));
    return typed;
}
const char* preloadProvider = nullptr;
using Create9Ex = HRESULT(WINAPI*)(UINT, IDirect3D9Ex**);
Create9Ex cachedCreate9Ex = nullptr;

Microsoft::WRL::ComPtr<IDirect3DDevice9Ex> CreateDirect3D9Device(HWND window) {
    Microsoft::WRL::ComPtr<IDirect3D9Ex> factory;
    if (FAILED(cachedCreate9Ex(D3D_SDK_VERSION, &factory)) || !factory)
        throw std::runtime_error("D3D9 factory creation");
    D3DPRESENT_PARAMETERS parameters{};
    parameters.BackBufferWidth = parameters.BackBufferHeight = 16;
    parameters.BackBufferFormat = D3DFMT_A8R8G8B8;
    parameters.BackBufferCount = 1;
    parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
    parameters.hDeviceWindow = window;
    parameters.Windowed = TRUE;
    Microsoft::WRL::ComPtr<IDirect3DDevice9Ex> device;
    if (FAILED(factory->CreateDeviceEx(0, D3DDEVTYPE_HAL, window,
        D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE, &parameters, nullptr, &device)) || !device)
        throw std::runtime_error("D3D9 device creation");
    Microsoft::WRL::ComPtr<IDirect3DSurface9> target, readback;
    if (FAILED(device->GetRenderTarget(0, &target))
        || FAILED(device->Clear(0, nullptr, D3DCLEAR_TARGET, 0xff2080bf, 1, 0))
        || FAILED(device->CreateOffscreenPlainSurface(16, 16, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, nullptr))
        || FAILED(device->GetRenderTargetData(target.Get(), readback.Get())))
        throw std::runtime_error("D3D9 native pixel readback");
    D3DLOCKED_RECT data{};
    if (FAILED(readback->LockRect(&data, nullptr, D3DLOCK_READONLY))) throw std::runtime_error("D3D9 readback lock");
    bool matches = true;
    for (UINT y = 0; y < 16; ++y) for (UINT x = 0; x < 16; ++x) {
        const auto* pixel = static_cast<const unsigned char*>(data.pBits) + y * data.Pitch + x * 4;
        matches = matches && pixel[0] == 191 && pixel[1] == 128 && pixel[2] == 32 && pixel[3] == 255;
    }
    const HRESULT unlocked = readback->UnlockRect();
    if (!matches || FAILED(unlocked)) throw std::runtime_error("D3D9 readback pixels");
    return device;
}

uint64_t Direct3D9Adapter(IDirect3DDevice9Ex* device) {
    D3DDEVICE_CREATION_PARAMETERS created{};
    Microsoft::WRL::ComPtr<IDirect3D9> parent;
    Microsoft::WRL::ComPtr<IDirect3D9Ex> extended;
    LUID luid{};
    if (FAILED(device->GetCreationParameters(&created)) || FAILED(device->GetDirect3D(&parent))
        || FAILED(parent.As(&extended)) || FAILED(extended->GetAdapterLUID(created.AdapterOrdinal, &luid)))
        throw std::runtime_error("D3D9 returned device identity");
    return (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32) | luid.LowPart;
}
IUnknown* CreateDirect3DDevice(HMODULE module, bool d12) {
    IUnknown* device = nullptr;
    HRESULT result;
    if (d12) {
        ID3D12Device* created = nullptr;
        result = Function<decltype(&D3D12CreateDevice)>(module, "D3D12CreateDevice")(
            nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), reinterpret_cast<void**>(&created));
        device = created;
    } else {
        ID3D11Device* created = nullptr;
        result = Function<decltype(&D3D11CreateDevice)>(module, "D3D11CreateDevice")(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &created, nullptr, nullptr);
        device = created;
    }
    if (FAILED(result) || !device) throw std::runtime_error("Direct3D device creation");
    return device;
}
uint64_t DeviceAdapter(IUnknown* device, bool d12) {
    LUID luid{};
    if (d12) {
        ID3D12Device* typed = nullptr;
        if (FAILED(device->QueryInterface(__uuidof(ID3D12Device), reinterpret_cast<void**>(&typed))))
            throw std::runtime_error("D3D12 device identity");
        luid = typed->GetAdapterLuid();
        typed->Release();
    } else {
        IDXGIDevice* typed = nullptr;
        IDXGIAdapter* adapter = nullptr;
        DXGI_ADAPTER_DESC description{};
        if (FAILED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&typed))))
            throw std::runtime_error("D3D11 device identity");
        const auto acquired = typed->GetAdapter(&adapter);
        typed->Release();
        if (FAILED(acquired) || !adapter) throw std::runtime_error("D3D11 adapter acquisition");
        const auto described = adapter->GetDesc(&description);
        adapter->Release();
        if (FAILED(described)) throw std::runtime_error("D3D11 adapter identity");
        luid = description.AdapterLuid;
    }
    return (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32) | luid.LowPart;
}
void Ready(const char* api, HMODULE module = nullptr, uint64_t adapterLuid = 0, HWND window = nullptr) {
    HMODULE provider = preloadProvider ? LoadLibraryA(preloadProvider) : nullptr;
    if (preloadProvider && !provider) throw std::runtime_error("test provider preload");
    if (!window) window = CreateWindowExW(0, L"STATIC", L"Resource Manager owned GPU probe", WS_POPUP,
        0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) throw std::runtime_error("owned hidden window creation");
    std::printf("{\"ready\":true,\"api\":\"%s\",\"pid\":%lu,\"hwnd\":%llu,\"adapterLuid\":%llu}\n",
        api, GetCurrentProcessId(), static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(window)),
        static_cast<unsigned long long>(adapterLuid));
    std::fflush(stdout);
    bool released = false;
    for (uint32_t index = 0; index < 8; ++index) {
        char command[8]{};
        if (!std::fgets(command, sizeof(command), stdin)) break;
        if (std::strcmp(command, "x\n") == 0) { released = true; break; }
        if (std::strcmp(command, "c\n") != 0 || !module) throw std::runtime_error("unsupported parent command");
        if (std::strcmp(api, "d3d9") == 0) {
            const auto returned = CreateDirect3D9Device(window);
            std::printf("{\"created\":true,\"actualLuid\":%llu,\"verifiedPixels\":256}\n",
                static_cast<unsigned long long>(Direct3D9Adapter(returned.Get())));
            std::fflush(stdout);
            continue;
        }
        const bool d12 = std::strcmp(api, "d3d12") == 0;
        auto returned = CreateDirect3DDevice(module, d12);
        auto actual = DeviceAdapter(returned, d12);
        returned->Release();
        std::printf("{\"created\":true,\"actualLuid\":%llu}\n", static_cast<unsigned long long>(actual));
        std::fflush(stdout);
    }
    if (!released) throw std::runtime_error("missing parent release");
    if (!DestroyWindow(window)) throw std::runtime_error("owned hidden window cleanup");
    if (provider) FreeLibrary(provider);
}
void Direct3D9() {
    const auto module = LoadLibraryExW(L"d3d9.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) throw std::runtime_error("D3D9 load");
    cachedCreate9Ex = Function<Create9Ex>(module, "Direct3DCreate9Ex");
    const auto window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"RM D3D9 remote target",
        WS_POPUP, 0, 0, 16, 16, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) throw std::runtime_error("D3D9 hidden window");
    try {
        const auto device = CreateDirect3D9Device(window);
        Ready("d3d9", module, Direct3D9Adapter(device.Get()), window);
    } catch (...) { if (IsWindow(window)) DestroyWindow(window); FreeLibrary(module); throw; }
    FreeLibrary(module);
}
void Direct3D(const char* api) {
    const bool d12 = std::strcmp(api, "d3d12") == 0;
    HMODULE module = LoadLibraryExW(d12 ? L"d3d12.dll" : L"d3d11.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) throw std::runtime_error("Direct3D load");
    auto device = CreateDirect3DDevice(module, d12);
    Ready(api, module, DeviceAdapter(device, d12));
    device->Release();
    FreeLibrary(module);
}
void Vulkan() {
    auto module = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) throw std::runtime_error("Vulkan load");
    auto get = Function<PFN_vkGetInstanceProcAddr>(module, "vkGetInstanceProcAddr");
    VkInstanceCreateInfo create{};
    create.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    VkInstance instance{};
    auto createInstance = reinterpret_cast<PFN_vkCreateInstance>(get(nullptr, "vkCreateInstance"));
    if (!createInstance || createInstance(&create, nullptr, &instance) != VK_SUCCESS)
        throw std::runtime_error("Vulkan instance creation");
    auto enumerate = reinterpret_cast<PFN_vkEnumeratePhysicalDevices>(get(instance, "vkEnumeratePhysicalDevices"));
    auto queueProperties = reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(get(instance, "vkGetPhysicalDeviceQueueFamilyProperties"));
    auto createDevice = reinterpret_cast<PFN_vkCreateDevice>(get(instance, "vkCreateDevice"));
    auto destroyInstance = reinterpret_cast<PFN_vkDestroyInstance>(get(instance, "vkDestroyInstance"));
    if (!enumerate || !queueProperties || !createDevice || !destroyInstance) throw std::runtime_error("Vulkan dispatch");
    uint32_t count = 0;
    if (enumerate(instance, &count, nullptr) != VK_SUCCESS || !count || count > 64)
        throw std::runtime_error("Vulkan adapter count");
    std::vector<VkPhysicalDevice> physical(count);
    if (enumerate(instance, &count, physical.data()) != VK_SUCCESS) throw std::runtime_error("Vulkan enumeration");
    uint32_t familyCount = 0;
    queueProperties(physical[0], &familyCount, nullptr);
    if (!familyCount || familyCount > 64) throw std::runtime_error("Vulkan queue count");
    std::vector<VkQueueFamilyProperties> families(familyCount);
    queueProperties(physical[0], &familyCount, families.data());
    uint32_t family = 0;
    while (family < familyCount && !(families[family].queueFlags & VK_QUEUE_GRAPHICS_BIT)) ++family;
    if (family == familyCount) throw std::runtime_error("Vulkan graphics queue");
    float priority = 1;
    VkDeviceQueueCreateInfo queue{};
    queue.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    queue.queueFamilyIndex = family;
    queue.queueCount = 1;
    queue.pQueuePriorities = &priority;
    VkDeviceCreateInfo info{};
    info.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    info.queueCreateInfoCount = 1;
    info.pQueueCreateInfos = &queue;
    VkDevice device{};
    if (createDevice(physical[0], &info, nullptr, &device) != VK_SUCCESS) throw std::runtime_error("Vulkan device creation");
    auto destroyDevice = reinterpret_cast<PFN_vkDestroyDevice>(get(instance, "vkDestroyDevice"));
    if (!destroyDevice) throw std::runtime_error("Vulkan device cleanup entry");
    Ready("vulkan");
    destroyDevice(device, nullptr);
    destroyInstance(instance, nullptr);
    FreeLibrary(module);
}
}
int main(int argc, char** argv) {
    try {
        if (argc != 2 && argc != 3) throw std::runtime_error("Expected API and optional test provider path");
        if (argc == 3) preloadProvider = argv[2];
        if (!std::strcmp(argv[1], "d3d9")) Direct3D9();
        else if (!std::strcmp(argv[1], "vulkan")) Vulkan();
        else if (!std::strcmp(argv[1], "d3d11") || !std::strcmp(argv[1], "d3d12")) Direct3D(argv[1]);
        else throw std::runtime_error("Unsupported test API");
        std::puts("{\"cleanup\":true}");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "%s\n", error.what());
        return 1;
    }
}
