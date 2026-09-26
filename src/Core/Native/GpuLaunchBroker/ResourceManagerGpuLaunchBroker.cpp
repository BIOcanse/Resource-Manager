#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>
#include <shellapi.h>

#include <algorithm>
#include <array>
#include <cwctype>
#include <map>
#include <string>
#include <vector>
#include "GpuLaunchEnvironment.h"

namespace
{
constexpr wchar_t BrokerArgument[] = L"--resource-manager-ifeo";
constexpr wchar_t CleanupArgument[] = L"--remove-all-resource-manager-ifeo";
constexpr wchar_t OwnerValue[] = L"ResourceManager.GpuLaunchInterception.v1";
constexpr wchar_t IfeoRoot[] = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";
constexpr wchar_t RecursionDepthEnvironmentVariable[] = L"RM_GPU_IFEO_DEPTH";
constexpr DWORD ProviderReadyTimeoutMilliseconds = 5000;
constexpr DWORD BackendPort = 9321;
constexpr DWORD MaxProtocolBytes = 64 * 1024;

struct ScopedHandle
{
    HANDLE value = nullptr;

    ScopedHandle() = default;
    explicit ScopedHandle(HANDLE handle) : value(handle) {}
    ~ScopedHandle()
    {
        if (value != nullptr && value != INVALID_HANDLE_VALUE)
        {
            CloseHandle(value);
        }
    }
    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
    ScopedHandle(ScopedHandle&& other) noexcept : value(other.value) { other.value = nullptr; }
    ScopedHandle& operator=(ScopedHandle&& other) noexcept
    {
        if (this != &other)
        {
            if (value != nullptr && value != INVALID_HANDLE_VALUE)
            {
                CloseHandle(value);
            }
            value = other.value;
            other.value = nullptr;
        }
        return *this;
    }
};

struct ScopedModule
{
    HMODULE value = nullptr;
    ~ScopedModule() { if (value != nullptr) FreeLibrary(value); }
};

struct StartupDecision
{
    bool injectDirect3D = false;
    bool enableVulkan = false;
    std::wstring status = L"backend-unavailable";
    std::wstring message;
    std::wstring policyPath;
    std::wstring softwareId;
    std::wstring processKey;
    std::wstring startupTargetGpu;
    std::wstring assignedPositionId;
    std::wstring targetAdapterName;
    std::string reportToken;
};

std::wstring Trim(std::wstring value)
{
    auto first = std::find_if_not(value.begin(), value.end(), [](wchar_t ch) { return std::iswspace(ch) != 0; });
    auto last = std::find_if_not(value.rbegin(), value.rend(), [](wchar_t ch) { return std::iswspace(ch) != 0; }).base();
    return first < last ? std::wstring(first, last) : std::wstring();
}

std::wstring ToLower(std::wstring value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) { return static_cast<wchar_t>(std::towlower(ch)); });
    return value;
}

std::wstring FullPath(const std::wstring& path)
{
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetFullPathNameW(path.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    return length > 0 && length < buffer.size() ? std::wstring(buffer.data(), length) : std::wstring();
}

std::wstring DirectoryName(const std::wstring& path)
{
    const size_t slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos)
    {
        return std::wstring();
    }
    return path.substr(0, slash);
}

std::wstring Combine(const std::wstring& left, const std::wstring& right)
{
    if (left.empty()) return right;
    if (right.empty()) return left;
    return left + (left.back() == L'\\' ? L"" : L"\\") + right;
}

bool FileExists(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool DirectoryExists(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

std::wstring ModulePath()
{
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    return length > 0 && length < buffer.size() ? std::wstring(buffer.data(), length) : std::wstring();
}

std::wstring ReadEnvironment(const wchar_t* name)
{
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0)
    {
        return std::wstring();
    }
    std::vector<wchar_t> buffer(required);
    const DWORD length = GetEnvironmentVariableW(name, buffer.data(), required);
    return length > 0 && length < required ? std::wstring(buffer.data(), length) : std::wstring();
}

std::string WideToUtf8(const std::wstring& value)
{
    if (value.empty()) return std::string();
    const int required = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (required <= 0) return std::string();
    std::string result(static_cast<size_t>(required), '\0');
    return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.c_str(), static_cast<int>(value.size()), result.data(), required, nullptr, nullptr) == required
        ? result
        : std::string();
}

