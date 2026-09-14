#include "DeviceObservationProbe.h"
#include <array>
#include <cstddef>
#include <cstdio>

using namespace ResourceManagerGpuObservation;
namespace {
unsigned checks = 0;
void Check(bool value, const char* name)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
    if (!value) throw std::runtime_error(name);
}
void VerifyEmpty(const Snapshot& snapshot)
{
    Check(snapshot.byteSize == 128 && snapshot.version == 3, "five-slot-header");
    for (const auto& entry : snapshot.api)
        Check(entry.returnedDeviceCount == 0 && entry.adapterLuid == 0 && entry.identity == Identity::NoDevice && entry.reserved == 0, "empty-api-is-a-value");
}
void VerifyExport(const wchar_t* path)
{
    const HMODULE module = LoadLibraryW(path);
    Check(module != nullptr, "load-current-observation-provider");
    // The provider installs process-lifetime hooks; keep its module loaded until this owned probe exits.
    auto snapshot = GpuObservationProbe::Read(module);
    VerifyEmpty(snapshot);
    using Read = DWORD(WINAPI*)(LPVOID);
    const auto address = GetProcAddress(module, "ResourceManagerGpuPlacementReadDeviceObservations");
    Read read{};
    static_assert(sizeof(read) == sizeof(address));
    std::memcpy(&read, &address, sizeof(read));
    snapshot.byteSize = 104;
    snapshot.version = 2;
    const auto before = snapshot;
    Check(read(&snapshot) == 0 && std::memcmp(&snapshot, &before, sizeof(snapshot)) == 0, "provider-rejects-old-layout-without-writing");
}
}
int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try {
        Check(argc == 4, "explicit-output-and-two-current-providers");
        Check(static_cast<uint32_t>(Api::OpenGL) == 4 && static_cast<uint32_t>(Api::Count) == 5 && offsetof(Snapshot, api) == 8, "stable-api-order-offset");
        Store store;
        struct Guarded { uint64_t before = 0x123456789abcdef0; Snapshot snapshot; uint64_t after = 0xfedcba9876543210; } guarded;
        Check(store.CopyTo(&guarded.snapshot) == 1, "empty-store-copy");
        VerifyEmpty(guarded.snapshot);
        Check(store.CopyTo(nullptr) == 0, "null-buffer-rejected");
        for (const auto size : {0U, 80U, 104U, 127U, 129U}) {
            Snapshot bad; bad.byteSize = size; const auto before = bad;
            Check(store.CopyTo(&bad) == 0 && std::memcmp(&bad, &before, sizeof(bad)) == 0, "bad-size-no-write");
        }
        for (const auto version : {0U, 1U, 2U, 4U}) {
            Snapshot bad; bad.version = version; const auto before = bad;
            Check(store.CopyTo(&bad) == 0 && std::memcmp(&bad, &before, sizeof(bad)) == 0, "bad-version-no-write");
        }
        for (uint32_t api = 0; api < 5; ++api) {
            const LUID luid{0x87654321U + api, static_cast<LONG>(0x81234567U + api)};
            for (uint32_t count = 0; count <= api; ++count) store.Publish(static_cast<Api>(api), Identity::Adapter, luid);
        }
        Check(store.CopyTo(&guarded.snapshot) == 1, "all-api-copy");
        for (uint32_t api = 0; api < 5; ++api) {
            const auto& entry = guarded.snapshot.api[api];
            Check(entry.returnedDeviceCount == api + 1 && entry.adapterLuid == ((uint64_t(0x81234567U + api) << 32) | (0x87654321U + api)) && entry.identity == Identity::Adapter && entry.reserved == 0, "isolated-unsigned-api-slot");
        }
        const auto stable = guarded.snapshot;
        Check(store.CopyTo(&guarded.snapshot) == 1 && std::memcmp(&stable, &guarded.snapshot, sizeof(stable)) == 0, "read-does-not-publish");
        Check(guarded.before == 0x123456789abcdef0 && guarded.after == 0xfedcba9876543210, "copy-preserves-canaries");
        const HANDLE file = CreateFileW(argv[1], GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        Check(file != INVALID_HANDLE_VALUE, "fresh-snapshot-output");
        DWORD written = 0;
        const BOOL saved = WriteFile(file, &stable, sizeof(stable), &written, nullptr);
        CloseHandle(file);
        Check(saved && written == 128, "native-snapshot-written");
        store.Publish(Api::OpenGL, Identity::Unavailable, {1, 1});
        Check(store.CopyTo(&guarded.snapshot) == 1, "unknown-opengl-copy");
        Check(guarded.snapshot.api[4].returnedDeviceCount == 6 && guarded.snapshot.api[4].adapterLuid == 0 && guarded.snapshot.api[4].identity == Identity::Unavailable, "unknown-opengl-clears-identity");
        Check(std::memcmp(stable.api, guarded.snapshot.api, sizeof(DeviceObservation) * 4) == 0, "other-apis-unchanged");
        VerifyExport(argv[2]);
        VerifyExport(argv[3]);
        std::printf("{\"passed\":true,\"checks\":%u,\"version\":3,\"byteSize\":128,\"apiCount\":5,\"gpuCreated\":false}\n", checks);
        return 0;
    } catch (const std::exception& error) {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
