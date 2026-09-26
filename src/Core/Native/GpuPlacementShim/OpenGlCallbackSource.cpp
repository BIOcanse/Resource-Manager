#include "OpenGlCallbackSource.h"
#include <bcrypt.h>
#include <psapi.h>
#include <algorithm>
#include <utility>

namespace ResourceManagerOpenGl
{
namespace
{
BOOL Fail(DWORD error) { SetLastError(error); return FALSE; }

struct ModuleReference
{
    HMODULE value{};
    ~ModuleReference() { const auto error = GetLastError(); if (value) FreeLibrary(value); SetLastError(error); }
};

struct FileReference
{
    HANDLE value{INVALID_HANDLE_VALUE};
    ~FileReference() { const auto error = GetLastError(); if (value != INVALID_HANDLE_VALUE) CloseHandle(value); SetLastError(error); }
};

struct HashReference
{
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    ~HashReference()
    {
        const auto error = GetLastError();
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        SetLastError(error);
    }
};

BOOL FileHash(HMODULE module, const std::wstring& path, std::array<unsigned char, 32>& output)
{
    FileReference file;
    file.value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file.value == INVALID_HANDLE_VALUE) return FALSE;
    std::wstring mappedPath(32768, L'\0'), openedPath(32768, L'\0');
    const DWORD mappedLength = K32GetMappedFileNameW(GetCurrentProcess(), module, mappedPath.data(), static_cast<DWORD>(mappedPath.size()));
    if (!mappedLength) return FALSE;
    const DWORD openedLength = GetFinalPathNameByHandleW(file.value, openedPath.data(), static_cast<DWORD>(openedPath.size()), VOLUME_NAME_NT);
    if (!openedLength) return FALSE;
    if (mappedLength >= mappedPath.size() || openedLength >= openedPath.size()) return Fail(ERROR_FILENAME_EXCED_RANGE);
    mappedPath.resize(mappedLength); openedPath.resize(openedLength);
    // A loaded DLL can outlive a rename. Do not hash a replacement at its original path.
    if (_wcsicmp(mappedPath.c_str(), openedPath.c_str())) return Fail(ERROR_FILE_INVALID);
    HashReference hash;
    if (BCryptOpenAlgorithmProvider(&hash.algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0
        || BCryptCreateHash(hash.algorithm, &hash.hash, nullptr, 0, nullptr, 0, 0) < 0)
        return Fail(ERROR_GEN_FAILURE);
    std::array<unsigned char, 65536> buffer;
    DWORD read{};
    while (true) {
        if (!ReadFile(file.value, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) return FALSE;
        if (!read) break;
        if (BCryptHashData(hash.hash, buffer.data(), read, 0) < 0) return Fail(ERROR_GEN_FAILURE);
    }
    if (BCryptFinishHash(hash.hash, output.data(), static_cast<ULONG>(output.size()), 0) < 0)
        return Fail(ERROR_GEN_FAILURE);
    return TRUE;
}

BOOL DescribeModule(HMODULE module, IcdCallbackModule& output)
{
    // 32768 is the Windows extended path bound, not a provider policy or cache limit.
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length) return FALSE;
    if (length == path.size()) return Fail(ERROR_FILENAME_EXCED_RANGE);
    path.resize(length);
    const auto base = reinterpret_cast<const unsigned char*>(module);
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 65536)
        return Fail(ERROR_BAD_EXE_FORMAT);
    const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR_MAGIC)
        return Fail(ERROR_BAD_EXE_FORMAT);
    IcdCallbackModule candidate;
    candidate.path = std::move(path);
    candidate.imageBytes = nt->OptionalHeader.SizeOfImage;
    candidate.timestamp = nt->FileHeader.TimeDateStamp;
    candidate.machine = nt->FileHeader.Machine;
    if (!FileHash(module, candidate.path, candidate.sha256)) return FALSE;
    output = std::move(candidate);
    return TRUE;
}

bool SameModule(const IcdCallbackModule& left, const IcdCallbackModule& right)
{
    return _wcsicmp(left.path.c_str(), right.path.c_str()) == 0
        && left.imageBytes == right.imageBytes && left.timestamp == right.timestamp
        && left.machine == right.machine && left.sha256 == right.sha256;
}

bool EntryPage(PROC entry, HMODULE module, size_t slot)
{
    MEMORY_BASIC_INFORMATION memory{};
    if (!VirtualQuery(reinterpret_cast<void*>(entry), &memory, sizeof(memory))
        || memory.State != MEM_COMMIT || memory.Type != MEM_IMAGE || memory.AllocationBase != module
        || (memory.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    return slot == 3 || (memory.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY));
}

bool Required(size_t slot) { return slot == 0 || slot == 1 || slot == 2 || slot == 5; }
}