std::wstring Utf8ToWide(const std::string& value)
{
    if (value.empty()) return std::wstring();
    const int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (required <= 0) return std::wstring();
    std::wstring result(static_cast<size_t>(required), L'\0');
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), required) == required
        ? result
        : std::wstring();
}

bool IsPathUnder(const std::wstring& path, const std::wstring& root)
{
    const std::wstring fullPath = ToLower(FullPath(path));
    std::wstring fullRoot = ToLower(FullPath(root));
    if (fullPath.empty() || fullRoot.empty()) return false;
    if (fullRoot.back() != L'\\') fullRoot.push_back(L'\\');
    return fullPath.rfind(fullRoot, 0) == 0;
}

std::wstring ResolvePackageRoot()
{
    const std::wstring overridePath = ReadEnvironment(L"RESOURCE_MANAGER_PACKAGE_ROOT");
    if (!overridePath.empty()) return FullPath(overridePath);

    std::wstring current = DirectoryName(ModulePath());
    for (int depth = 0; depth < 10 && !current.empty(); ++depth)
    {
        if (DirectoryExists(Combine(current, L"Config"))
            && DirectoryExists(Combine(current, L"Bin")))
        {
            return current;
        }
        const std::wstring parent = DirectoryName(current);
        if (parent == current) break;
        current = parent;
    }
    return DirectoryName(ModulePath());
}

bool ReadUtf8File(const std::wstring& path, std::string& output)
{
    ScopedHandle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.value, &size) || size.QuadPart < 0 || size.QuadPart > MaxProtocolBytes) return false;
    output.assign(static_cast<size_t>(size.QuadPart), '\0');
    DWORD read = 0;
    return output.empty() || (ReadFile(file.value, output.data(), static_cast<DWORD>(output.size()), &read, nullptr) && read == output.size());
}

std::map<std::wstring, std::wstring> ParseProtocol(const std::wstring& text)
{
    std::map<std::wstring, std::wstring> fields;
    size_t offset = 0;
    while (offset < text.size())
    {
        const size_t end = text.find(L'\n', offset);
        const std::wstring line = text.substr(offset, end == std::wstring::npos ? std::wstring::npos : end - offset);
        const size_t equals = line.find(L'=');
        if (equals != std::wstring::npos && equals > 0)
        {
            fields[Trim(line.substr(0, equals))] = Trim(line.substr(equals + 1));
        }
        if (end == std::wstring::npos) break;
        offset = end + 1;
    }
    return fields;
}

bool PostStartupRequest(const std::wstring& executablePath, const std::string& token, std::wstring& response)
{
    HINTERNET session = WinHttpOpen(L"ResourceManagerGpuLaunchBroker/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (session == nullptr) return false;
    WinHttpSetTimeouts(session, 500, 500, 1000, 2000);
    HINTERNET connection = WinHttpConnect(session, L"127.0.0.1", static_cast<INTERNET_PORT>(BackendPort), 0);
    if (connection == nullptr)
    {
        WinHttpCloseHandle(session);
        return false;
    }
    HINTERNET request = WinHttpOpenRequest(connection, L"POST", L"/api/gpu-placement/startup/resolve",
        nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0);
    if (request == nullptr)
    {
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return false;
    }

    const std::wstring tokenWide = Utf8ToWide(token);
    const std::wstring headers = L"X-Resource-Manager-Token: " + tokenWide
        + L"\r\nContent-Type: text/plain; charset=utf-8\r\nAccept: application/vnd.resource-manager.gpu-launch.v1+text\r\n";
    const std::string body = WideToUtf8(executablePath);
    bool success = !body.empty()
        && WinHttpSendRequest(request, headers.c_str(), static_cast<DWORD>(headers.size()),
            const_cast<char*>(body.data()), static_cast<DWORD>(body.size()), static_cast<DWORD>(body.size()), 0)
        && WinHttpReceiveResponse(request, nullptr);
    DWORD statusCode = 0;
    DWORD statusSize = sizeof(statusCode);
    success = success && WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_HEADER_NAME_BY_INDEX, &statusCode, &statusSize, WINHTTP_NO_HEADER_INDEX)
        && statusCode == 200;

    std::string bytes;
    while (success)
    {
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request, &available))
        {
            success = false;
            break;
        }
        if (available == 0) break;
        if (bytes.size() + available > MaxProtocolBytes)
        {
            success = false;
            break;
        }
        const size_t offset = bytes.size();
        bytes.resize(offset + available);
        DWORD read = 0;
        if (!WinHttpReadData(request, bytes.data() + offset, available, &read))
        {
            success = false;
            break;
        }
        bytes.resize(offset + read);
    }

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    response = success ? Utf8ToWide(bytes) : std::wstring();
    return success && !response.empty();
}

