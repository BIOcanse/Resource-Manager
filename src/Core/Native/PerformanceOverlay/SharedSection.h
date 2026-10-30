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
    // GPU caches use this local ID because a new section may restart generation at 2.
    uint64_t uploadId;
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
    // OpenIfNeeded opens SYNCHRONIZE once; CheckPublisher detects publisher exit
    // without reopening a live section or reading its pixel pages per present.
    HANDLE publisher_ = nullptr;
    uint32_t publisherProcessId_ = 0; // CheckPublisher rejects a live publisher handoff on the same section.
    size_t mappedBytes_ = 0;
    std::shared_ptr<const Bitmap> bitmap_;
    uint64_t nextUploadId_ = 0; // Frame assigns the ID consumed by GPU upload caches.
    ULONGLONG nextOpenAt_ = 0; // OpenIfNeeded bounds missing/disabled probes to 250 ms.

    // RuntimeRead: Sampled
    void Close()
    {
        bitmap_.reset();
        if (header_) UnmapViewOfFile(header_);
        if (section_) CloseHandle(section_);
        if (publisher_) CloseHandle(publisher_);
        header_ = nullptr;
        section_ = nullptr;
        publisher_ = nullptr;
        publisherProcessId_ = 0;
        mappedBytes_ = 0;
        nextOpenAt_ = GetTickCount64() + 250;
    }

    bool CheckPublisher()
    {
        // A process handle is signaled on exit; a zero timeout never blocks.
        if (publisher_ && header_->publisherProcessId == publisherProcessId_
            && WaitForSingleObject(publisher_, 0) == WAIT_TIMEOUT) return true;
        Close();
        return false;
    }

    // RuntimeRead: Sampled
    void OpenIfNeeded()
    {
        if (header_) { CheckPublisher(); return; }
        const ULONGLONG now = GetTickCount64();
        if (now < nextOpenAt_) return;
        nextOpenAt_ = now + 250;
        wchar_t name[96]{};
        if (!OverlayProtocol::SectionName(name)) return;
        section_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
        if (!section_) return;
        header_ = static_cast<OverlayProtocol::Header*>(MapViewOfFile(section_,
            FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, OverlayProtocol::HeaderBytes));
        if (!header_ || header_->magic != OverlayProtocol::Magic
            || header_->version != OverlayProtocol::Version
            || header_->headerBytes != OverlayProtocol::HeaderBytes
            || !header_->pixelCapacity
            || header_->pixelCapacity > OverlayProtocol::MaximumPixelBytes
            || !header_->publisherProcessId)
        {
            Close();
            return;
        }
        publisherProcessId_ = header_->publisherProcessId;
        publisher_ = OpenProcess(SYNCHRONIZE, FALSE, publisherProcessId_);
        mappedBytes_ = OverlayProtocol::HeaderBytes;
        CheckPublisher();
    }

    bool MapBytes(size_t bytes)
    {
        if (mappedBytes_ == bytes) return true;
        auto* view = static_cast<OverlayProtocol::Header*>(MapViewOfFile(section_,
            FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, bytes));
        if (!view) { Close(); return false; }
        UnmapViewOfFile(header_);
        header_ = view;
        mappedBytes_ = bytes;
        return true;
    }

    LONG RefreshState()
    {
        const LONG state = OverlayProtocol::Read32(&header_->enabled);
        if (state == OverlayProtocol::Handshake)
        {
            // An existing-section adoption can restart size discovery without
            // changing PID. Relinquish its old bitmap/full view once, then keep
            // only the actively used header until Publish or Disable.
            bitmap_.reset();
            if (!MapBytes(OverlayProtocol::HeaderBytes)) return OverlayProtocol::Disabled;
        }
        else if (state != OverlayProtocol::Enabled) Close();
        return state;
    }

    bool Record(HWND window, uint32_t width, uint32_t height)
    {
        if (header_->targetWindow && header_->targetWindow != reinterpret_cast<uint64_t>(window)) return false;
        InterlockedIncrement64(&header_->frameCounter);
        InterlockedExchange(&header_->swapChainWidth, static_cast<LONG>(width));
        InterlockedExchange(&header_->swapChainHeight, static_cast<LONG>(height));
        return true;
    }

public:
    ~Reader() { Close(); }

    std::shared_ptr<const Bitmap> Frame(HWND window, uint32_t width, uint32_t height)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        OpenIfNeeded();
        if (!header_) return {};
        const LONG state = RefreshState();
        if (!header_) return {};
        const bool matches = Record(window, width, height);
        if (state != OverlayProtocol::Enabled || !matches) return {};
        if (!MapBytes(size_t(OverlayProtocol::HeaderBytes) + header_->pixelCapacity)) return {};
        auto* header = header_;
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
        const auto* pixels = reinterpret_cast<const uint8_t*>(header) + OverlayProtocol::HeaderBytes;
        auto next = std::make_shared<Bitmap>();
        next->generation = static_cast<uint64_t>(generation);
        next->uploadId = ++nextUploadId_;
        next->width = pixelWidth;
        next->height = pixelHeight;
        next->stride = stride;
        next->pixels.assign(pixels, pixels + size_t(stride) * pixelHeight);
        if (OverlayProtocol::Read64(&header->generation) != generation) return {};
        bitmap_ = next;
        return bitmap_;
    }

    void ObserveFrame(HWND window, uint32_t width, uint32_t height)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        OpenIfNeeded();
        if (!header_) return;
        RefreshState();
        if (header_) Record(window, width, height);
    }

    void Drew(uint64_t generation, uint64_t uploadId)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        // Feedback belongs only to the section that supplied this draw.
        if (!header_) return;
        if (header_->publisherProcessId != publisherProcessId_) { Close(); return; }
        if (RefreshState() != OverlayProtocol::Enabled) return;
        if (!bitmap_ || bitmap_->uploadId != uploadId) return;
        InterlockedExchange64(&header_->consumedGeneration, static_cast<LONG64>(generation));
        InterlockedIncrement64(&header_->drawSuccessCounter);
    }
};
}