ResolvedIcdCallbacks::~ResolvedIcdCallbacks()
{
    const auto error = GetLastError();
    for (const auto module : modules) FreeLibrary(module);
    SetLastError(error);
}

ResolvedIcdCallbacks::ResolvedIcdCallbacks(ResolvedIcdCallbacks&& other) noexcept
{
    modules.swap(other.modules);
    std::swap(callbacks, other.callbacks);
}

ResolvedIcdCallbacks& ResolvedIcdCallbacks::operator=(ResolvedIcdCallbacks&& other) noexcept
{
    if (this != &other) {
        ResolvedIcdCallbacks previous;
        previous.modules.swap(modules);
        modules.swap(other.modules);
        callbacks = std::exchange(other.callbacks, WGLCALLBACKS{});
    }
    return *this;
}

BOOL DescribeIcdCallbackSource(const WGLCALLBACKS& callbacks, IcdCallbackSource& output)
{
    const DWORD error = GetLastError();
    IcdCallbackSource candidate;
    std::array<HMODULE, 9> known{};
    std::array<PROC, 9> entries{};
    std::memcpy(entries.data(), &callbacks, sizeof(callbacks));
    for (size_t slot = 0; slot < entries.size(); ++slot) {
        const auto entry = entries[slot];
        if (!entry) { if (Required(slot)) return Fail(ERROR_PROC_NOT_FOUND); continue; }
        ModuleReference owner;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                reinterpret_cast<LPCWSTR>(entry), &owner.value)) return FALSE;
        if (!EntryPage(entry, owner.value, slot)) return Fail(ERROR_INVALID_ADDRESS);
        const auto found = std::find(known.begin(), known.begin() + candidate.modules.size(), owner.value);
        const auto index = static_cast<size_t>(found - known.begin());
        if (index == candidate.modules.size()) {
            IcdCallbackModule module;
            if (!DescribeModule(owner.value, module)) return FALSE;
            known[index] = owner.value;
            candidate.modules.push_back(std::move(module));
        }
        const auto offset = reinterpret_cast<uintptr_t>(entry) - reinterpret_cast<uintptr_t>(owner.value);
        if (offset >= candidate.modules[index].imageBytes) return Fail(ERROR_INVALID_ADDRESS);
        candidate.entries[slot] = {static_cast<uint32_t>(index + 1), static_cast<uint32_t>(offset)};
    }
    output = std::move(candidate);
    SetLastError(error);
    return TRUE;
}

BOOL ResolveIcdCallbackSource(const IcdCallbackSource& source, ResolvedIcdCallbacks& output)
{
    const DWORD error = GetLastError();
    if (source.modules.empty() || source.modules.size() > source.entries.size()) return Fail(ERROR_INVALID_DATA);
    ResolvedIcdCallbacks candidate;
    std::array<bool, 9> used{};
    for (const auto& identity : source.modules) {
        if (identity.path.empty() || identity.path.size() >= 32768 || identity.path.find(L'\0') != std::wstring::npos)
            return Fail(ERROR_INVALID_DATA);
        ModuleReference owner;
        if (!GetModuleHandleExW(0, identity.path.c_str(), &owner.value)) return FALSE;
        if (std::find(candidate.modules.begin(), candidate.modules.end(), owner.value) != candidate.modules.end())
            return Fail(ERROR_INVALID_DATA);
        IcdCallbackModule actual;
        if (!DescribeModule(owner.value, actual)) return FALSE;
        if (!SameModule(identity, actual)) return Fail(ERROR_REVISION_MISMATCH);
        candidate.modules.push_back(owner.value);
        owner.value = nullptr;
    }
    std::array<PROC, 9> entries{};
    for (size_t slot = 0; slot < entries.size(); ++slot) {
        const auto entry = source.entries[slot];
        if (!entry.module) {
            if (entry.rva || Required(slot)) return Fail(ERROR_INVALID_DATA);
            continue;
        }
        if (entry.module > candidate.modules.size()) return Fail(ERROR_INVALID_DATA);
        const auto index = entry.module - 1;
        if (entry.rva >= source.modules[index].imageBytes) return Fail(ERROR_INVALID_ADDRESS);
        const auto module = candidate.modules[index];
        entries[slot] = reinterpret_cast<PROC>(reinterpret_cast<uintptr_t>(module) + entry.rva);
        if (!EntryPage(entries[slot], module, slot)) return Fail(ERROR_INVALID_ADDRESS);
        used[index] = true;
    }
    if (std::find(used.begin(), used.begin() + candidate.modules.size(), false) != used.begin() + candidate.modules.size())
        return Fail(ERROR_INVALID_DATA);
    std::memcpy(&candidate.callbacks, entries.data(), sizeof(candidate.callbacks));
    output = std::move(candidate);
    SetLastError(error);
    return TRUE;
}
}