std::wstring SanitizeProtocolValue(std::wstring value)
{
    std::replace(value.begin(), value.end(), L'\r', L' ');
    std::replace(value.begin(), value.end(), L'\n', L' ');
    return Trim(value);
}

void AppendProtocolField(std::wstring& protocol, const wchar_t* name, const std::wstring& value)
{
    const std::wstring clean = SanitizeProtocolValue(value);
    if (!clean.empty()) protocol += name + std::wstring(L"=") + clean + L"\n";
}

bool PostStartupReport(
    const StartupDecision& decision,
    const std::wstring& executablePath,
    DWORD processId,
    const std::wstring& outcome,
    const std::wstring& message)
{
    if (decision.reportToken.empty()) return false;

    std::wstring protocol;
    protocol.reserve(1024);
    AppendProtocolField(protocol, L"executablePath", executablePath);
    AppendProtocolField(protocol, L"softwareId", decision.softwareId);
    AppendProtocolField(protocol, L"processKey", decision.processKey);
    if (processId > 0) AppendProtocolField(protocol, L"processId", std::to_wstring(processId));
    AppendProtocolField(protocol, L"outcome", outcome);
    AppendProtocolField(protocol, L"message", message);
    AppendProtocolField(protocol, L"startupTargetGpu", decision.startupTargetGpu);
    AppendProtocolField(protocol, L"assignedPositionId", decision.assignedPositionId);
    AppendProtocolField(protocol, L"targetAdapterName", decision.targetAdapterName);
    const std::string body = WideToUtf8(protocol);
    if (body.empty() || body.size() > MaxProtocolBytes) return false;

    HINTERNET session = WinHttpOpen(L"ResourceManagerGpuLaunchBroker/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (session == nullptr) return false;
    WinHttpSetTimeouts(session, 500, 500, 1000, 1000);
    HINTERNET connection = WinHttpConnect(session, L"127.0.0.1", static_cast<INTERNET_PORT>(BackendPort), 0);
    if (connection == nullptr)
    {
        WinHttpCloseHandle(session);
        return false;
    }
    HINTERNET request = WinHttpOpenRequest(connection, L"POST", L"/api/gpu-placement/startup/report",
        nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0);
    if (request == nullptr)
    {
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return false;
    }

    const std::wstring headers = L"X-Resource-Manager-Token: " + Utf8ToWide(decision.reportToken)
        + L"\r\nContent-Type: application/vnd.resource-manager.gpu-launch.v1+text; charset=utf-8\r\n";
    bool success = WinHttpSendRequest(request, headers.c_str(), static_cast<DWORD>(headers.size()),
            const_cast<char*>(body.data()), static_cast<DWORD>(body.size()), static_cast<DWORD>(body.size()), 0)
        && WinHttpReceiveResponse(request, nullptr);
    DWORD statusCode = 0;
    DWORD statusSize = sizeof(statusCode);
    success = success && WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_HEADER_NAME_BY_INDEX, &statusCode, &statusSize, WINHTTP_NO_HEADER_INDEX)
        && (statusCode == 200 || statusCode == 204);
    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    return success;
}

