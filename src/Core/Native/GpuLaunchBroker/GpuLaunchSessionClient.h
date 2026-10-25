#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <winsvc.h>

#include "../GpuPlacementCommon/WorkerPipeClient.h"
#include <algorithm>
#include <array>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace ResourceManagerGpuLaunchSession
{
constexpr unsigned char ResolveOperation = 2;
constexpr unsigned char ReportOperation = 3;
constexpr DWORD MaximumPayloadBytes = 64 * 1024;
constexpr DWORD MaximumFrameBytes = 128 * 1024;
constexpr DWORD MaximumKeyBytes = 4096;
constexpr DWORD MaximumDefaultKeyFileBytes = 72;
constexpr wchar_t TrustedAccessPipeName[] = L"ResourceManager.TrustedAccess.Invoke.v1";
using ResourceManagerGpuWorker::Handle;

struct BackendIdentity { DWORD processId{}; uint64_t creation{}; };

inline bool ProcessIdentity(HANDLE process, const std::wstring& expectedImage, uint64_t& creation)
{
    FILETIME born{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &born, &exited, &kernel, &user) || exited.dwHighDateTime || exited.dwLowDateTime) return false;
    std::array<wchar_t, 32768> image{};
    DWORD size = static_cast<DWORD>(image.size());
    if (!QueryFullProcessImageNameW(process, 0, image.data(), &size) || _wcsicmp(image.data(), expectedImage.c_str()) != 0) return false;
    creation = (static_cast<uint64_t>(born.dwHighDateTime) << 32) | born.dwLowDateTime;
    return true;
}

inline bool Transfer(HANDLE pipe, void* bytes, DWORD length, bool writing, ULONGLONG deadline)
{
    auto at = static_cast<unsigned char*>(bytes);
    while (length)
    {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.valid()) return false;
        OVERLAPPED pending{};
        pending.hEvent = event.get();
        DWORD done{};
        if (!(writing ? WriteFile(pipe, at, length, &done, &pending) : ReadFile(pipe, at, length, &done, &pending)))
        {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            const auto now = GetTickCount64();
            if (now >= deadline || WaitForSingleObject(event.get(), static_cast<DWORD>(deadline - now)) != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe, &pending);
                GetOverlappedResult(pipe, &pending, &done, TRUE);
                return false;
            }
            if (!GetOverlappedResult(pipe, &pending, &done, FALSE)) return false;
        }
        if (!done || done > length) return false;
        at += done;
        length -= done;
    }
    return true;
}

