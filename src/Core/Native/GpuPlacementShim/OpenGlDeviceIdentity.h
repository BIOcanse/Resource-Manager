#pragma once
#include "GpuPlacementDeviceObservation.h"
#include <GL/gl.h>
#include <cstddef>
#include <limits>
#include <string_view>

namespace ResourceManagerOpenGl
{
struct DeviceIdentityQueries
{
    decltype(&glGetString) text{};
    decltype(&glGetIntegerv) integer{};
    const GLubyte*(APIENTRY* indexedText)(GLenum, GLuint){};
    void(APIENTRY* bytes)(GLenum, GLubyte*){};
};

struct DeviceIdentityQueryLimits
{
    size_t versionBytes;
    size_t extensionBytes;
    GLint extensionCount;
};

struct DeviceIdentity
{
    ResourceManagerGpuObservation::Identity identity = ResourceManagerGpuObservation::Identity::Unavailable;
    LUID adapter{};
};

namespace DeviceIdentityDetail
{
inline bool Text(const GLubyte* value, size_t maximumBytes, std::string_view& result)
{
    if (!value) return false;
    size_t length = 0;
    while (length < maximumBytes && value[length]) ++length;
    if (length == maximumBytes) return false;
    result = {reinterpret_cast<const char*>(value), length};
    return true;
}

inline bool DesktopVersion(std::string_view text, unsigned& major)
{
    size_t position = 0;
    auto number = [&](unsigned& value) {
        const size_t start = position;
        value = 0;
        while (position < text.size() && text[position] >= '0' && text[position] <= '9') {
            const unsigned digit = static_cast<unsigned>(text[position++] - '0');
            if (value > (std::numeric_limits<unsigned>::max() - digit) / 10) return false;
            value = value * 10 + digit;
        }
        return position != start;
    };
    unsigned minor{};
    return number(major) && major && position < text.size() && text[position++] == '.'
        && number(minor) && (position == text.size() || text[position] == ' ' || text[position] == '.');
}

inline bool Win32IdentityExtension(std::string_view text)
{
    return text == "GL_EXT_memory_object_win32" || text == "GL_EXT_semaphore_win32";
}

inline DeviceIdentity Query(const DeviceIdentityQueries& queries, DeviceIdentityQueryLimits limits)
{
    if (!queries.text || !queries.bytes || !limits.versionBytes || !limits.extensionBytes
        || limits.extensionCount <= 0) return {};
    std::string_view text;
    unsigned major{};
    if (!Text(queries.text(GL_VERSION), limits.versionBytes, text) || !DesktopVersion(text, major)) return {};
    bool supported = false;
    if (major >= 3) {
        if (!queries.integer || !queries.indexedText) return {};
        GLint count = -1;
        queries.integer(0x821D /* GL_NUM_EXTENSIONS */, &count);
        if (count < 0 || count > limits.extensionCount) return {};
        for (GLint index = 0; index < count; ++index) {
            if (!Text(queries.indexedText(GL_EXTENSIONS, static_cast<GLuint>(index)), limits.extensionBytes, text)) return {};
            if (Win32IdentityExtension(text)) { supported = true; break; }
        }
    } else {
        if (!Text(queries.text(GL_EXTENSIONS), limits.extensionBytes, text)) return {};
        size_t position = 0;
        while (position < text.size()) {
            const size_t end = text.find(' ', position);
            const size_t length = (end == std::string_view::npos ? text.size() : end) - position;
            if (Win32IdentityExtension(text.substr(position, length))) { supported = true; break; }
            if (end == std::string_view::npos) break;
            position = end + 1;
        }
    }
    if (!supported) return {};
    LUID actual{};
    queries.bytes(0x9599 /* GL_DEVICE_LUID_EXT */, reinterpret_cast<GLubyte*>(&actual));
    if (!ResourceManagerGpuObservation::Pack(actual)) return {};
    return {ResourceManagerGpuObservation::Identity::Adapter, actual};
}
}

// The caller supplies real entries for its already-current desktop context.
inline DeviceIdentity QueryCurrentDeviceIdentity(const DeviceIdentityQueries& queries, DeviceIdentityQueryLimits limits)
{
    const DWORD priorError = GetLastError();
    const auto result = DeviceIdentityDetail::Query(queries, limits);
    SetLastError(priorError);
    return result;
}
}