void ParseStartupDecision(
    const std::wstring& response,
    const std::wstring& targetPath,
    const std::wstring& packageRoot,
    StartupDecision& decision)
{
    const auto fields = ParseProtocol(response);
    auto status = fields.find(L"status");
    auto message = fields.find(L"message");
    decision.status = status == fields.end() ? L"invalid-response" : status->second;
    decision.message = message == fields.end() ? std::wstring() : message->second;
    const auto copyField = [&fields](const wchar_t* name, std::wstring& target)
    {
        const auto field = fields.find(name);
        if (field != fields.end()) target = field->second;
    };
    copyField(L"softwareId", decision.softwareId);
    copyField(L"processKey", decision.processKey);
    copyField(L"startupTargetGpu", decision.startupTargetGpu);
    copyField(L"assignedPositionId", decision.assignedPositionId);
    copyField(L"targetAdapterName", decision.targetAdapterName);
    const auto kind = fields.find(L"decision");
    if (kind == fields.end() || kind->second != L"inject") return;

    const auto version = fields.find(L"version");
    const auto providers = fields.find(L"startupProviders");
    bool direct3D = false;
    bool vulkan = false;
    bool validProviders = providers != fields.end() && !providers->second.empty();
    if (validProviders)
    {
        size_t offset = 0;
        do
        {
            const size_t end = providers->second.find(L',', offset);
            const auto provider = providers->second.substr(offset, end == std::wstring::npos ? end : end - offset);
            if (provider == L"d3d-device-create-shim" && !direct3D) direct3D = true;
            else if (provider == L"vulkan-explicit-layer" && !vulkan) vulkan = true;
            else { validProviders = false; break; }
            if (end == std::wstring::npos) break;
            offset = end + 1;
        } while (offset <= providers->second.size());
    }
    if (version == fields.end() || version->second != L"1" || !validProviders)
    {
        decision.status = L"untrusted-response";
        decision.message = L"startup resolver returned unsupported providers or protocol";
        return;
    }

    const auto responsePath = fields.find(L"executablePath");
    const auto policyPath = fields.find(L"policyPath");
    const std::wstring policyRoot = Combine(packageRoot, L"UserData\\GpuPlacement");
    if (responsePath == fields.end()
        || ToLower(FullPath(responsePath->second)) != ToLower(FullPath(targetPath))
        || policyPath == fields.end()
        || !FileExists(policyPath->second)
        || !IsPathUnder(policyPath->second, policyRoot))
    {
        decision.status = L"untrusted-response";
        decision.message = L"startup resolver returned an untrusted path";
        return;
    }

    decision.injectDirect3D = direct3D;
    decision.enableVulkan = vulkan;
    decision.policyPath = FullPath(policyPath->second);
}

StartupDecision ResolveStartupDecision(const std::wstring& targetPath, const std::wstring& packageRoot)
{
    StartupDecision decision;
    std::string token;
    if (!ReadUtf8File(Combine(packageRoot, L"Config\\Runtime\\loopback-api-token"), token))
    {
        decision.message = L"loopback token unavailable";
        return decision;
    }
    token = WideToUtf8(Trim(Utf8ToWide(token)));
    if (token.empty())
    {
        decision.message = L"loopback token empty";
        return decision;
    }
    decision.reportToken = token;

    std::wstring response;
    if (!PostStartupRequest(targetPath, token, response))
    {
        decision.message = L"startup resolver unavailable";
        return decision;
    }
    ParseStartupDecision(response, targetPath, packageRoot, decision);
    return decision;
}

std::wstring ExtractOriginalCommandLine()
{
    const std::wstring raw = GetCommandLineW();
    const size_t marker = raw.find(BrokerArgument);
    if (marker == std::wstring::npos) return std::wstring();
    size_t offset = marker + std::wcslen(BrokerArgument);
    while (offset < raw.size() && std::iswspace(raw[offset]) != 0) ++offset;
    return raw.substr(offset);
}

std::wstring QueryProcessPath(HANDLE process)
{
    std::array<wchar_t, 32768> buffer{};
    DWORD length = static_cast<DWORD>(buffer.size());
    return QueryFullProcessImageNameW(process, 0, buffer.data(), &length)
        ? std::wstring(buffer.data(), length)
        : std::wstring();
}

