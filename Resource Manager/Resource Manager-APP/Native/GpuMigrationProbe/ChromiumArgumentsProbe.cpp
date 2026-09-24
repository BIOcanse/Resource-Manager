#include "../GpuPlacementExternal/ChromiumArguments.h"
#include <cstdio>

int wmain() {
    SetErrorMode(32771);
    const std::vector<std::pair<std::wstring,bool>> cases{
        {L"app.exe --type=gpu-process",true},
        {L"msedge.exe --type=gpu-process --use-angle=d3d11",true},
        {L"app.exe --description=--type=gpu-process",false},
        {L"app.exe --type=gpu-process-invalid",false},
        {L"app.exe --type=gpu-process --type=renderer",false},
        {L"app.exe --type=gpu-process --type=gpu-process",false},
        {L"app.exe --type=gpu-process --use-angle=vulkan",false},
        {L"app.exe --type=gpu-process --use-angle=swiftshader",false},
        {L"app.exe --type=gpu-process --use-gl=desktop",false},
        {L"app.exe --type=gpu-process --enable-features=Vulkan.Group",false},
        {L"app.exe --type=gpu-process --enable-features=Vulkan<Trial",false},
        {L"app.exe --type=gpu-process --enable-features=Vulkan:key/value",false},
        {L"app.exe --type=gpu-process \"--enable-features=Other, Vulkan \"",false},
        {L"app.exe --type=gpu-process --enable-features=VulkanFromANGLE",true},
        {L"app.exe --type=gpu-process --use-angle=default --use-gl=angle",true}
    };
    for(const auto& test:cases)
        if(ChromiumArguments::IsCompatibleGpu(ChromiumArguments::Parse(test.first))!=test.second) return 1;
    if(ChromiumArguments::HasType(ChromiumArguments::Parse(L"app.exe --description=--type=renderer")))return 1;
    puts("{\"passed\":true,\"checks\":16}");return 0;
}
