#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <wincrypt.h>

#include <algorithm>
#include <array>
#include <climits>
#include <cwctype>
#include <map>
#include <set>
#include <string>
#include <vector>
#include "GpuLaunchEnvironment.h"
#include "GpuLaunchSessionClient.h"

namespace
{
constexpr wchar_t BrokerArgument[] = L"--resource-manager-ifeo";
constexpr wchar_t CleanupArgument[] = L"--remove-all-resource-manager-ifeo";
constexpr wchar_t OwnerValue[] = L"ResourceManager.GpuLaunchInterception.v1";
constexpr wchar_t IfeoRoot[] = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";
constexpr DWORD ProviderReadyTimeoutMilliseconds = 5000;
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

struct StartupModule
{
    std::wstring id, kind, imagePath, manifestPath, layerName;
};

struct StartupDecision
{
    bool injectDirect3D = false;
    bool enableVulkan = false;
    std::vector<StartupModule> modules;
    std::vector<ScopedHandle> admittedFiles;
    std::wstring status = L"backend-unavailable";
    std::wstring message;
    std::wstring policyPath;
    std::wstring softwareId;
    std::wstring processKey;
    std::wstring startupTargetGpu;
    std::wstring assignedPositionId;
    std::wstring targetAdapterName;
    ResourceManagerGpuLaunchSession::BackendIdentity backend;
    std::wstring backendPath;
    std::wstring trustedAccessPackageRoot;
    std::wstring testPipeName;
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
        if (FileExists(Combine(current, L"resource-manager-machine-installation.json"))
            || (FileExists(Combine(current, L"resource-manager-final-image.manifest.json"))
                && DirectoryExists(Combine(current, L"UserData\\Security")))
            || (DirectoryExists(Combine(current, L"Config")) && DirectoryExists(Combine(current, L"Bin"))))
        {
            return current;
        }
        const std::wstring parent = DirectoryName(current);
        if (parent == current) break;
        current = parent;
    }
    return DirectoryName(ModulePath());
}

std::wstring ResolveRuntimeCatalogRoot()
{
    auto current = DirectoryName(ModulePath());
    for (int depth = 0; depth < 10 && !current.empty(); ++depth)
    {
        if (FileExists(Combine(current, L"release-manifest.json"))
            || FileExists(Combine(current, L"resource-manager-final-image.manifest.json"))) return current;
        const auto parent = DirectoryName(current);
        if (parent == current) break;
        current = parent;
    }
    return DirectoryName(ModulePath());
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
            if (!fields.emplace(Trim(line.substr(0, equals)), Trim(line.substr(equals + 1))).second) return {};
        }
        if (end == std::wstring::npos) break;
        offset = end + 1;
    }
    return fields;
}

bool PostStartupRequest(const std::wstring& executablePath, StartupDecision& decision, std::wstring& response)
{
    const std::string body = WideToUtf8(executablePath);
    std::string bytes;
    const bool success = ResourceManagerGpuLaunchSession::Call(ResourceManagerGpuLaunchSession::ResolveOperation,
        body, decision.backendPath, decision.trustedAccessPackageRoot, decision.testPipeName,
        decision.backend, false, bytes);
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
    if (!decision.backend.processId || !decision.backend.creation) return false;

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

    std::string reply;
    auto backend = decision.backend;
    return ResourceManagerGpuLaunchSession::Call(ResourceManagerGpuLaunchSession::ReportOperation,
        body, decision.backendPath, decision.trustedAccessPackageRoot, decision.testPipeName,
        backend, true, reply) && reply == "ok";
}

bool ParseUnsigned(const std::wstring& text, unsigned long long& value)
{
    if (text.empty() || text.size() > 20) return false;
    value = 0;
    for (wchar_t ch : text)
    {
        if (ch < L'0' || ch > L'9' || value > (ULLONG_MAX - (ch - L'0')) / 10) return false;
        value = value * 10 + (ch - L'0');
    }
    return true;
}