inline std::wstring FullPath(const std::wstring& path)
{
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetFullPathNameW(path.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (!length || length >= buffer.size()) return {};
    std::wstring result(buffer.data(), length);
    if (result.rfind(L"\\\\?\\UNC\\", 0) == 0) result = L"\\\\" + result.substr(8);
    else if (result.rfind(L"\\\\?\\", 0) == 0) result.erase(0, 4);
    return result;
}

inline bool MakeWellKnownSid(WELL_KNOWN_SID_TYPE type, std::array<unsigned char, SECURITY_MAX_SID_SIZE>& buffer, DWORD& size)
{
    size = static_cast<DWORD>(buffer.size());
    return CreateWellKnownSid(type, nullptr, buffer.data(), &size) != FALSE;
}

inline bool IsExpectedOwner(PSID owner)
{
    std::array<unsigned char, SECURITY_MAX_SID_SIZE> systemBuffer{}, administratorsBuffer{};
    DWORD systemSize{}, administratorsSize{};
    return owner && MakeWellKnownSid(WinLocalSystemSid, systemBuffer, systemSize)
        && MakeWellKnownSid(WinBuiltinAdministratorsSid, administratorsBuffer, administratorsSize)
        && (EqualSid(owner, systemBuffer.data()) || EqualSid(owner, administratorsBuffer.data()));
}

inline bool VerifyStorageAcl(HANDLE handle, bool allowUsersRead, bool directory)
{
    PSID owner = nullptr;
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    const DWORD status = GetSecurityInfo(handle, SE_FILE_OBJECT,
        OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner, nullptr, &dacl, nullptr, &descriptor);
    if (status != ERROR_SUCCESS) return false;
    bool valid = false;
    do
    {
        if (!IsExpectedOwner(owner) || !dacl) break;
        SECURITY_DESCRIPTOR_CONTROL control{};
        DWORD revision{};
        if (!GetSecurityDescriptorControl(descriptor, &control, &revision)
            || (control & SE_DACL_PROTECTED) == 0) break;

        std::array<unsigned char, SECURITY_MAX_SID_SIZE> systemBuffer{}, administratorsBuffer{}, usersBuffer{};
        DWORD systemSize{}, administratorsSize{}, usersSize{};
        if (!MakeWellKnownSid(WinLocalSystemSid, systemBuffer, systemSize)
            || !MakeWellKnownSid(WinBuiltinAdministratorsSid, administratorsBuffer, administratorsSize)
            || !MakeWellKnownSid(WinBuiltinUsersSid, usersBuffer, usersSize)) break;

        ACL_SIZE_INFORMATION info{};
        if (!GetAclInformation(dacl, &info, sizeof(info), AclSizeInformation) || info.AceCount > 16) break;
        bool systemFull = false, administratorsFull = false, usersAllowed = false;
        for (DWORD index = 0; index < info.AceCount; ++index)
        {
            void* rawAce = nullptr;
            if (!GetAce(dacl, index, &rawAce) || !rawAce) { valid = false; break; }
            const auto* header = static_cast<ACE_HEADER*>(rawAce);
            if (header->AceType != ACCESS_ALLOWED_ACE_TYPE || (header->AceFlags & INHERITED_ACE) != 0)
            {
                valid = false;
                break;
            }
            const auto* ace = static_cast<ACCESS_ALLOWED_ACE*>(rawAce);
            PSID sid = const_cast<DWORD*>(&ace->SidStart);
            if (EqualSid(sid, systemBuffer.data()))
            {
                if (ace->Mask != FILE_ALL_ACCESS) { valid = false; break; }
                systemFull = true;
            }
            else if (EqualSid(sid, administratorsBuffer.data()))
            {
                if (ace->Mask != FILE_ALL_ACCESS) { valid = false; break; }
                administratorsFull = true;
            }
            else if (EqualSid(sid, usersBuffer.data()) && directory)
            {
                constexpr DWORD usersDirectoryMask = FILE_TRAVERSE | FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE;
                if (ace->Mask != usersDirectoryMask) { valid = false; break; }
                usersAllowed = true;
            }
            else if (EqualSid(sid, usersBuffer.data()) && allowUsersRead && !directory)
            {
                if (ace->Mask != FILE_GENERIC_READ) { valid = false; break; }
                usersAllowed = true;
            }
            else
            {
                valid = false;
                break;
            }
            valid = true;
        }
        if (!systemFull || !administratorsFull || (allowUsersRead && !directory && !usersAllowed)) valid = false;
        else if (info.AceCount == 0) valid = false;
    } while (false);
    LocalFree(descriptor);
    return valid;
}

inline bool NoReparseComponents(const std::wstring& fullPath, bool expectDirectory)
{
    if (fullPath.size() < 3 || fullPath[1] != L':' || fullPath[2] != L'\\') return false;
    size_t end = 3;
    while (end <= fullPath.size())
    {
        const size_t separator = fullPath.find(L'\\', end);
        const size_t length = separator == std::wstring::npos ? fullPath.size() : separator;
        const std::wstring component = fullPath.substr(0, length);
        const DWORD attributes = GetFileAttributesW(component.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) return false;
        const bool final = separator == std::wstring::npos;
        if (!final && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) return false;
        if (final && expectDirectory != ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)) return false;
        if (final) return true;
        end = separator + 1;
    }
    return false;
}