void CloseDebugEventHandles(DEBUG_EVENT& event)
{
    if (event.dwDebugEventCode == CREATE_PROCESS_DEBUG_EVENT)
    {
        if (event.u.CreateProcessInfo.hFile != nullptr) CloseHandle(event.u.CreateProcessInfo.hFile);
        if (event.u.CreateProcessInfo.hProcess != nullptr) CloseHandle(event.u.CreateProcessInfo.hProcess);
        if (event.u.CreateProcessInfo.hThread != nullptr) CloseHandle(event.u.CreateProcessInfo.hThread);
    }
    else if (event.dwDebugEventCode == LOAD_DLL_DEBUG_EVENT && event.u.LoadDll.hFile != nullptr)
    {
        CloseHandle(event.u.LoadDll.hFile);
    }
}

bool DetachDebugger(DWORD processId)
{
    DebugSetProcessKillOnExit(FALSE);
    if (DebugActiveProcessStop(processId)) return true;

    for (int attempt = 0; attempt < 16; ++attempt)
    {
        DEBUG_EVENT event{};
        if (WaitForDebugEvent(&event, 125))
        {
            CloseDebugEventHandles(event);
            ContinueDebugEvent(event.dwProcessId, event.dwThreadId, DBG_CONTINUE);
        }
        if (DebugActiveProcessStop(processId)) return true;
    }
    return false;
}

bool ApplyBootstrap(HANDLE process, const std::wstring& bootstrapPath, const std::wstring& providerPath)
{
    ScopedModule module{LoadLibraryW(bootstrapPath.c_str())};
    if (module.value == nullptr) return false;
    using UpdateFunction = DWORD (WINAPI*)(HANDLE, const wchar_t*, DWORD*);
    union
    {
        FARPROC raw;
        UpdateFunction typed;
    } updateAddress{};
    updateAddress.raw = GetProcAddress(module.value, "ResourceManagerGpuPlacementBootstrapUpdate");
    const auto update = updateAddress.typed;
    if (update == nullptr) return false;
    DWORD error = ERROR_SUCCESS;
    return update(process, providerPath.c_str(), &error) != 0;
}

int MirrorTargetExit(HANDLE process)
{
    WaitForSingleObject(process, INFINITE);
    DWORD exitCode = ERROR_PROCESS_ABORTED;
    return GetExitCodeProcess(process, &exitCode) ? static_cast<int>(exitCode) : static_cast<int>(ERROR_PROCESS_ABORTED);
}

bool CanUseVulkanEnvironment()
{
    HANDLE rawToken = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken)) return false;
    ScopedHandle token(rawToken);
    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    return GetTokenInformation(token.value, TokenElevation, &elevation, sizeof(elevation), &size)
        && elevation.TokenIsElevated == 0;
}