bool ReadAdmittedAsset(const std::map<std::wstring, std::wstring>& fields,
    const std::wstring& prefix, const std::wstring& root, std::wstring& path,
    std::vector<ScopedHandle>& handles)
{
    const auto relative = fields.find(prefix + L"path");
    const auto length = fields.find(prefix + L"length");
    const auto sha256 = fields.find(prefix + L"sha256");
    unsigned long long expected = 0;
    if (relative == fields.end() || length == fields.end() || sha256 == fields.end()
        || relative->second.empty() || relative->second.size() > 1024 || sha256->second.size() != 64
        || relative->second.front() == L'/' || relative->second.find(L'\\') != std::wstring::npos
        || relative->second.find_first_of(L":;\r\n") != std::wstring::npos || !ParseUnsigned(length->second, expected)
        || expected > 512ULL * 1024 * 1024) return false;
    size_t offset = 0;
    while (offset <= relative->second.size())
    {
        const auto end = relative->second.find(L'/', offset);
        const auto part = relative->second.substr(offset, end == std::wstring::npos ? end : end - offset);
        if (part.empty() || part == L"." || part == L"..") return false;
        if (end == std::wstring::npos) break;
        offset = end + 1;
    }
    path = FullPath(Combine(root, relative->second));
    if (!IsPathUnder(path, root) || !ResourceManagerGpuLaunchSession::NoReparseComponents(path, false)) return false;
    ScopedHandle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    BY_HANDLE_FILE_INFORMATION info{};
    if (file.value == INVALID_HANDLE_VALUE || !GetFileInformationByHandle(file.value, &info)
        || (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0
        || ((static_cast<unsigned long long>(info.nFileSizeHigh) << 32) | info.nFileSizeLow) != expected) return false;
    HCRYPTPROV provider = 0;
    HCRYPTHASH hash = 0;
    bool valid = CryptAcquireContextW(&provider, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT)
        && CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash);
    std::array<BYTE, 65536> buffer{};
    DWORD bytes = 0;
    unsigned long long read = 0;
    while (valid && read < expected)
    {
        if (!ReadFile(file.value, buffer.data(), static_cast<DWORD>(std::min<unsigned long long>(buffer.size(), expected - read)), &bytes, nullptr)
            || bytes == 0 || !CryptHashData(hash, buffer.data(), bytes, 0)) { valid = false; break; }
        read += bytes;
    }
    std::array<BYTE, 32> digest{};
    DWORD size = static_cast<DWORD>(digest.size());
    valid = valid && CryptGetHashParam(hash, HP_HASHVAL, digest.data(), &size, 0) && size == digest.size();
    if (hash) CryptDestroyHash(hash);
    if (provider) CryptReleaseContext(provider, 0);
    constexpr wchar_t hex[] = L"0123456789abcdef";
    std::wstring actual;
    for (BYTE byte : digest) { actual += hex[byte >> 4]; actual += hex[byte & 15]; }
    if (!valid || actual != ToLower(sha256->second)
        || !ResourceManagerGpuLaunchSession::NoReparseComponents(path, false)) return false;
    handles.push_back(std::move(file));
    return true;
}