inline std::wstring FinalPath(HANDLE handle)
{
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetFinalPathNameByHandleW(handle, buffer.data(), static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!length || length >= buffer.size()) return {};
    return FullPath(std::wstring(buffer.data(), length));
}

inline bool ReadDefaultKey(const std::wstring& packageRoot, std::string& key)
{
    key.clear();
    std::wstring fullRoot = FullPath(packageRoot);
    if (fullRoot.empty()) return false;
    while (fullRoot.size() > 3 && fullRoot.back() == L'\\') fullRoot.pop_back();
    const std::wstring securityDirectory = fullRoot + L"\\UserData\\Security";
    const std::wstring filePath = securityDirectory + L"\\default-key";
    const DWORD directoryAttributes = GetFileAttributesW(securityDirectory.c_str());
    if (directoryAttributes == INVALID_FILE_ATTRIBUTES)
    {
        const DWORD error = GetLastError();
        return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
    }
    if (!NoReparseComponents(securityDirectory, true)) return false;

    Handle directory(CreateFileW(securityDirectory.c_str(), FILE_READ_ATTRIBUTES | READ_CONTROL,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!directory.valid() || !VerifyStorageAcl(directory.get(), false, true)) return false;

    const DWORD keyAttributes = GetFileAttributesW(filePath.c_str());
    if (keyAttributes == INVALID_FILE_ATTRIBUTES)
    {
        return GetLastError() == ERROR_FILE_NOT_FOUND;
    }
    if (!NoReparseComponents(filePath, false)) return false;

    Handle file(CreateFileW(filePath.c_str(), GENERIC_READ | READ_CONTROL,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_SEQUENTIAL_SCAN | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!file.valid()) return false;
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(file.get(), &info)
        || (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0
        || !VerifyStorageAcl(file.get(), true, false)
        || _wcsicmp(FinalPath(file.get()).c_str(), FullPath(filePath).c_str()) != 0) return false;

    LARGE_INTEGER length{};
    if (!GetFileSizeEx(file.get(), &length) || length.QuadPart < 0 || length.QuadPart > MaximumDefaultKeyFileBytes) return false;
    if (length.QuadPart == 0) return true;
    key.resize(static_cast<size_t>(length.QuadPart));
    DWORD total = 0;
    while (total < key.size())
    {
        DWORD read{};
        const DWORD wanted = static_cast<DWORD>(std::min<size_t>(key.size() - total, MAXDWORD));
        if (!ReadFile(file.get(), key.data() + total, wanted, &read, nullptr) || read == 0)
        {
            SecureZeroMemory(key.data(), key.size());
            key.clear();
            return false;
        }
        total += read;
    }
    size_t keyLength = key.size();
    while (keyLength && (key[keyLength - 1] == '\r' || key[keyLength - 1] == '\n')) --keyLength;
    if (keyLength == 0)
    {
        SecureZeroMemory(key.data(), key.size());
        key.clear();
        return true;
    }
    if (keyLength != 70 || key.compare(0, 6, "rm_sk_") != 0)
    {
        SecureZeroMemory(key.data(), key.size());
        key.clear();
        return false;
    }
    for (size_t index = 6; index < keyLength; ++index)
    {
        const unsigned char ch = static_cast<unsigned char>(key[index]);
        if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
        {
            SecureZeroMemory(key.data(), key.size());
            key.clear();
            return false;
        }
    }
    std::string normalized(key.data(), keyLength);
    SecureZeroMemory(key.data(), key.size());
    key = std::move(normalized);
    return true;
}

inline bool ReadSystemServiceIdentity(const std::wstring& expectedImage, BackendIdentity& identity)
{
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!manager) return false;
    SC_HANDLE service = OpenServiceW(manager, L"ResourceManager.Service", SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS);
    if (!service) { CloseServiceHandle(manager); return false; }

    bool valid = false;
    DWORD required = 0;
    QueryServiceConfigW(service, nullptr, 0, &required);
    if (GetLastError() == ERROR_INSUFFICIENT_BUFFER && required > 0 && required <= 64 * 1024)
    {
        std::vector<unsigned char> configBuffer(required);
        auto* config = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(configBuffer.data());
        SERVICE_STATUS_PROCESS status{};
        DWORD statusBytes{};
        if (QueryServiceConfigW(service, config, required, &required)
            && QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO,
                reinterpret_cast<LPBYTE>(&status), sizeof(status), &statusBytes)
            && statusBytes >= sizeof(status) && status.dwCurrentState == SERVICE_RUNNING
            && status.dwProcessId != 0 && config->lpServiceStartName
            && _wcsicmp(config->lpServiceStartName, L"LocalSystem") == 0
            && config->lpBinaryPathName)
        {
            std::wstring registeredPath(config->lpBinaryPathName);
            if (registeredPath.size() >= 2 && registeredPath.front() == L'"')
            {
                const size_t close = registeredPath.find(L'"', 1);
                if (close == std::wstring::npos || close + 1 != registeredPath.size()) registeredPath.clear();
                else registeredPath = registeredPath.substr(1, close - 1);
            }
            if (!registeredPath.empty() && _wcsicmp(FullPath(registeredPath).c_str(), FullPath(expectedImage).c_str()) == 0)
            {
                Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, status.dwProcessId));
                uint64_t creation{};
                SERVICE_STATUS_PROCESS after{};
                DWORD afterBytes{};
                // SCM attests LocalSystem above. Process-query access does not
                // grant TOKEN_QUERY against the separate SYSTEM token DACL.
                if (process.valid()
                    && ProcessIdentity(process.get(), FullPath(expectedImage), creation)
                    && QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO,
                        reinterpret_cast<LPBYTE>(&after), sizeof(after), &afterBytes)
                    && afterBytes >= sizeof(after) && after.dwCurrentState == SERVICE_RUNNING
                    && after.dwProcessId == status.dwProcessId)
                {
                    uint64_t confirmed{};
                    if (ProcessIdentity(process.get(), FullPath(expectedImage), confirmed) && confirmed == creation)
                    {
                        identity = {status.dwProcessId, creation};
                        valid = true;
                    }
                }
            }
        }
    }
    CloseServiceHandle(service);
    CloseServiceHandle(manager);
    return valid;
}

