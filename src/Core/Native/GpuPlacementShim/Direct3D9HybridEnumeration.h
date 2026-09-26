#pragma once
#include <windows.h>
#include <bcrypt.h>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <memory>
#include <new>
#include <string>

namespace ResourceManagerD3D9
{
// This private store-only entry is qualified for one exact system image, not an API version range.
class HybridEnumeration
{
    static constexpr DWORD QualifiedFileSize = 1774184;
    static constexpr size_t EntryRva = 0xb5130;
    static constexpr size_t ValueRva = 0x19b084;
    volatile LONG* value_ = nullptr;

    static bool MatchesFile(const wchar_t* path) noexcept
    {
        const HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER size{};
        bool valid = GetFileSizeEx(file, &size) && size.QuadPart == QualifiedFileSize;
        auto bytes = valid ? std::unique_ptr<unsigned char[]>(new (std::nothrow) unsigned char[QualifiedFileSize]) : nullptr;
        DWORD count = 0;
        valid = valid && bytes && ReadFile(file, bytes.get(), QualifiedFileSize, &count, nullptr) && count == QualifiedFileSize;
        const bool closed = CloseHandle(file) != FALSE;
        if (!valid || !closed) return false;

        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        unsigned char digest[32]{};
        valid = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0;
        if (valid) valid = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
        if (valid) valid = BCryptHashData(hash, bytes.get(), QualifiedFileSize, 0) >= 0;
        if (valid) valid = BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
        if (hash && BCryptDestroyHash(hash) < 0) valid = false;
        if (algorithm && BCryptCloseAlgorithmProvider(algorithm, 0) < 0) valid = false;
        constexpr unsigned char expected[]{
            0xd6,0x48,0x5a,0x26,0x2c,0x14,0xce,0xf8,0xa6,0x9e,0x62,0xf3,0xb5,0x9a,0xc6,0x27,
            0xc6,0xa8,0xae,0x48,0x94,0xee,0x98,0x0f,0x5b,0x70,0xbb,0x16,0x03,0x85,0xc2,0xef };
        return valid && std::memcmp(digest, expected, sizeof(expected)) == 0;
    }

public:
    static bool IsQualifiedValue(UINT value) noexcept
    {
        return value == 0 || value == 1 || value == 3 || value == 4;
    }

    // The caller holds the module reference for the lifetime of any installed hook.
    bool Open(HMODULE module)
    {
        if (value_ || !module) return false;
        wchar_t system[32768]{}, path[32768]{};
        const UINT systemLength = GetSystemDirectoryW(system, 32768);
        const DWORD pathLength = GetModuleFileNameW(module, path, 32768);
        if (!systemLength || systemLength >= 32768 || !pathLength || pathLength >= 32768) return false;
        const auto expectedPath = std::wstring(system, systemLength) + L"\\d3d9.dll";
        if (_wcsicmp(path, expectedPath.c_str()) != 0 || !MatchesFile(path)) return false;
        const auto* base = reinterpret_cast<const unsigned char*>(module);
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew >= 4096) return false;
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64
            || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC
            || nt->OptionalHeader.SizeOfImage < ValueRva + sizeof(LONG)) return false;
        if (reinterpret_cast<const unsigned char*>(GetProcAddress(module, MAKEINTRESOURCEA(16))) != base + EntryRva) return false;
        MEMORY_BASIC_INFORMATION code{}, data{};
        if (VirtualQuery(base + EntryRva, &code, sizeof(code)) != sizeof(code)
            || code.AllocationBase != module || code.Type != MEM_IMAGE || code.State != MEM_COMMIT
            || code.Protect != PAGE_EXECUTE_READ) return false;
        if (VirtualQuery(base + ValueRva, &data, sizeof(data)) != sizeof(data)
            || data.AllocationBase != module || data.Type != MEM_IMAGE || data.State != MEM_COMMIT
            || (data.Protect != PAGE_READWRITE && data.Protect != PAGE_WRITECOPY)) return false;
        constexpr unsigned char instructions[]{0x89,0x0d,0x4e,0x5f,0x0e,0x00,0xc3};
        if (std::memcmp(base + EntryRva, instructions, sizeof(instructions)) != 0) return false;
        static_assert(ValueRva % alignof(LONG) == 0 && sizeof(LONG) == 4);
        value_ = reinterpret_cast<volatile LONG*>(reinterpret_cast<unsigned char*>(module) + ValueRva);
        return true;
    }

    bool Read(UINT& value) const noexcept
    {
        if (!value_) return false;
        value = static_cast<UINT>(*value_);
        return IsQualifiedValue(value);
    }

    // A competing writer wins; never overwrite a different current value or retry.
    bool TryChange(UINT expected, UINT desired) const noexcept
    {
        return value_ && IsQualifiedValue(expected) && IsQualifiedValue(desired)
            && static_cast<UINT>(InterlockedCompareExchange(value_, static_cast<LONG>(desired), static_cast<LONG>(expected))) == expected;
    }
};
}
