#pragma once

#include <windows.h>
#include <map>
#include <string>
#include <vector>

namespace ResourceManagerGpuLaunch
{
struct EnvironmentNameLess
{
    bool operator()(const std::wstring& left, const std::wstring& right) const
    {
        return CompareStringOrdinal(left.data(), static_cast<int>(left.size()),
            right.data(), static_cast<int>(right.size()), TRUE) == CSTR_LESS_THAN;
    }
};

using Environment = std::map<std::wstring, std::wstring, EnvironmentNameLess>;

inline bool ReadEnvironmentBlock(Environment& environment)
{
    wchar_t* block = GetEnvironmentStringsW();
    if (block == nullptr) return false;
    for (const wchar_t* item = block; *item != L'\0'; item += std::wcslen(item) + 1)
    {
        const std::wstring entry(item);
        // Drive-current-directory entries start with '=', for example '=C:=C:\\work'.
        const size_t separator = entry.find(L'=', 1);
        if (separator != std::wstring::npos)
            environment.emplace(entry.substr(0, separator), entry.substr(separator + 1));
    }
    FreeEnvironmentStringsW(block);
    return true;
}

inline std::vector<wchar_t> BuildEnvironmentBlock(const Environment& environment)
{
    std::vector<wchar_t> block;
    for (const auto& entry : environment)
    {
        block.insert(block.end(), entry.first.begin(), entry.first.end());
        block.push_back(L'=');
        block.insert(block.end(), entry.second.begin(), entry.second.end());
        block.push_back(L'\0');
    }
    if (block.empty()) block.push_back(L'\0');
    block.push_back(L'\0');
    return block;
}

inline void PrependEnvironmentList(Environment& environment, const wchar_t* name, const std::wstring& value)
{
    auto& previous = environment[name];
    previous = value + (previous.empty() ? L"" : L";" + previous);
}

inline Environment ConfigureChildEnvironment(
    const Environment& inherited,
    const std::wstring& policyPath,
    const std::wstring& readyEvent,
    const std::wstring& vulkanDirectory)
{
    Environment child = inherited;
    child[L"RM_GPU_IFEO_DEPTH"] = L"1";
    child.erase(L"RM_GPU_SHIM_POLICY_FILE");
    child.erase(L"RM_GPU_SHIM_READY_EVENT");
    if (!policyPath.empty()) child[L"RM_GPU_SHIM_POLICY_FILE"] = policyPath;
    if (!readyEvent.empty()) child[L"RM_GPU_SHIM_READY_EVENT"] = readyEvent;
    if (!vulkanDirectory.empty())
    {
        const wchar_t* pathVariable = child.find(L"VK_LAYER_PATH") != child.end()
            ? L"VK_LAYER_PATH" : L"VK_ADD_LAYER_PATH";
        PrependEnvironmentList(child, pathVariable, vulkanDirectory);
        PrependEnvironmentList(child, L"VK_INSTANCE_LAYERS", L"VK_LAYER_RESOURCE_MANAGER_gpu_placement");
    }
    return child;
}
}
