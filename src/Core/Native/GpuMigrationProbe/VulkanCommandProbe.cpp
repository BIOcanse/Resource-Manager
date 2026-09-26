#define wmain RetainedVulkanStartupProbeMain
#include "VulkanPlacementProbe.cpp"
#undef wmain
#include "VulkanProbeReadback.h"

static void PrintReadback(const char* name, const void* mapped)
{
    std::printf(",\"%s\":\"", name);
    const auto bytes = static_cast<const unsigned char*>(mapped);
    for (uint32_t i = 0; i < 4096; ++i) std::printf("%02x", bytes[i]);
    std::printf("\"");
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try
    {
        Check((argc == 2 || argc == 3 || argc == 4) && std::wcscmp(argv[1], L"vulkan") == 0, "explicit-vulkan-command-target");
        const bool layerEnabled = argc >= 3;
        HMODULE idleD3d11 = nullptr;
        if (argc == 4)
        {
            Check(std::wcscmp(argv[3], L"idle-d3d11") == 0, "explicit-idle-d3d11-candidate");
            idleD3d11 = LoadLibraryExW(L"d3d11.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            Check(idleD3d11 != nullptr, "owned-idle-d3d11-library");
        }
        if (layerEnabled)
        {
            const std::wstring path = argv[2];
            const auto separator = path.find_last_of(L"\\/");
            Check(separator != std::wstring::npos, "absolute-layer-directory");
            Check(SetEnvironmentVariableW(L"VK_LAYER_PATH", path.substr(0, separator).c_str()), "owned-layer-search");
        }
        const auto loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "system-loader");
        const auto proc = GetProcAddress(loader, "vkGetInstanceProcAddr");
        std::memcpy(&gipa, &proc, sizeof(gipa));
        Check(gipa != nullptr, "loader-entry");
        {
            OwnedInstance active;
            Check(Create(layerEnabled, VK_API_VERSION_1_1, &active.value, true) == VK_SUCCESS, "original-instance-created");
            const auto instance = active.value;
            const auto enumerate = IP(vkEnumeratePhysicalDevices);
            const auto original = Enumerate(instance);
            Check(original.size() >= 2, "two-real-adapters");
            const auto a = Identity(instance, original[0]);
            const auto b = Identity(instance, original[1]);
            Check(a && b && a != b, "distinct-adapter-identities");
            auto old = GpuReadbackCore(instance, original[0], CapabilitiesOf(instance, original[0]), nullptr);
            const auto window = CreateWindowExW(0, L"STATIC", L"Owned Vulkan command probe", WS_POPUP,
                0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
            Check(window != nullptr, "owned-hidden-window");
            std::printf("{\"ready\":true,\"api\":\"vulkan\",\"pid\":%lu,\"hwnd\":%llu,\"adapterLuid\":%llu,\"alternateLuid\":%llu,\"verifiedWords\":1024",
                GetCurrentProcessId(), static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(window)),
                static_cast<unsigned long long>(a), static_cast<unsigned long long>(b));
            PrintReadback("rawReadbackHex", old->mapped);
            std::printf("}\n");
            std::fflush(stdout);
            bool released = false;
            for (uint32_t i = 0; i < 8; ++i)
            {
                char command[8]{};
                if (!std::fgets(command, sizeof(command), stdin)) break;
                if (!std::strcmp(command, "x\n")) { released = true; break; }
                Check(!std::strcmp(command, "c\n"), "explicit-create-command");
                uint32_t count = 0;
                Check(enumerate(instance, &count, nullptr) == VK_SUCCESS && count > 0, "saved-entry-current-count");
                std::vector<VkPhysicalDevice> physical(count);
                Check(enumerate(instance, &count, physical.data()) == VK_SUCCESS && count > 0, "saved-entry-current-handles");
                const auto actual = Identity(instance, physical[0]);
                auto created = GpuReadbackCore(instance, physical[0], CapabilitiesOf(instance, physical[0]), nullptr);
                RepeatReadback(*old, a);
                std::printf("{\"created\":true,\"actualLuid\":%llu,\"verifiedWords\":1024,\"oldDeviceWords\":1024",
                    static_cast<unsigned long long>(actual));
                PrintReadback("rawReadbackHex", created->mapped);
                PrintReadback("rawOldReadbackHex", old->mapped);
                std::printf("}\n");
                std::fflush(stdout);
            }
            Check(released, "parent-requested-normal-exit");
            Check(DestroyWindow(window), "owned-window-destroyed");
        }
        Check(FreeLibrary(loader), "caller-loader-reference-released");
        if (idleD3d11) Check(FreeLibrary(idleD3d11), "caller-idle-d3d11-reference-released");
        std::printf("{\"cleanup\":true,\"checks\":%u}\n", checks);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"error\":\"%s\"}\n", error.what());
        return 1;
    }
}