int LaunchTarget(
    const std::wstring& targetPath,
    const std::wstring& originalCommandLine,
    const std::wstring& brokerDirectory,
    const StartupDecision& decision,
    bool allowInjectionFallback)
{
    const std::wstring providerPath = Combine(brokerDirectory, L"GpuPlacementShim\\ResourceManager.GpuPlacementShim.dll");
    const std::wstring bootstrapPath = Combine(brokerDirectory, L"GpuPlacementShim\\ResourceManager.GpuPlacementBootstrap.dll");
    const std::wstring vulkanDirectory = Combine(brokerDirectory, L"GpuPlacementShim");
    ResourceManagerGpuLaunch::Environment inherited;
    if (!ResourceManagerGpuLaunch::ReadEnvironmentBlock(inherited)) return static_cast<int>(GetLastError());

    bool applyProviders = decision.injectDirect3D || decision.enableVulkan;
    std::wstring fallbackOutcome;
    std::wstring fallbackMessage;
    if ((decision.injectDirect3D && (!FileExists(providerPath) || !FileExists(bootstrapPath)))
        || (decision.enableVulkan && (!FileExists(Combine(vulkanDirectory, L"ResourceManager.VulkanPlacementLayer.dll"))
            || !FileExists(Combine(vulkanDirectory, L"ResourceManager.VulkanPlacementLayer.json"))
            || vulkanDirectory.find(L';') != std::wstring::npos
            || !CanUseVulkanEnvironment())))
    {
        applyProviders = false;
        fallbackOutcome = L"provider-unavailable-fallback";
        fallbackMessage = L"requested startup artifacts or non-elevated Vulkan environment unavailable; starting without providers";
    }

    for (int attempt = 0; attempt < 2; ++attempt)
    {
        bool inject = applyProviders && decision.injectDirect3D;
        bool vulkan = applyProviders && decision.enableVulkan;
        const std::wstring readyEventName = L"Local\\ResourceManager.GpuPlacement.Broker."
            + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(GetTickCount64());
        ScopedHandle readyEvent(inject ? CreateEventW(nullptr, TRUE, FALSE, readyEventName.c_str()) : nullptr);
        if (inject && readyEvent.value == nullptr)
        {
            applyProviders = inject = vulkan = false;
            fallbackOutcome = L"provider-unavailable-fallback";
            fallbackMessage = L"provider ready event creation failed; starting without providers";
        }
        auto environment = ResourceManagerGpuLaunch::BuildEnvironmentBlock(
            ResourceManagerGpuLaunch::ConfigureChildEnvironment(inherited,
                applyProviders ? decision.policyPath : std::wstring(),
                inject ? readyEventName : std::wstring(),
                vulkan ? vulkanDirectory : std::wstring()));

        STARTUPINFOW startup{};
        GetStartupInfoW(&startup);
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process{};
        std::vector<wchar_t> commandLine(originalCommandLine.begin(), originalCommandLine.end());
        commandLine.push_back(L'\0');
        if (!CreateProcessW(targetPath.c_str(), commandLine.data(), nullptr, nullptr, TRUE,
                CREATE_SUSPENDED | DEBUG_ONLY_THIS_PROCESS | CREATE_UNICODE_ENVIRONMENT,
                environment.data(), nullptr, &startup, &process))
        {
            const DWORD error = GetLastError();
            PostStartupReport(decision, targetPath, 0, L"launch-failed",
                L"CreateProcessW failed with Win32 error " + std::to_wstring(error));
            return static_cast<int>(error);
        }

        ScopedHandle processHandle(process.hProcess);
        ScopedHandle threadHandle(process.hThread);
        if (ToLower(FullPath(QueryProcessPath(process.hProcess))) != ToLower(FullPath(targetPath)))
        {
            PostStartupReport(decision, targetPath, process.dwProcessId, L"recursion-blocked",
                L"created process image did not match the fixed target path");
            TerminateProcess(process.hProcess, ERROR_CIRCULAR_DEPENDENCY);
            DetachDebugger(process.dwProcessId);
            WaitForSingleObject(process.hProcess, 2000);
            return ERROR_CIRCULAR_DEPENDENCY;
        }

        if (inject && !ApplyBootstrap(process.hProcess, bootstrapPath, providerPath))
        {
            PostStartupReport(decision, targetPath, process.dwProcessId, L"bootstrap-failed-fallback",
                L"startup bootstrap failed before target resume");
            const bool terminated = TerminateProcess(process.hProcess, ERROR_DLL_INIT_FAILED) != FALSE;
            const bool detached = DetachDebugger(process.dwProcessId);
            const bool exited = WaitForSingleObject(process.hProcess, 2000) == WAIT_OBJECT_0;
            if (!terminated || !detached || !exited || !allowInjectionFallback) return ERROR_DLL_INIT_FAILED;
            applyProviders = false;
            fallbackOutcome = L"bootstrap-failed-fallback";
            fallbackMessage = L"bootstrap failed before resume; target restarted once without providers";
            continue;
        }

        if (!DetachDebugger(process.dwProcessId))
        {
            PostStartupReport(decision, targetPath, process.dwProcessId, L"debugger-detach-failed",
                L"failed to detach launch debugger before target resume");
            TerminateProcess(process.hProcess, ERROR_DEBUGGER_INACTIVE);
            WaitForSingleObject(process.hProcess, 2000);
            return ERROR_DEBUGGER_INACTIVE;
        }
        if (ResumeThread(process.hThread) == static_cast<DWORD>(-1))
        {
            const DWORD error = GetLastError();
            PostStartupReport(decision, targetPath, process.dwProcessId, L"launch-failed",
                L"ResumeThread failed with Win32 error " + std::to_wstring(error));
            TerminateProcess(process.hProcess, error);
            WaitForSingleObject(process.hProcess, 2000);
            return static_cast<int>(error);
        }

        if (inject)
        {
            HANDLE waits[] = {readyEvent.value, process.hProcess};
            const DWORD wait = WaitForMultipleObjects(2, waits, FALSE, ProviderReadyTimeoutMilliseconds);
            if (wait != WAIT_OBJECT_0)
            {
                PostStartupReport(decision, targetPath, process.dwProcessId, L"provider-timeout",
                    L"Direct3D startup provider did not report ready");
                if (wait == WAIT_OBJECT_0 + 1) return MirrorTargetExit(process.hProcess);
                TerminateProcess(process.hProcess, ERROR_TIMEOUT);
                WaitForSingleObject(process.hProcess, 2000);
                return ERROR_TIMEOUT;
            }
        }

        if (!fallbackOutcome.empty())
            PostStartupReport(decision, targetPath, process.dwProcessId, fallbackOutcome, fallbackMessage);
        else if (vulkan)
            PostStartupReport(decision, targetPath, process.dwProcessId, L"startup-configured",
                inject ? L"Direct3D preload ready; Vulkan child environment configured, device selection not yet observed"
                       : L"Vulkan child environment configured, device selection not yet observed");
        else if (inject)
            PostStartupReport(decision, targetPath, process.dwProcessId, L"provider-ready",
                L"Direct3D startup provider reported ready after target resume");
        else
            PostStartupReport(decision, targetPath, process.dwProcessId, L"pass-through-started",
                decision.status + (decision.message.empty() ? L"" : L": " + decision.message));
        return MirrorTargetExit(process.hProcess);
    }
    return ERROR_DLL_INIT_FAILED;
}

