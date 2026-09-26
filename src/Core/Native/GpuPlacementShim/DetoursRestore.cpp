// Startup restore subset derived from Microsoft Research Detours 4.0.1.
// Copyright (c) Microsoft Corporation. See third_party/detours/LICENSE.md.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstdint>
#include <cstring>

namespace
{
constexpr DWORD DetourSectionSignature = 0x00727444;
constexpr GUID DetourExeRestoreGuid = {
    0x2ed7a3ff, 0x3339, 0x4a8d,
    {0x80, 0x5c, 0xd4, 0x98, 0x15, 0x3f, 0xc2, 0x8f}};

#pragma pack(push, 8)
struct DetourSectionHeader
{
    DWORD headerSize;
    DWORD signature;
    DWORD dataOffset;
    DWORD dataSize;
    DWORD originalImportVirtualAddress;
    DWORD originalImportSize;
    DWORD originalBoundImportVirtualAddress;
    DWORD originalBoundImportSize;
    DWORD originalIatVirtualAddress;
    DWORD originalIatSize;
    DWORD originalSizeOfImage;
    DWORD prePeSize;
    DWORD originalClrFlags;
    DWORD reserved1;
    DWORD reserved2;
    DWORD reserved3;
};

struct DetourSectionRecord
{
    DWORD byteCount;
    DWORD reserved;
    GUID guid;
};

struct DetourClrHeader
{
    ULONG byteCount;
    USHORT majorRuntimeVersion;
    USHORT minorRuntimeVersion;
    IMAGE_DATA_DIRECTORY metadata;
    ULONG flags;
};

struct DetourExeRestore
{
    DWORD byteCount;
    DWORD dosHeaderByteCount;
    DWORD ntHeaderByteCount;
    DWORD clrHeaderByteCount;
    PBYTE dosHeaderAddress;
    PBYTE ntHeaderAddress;
    PBYTE clrHeaderAddress;
    IMAGE_DOS_HEADER dosHeader;
    union
    {
        IMAGE_NT_HEADERS ntHeaders;
        IMAGE_NT_HEADERS32 ntHeaders32;
        IMAGE_NT_HEADERS64 ntHeaders64;
        BYTE raw[sizeof(IMAGE_NT_HEADERS64) + sizeof(IMAGE_SECTION_HEADER) * 32];
    };
    DetourClrHeader clrHeader;
};
#pragma pack(pop)

bool SameGuid(const GUID& left, const GUID& right)
{
    return std::memcmp(&left, &right, sizeof(GUID)) == 0;
}

bool IsReadableRegion(const MEMORY_BASIC_INFORMATION& memory)
{
    return memory.State == MEM_COMMIT
        && (memory.Protect & PAGE_GUARD) == 0
        && (memory.Protect & 0xff) != PAGE_NOACCESS;
}

bool ContainsRange(
    const MEMORY_BASIC_INFORMATION& memory,
    const void* address,
    size_t byteCount)
{
    const auto regionStart = reinterpret_cast<uintptr_t>(memory.BaseAddress);
    if (memory.RegionSize > UINTPTR_MAX - regionStart)
    {
        return false;
    }
    const auto regionEnd = regionStart + memory.RegionSize;
    const auto rangeStart = reinterpret_cast<uintptr_t>(address);
    return rangeStart >= regionStart
        && rangeStart <= regionEnd
        && byteCount <= regionEnd - rangeStart;
}

bool TryFindRestorePayload(DetourExeRestore*& restore, DWORD& restoreByteCount)
{
    BYTE* cursor = nullptr;
    MEMORY_BASIC_INFORMATION memory{};
    while (VirtualQuery(cursor, &memory, sizeof(memory)) != 0)
    {
        BYTE* const next = static_cast<BYTE*>(memory.BaseAddress) + memory.RegionSize;
        if (next <= cursor)
        {
            break;
        }
        cursor = next;

        if (!IsReadableRegion(memory)
            || !ContainsRange(memory, memory.BaseAddress, sizeof(IMAGE_DOS_HEADER)))
        {
            continue;
        }

        auto* const dosHeader = static_cast<IMAGE_DOS_HEADER*>(memory.BaseAddress);
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE
            || dosHeader->e_lfanew < static_cast<LONG>(sizeof(IMAGE_DOS_HEADER)))
        {
            continue;
        }

        auto* const ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(
            static_cast<BYTE*>(memory.BaseAddress) + dosHeader->e_lfanew);
        if (!ContainsRange(memory, ntHeaders, sizeof(IMAGE_NT_HEADERS))
            || ntHeaders->Signature != IMAGE_NT_SIGNATURE
            || ntHeaders->FileHeader.NumberOfSections == 0)
        {
            continue;
        }

        auto* const sections = reinterpret_cast<IMAGE_SECTION_HEADER*>(
            reinterpret_cast<BYTE*>(&ntHeaders->OptionalHeader)
            + ntHeaders->FileHeader.SizeOfOptionalHeader);
        const size_t sectionBytes = static_cast<size_t>(ntHeaders->FileHeader.NumberOfSections)
            * sizeof(IMAGE_SECTION_HEADER);
        if (!ContainsRange(memory, sections, sectionBytes))
        {
            continue;
        }

        for (WORD index = 0; index < ntHeaders->FileHeader.NumberOfSections; ++index)
        {
            if (std::memcmp(sections[index].Name, ".detour", 7) != 0)
            {
                continue;
            }

            auto* const section = static_cast<BYTE*>(memory.BaseAddress)
                + sections[index].VirtualAddress;
            if (!ContainsRange(memory, section, sizeof(DetourSectionHeader)))
            {
                continue;
            }

            auto* const header = reinterpret_cast<DetourSectionHeader*>(section);
            if (header->signature != DetourSectionSignature
                || header->headerSize < sizeof(DetourSectionHeader)
                || header->dataOffset < header->headerSize
                || header->dataSize < header->dataOffset
                || !ContainsRange(memory, section, header->dataSize))
            {
                continue;
            }

            BYTE* recordCursor = section + header->dataOffset;
            BYTE* const recordEnd = section + header->dataSize;
            while (recordCursor < recordEnd)
            {
                if (static_cast<size_t>(recordEnd - recordCursor) < sizeof(DetourSectionRecord))
                {
                    break;
                }

                auto* const record = reinterpret_cast<DetourSectionRecord*>(recordCursor);
                if (record->byteCount < sizeof(DetourSectionRecord)
                    || record->byteCount > static_cast<DWORD>(recordEnd - recordCursor))
                {
                    break;
                }

                if (SameGuid(record->guid, DetourExeRestoreGuid))
                {
                    restoreByteCount = record->byteCount - sizeof(DetourSectionRecord);
                    restore = reinterpret_cast<DetourExeRestore*>(record + 1);
                    return restoreByteCount >= sizeof(DetourExeRestore);
                }
                recordCursor += record->byteCount;
            }
        }
    }
    return false;
}