bool ParseStartupModules(const std::map<std::wstring, std::wstring>& fields, const std::wstring& root,
    std::vector<StartupModule>& modules, std::vector<ScopedHandle>& handles)
{
    const auto count = fields.find(L"moduleCount");
    unsigned long long length = 0;
    if (count == fields.end() || !ParseUnsigned(count->second, length) || length > 16) return false;
    std::set<std::wstring> ids, images;
    std::set<std::wstring> allowed{L"version", L"decision", L"startupProviders", L"moduleCount", L"moduleRoot", L"status", L"message",
        L"executablePath", L"softwareId", L"processKey", L"startupTargetGpu", L"assignedPositionId", L"policyPath", L"targetAdapterName", L"targetLuid"};
    for (unsigned long long index = 0; index < length; ++index)
    {
        const auto prefix = L"module" + std::to_wstring(index) + L".";
        for (const auto suffix : {L"id", L"kind", L"image.path", L"image.length", L"image.sha256"}) allowed.insert(prefix + suffix);
        const auto id = fields.find(prefix + L"id");
        const auto kind = fields.find(prefix + L"kind");
        if (id == fields.end() || kind == fields.end() || id->second.empty() || id->second.size() > 128
            || !std::all_of(id->second.begin(), id->second.end(), [](wchar_t ch) {
                return (ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') || (ch >= L'0' && ch <= L'9') || ch == L'-' || ch == L'_' || ch == L'.'; })
            || !ids.insert(id->second).second) return false;
        StartupModule module;
        module.id = id->second;
        module.kind = kind->second;
        if (!ReadAdmittedAsset(fields, prefix + L"image.", root, module.imagePath, handles)
            || !images.insert(ToLower(module.imagePath)).second) return false;
        if (module.kind == L"vulkan-layer")
        {
            for (const auto suffix : {L"layerName", L"manifest.path", L"manifest.length", L"manifest.sha256"}) allowed.insert(prefix + suffix);
            const auto layer = fields.find(prefix + L"layerName");
            if (layer == fields.end() || layer->second.empty() || layer->second.size() > 256
                || !std::all_of(layer->second.begin(), layer->second.end(), [](wchar_t ch) {
                    return (ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') || (ch >= L'0' && ch <= L'9') || ch == L'_'; })
                || !ReadAdmittedAsset(fields, prefix + L"manifest.", root, module.manifestPath, handles)) return false;
            module.layerName = layer->second;
        }
        else if (module.kind != L"preload" || fields.count(prefix + L"manifest.path") || fields.count(prefix + L"layerName")) return false;
        modules.push_back(std::move(module));
    }
    return std::all_of(fields.begin(), fields.end(), [&allowed](const auto& field) { return allowed.count(field.first) != 0; });
}

void ParseStartupDecision(
    const std::wstring& response,
    const std::wstring& targetPath,
    const std::wstring& packageRoot,
    StartupDecision& decision, const std::wstring& runtimeRoot = std::wstring())
{
    decision.injectDirect3D = decision.enableVulkan = false;
    decision.modules.clear();
    decision.admittedFiles.clear();
    const auto fields = ParseProtocol(response);
    if (fields.empty())
    {
        decision.status = L"untrusted-response";
        decision.message = L"startup resolver returned an invalid protocol";
        return;
    }
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
    std::vector<StartupModule> modules;
    std::vector<ScopedHandle> admittedFiles;
    const auto moduleRoot = fields.find(L"moduleRoot");
    const auto expectedModuleRoot = runtimeRoot.empty() ? packageRoot : runtimeRoot;
    const bool rootValid = moduleRoot == fields.end() || ToLower(FullPath(moduleRoot->second)) == ToLower(FullPath(expectedModuleRoot));
    const bool validModules = rootValid && ParseStartupModules(fields, expectedModuleRoot, modules, admittedFiles);
    bool direct3D = false;
    bool vulkan = false;
    bool validProviders = providers == fields.end() || !providers->second.empty();
    if (validProviders && providers != fields.end())
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
    if (version == fields.end() || version->second != L"2" || !validProviders || !validModules
        || (!direct3D && !vulkan && modules.empty()))
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
        || ((direct3D || vulkan) && (policyPath == fields.end()
            || !FileExists(policyPath->second)
            || !IsPathUnder(policyPath->second, policyRoot))))
    {
        decision.status = L"untrusted-response";
        decision.message = L"startup resolver returned an untrusted path";
        return;
    }

    decision.injectDirect3D = direct3D;
    decision.enableVulkan = vulkan;
    decision.modules = std::move(modules);
    decision.admittedFiles = std::move(admittedFiles);
    if (direct3D || vulkan) decision.policyPath = FullPath(policyPath->second);
}

StartupDecision ResolveStartupDecision(const std::wstring& targetPath, const std::wstring& packageRoot,
    const std::wstring& expectedBackend = std::wstring(), const std::wstring& testPipeName = std::wstring())
{
    StartupDecision decision;
    decision.backendPath = expectedBackend.empty() ? Combine(DirectoryName(ModulePath()), L"ResourceManager.exe") : expectedBackend;
    decision.trustedAccessPackageRoot = ResolvePackageRoot();
    decision.testPipeName = testPipeName;

    std::wstring response;
    if (!PostStartupRequest(targetPath, decision, response))
    {
        decision.message = L"startup resolver unavailable";
        return decision;
    }
    ParseStartupDecision(response, targetPath, packageRoot, decision, ResolveRuntimeCatalogRoot());
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

bool ApplyBootstrap(HANDLE process, const std::wstring& bootstrapPath,
    const std::vector<std::wstring>& paths)
{
    if (paths.empty() || paths.size() > 17) return false;
    ScopedModule module{LoadLibraryW(bootstrapPath.c_str())};
    if (module.value == nullptr) return false;
    using UpdateFunction = DWORD (WINAPI*)(HANDLE, const wchar_t* const*, DWORD, DWORD*);
    union { FARPROC raw; UpdateFunction typed; } updateAddress{};
    updateAddress.raw = GetProcAddress(module.value, "ResourceManagerGpuPlacementBootstrapUpdate");
    if (updateAddress.typed == nullptr) return false;
    std::vector<const wchar_t*> entries;
    for (const auto& path : paths) entries.push_back(path.c_str());
    DWORD error = ERROR_SUCCESS;
    return updateAddress.typed(process, entries.data(), static_cast<DWORD>(entries.size()), &error) != 0;
}

ScopedHandle AcquireInheritedPackageGuard(const std::wstring& root)
{
    const auto path = Combine(root, L"Internal\\Management\\package-use.guard");
    if (!ResourceManagerGpuLaunchSession::NoReparseComponents(path, false)) return {};
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    ScopedHandle handle(CreateFileW(path.c_str(), GENERIC_READ | READ_CONTROL, FILE_SHARE_READ, &security,
        OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (handle.value == INVALID_HANDLE_VALUE || !ResourceManagerGpuLaunchSession::VerifyStorageAcl(handle.value, true, false)
        || !ResourceManagerGpuLaunchSession::NoReparseComponents(path, false)) return {};
    return handle;
}

struct ChildStartup
{
    STARTUPINFOEXW info{};
    std::vector<unsigned char> attributes;
    std::vector<ScopedHandle> standardHandles;
    std::vector<HANDLE> inherited;
    ~ChildStartup() { if (info.lpAttributeList) DeleteProcThreadAttributeList(info.lpAttributeList); }
    bool Prepare(HANDLE guard)
    {
        GetStartupInfoW(&info.StartupInfo);
        info.StartupInfo.cb = sizeof(STARTUPINFOW);
        if (guard && guard != INVALID_HANDLE_VALUE) inherited.push_back(guard);
        if ((info.StartupInfo.dwFlags & STARTF_USESTDHANDLES) != 0)
        {
            HANDLE* slots[] = { &info.StartupInfo.hStdInput, &info.StartupInfo.hStdOutput, &info.StartupInfo.hStdError };
            for (auto slot : slots)
            {
                HANDLE copy = nullptr;
                if (*slot && *slot != INVALID_HANDLE_VALUE)
                {
                    if (!DuplicateHandle(GetCurrentProcess(), *slot, GetCurrentProcess(), &copy, 0, TRUE, DUPLICATE_SAME_ACCESS)) return false;
                    standardHandles.emplace_back(copy);
                    inherited.push_back(copy);
                }
                *slot = copy;
            }
        }
        if (inherited.empty()) return true;
        SIZE_T size = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
        if (!size) return false;
        attributes.resize(size);
        auto list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
        if (!InitializeProcThreadAttributeList(list, 1, 0, &size)) return false;
        info.lpAttributeList = list;
        info.StartupInfo.cb = sizeof(STARTUPINFOEXW);
        return UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited.data(),
            inherited.size() * sizeof(HANDLE), nullptr, nullptr) != FALSE;
    }
};

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
    bool applyGpu = decision.injectDirect3D || decision.enableVulkan;
    bool applyModules = !decision.modules.empty();
    std::wstring fallbackOutcome;
    std::wstring fallbackMessage;
    auto packageGuard = (applyGpu || applyModules) ? AcquireInheritedPackageGuard(decision.trustedAccessPackageRoot) : ScopedHandle{};
    if ((applyGpu || applyModules) && (packageGuard.value == nullptr || packageGuard.value == INVALID_HANDLE_VALUE))
    {
        applyGpu = applyModules = false;
        fallbackOutcome = L"provider-unavailable-fallback";
        fallbackMessage = L"package maintenance guard unavailable; starting without startup modules";
    }
    if ((decision.injectDirect3D && (!FileExists(providerPath) || !FileExists(bootstrapPath)))
        || (decision.enableVulkan && (!FileExists(Combine(vulkanDirectory, L"ResourceManager.VulkanPlacementLayer.dll"))
            || !FileExists(Combine(vulkanDirectory, L"ResourceManager.VulkanPlacementLayer.json"))
            || vulkanDirectory.find(L';') != std::wstring::npos || !CanUseVulkanEnvironment())))
    {
        applyGpu = false;
        fallbackOutcome = L"provider-unavailable-fallback";
        fallbackMessage += L"; requested GPU artifacts or non-elevated Vulkan environment unavailable";
    }
    if (applyModules && !FileExists(bootstrapPath)
        && std::any_of(decision.modules.begin(), decision.modules.end(), [](const StartupModule& module) { return module.kind == L"preload"; }))
    {
        applyModules = false;
        fallbackOutcome = L"provider-unavailable-fallback";
        fallbackMessage += L"; startup bootstrap unavailable";
    }
    if (applyModules && !CanUseVulkanEnvironment()
        && std::any_of(decision.modules.begin(), decision.modules.end(), [](const StartupModule& module) { return module.kind == L"vulkan-layer"; }))
    {
        fallbackOutcome = L"provider-unavailable-fallback";
        fallbackMessage += L"; non-elevated Vulkan environment unavailable; preload modules remain enabled";
    }

    for (int attempt = 0; attempt < 3; ++attempt)
    {
        bool inject = applyGpu && decision.injectDirect3D;
        bool vulkan = applyGpu && decision.enableVulkan;
        std::vector<std::wstring> preloadPaths;
        std::vector<std::pair<std::wstring, std::wstring>> vulkanModules;
        if (inject) preloadPaths.push_back(providerPath);
        if (applyModules)
            for (const auto& module : decision.modules)
            {
                if (module.kind == L"preload") preloadPaths.push_back(module.imagePath);
                else if (CanUseVulkanEnvironment()) vulkanModules.emplace_back(DirectoryName(module.manifestPath), module.layerName);
            }
        const std::wstring readyEventName = L"Local\\ResourceManager.GpuPlacement.Broker."
            + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(GetTickCount64());
        ScopedHandle readyEvent(inject ? CreateEventW(nullptr, TRUE, FALSE, readyEventName.c_str()) : nullptr);
        if (inject && readyEvent.value == nullptr)
        {
            applyGpu = inject = vulkan = false;
            fallbackOutcome = L"provider-unavailable-fallback";
            if (!fallbackMessage.empty()) fallbackMessage += L"; ";
            preloadPaths.erase(std::remove(preloadPaths.begin(), preloadPaths.end(), providerPath), preloadPaths.end());
            fallbackMessage += L"provider ready event creation failed; starting without GPU providers";
        }
        auto environment = ResourceManagerGpuLaunch::BuildEnvironmentBlock(
            ResourceManagerGpuLaunch::ConfigureChildEnvironment(inherited,
                applyGpu ? decision.policyPath : std::wstring(),
                inject ? readyEventName : std::wstring(),
                vulkan ? vulkanDirectory : std::wstring(),
                vulkanModules));

        ChildStartup startup;
        if (!startup.Prepare((applyGpu || applyModules) ? packageGuard.value : nullptr))
        {
            const DWORD error = GetLastError();
            PostStartupReport(decision, targetPath, 0, L"launch-failed", L"child handle admission failed");
            return static_cast<int>(error ? error : ERROR_INVALID_HANDLE);
        }
        PROCESS_INFORMATION process{};
        std::vector<wchar_t> commandLine(originalCommandLine.begin(), originalCommandLine.end());
        commandLine.push_back(L'\0');
        if (!CreateProcessW(targetPath.c_str(), commandLine.data(), nullptr, nullptr, startup.inherited.empty() ? FALSE : TRUE,
                CREATE_SUSPENDED | DEBUG_ONLY_THIS_PROCESS | CREATE_UNICODE_ENVIRONMENT
                    | (startup.info.lpAttributeList ? EXTENDED_STARTUPINFO_PRESENT : 0),
                environment.data(), nullptr, &startup.info.StartupInfo, &process))
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

        if (!preloadPaths.empty() && !ApplyBootstrap(process.hProcess, bootstrapPath, preloadPaths))
        {
            PostStartupReport(decision, targetPath, process.dwProcessId, L"bootstrap-failed-fallback",
                L"startup bootstrap failed before target resume");
            const bool terminated = TerminateProcess(process.hProcess, ERROR_DLL_INIT_FAILED) != FALSE;
            const bool detached = DetachDebugger(process.dwProcessId);
            const bool exited = WaitForSingleObject(process.hProcess, 2000) == WAIT_OBJECT_0;
            if (!terminated || !detached || !exited || !allowInjectionFallback) return ERROR_DLL_INIT_FAILED;
            if (applyModules) applyModules = false;
            else applyGpu = false;
            fallbackOutcome = L"bootstrap-failed-fallback";
            if (!fallbackMessage.empty()) fallbackMessage += L"; ";
            fallbackMessage += L"startup bootstrap failed before resume; target restarted without failing component";
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
                if (applyModules) return MirrorTargetExit(process.hProcess);
                TerminateProcess(process.hProcess, ERROR_TIMEOUT);
                WaitForSingleObject(process.hProcess, 2000);
                return ERROR_TIMEOUT;
            }
        }

        if (!fallbackOutcome.empty())
            PostStartupReport(decision, targetPath, process.dwProcessId, fallbackOutcome, fallbackMessage);
        else if (applyModules)
            PostStartupReport(decision, targetPath, process.dwProcessId,
                inject ? L"provider-ready" : L"startup-configured",
                inject ? L"Direct3D preload ready; startup modules and Vulkan child environment configured"
                       : L"startup modules and Vulkan child environment configured");
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

bool FilterMatchesRule(const std::wstring& path, const std::wstring& ruleName)
{
    if (path.empty()) return false;
    const auto fullPath = FullPath(path);
    if (fullPath.empty()) return false;
    const int length = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, fullPath.data(),
        static_cast<int>(fullPath.size()), nullptr, 0, nullptr, nullptr, 0);
    if (length <= 0) return false;
    std::wstring upper(length, L'\0');
    if (LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, fullPath.data(),
        static_cast<int>(fullPath.size()), upper.data(), length, nullptr, nullptr, 0) != length) return false;
    const int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, upper.data(), length, nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return false;
    std::string utf8(bytes, '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, upper.data(), length, utf8.data(), bytes, nullptr, nullptr) != bytes) return false;
    HCRYPTPROV provider = 0;
    HCRYPTHASH hash = 0;
    if (!CryptAcquireContextW(&provider, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT)) return false;
    std::array<BYTE, 32> digest{};
    DWORD size = static_cast<DWORD>(digest.size());
    const bool valid = CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash)
        && CryptHashData(hash, reinterpret_cast<const BYTE*>(utf8.data()), static_cast<DWORD>(utf8.size()), 0)
        && CryptGetHashParam(hash, HP_HASHVAL, digest.data(), &size, 0);
    if (hash) CryptDestroyHash(hash);
    CryptReleaseContext(provider, 0);
    if (!valid || size != digest.size()) return false;
    std::wstring expected = L"ResourceManager-";
    constexpr wchar_t hex[] = L"0123456789abcdef";
    for (size_t index = 0; index < 12; ++index) { expected += hex[digest[index] >> 4]; expected += hex[digest[index] & 15]; }
    return expected == ruleName;
}

std::vector<std::wstring> EnumerateSubKeys(HKEY key, DWORD& firstError, bool* complete = nullptr)
{
    std::vector<std::wstring> names;
    for (DWORD index = 0;; ++index)
    {
        std::array<wchar_t, 512> name{};
        DWORD length = static_cast<DWORD>(name.size());
        const LONG status = RegEnumKeyExW(key, index, name.data(), &length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status != ERROR_SUCCESS) {
            if (firstError == ERROR_SUCCESS) firstError = status;
            if (complete) *complete = false;
            break;
        }
        names.emplace_back(name.data(), length);
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
    for (const auto& imageName : EnumerateSubKeys(rawRoot, firstError))
    {
        HKEY imageReadKey = nullptr;
        const LONG imageReadStatus = RegOpenKeyExW(
                rawRoot,
                imageName.c_str(),
                0,
                KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE,
                &imageReadKey);
        if (imageReadStatus != ERROR_SUCCESS)
        {
            if (firstError == ERROR_SUCCESS) firstError = imageReadStatus;
            continue;
        }

        std::vector<std::wstring> ownedRules;
        for (const auto& ruleName : EnumerateSubKeys(imageReadKey, firstError))
        {
            HKEY ruleKey = nullptr;
            const LONG ruleReadStatus = RegOpenKeyExW(imageReadKey, ruleName.c_str(), 0, KEY_QUERY_VALUE, &ruleKey);
            if (ruleReadStatus != ERROR_SUCCESS) { if (firstError == ERROR_SUCCESS) firstError = ruleReadStatus; continue; }
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
            HKEY ruleKey = nullptr;
            LONG status = RegOpenKeyExW(imageWriteKey, ruleName.c_str(), 0,
                KEY_QUERY_VALUE | KEY_SET_VALUE | KEY_ENUMERATE_SUB_KEYS, &ruleKey);
            if (status == ERROR_FILE_NOT_FOUND) continue;
            if (status != ERROR_SUCCESS) { if (firstError == ERROR_SUCCESS) firstError = status; continue; }
            if (ReadRegistryString(ruleKey, L"ResourceManagerOwner") != OwnerValue) { RegCloseKey(ruleKey); continue; }
            auto removeValue = [&](const wchar_t* name) {
                const LONG result = RegDeleteValueW(ruleKey, name);
                if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND && status == ERROR_SUCCESS) status = result;
            };
            const auto debugger = ReadRegistryString(ruleKey, L"Debugger");
            const bool ownedDebugger = debugger == L"\"" + ModulePath() + L"\" --resource-manager-ifeo";
            if (ownedDebugger) removeValue(L"Debugger");
            if ((ownedDebugger || debugger.empty()) && FilterMatchesRule(ReadRegistryString(ruleKey, L"FilterFullPath"), ruleName))
                removeValue(L"FilterFullPath");
            if (ReadRegistryString(ruleKey, L"ResourceManagerRuleId") == ruleName) removeValue(L"ResourceManagerRuleId");
            if (status == ERROR_SUCCESS) removeValue(L"ResourceManagerOwner");
            DWORD subkeys = 0, values = 0;
            const LONG query = RegQueryInfoKeyW(ruleKey, nullptr, nullptr, nullptr, &subkeys, nullptr,
                nullptr, &values, nullptr, nullptr, nullptr, nullptr);
            RegCloseKey(ruleKey);
            if (status == ERROR_SUCCESS && query != ERROR_SUCCESS) status = query;
            if (status == ERROR_SUCCESS && subkeys == 0 && values == 0)
                status = RegDeleteKeyExW(imageWriteKey, ruleName.c_str(), view, 0);
            if (status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND) ++removed;
            else if (firstError == ERROR_SUCCESS)
            {
                firstError = static_cast<DWORD>(status);
            }
        }

        bool hasFullPathRules = false;
        bool hasOwnedRules = false;
        bool complete = true;
        for (const auto& ruleName : EnumerateSubKeys(imageWriteKey, firstError, &complete))
        {
            HKEY ruleKey = nullptr;
            const LONG readStatus = RegOpenKeyExW(imageWriteKey, ruleName.c_str(), 0, KEY_QUERY_VALUE, &ruleKey);
            if (readStatus != ERROR_SUCCESS) { complete = false; if (firstError == ERROR_SUCCESS) firstError = readStatus; continue; }
            hasFullPathRules = hasFullPathRules || !ReadRegistryString(ruleKey, L"FilterFullPath").empty();
            hasOwnedRules = hasOwnedRules || ReadRegistryString(ruleKey, L"ResourceManagerOwner") == OwnerValue;
            RegCloseKey(ruleKey);
        }
        if (parentOwned && complete && !hasOwnedRules
            && ReadRegistryString(imageWriteKey, L"ResourceManagerUseFilterOwner") == OwnerValue)
        {
            bool parentSettled = true;
            DWORD useFilter = 0, type = 0, size = sizeof(useFilter);
            const LONG readFilter = RegQueryValueExW(imageWriteKey, L"UseFilter", nullptr, &type,
                reinterpret_cast<BYTE*>(&useFilter), &size);
            if (!hasFullPathRules && readFilter == ERROR_SUCCESS && type == REG_DWORD && size == sizeof(useFilter) && useFilter == 1)
            {
                const LONG status = RegDeleteValueW(imageWriteKey, L"UseFilter");
                if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND)
                {
                    parentSettled = false;
                    if (firstError == ERROR_SUCCESS) firstError = static_cast<DWORD>(status);
                }
            }
            if (parentSettled)
            {
                const LONG status = RegDeleteValueW(imageWriteKey, L"ResourceManagerUseFilterOwner");
                if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND && firstError == ERROR_SUCCESS)
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