std::wstring ReadRegistryString(HKEY key, const wchar_t* valueName)
{
    DWORD type = 0;
    DWORD bytes = 0;
    if (RegQueryValueExW(key, valueName, nullptr, &type, nullptr, &bytes) != ERROR_SUCCESS
        || (type != REG_SZ && type != REG_EXPAND_SZ)
        || bytes < sizeof(wchar_t))
    {
        return std::wstring();
    }
    std::vector<wchar_t> buffer(bytes / sizeof(wchar_t) + 1, L'\0');
    return RegQueryValueExW(key, valueName, nullptr, &type, reinterpret_cast<BYTE*>(buffer.data()), &bytes) == ERROR_SUCCESS
        ? std::wstring(buffer.data())
        : std::wstring();
}

std::vector<std::wstring> EnumerateSubKeys(HKEY key)
{
    std::vector<std::wstring> names;
    for (DWORD index = 0;; ++index)
    {
        std::array<wchar_t, 512> name{};
        DWORD length = static_cast<DWORD>(name.size());
        const LONG status = RegEnumKeyExW(key, index, name.data(), &length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status == ERROR_SUCCESS) names.emplace_back(name.data(), length);
    }
    return names;
}

DWORD RemoveOwnedRulesFromView(REGSAM view, int& removed)
{
    HKEY rawRoot = nullptr;
    const LONG openRootStatus = RegOpenKeyExW(
        HKEY_LOCAL_MACHINE,
        IfeoRoot,
        0,
        KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE | view,
        &rawRoot);
    if (openRootStatus == ERROR_FILE_NOT_FOUND)
    {
        return ERROR_SUCCESS;
    }
    if (openRootStatus != ERROR_SUCCESS)
    {
        return static_cast<DWORD>(openRootStatus);
    }

    DWORD firstError = ERROR_SUCCESS;
    for (const auto& imageName : EnumerateSubKeys(rawRoot))
    {
        HKEY imageReadKey = nullptr;
        if (RegOpenKeyExW(
                rawRoot,
                imageName.c_str(),
                0,
                KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE,
                &imageReadKey) != ERROR_SUCCESS)
        {
            continue;
        }

        std::vector<std::wstring> ownedRules;
        for (const auto& ruleName : EnumerateSubKeys(imageReadKey))
        {
            HKEY ruleKey = nullptr;
            if (RegOpenKeyExW(imageReadKey, ruleName.c_str(), 0, KEY_QUERY_VALUE, &ruleKey) != ERROR_SUCCESS) continue;
            const bool owned = ReadRegistryString(ruleKey, L"ResourceManagerOwner") == OwnerValue;
            RegCloseKey(ruleKey);
            if (owned) ownedRules.push_back(ruleName);
        }
        const bool parentOwned = ReadRegistryString(imageReadKey, L"ResourceManagerUseFilterOwner") == OwnerValue;
        RegCloseKey(imageReadKey);
        if (ownedRules.empty() && !parentOwned) continue;

        HKEY imageWriteKey = nullptr;
        const LONG openWriteStatus = RegOpenKeyExW(
            rawRoot,
            imageName.c_str(),
            0,
            KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE | KEY_SET_VALUE | DELETE,
            &imageWriteKey);
        if (openWriteStatus != ERROR_SUCCESS)
        {
            if (firstError == ERROR_SUCCESS) firstError = static_cast<DWORD>(openWriteStatus);
            continue;
        }

        for (const auto& ruleName : ownedRules)
        {
            const LONG deleteStatus = RegDeleteTreeW(imageWriteKey, ruleName.c_str());
            if (deleteStatus == ERROR_SUCCESS || deleteStatus == ERROR_FILE_NOT_FOUND)
            {
                ++removed;
            }
            else if (firstError == ERROR_SUCCESS)
            {
                firstError = static_cast<DWORD>(deleteStatus);
            }
        }

        bool hasFullPathRules = false;
        for (const auto& ruleName : EnumerateSubKeys(imageWriteKey))
        {
            HKEY ruleKey = nullptr;
            if (RegOpenKeyExW(imageWriteKey, ruleName.c_str(), 0, KEY_QUERY_VALUE, &ruleKey) != ERROR_SUCCESS) continue;
            hasFullPathRules = !ReadRegistryString(ruleKey, L"FilterFullPath").empty();
            RegCloseKey(ruleKey);
            if (hasFullPathRules) break;
        }
        if (parentOwned)
        {
            if (!hasFullPathRules)
            {
                const LONG status = RegDeleteValueW(imageWriteKey, L"UseFilter");
                if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND && firstError == ERROR_SUCCESS)
                {
                    firstError = static_cast<DWORD>(status);
                }
            }
            const LONG status = RegDeleteValueW(imageWriteKey, L"ResourceManagerUseFilterOwner");
            if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND && firstError == ERROR_SUCCESS)
            {
                firstError = static_cast<DWORD>(status);
            }
        }
        RegCloseKey(imageWriteKey);
    }
    RegCloseKey(rawRoot);
    return firstError;
}