DWORD AdjustExecuteProtection(DWORD oldProtection, DWORD requestedProtection)
{
    constexpr DWORD executeProtection = PAGE_EXECUTE | PAGE_EXECUTE_READ
        | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    constexpr DWORD nonExecuteProtection = PAGE_NOACCESS | PAGE_READONLY
        | PAGE_READWRITE | PAGE_WRITECOPY;
    constexpr DWORD attributes = ~(executeProtection | nonExecuteProtection);
    const bool wasExecutable = (oldProtection & executeProtection) != 0;
    const bool willBeExecutable = (requestedProtection & executeProtection) != 0;
    if (wasExecutable && !willBeExecutable)
    {
        return ((requestedProtection & nonExecuteProtection) << 4)
            | (requestedProtection & attributes);
    }
    if (!wasExecutable && willBeExecutable)
    {
        return ((requestedProtection & executeProtection) >> 4)
            | (requestedProtection & attributes);
    }
    return requestedProtection;
}

bool ProtectSameExecute(
    void* address,
    size_t byteCount,
    DWORD requestedProtection,
    DWORD& oldProtection)
{
    MEMORY_BASIC_INFORMATION memory{};
    return VirtualQuery(address, &memory, sizeof(memory)) != 0
        && ContainsRange(memory, address, byteCount)
        && VirtualProtect(
            address,
            byteCount,
            AdjustExecuteProtection(memory.Protect, requestedProtection),
            &oldProtection) != FALSE;
}
}