inline bool AppendJsonString(const std::string& value, std::string& output)
{
    static constexpr char hex[] = "0123456789abcdef";
    output.push_back('"');
    for (unsigned char ch : value)
    {
        if (ch == '"' || ch == '\\') { output.push_back('\\'); output.push_back(static_cast<char>(ch)); }
        else if (ch < 0x20) { output += "\\u00"; output.push_back(hex[ch >> 4]); output.push_back(hex[ch & 0x0f]); }
        else output.push_back(static_cast<char>(ch));
        if (output.size() > MaximumFrameBytes) return false;
    }
    output.push_back('"');
    return output.size() <= MaximumFrameBytes;
}

inline bool ParseHex4(const std::string& json, size_t& offset, unsigned& value)
{
    if (offset + 4 > json.size()) return false;
    value = 0;
    for (unsigned index = 0; index < 4; ++index)
    {
        const char ch = json[offset++];
        unsigned digit{};
        if (ch >= '0' && ch <= '9') digit = static_cast<unsigned>(ch - '0');
        else if (ch >= 'a' && ch <= 'f') digit = static_cast<unsigned>(ch - 'a' + 10);
        else if (ch >= 'A' && ch <= 'F') digit = static_cast<unsigned>(ch - 'A' + 10);
        else return false;
        value = (value << 4) | digit;
    }
    return true;
}

