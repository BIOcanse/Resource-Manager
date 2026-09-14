#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>

#include <array>
#include <string>

namespace
{
std::wstring ReadEnvironment(const wchar_t* name)
{
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) return std::wstring();
    std::wstring value(required - 1, L'\0');
    GetEnvironmentVariableW(name, value.data(), required);
    return value;
}

void AppendLine(std::wstring& output, const std::wstring& key, const std::wstring& value)
{
    output += key + L"=" + value + L"\r\n";
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == nullptr) return 2;

    std::wstring output;
    AppendLine(output, L"argc", std::to_wstring(argc));
    for (int index = 0; index < argc; ++index)
    {
        AppendLine(output, L"argv" + std::to_wstring(index), argv[index]);
    }
    std::array<wchar_t, 32768> currentDirectory{};
    const DWORD currentLength = GetCurrentDirectoryW(static_cast<DWORD>(currentDirectory.size()), currentDirectory.data());
    AppendLine(output, L"cwd", currentLength > 0 && currentLength < currentDirectory.size()
        ? std::wstring(currentDirectory.data(), currentLength)
        : std::wstring());
    AppendLine(output, L"probeEnvironment", ReadEnvironment(L"RM_GPU_LAUNCH_PROBE_VALUE"));

    const std::wstring outputPath = ReadEnvironment(L"RM_GPU_LAUNCH_PROBE_OUTPUT");
    if (outputPath.empty())
    {
        LocalFree(argv);
        return 3;
    }
    HANDLE file = CreateFileW(outputPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        LocalFree(argv);
        return 4;
    }
    DWORD written = 0;
    const bool success = WriteFile(file, output.data(), static_cast<DWORD>(output.size() * sizeof(wchar_t)), &written, nullptr) != FALSE;
    CloseHandle(file);
    LocalFree(argv);
    return success ? 37 : 5;
}