// This is the startup-only restore subset of Microsoft Detours 4.0.1.
// The payload layout and restoration order intentionally match DetourRestoreAfterWith.
bool RestoreDetoursStartupHeaders()
{
    DetourExeRestore* restore = nullptr;
    DWORD restoreByteCount = 0;
    if (!TryFindRestorePayload(restore, restoreByteCount)
        || restore == nullptr
        || restore->byteCount != sizeof(DetourExeRestore)
        || restore->byteCount > restoreByteCount
        || restore->dosHeaderAddress == nullptr
        || restore->ntHeaderAddress == nullptr
        || restore->dosHeaderByteCount == 0
        || restore->dosHeaderByteCount > sizeof(restore->dosHeader)
        || restore->ntHeaderByteCount == 0
        || restore->ntHeaderByteCount > sizeof(restore->raw)
        || restore->clrHeaderByteCount > sizeof(restore->clrHeader))
    {
        return false;
    }

    bool clrWasPromoted = false;
    if (restore->clrHeaderAddress != nullptr)
    {
        MEMORY_BASIC_INFORMATION clrMemory{};
        if (VirtualQuery(restore->clrHeaderAddress, &clrMemory, sizeof(clrMemory)) == 0
            || !ContainsRange(clrMemory, restore->clrHeaderAddress, sizeof(DetourClrHeader)))
        {
            return false;
        }
        clrWasPromoted = restore->clrHeader.flags
            != reinterpret_cast<DetourClrHeader*>(restore->clrHeaderAddress)->flags;
    }

    DWORD dosProtection = 0;
    DWORD ntProtection = 0;
    DWORD ignoredProtection = 0;
    if (!ProtectSameExecute(
            restore->dosHeaderAddress,
            restore->dosHeaderByteCount,
            PAGE_EXECUTE_READWRITE,
            dosProtection))
    {
        return false;
    }
    if (!ProtectSameExecute(
            restore->ntHeaderAddress,
            restore->ntHeaderByteCount,
            PAGE_EXECUTE_READWRITE,
            ntProtection))
    {
        VirtualProtect(
            restore->dosHeaderAddress,
            restore->dosHeaderByteCount,
            dosProtection,
            &ignoredProtection);
        return false;
    }

    std::memcpy(
        restore->dosHeaderAddress,
        &restore->dosHeader,
        restore->dosHeaderByteCount);
    std::memcpy(
        restore->ntHeaderAddress,
        &restore->ntHeaders,
        restore->ntHeaderByteCount);

    bool succeeded = restore->clrHeaderAddress == nullptr || clrWasPromoted;
    if (restore->clrHeaderAddress != nullptr && !clrWasPromoted)
    {
        DWORD clrProtection = 0;
        if (ProtectSameExecute(
                restore->clrHeaderAddress,
                restore->clrHeaderByteCount,
                PAGE_EXECUTE_READWRITE,
                clrProtection))
        {
            std::memcpy(
                restore->clrHeaderAddress,
                &restore->clrHeader,
                restore->clrHeaderByteCount);
            VirtualProtect(
                restore->clrHeaderAddress,
                restore->clrHeaderByteCount,
                clrProtection,
                &ignoredProtection);
            succeeded = true;
        }
    }

    VirtualProtect(
        restore->ntHeaderAddress,
        restore->ntHeaderByteCount,
        ntProtection,
        &ignoredProtection);
    VirtualProtect(
        restore->dosHeaderAddress,
        restore->dosHeaderByteCount,
        dosProtection,
        &ignoredProtection);
    return succeeded;
}
