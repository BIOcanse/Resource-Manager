#pragma once

#include "OverlayProtocol.h"
#include <memory>
#include <mutex>
#include <vector>

namespace OverlaySection
{
struct Bitmap
{
    uint64_t generation;
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    std::vector<uint8_t> pixels;
};

class Reader
{
    std::mutex mutex_;
    HANDLE section_ = nullptr;
    OverlayProtocol::Header* header_ = nullptr;
    size_t mappedBytes_ = 0;
    ULONGLONG lastOpenAttempt_ = 0;
    std::shared_ptr<const Bitmap> bitmap_;

    void OpenIfNeeded()
    {
        if (header_ || GetTickCount64() - lastOpenAttempt_ < 250) return;
        lastOpenAttempt_ = GetTickCount64();
        wchar_t name[96]{};
        if (!OverlayProtocol::SectionName(name)) return;
        HANDLE section = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
        if (!section) return;
        void* view = MapViewOfFile(section, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);
        MEMORY_BASIC_INFORMATION memory{};
        if (!view || !VirtualQuery(view, &memory, sizeof(memory))
            || memory.RegionSize < OverlayProtocol::HeaderBytes)
        {
            if (view) UnmapViewOfFile(view);
            CloseHandle(section);
            return;
        }
        auto* candidate = static_cast<OverlayProtocol::Header*>(view);
        if (candidate->magic != OverlayProtocol::Magic || candidate->version != OverlayProtocol::Version
            || candidate->headerBytes != OverlayProtocol::HeaderBytes
            || candidate->pixelCapacity > OverlayProtocol::MaximumPixelBytes
            || candidate->pixelCapacity > memory.RegionSize - OverlayProtocol::HeaderBytes)
        {
            UnmapViewOfFile(view);
            CloseHandle(section);
            return;
        }
        section_ = section;
        header_ = candidate;
        mappedBytes_ = memory.RegionSize;
    }

public:
    ~Reader()
    {
        if (header_) UnmapViewOfFile(header_);
        if (section_) CloseHandle(section_);
    }

    std::shared_ptr<const Bitmap> Frame(HWND window, uint32_t width, uint32_t height)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        OpenIfNeeded();
        auto* header = header_;
        if (!header) return {};
        if (header->targetWindow && header->targetWindow != reinterpret_cast<uint64_t>(window)) return {};
        InterlockedIncrement64(&header->frameCounter);
        InterlockedExchange(&header->swapChainWidth, static_cast<LONG>(width));
        InterlockedExchange(&header->swapChainHeight, static_cast<LONG>(height));
        if (!OverlayProtocol::Read32(&header->enabled)) return {};
        const LONG64 generation = OverlayProtocol::Read64(&header->generation);
        if (generation <= 0 || (generation & 1)) return {};
        if (bitmap_ && bitmap_->generation == static_cast<uint64_t>(generation)) return bitmap_;
        const uint32_t pixelWidth = header->width;
        const uint32_t pixelHeight = header->height;
        const uint32_t stride = header->stride;
        if (!pixelWidth || !pixelHeight || pixelWidth > 8192 || pixelHeight > 8192
            || uint64_t(pixelWidth) * 4 > stride || (stride & 3)
            || uint64_t(stride) * pixelHeight > header->pixelCapacity
            || uint64_t(stride) * pixelHeight > mappedBytes_ - OverlayProtocol::HeaderBytes) return {};
        auto next = std::make_shared<Bitmap>();
        next->generation = static_cast<uint64_t>(generation);
        next->width = pixelWidth;
        next->height = pixelHeight;
        next->stride = stride;
        const auto* pixels = reinterpret_cast<const uint8_t*>(header) + OverlayProtocol::HeaderBytes;
        next->pixels.assign(pixels, pixels + size_t(stride) * pixelHeight);
        if (OverlayProtocol::Read64(&header->generation) != generation) return {};
        bitmap_ = next;
        return bitmap_;
    }

    // Records a present when composition is unavailable for this swap chain.
    void ObserveFrame(HWND window, uint32_t width, uint32_t height)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        OpenIfNeeded();
        if (!header_ || (header_->targetWindow
            && header_->targetWindow != reinterpret_cast<uint64_t>(window))) return;
        InterlockedIncrement64(&header_->frameCounter);
        InterlockedExchange(&header_->swapChainWidth, static_cast<LONG>(width));
        InterlockedExchange(&header_->swapChainHeight, static_cast<LONG>(height));
    }

    void Drew(uint64_t generation)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        if (!header_) return;
        InterlockedExchange64(&header_->consumedGeneration, static_cast<LONG64>(generation));
        InterlockedIncrement64(&header_->drawSuccessCounter);
    }
};
}