int RunBroker(int argc, wchar_t** argv)
{
    if (argc >= 2 && _wcsicmp(argv[1], CleanupArgument) == 0)
    {
        int removed = 0;
        const DWORD error64 = RemoveOwnedRulesFromView(KEY_WOW64_64KEY, removed);
        const DWORD error32 = RemoveOwnedRulesFromView(KEY_WOW64_32KEY, removed);
        return static_cast<int>(error64 != ERROR_SUCCESS ? error64 : error32);
    }
    if (argc < 3 || _wcsicmp(argv[1], BrokerArgument) != 0) return ERROR_INVALID_PARAMETER;
    if (!ReadEnvironment(RecursionDepthEnvironmentVariable).empty()) return ERROR_CIRCULAR_DEPENDENCY;

    const std::wstring targetPath = FullPath(argv[2]);
    const std::wstring originalCommandLine = ExtractOriginalCommandLine();
    if (targetPath.empty() || originalCommandLine.empty() || !FileExists(targetPath)) return ERROR_FILE_NOT_FOUND;

    const std::wstring modulePath = ModulePath();
    const std::wstring packageRoot = ResolvePackageRoot();
    const StartupDecision decision = ResolveStartupDecision(targetPath, packageRoot);
    return LaunchTarget(targetPath, originalCommandLine, DirectoryName(modulePath), decision, true);
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == nullptr) return static_cast<int>(GetLastError());
    const int result = RunBroker(argc, argv);
    LocalFree(argv);
    return result;
}
