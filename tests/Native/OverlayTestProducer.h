#pragma once

#include "../../src/Core/Native/PerformanceOverlay/OverlayProtocol.h"
#include <sddl.h>
#include <cstdio>
#include <cstring>

struct OverlayTestProducer
{
    HANDLE mapping = nullptr;
    OverlayProtocol::Header* header = nullptr;
    LONG windowWidth = 0;
    LONG windowHeight = 0;
    static constexpr uint32_t Width = 128;
    static constexpr uint32_t Height = 48;
    static constexpr uint32_t PixelBytes = Width * Height * 4;

    bool Start(HWND window)
    {
        RECT bounds{};
        if (!GetClientRect(window, &bounds)) return false;
        windowWidth = bounds.right;
        windowHeight = bounds.bottom;
        wchar_t name[96]{};
        if (!OverlayProtocol::SectionName(name)) return false;
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
        DWORD size = 0;
        GetTokenInformation(token, TokenUser, nullptr, 0, &size);
        auto* user = static_cast<TOKEN_USER*>(HeapAlloc(GetProcessHeap(), 0, size));
        const bool gotUser = user && GetTokenInformation(token, TokenUser, user, size, &size);
        CloseHandle(token);
        if (!gotUser) { if (user) HeapFree(GetProcessHeap(), 0, user); return false; }
        LPWSTR sid = nullptr;
        const bool gotSid = ConvertSidToStringSidW(user->User.Sid, &sid);
        HeapFree(GetProcessHeap(), 0, user);
        if (!gotSid) return false;
        wchar_t sddl[256]{};
        swprintf(sddl, 256, L"D:P(A;;GA;;;SY)(A;;GA;;;%ls)", sid);
        LocalFree(sid);
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &descriptor, nullptr)) return false;
        SECURITY_ATTRIBUTES attributes{ sizeof(attributes), descriptor, FALSE };
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0,
            OverlayProtocol::HeaderBytes + PixelBytes, name);
        LocalFree(descriptor);
        if (!mapping) return false;
        header = static_cast<OverlayProtocol::Header*>(MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0));
        if (!header) { CloseHandle(mapping); mapping = nullptr; return false; }
        std::memset(header, 0, OverlayProtocol::HeaderBytes + PixelBytes);
        header->magic = OverlayProtocol::Magic;
        header->version = OverlayProtocol::Version;
        header->headerBytes = OverlayProtocol::HeaderBytes;
        header->pixelCapacity = PixelBytes;
        header->targetWindow = reinterpret_cast<uint64_t>(window);
        header->width = Width;
        header->height = Height;
        header->stride = Width * 4;
        InterlockedExchange(&header->enabled, 1);
        auto* pixels = reinterpret_cast<uint8_t*>(header) + OverlayProtocol::HeaderBytes;
        for (size_t i = 0; i < PixelBytes; i += 4)
        {
            pixels[i] = 0; pixels[i + 1] = 48; pixels[i + 2] = 192; pixels[i + 3] = 192;
        }
        InterlockedExchange64(&header->generation, 2);
        return true;
    }

    bool Check(long long expectedFrames) const
    {
        if (!header) return false;
        const long long frames = OverlayProtocol::Read64(&header->frameCounter);
        const long long draws = OverlayProtocol::Read64(&header->drawSuccessCounter);
        const long long consumed = OverlayProtocol::Read64(&header->consumedGeneration);
        std::printf("overlay: frames=%lld draws=%lld consumed=%lld swap=%ldx%ld\n",
            frames, draws, consumed,
            OverlayProtocol::Read32(&header->swapChainWidth), OverlayProtocol::Read32(&header->swapChainHeight));
        return frames >= expectedFrames - 2 && draws >= expectedFrames * 7 / 10
            && draws <= expectedFrames * 8 / 10 && consumed == 4
            && header->swapChainWidth == windowWidth && header->swapChainHeight == windowHeight;
    }

    void ChangeBitmap()
    {
        InterlockedExchange64(&header->generation, 3);
        auto* pixels = reinterpret_cast<uint8_t*>(header) + OverlayProtocol::HeaderBytes;
        for (size_t i = 0; i < PixelBytes; i += 4)
        {
            pixels[i] = 0; pixels[i + 1] = 192; pixels[i + 2] = 0; pixels[i + 3] = 192;
        }
        InterlockedExchange64(&header->generation, 4);
    }

    void Disable() { InterlockedExchange(&header->enabled, 0); }

    ~OverlayTestProducer()
    {
        if (header) { InterlockedExchange(&header->enabled, 0); UnmapViewOfFile(header); }
        if (mapping) CloseHandle(mapping);
    }
};