inline void AppendUtf8(unsigned codePoint, std::string& output)
{
    if (codePoint <= 0x7f) output.push_back(static_cast<char>(codePoint));
    else if (codePoint <= 0x7ff)
    {
        output.push_back(static_cast<char>(0xc0 | (codePoint >> 6)));
        output.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
    }
    else if (codePoint <= 0xffff)
    {
        output.push_back(static_cast<char>(0xe0 | (codePoint >> 12)));
        output.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3f)));
        output.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
    }
    else
    {
        output.push_back(static_cast<char>(0xf0 | (codePoint >> 18)));
        output.push_back(static_cast<char>(0x80 | ((codePoint >> 12) & 0x3f)));
        output.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3f)));
        output.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
    }
}

inline bool ParseJsonString(const std::string& json, size_t& offset, std::string& value)
{
    if (offset >= json.size() || json[offset++] != '"') return false;
    value.clear();
    while (offset < json.size())
    {
        const unsigned char ch = static_cast<unsigned char>(json[offset++]);
        if (ch == '"') return true;
        if (ch < 0x20) return false;
        if (ch != '\\') { value.push_back(static_cast<char>(ch)); continue; }
        if (offset >= json.size()) return false;
        const char escaped = json[offset++];
        switch (escaped)
        {
        case '"': value.push_back('"'); break;
        case '\\': value.push_back('\\'); break;
        case '/': value.push_back('/'); break;
        case 'b': value.push_back('\b'); break;
        case 'f': value.push_back('\f'); break;
        case 'n': value.push_back('\n'); break;
        case 'r': value.push_back('\r'); break;
        case 't': value.push_back('\t'); break;
        case 'u':
        {
            unsigned first{};
            if (!ParseHex4(json, offset, first)) return false;
            unsigned codePoint = first;
            if (first >= 0xd800 && first <= 0xdbff)
            {
                if (offset + 2 > json.size() || json[offset++] != '\\' || json[offset++] != 'u') return false;
                unsigned second{};
                if (!ParseHex4(json, offset, second) || second < 0xdc00 || second > 0xdfff) return false;
                codePoint = 0x10000 + ((first - 0xd800) << 10) + (second - 0xdc00);
            }
            else if (first >= 0xdc00 && first <= 0xdfff) return false;
            AppendUtf8(codePoint, value);
            break;
        }
        default: return false;
        }
        if (value.size() > MaximumFrameBytes) return false;
    }
    return false;
}

inline void SkipWhitespace(const std::string& json, size_t& offset)
{
    while (offset < json.size() && (json[offset] == ' ' || json[offset] == '\r' || json[offset] == '\n' || json[offset] == '\t')) ++offset;
}

inline bool ParseResponse(const std::string& json, std::string& status, std::string& payload)
{
    size_t offset = 0;
    bool sawStatus = false, sawPayload = false, payloadNull = false;
    SkipWhitespace(json, offset);
    if (offset >= json.size() || json[offset++] != '{') return false;
    while (true)
    {
        SkipWhitespace(json, offset);
        if (offset < json.size() && json[offset] == '}') { ++offset; break; }
        std::string name, value;
        if (!ParseJsonString(json, offset, name)) return false;
        SkipWhitespace(json, offset);
        if (offset >= json.size() || json[offset++] != ':') return false;
        SkipWhitespace(json, offset);
        if (name == "payload" && json.compare(offset, 4, "null") == 0)
        {
            offset += 4;
            payloadNull = true;
            sawPayload = true;
        }
        else
        {
            if (!ParseJsonString(json, offset, value)) return false;
            if (name == "status") { status = value; sawStatus = true; }
            else if (name == "payload") { payload = value; sawPayload = true; }
            else return false;
        }
        SkipWhitespace(json, offset);
        if (offset < json.size() && json[offset] == ',') { ++offset; continue; }
        if (offset < json.size() && json[offset] == '}') { ++offset; break; }
        return false;
    }
    SkipWhitespace(json, offset);
    return offset == json.size() && sawStatus && sawPayload && !(status == "ok" && (payloadNull || payload.empty()));
}

