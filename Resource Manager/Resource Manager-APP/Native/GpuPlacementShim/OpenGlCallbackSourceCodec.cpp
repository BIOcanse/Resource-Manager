#include "OpenGlCallbackSourceCodec.h"
#include <algorithm>
#include <utility>

namespace ResourceManagerOpenGl
{
namespace
{
static_assert(sizeof(wchar_t) == 2);
BOOL Invalid() { SetLastError(ERROR_INVALID_DATA); return FALSE; }
bool Shape(const IcdCallbackSource& source)
{
    if (source.modules.empty() || source.modules.size() > 9) return false;
    std::array<bool, 9> used{};
    for (const auto& module : source.modules)
        if (module.machine != IMAGE_FILE_MACHINE_AMD64 || !module.imageBytes || module.path.empty()
            || module.path.size() > MaximumCallbackPathCharacters || module.path.find(L'\0') != std::wstring::npos) return false;
    for (size_t slot = 0; slot < source.entries.size(); ++slot) {
        const auto& entry = source.entries[slot];
        if (!entry.module) {
            if (entry.rva || slot == 0 || slot == 1 || slot == 2 || slot == 5) return false;
        } else {
            if (entry.module > source.modules.size() || entry.rva >= source.modules[entry.module - 1].imageBytes) return false;
            used[entry.module - 1] = true;
        }
    }
    return std::find(used.begin(), used.begin() + source.modules.size(), false) == used.begin() + source.modules.size();
}
template<typename T> void Append(std::vector<unsigned char>& bytes, T value)
{
    for (size_t i = 0; i < sizeof(T); ++i) bytes.push_back(static_cast<unsigned char>(value >> (8 * i)));
}
class Reader
{
    const unsigned char* bytes;
    size_t remaining;
public:
    Reader(const unsigned char* bytes, size_t length) : bytes(bytes), remaining(length) {}
    template<typename T> bool Number(T& value)
    {
        if (remaining < sizeof(T)) return false;
        value = 0;
        for (size_t i = 0; i < sizeof(T); ++i) value |= static_cast<T>(static_cast<T>(bytes[i]) << (8 * i));
        bytes += sizeof(T); remaining -= sizeof(T); return true;
    }
    bool Copy(void* output, size_t size)
    {
        if (size > remaining) return false;
        std::memcpy(output, bytes, size); bytes += size; remaining -= size; return true;
    }
    size_t Remaining() const { return remaining; }
};
}
BOOL EncodeIcdCallbackSource(const IcdCallbackSource& source, std::vector<unsigned char>& output)
{
    const DWORD error = GetLastError();
    if (!Shape(source)) return Invalid();
    std::vector<unsigned char> candidate;
    Append(candidate, static_cast<uint32_t>(source.modules.size()));
    for (const auto& entry : source.entries) { Append(candidate, entry.module); Append(candidate, entry.rva); }
    for (const auto& module : source.modules) {
        Append(candidate, static_cast<uint16_t>(module.machine)); Append(candidate, module.imageBytes);
        Append(candidate, module.timestamp); Append(candidate, static_cast<uint32_t>(module.path.size()));
        candidate.insert(candidate.end(), module.sha256.begin(), module.sha256.end());
        for (const auto character : module.path) Append(candidate, static_cast<uint16_t>(character));
    }
    output = std::move(candidate); SetLastError(error); return TRUE;
}
BOOL DecodeIcdCallbackSource(const unsigned char* bytes, size_t length, IcdCallbackSource& output)
{
    const DWORD error = GetLastError();
    if (!bytes || length > MaximumCallbackSourceBytes) return Invalid();
    Reader reader(bytes, length);
    uint32_t count{};
    if (!reader.Number(count) || !count || count > 9) return Invalid();
    IcdCallbackSource candidate;
    for (auto& entry : candidate.entries)
        if (!reader.Number(entry.module) || !reader.Number(entry.rva)) return Invalid();
    candidate.modules.resize(count);
    for (auto& module : candidate.modules) {
        uint32_t characters{};
        if (!reader.Number(module.machine) || !reader.Number(module.imageBytes) || !reader.Number(module.timestamp)
            || !reader.Number(characters) || !characters || characters > MaximumCallbackPathCharacters
            || !reader.Copy(module.sha256.data(), module.sha256.size()) || characters > reader.Remaining() / 2) return Invalid();
        module.path.resize(characters);
        for (auto& character : module.path) {
            uint16_t value{}; if (!reader.Number(value)) return Invalid(); character = static_cast<wchar_t>(value);
        }
    }
    if (reader.Remaining() || !Shape(candidate)) return Invalid();
    output = std::move(candidate); SetLastError(error); return TRUE;
}
}
