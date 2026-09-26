#pragma once
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <stdexcept>
#include <vector>

namespace ChromiumArguments {
inline std::vector<std::wstring> Parse(const std::wstring& command) {
    int count=0; auto values=CommandLineToArgvW(command.c_str(),&count);
    if(!values) throw std::runtime_error("process arguments");
    std::vector<std::wstring> result;
    try {for(int i=0;i<count;++i) result.emplace_back(values[i]);}
    catch(...) {LocalFree(values);throw;}
    LocalFree(values);return result;
}
inline bool HasType(const std::vector<std::wstring>& args) {
    for(const auto& arg:args) if(arg.rfind(L"--type=",0)==0) return true;
    return false;
}
inline bool IsCompatibleGpu(const std::vector<std::wstring>& args) {
    unsigned types=0;
    for(const auto& arg:args) {
        if(arg.rfind(L"--type=",0)==0) {if(arg!=L"--type=gpu-process")return false;++types;}
        if(arg.rfind(L"--use-angle=",0)==0 && arg!=L"--use-angle=d3d11" && arg!=L"--use-angle=default")return false;
        if(arg.rfind(L"--use-gl=",0)==0 && arg!=L"--use-gl=angle" && arg!=L"--use-gl=default")return false;
        if(arg.rfind(L"--enable-features=",0)==0) {
            size_t start=18;
            while(start<arg.size()) {
                const auto end=arg.find(L',',start);
                auto feature=arg.substr(start,end==std::wstring::npos?end:end-start);
                const auto first=feature.find_first_not_of(L" \t\r\n");
                if(first!=std::wstring::npos) feature=feature.substr(first);
                feature=feature.substr(0,feature.find_first_of(L":<. \t\r\n"));
                if(feature==L"Vulkan")return false;
                if(end==std::wstring::npos)break;
                start=end+1;
            }
        }
    }
    return types==1;
}
}