inline bool Call(unsigned char operation, const std::string& body, const std::wstring& expectedBackend,
    const std::wstring& packageRoot, const std::wstring& testPipeName,
    BackendIdentity& backend, bool requireExistingBackend, std::string& reply)
{
    reply.clear();
    if (body.empty() || body.size() > MaximumPayloadBytes) return false;
    const char* operationName = operation == ResolveOperation ? "gpu.resolve"
        : operation == ReportOperation ? "gpu.report" : nullptr;
    if (!operationName) return false;

    const std::wstring pipeName = testPipeName.empty()
        ? TrustedAccessPipeName : testPipeName;
    const std::wstring name = L"\\\\.\\pipe\\" + pipeName;
    const auto deadline = GetTickCount64() + 2000;
    Handle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr));
    if (!pipe.valid() && GetLastError() == ERROR_PIPE_BUSY)
    {
        if (!WaitNamedPipeW(name.c_str(), 1000)) return false;
        pipe = Handle(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr));
    }
    if (!pipe.valid()) return false;

    ULONG pid{};
    if (!GetNamedPipeServerProcessId(pipe.get(), &pid)) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    BackendIdentity serverIdentity{};
    if (!process.valid()) return false;
    if (testPipeName.empty())
    {
        if (!ReadSystemServiceIdentity(expectedBackend, serverIdentity) || serverIdentity.processId != pid) return false;
    }
    else
    {
        if (!ProcessIdentity(process.get(), FullPath(expectedBackend), serverIdentity.creation)) return false;
        serverIdentity.processId = pid;
    }
    uint64_t checkedCreation{};
    if (!ProcessIdentity(process.get(), FullPath(expectedBackend), checkedCreation)
        || checkedCreation != serverIdentity.creation
        || (requireExistingBackend && (backend.processId != pid || backend.creation != serverIdentity.creation))) return false;

    std::string key;
    if (!ReadDefaultKey(packageRoot, key)) return false;
    struct SensitiveBufferWiper
    {
        std::string& value;
        ~SensitiveBufferWiper() { if (!value.empty()) SecureZeroMemory(value.data(), value.size()); }
    } wipeKey{key};

    std::string request;
    request.reserve(MaximumFrameBytes);
    SensitiveBufferWiper wipeRequest{request};
    request = "{\"operation\":";
    if (!AppendJsonString(operationName, request)) return false;
    request += ",\"key\":";
    if (!AppendJsonString(key, request)) return false;
    request += ",\"payload\":";
    if (!AppendJsonString(body, request)) return false;
    request.push_back('}');
    if (request.empty() || request.size() > MaximumFrameBytes) return false;
    DWORD size = static_cast<DWORD>(request.size());
    if (!Transfer(pipe.get(), &size, sizeof(size), true, deadline)
        || !Transfer(pipe.get(), request.data(), size, true, deadline)
        || !Transfer(pipe.get(), &size, sizeof(size), false, deadline)
        || size == 0 || size > MaximumFrameBytes) return false;
    std::string response(size, '\0');
    if (!Transfer(pipe.get(), response.data(), size, false, deadline)) return false;
    std::string status, responsePayload;
    if (!ParseResponse(response, status, responsePayload) || status != "ok") return false;

    ULONG currentPid{};
    uint64_t currentCreation{};
    if (!GetNamedPipeServerProcessId(pipe.get(), &currentPid) || currentPid != pid
        || !ProcessIdentity(process.get(), FullPath(expectedBackend), currentCreation)
        || currentCreation != serverIdentity.creation) return false;
    if (testPipeName.empty())
    {
        BackendIdentity confirmed{};
        if (!ReadSystemServiceIdentity(expectedBackend, confirmed)
            || confirmed.processId != pid || confirmed.creation != serverIdentity.creation) return false;
    }
    backend = serverIdentity;
    reply = std::move(responsePayload);
    return true;
}
}
