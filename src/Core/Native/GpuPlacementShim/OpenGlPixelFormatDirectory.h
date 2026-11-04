#pragma once
#include <windows.h>
#include <climits>
#include <cstring>
#include <memory>
#include <vector>

namespace ResourceManagerOpenGl
{
class PixelFormatDirectory final
{
    const std::vector<PIXELFORMATDESCRIPTOR> formats_;
    const int nativeCount_;

    PixelFormatDirectory(std::vector<PIXELFORMATDESCRIPTOR> formats, int nativeCount)
        : formats_(std::move(formats)), nativeCount_(nativeCount) {}

public:
    // Explicit preparation only. The owning runtime publishes a complete returned record.
    static std::shared_ptr<const PixelFormatDirectory> Create(
        std::vector<PIXELFORMATDESCRIPTOR> formats, int nativeCount)
    {
        const DWORD error = GetLastError();
        if (nativeCount <= 0 || formats.size() > static_cast<size_t>(INT_MAX) ||
            formats.size() < static_cast<size_t>(nativeCount)) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return nullptr;
        }
        for (size_t i = 0; i < formats.size(); ++i) {
            const auto& format = formats[i];
            if (format.nSize != sizeof(PIXELFORMATDESCRIPTOR) || format.nVersion != 1 ||
                (i >= static_cast<size_t>(nativeCount) && !(format.dwFlags & PFD_GENERIC_FORMAT))) {
                SetLastError(ERROR_INVALID_DATA);
                return nullptr;
            }
        }
        try {
            auto result = std::shared_ptr<const PixelFormatDirectory>(
                new PixelFormatDirectory(std::move(formats), nativeCount));
            SetLastError(error);
            return result;
        } catch (const std::bad_alloc&) {
            SetLastError(ERROR_NOT_ENOUGH_MEMORY);
            return nullptr;
        }
    }

    int Count() const noexcept { return static_cast<int>(formats_.size()); }
    int NativeCount() const noexcept { return nativeCount_; }

    int Describe(int index, UINT capacity, PIXELFORMATDESCRIPTOR* output) const noexcept
    {
        // GDI's count-only query ignores both index and output capacity.
        if (!output || capacity == 0) return Count();
        if (index <= 0 || index > Count() || capacity < sizeof(PIXELFORMATDESCRIPTOR)) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return 0;
        }
        std::memcpy(output, &formats_[static_cast<size_t>(index - 1)], sizeof(*output));
        return Count();
    }
};
}

