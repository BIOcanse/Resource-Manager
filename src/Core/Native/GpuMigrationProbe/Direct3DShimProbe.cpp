#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <initguid.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>
#include <array>
#include <atomic>
#include <thread>
#include "DeviceObservationProbe.h"

using Microsoft::WRL::ComPtr;

namespace
{
unsigned checks = 0;
constexpr UINT Width = 16;
constexpr UINT Height = 16;
constexpr float ClearColor[] = {0.25f, 0.5f, 0.75f, 1.0f};
const GUID UnsupportedInterface = {0xa38c6d3e, 0xee83, 0x4db5, {0x98, 0x91, 0x27, 0x4e, 0x86, 0x74, 0x57, 0x21}};

using Status = DWORD(WINAPI*)(LPVOID);
Status StatusExport(HMODULE module, const char* name)
{
    const FARPROC address = GetProcAddress(module, name);
    Status function = nullptr;
    static_assert(sizeof(function) == sizeof(address));
    std::memcpy(&function, &address, sizeof(function));
    return function;
}

void Require(bool passed, const char* name)
{
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, passed ? "true" : "false");
    std::fflush(stdout);
    if (!passed) throw std::runtime_error(name);
    ++checks;
}

void Hr(HRESULT result, const char* operation)
{
    if (FAILED(result))
    {
        std::fprintf(stderr, "%s HRESULT=0x%08lx\n", operation, static_cast<unsigned long>(result));
        throw std::runtime_error(operation);
    }
}

uint64_t LuidValue(LUID luid)
{
    return (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32) | luid.LowPart;
}

void CheckLuid(const char* api, LUID expected, LUID actual)
{
    std::printf("{\"api\":\"%s\",\"expectedLuid\":\"%016llx\",\"actualLuid\":\"%016llx\"}\n",
        api, static_cast<unsigned long long>(LuidValue(expected)), static_cast<unsigned long long>(LuidValue(actual)));
    Require(LuidValue(expected) == LuidValue(actual), "actual-device-luid");
}

void CheckObservation(HMODULE provider, ResourceManagerGpuObservation::Api api, LUID actual)
{
    const auto fact = GpuObservationProbe::Read(provider, api);
    Require(fact.returnedDeviceCount > 0 && fact.identity == ResourceManagerGpuObservation::Identity::Adapter &&
        fact.adapterLuid == LuidValue(actual), "observation-matches-returned-device");
    std::printf("{\"deviceObservation\":true,\"apiIndex\":%u,\"returnedDeviceCount\":%llu,\"actualLuid\":\"%016llx\"}\n",
        static_cast<unsigned>(api), static_cast<unsigned long long>(fact.returnedDeviceCount),
        static_cast<unsigned long long>(fact.adapterLuid));
}

void CheckUnchanged(HMODULE provider, const ResourceManagerGpuObservation::Snapshot& before)
{
    const auto after = GpuObservationProbe::Read(provider);
    Require(std::memcmp(&before, &after, sizeof(before)) == 0, "no-returned-device-no-new-fact");
}

void WritePolicy(const wchar_t* path, const std::string& value)
{
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("open-owned-policy");
    DWORD written = 0;
    const bool passed = WriteFile(file, value.data(), static_cast<DWORD>(value.size()), &written, nullptr)
        && written == value.size();
    CloseHandle(file);
    if (!passed) throw std::runtime_error("write-owned-policy");
}

void Target(const wchar_t* path, LUID luid)
{
    char text[64]{};
    std::snprintf(text, sizeof(text), "TargetLuid=0x%08x_0x%08x\n",
        static_cast<uint32_t>(luid.HighPart), static_cast<uint32_t>(luid.LowPart));
    WritePolicy(path, text);
}

bool PixelsMatch(const uint8_t* bytes, size_t rowPitch)
{
    constexpr int expected[] = {64, 128, 191, 255};
    for (UINT y = 0; y < Height; ++y)
        for (UINT x = 0; x < Width; ++x)
            for (UINT c = 0; c < 4; ++c)
                if (std::abs(static_cast<int>(bytes[y * rowPitch + x * 4 + c]) - expected[c]) > 1)
                    return false;
    return true;
}

void Readback12(ID3D12Device* device)
{
    D3D12_COMMAND_QUEUE_DESC queueDescription{};
    queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ComPtr<ID3D12CommandQueue> queue;
    Hr(device->CreateCommandQueue(&queueDescription, IID_PPV_ARGS(&queue)), "create-queue");
    ComPtr<ID3D12CommandAllocator> allocator;
    Hr(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "create-allocator");
    ComPtr<ID3D12GraphicsCommandList> commands;
    Hr(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr,
        IID_PPV_ARGS(&commands)), "create-command-list");

    D3D12_HEAP_PROPERTIES defaultHeap{};
    defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC textureDescription{};
    textureDescription.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    textureDescription.Width = Width;
    textureDescription.Height = Height;
    textureDescription.DepthOrArraySize = 1;
    textureDescription.MipLevels = 1;
    textureDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    textureDescription.SampleDesc.Count = 1;
    textureDescription.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    ComPtr<ID3D12Resource> texture;
    Hr(device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &textureDescription,
        D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr, IID_PPV_ARGS(&texture)), "create-render-target");
    D3D12_DESCRIPTOR_HEAP_DESC rtvDescription{};
    rtvDescription.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    rtvDescription.NumDescriptors = 1;
    ComPtr<ID3D12DescriptorHeap> rtvHeap;
    Hr(device->CreateDescriptorHeap(&rtvDescription, IID_PPV_ARGS(&rtvHeap)), "create-rtv-heap");
    const auto rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
    device->CreateRenderTargetView(texture.Get(), nullptr, rtv);

    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    UINT64 bytes = 0;
    device->GetCopyableFootprints(&textureDescription, 0, 1, 0, &footprint, nullptr, nullptr, &bytes);
    D3D12_HEAP_PROPERTIES readbackHeap{};
    readbackHeap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC bufferDescription{};
    bufferDescription.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bufferDescription.Width = bytes;
    bufferDescription.Height = 1;
    bufferDescription.DepthOrArraySize = 1;
    bufferDescription.MipLevels = 1;
    bufferDescription.SampleDesc.Count = 1;
    bufferDescription.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> readback;
    Hr(device->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &bufferDescription,
        D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)), "create-readback");

    commands->ClearRenderTargetView(rtv, ClearColor, 0, nullptr);
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = texture.Get();
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    commands->ResourceBarrier(1, &barrier);
    D3D12_TEXTURE_COPY_LOCATION source{};
    source.pResource = texture.Get();
    source.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    D3D12_TEXTURE_COPY_LOCATION destination{};
    destination.pResource = readback.Get();
    destination.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    destination.PlacedFootprint = footprint;
    commands->CopyTextureRegion(&destination, 0, 0, 0, &source, nullptr);
    Hr(commands->Close(), "close-command-list");
    ID3D12CommandList* submitted[] = {commands.Get()};
    queue->ExecuteCommandLists(1, submitted);
    ComPtr<ID3D12Fence> fence;
    Hr(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)), "create-fence");
    HANDLE completed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (completed == nullptr) throw std::runtime_error("create-fence-event");
    const HRESULT signal = queue->Signal(fence.Get(), 1);
    const HRESULT subscribe = SUCCEEDED(signal) ? fence->SetEventOnCompletion(1, completed) : signal;
    const DWORD waited = SUCCEEDED(subscribe) ? WaitForSingleObject(completed, 3000) : WAIT_FAILED;
    CloseHandle(completed);
    Hr(subscribe, "signal-fence");
    Require(waited == WAIT_OBJECT_0 && fence->GetCompletedValue() == 1, "d3d12-real-queue-completed");
    void* mapped = nullptr;
    D3D12_RANGE readRange{0, static_cast<SIZE_T>(bytes)};
    Hr(readback->Map(0, &readRange, &mapped), "map-d3d12-readback");
    const bool pixels = PixelsMatch(static_cast<const uint8_t*>(mapped), footprint.Footprint.RowPitch);
    D3D12_RANGE noWrites{0, 0};
    readback->Unmap(0, &noWrites);
    Require(pixels, "d3d12-all-256-pixels-match");
}

LUID Luid11(ID3D11Device* device)
{
    ComPtr<IDXGIDevice> dxgiDevice;
    Hr(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)), "query-dxgi-device");
    ComPtr<IDXGIAdapter> adapter;
    Hr(dxgiDevice->GetAdapter(&adapter), "get-d3d11-adapter");
    DXGI_ADAPTER_DESC description{};
    Hr(adapter->GetDesc(&description), "get-d3d11-description");
    return description.AdapterLuid;
}

void Readback11(ID3D11Device* device, ID3D11DeviceContext* context)
{
    D3D11_TEXTURE2D_DESC description{};
    description.Width = Width;
    description.Height = Height;
    description.MipLevels = 1;
    description.ArraySize = 1;
    description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BindFlags = D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> texture;
    Hr(device->CreateTexture2D(&description, nullptr, &texture), "create-d3d11-texture");
    ComPtr<ID3D11RenderTargetView> rtv;
    Hr(device->CreateRenderTargetView(texture.Get(), nullptr, &rtv), "create-d3d11-rtv");
    description.BindFlags = 0;
    description.Usage = D3D11_USAGE_STAGING;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> readback;
    Hr(device->CreateTexture2D(&description, nullptr, &readback), "create-d3d11-readback");
    context->ClearRenderTargetView(rtv.Get(), ClearColor);
    context->CopyResource(readback.Get(), texture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    Hr(context->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapped), "map-d3d11-readback");
    const bool pixels = PixelsMatch(static_cast<const uint8_t*>(mapped.pData), mapped.RowPitch);
    context->Unmap(readback.Get(), 0);
    Require(pixels, "d3d11-all-256-pixels-match");
}

enum class InterfaceOutputState { NotRequested, Unchanged, Empty, Assigned };

template<typename T>
InterfaceOutputState OutputState(T* value)
{
    if (value == nullptr) return InterfaceOutputState::Empty;
    return reinterpret_cast<uintptr_t>(value) == 1 ? InterfaceOutputState::Unchanged : InterfaceOutputState::Assigned;
}

struct D3D11CallResult
{
    HRESULT result = E_FAIL;
    std::array<InterfaceOutputState, 3> outputs{};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain> swap;
};

D3D11CallResult CallD3D11(IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType, const DXGI_SWAP_CHAIN_DESC* swapDescription)
{
    // Distinguish untouched, cleared and assigned outputs without dereferencing sentinels.
    auto* device = reinterpret_cast<ID3D11Device*>(uintptr_t{1});
    auto* context = reinterpret_cast<ID3D11DeviceContext*>(uintptr_t{1});
    auto* swap = reinterpret_cast<IDXGISwapChain*>(uintptr_t{1});
    D3D11CallResult call;
    call.result = swapDescription == nullptr
        ? D3D11CreateDevice(adapter, driverType, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
            &device, nullptr, &context)
        : D3D11CreateDeviceAndSwapChain(adapter, driverType, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
            swapDescription, &swap, &device, nullptr, &context);
    call.outputs = {OutputState(device), OutputState(context),
        swapDescription == nullptr ? InterfaceOutputState::NotRequested : OutputState(swap)};
    if (call.outputs[0] == InterfaceOutputState::Assigned) call.device.Attach(device);
    if (call.outputs[1] == InterfaceOutputState::Assigned) call.context.Attach(context);
    if (call.outputs[2] == InterfaceOutputState::Assigned) call.swap.Attach(swap);
    return call;
}

bool CheckD3D11Request(const char* name, IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType,
    const DXGI_SWAP_CHAIN_DESC* swapDescription, HRESULT expectedResult, LUID expectedLuid,
    const std::array<InterfaceOutputState, 3>* expectedOutputs = nullptr)
{
    const auto provider = GetModuleHandleW(L"ResourceManager.GpuPlacementShim.dll");
    const auto before = GpuObservationProbe::Read(provider);
    const auto call = CallD3D11(adapter, driverType, swapDescription);
    const bool outputsCleared = call.outputs[0] == InterfaceOutputState::Empty
        && call.outputs[1] == InterfaceOutputState::Empty
        && (swapDescription == nullptr || call.outputs[2] == InterfaceOutputState::Empty);
    LUID actualLuid{};
    bool passed = call.result == expectedResult && (expectedOutputs == nullptr || call.outputs == *expectedOutputs);
    if (SUCCEEDED(expectedResult))
    {
        passed = passed && call.device && call.context && (swapDescription == nullptr || call.swap);
        if (call.device && call.context)
        {
            actualLuid = Luid11(call.device.Get());
            passed = passed && LuidValue(actualLuid) == LuidValue(expectedLuid);
            Readback11(call.device.Get(), call.context.Get());
            CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D11, actualLuid);
        }
    }
    else if (expectedResult == DXGI_ERROR_NOT_FOUND)
    {
        passed = passed && outputsCleared;
    }
    if (FAILED(call.result)) CheckUnchanged(provider, before);
    std::printf("{\"d3d11Boundary\":\"%s\",\"passed\":%s,\"expectedHresult\":%ld,\"actualHresult\":%ld,"
        "\"outputStates\":[%u,%u,%u],\"outputsCleared\":%s,\"expectedLuid\":\"%016llx\",\"actualLuid\":\"%016llx\"}\n",
        name, passed ? "true" : "false", static_cast<long>(expectedResult), static_cast<long>(call.result),
        static_cast<unsigned>(call.outputs[0]), static_cast<unsigned>(call.outputs[1]), static_cast<unsigned>(call.outputs[2]),
        outputsCleared ? "true" : "false", static_cast<unsigned long long>(LuidValue(expectedLuid)),
        static_cast<unsigned long long>(LuidValue(actualLuid)));
    std::fflush(stdout);
    if (passed) ++checks;
    return passed;
}

struct Adapter
{
    ComPtr<IDXGIAdapter1> dxgi;
    DXGI_ADAPTER_DESC1 description{};
};

ComPtr<ID3D12DeviceFactory> CreateDeviceFactory(ID3D12SDKConfiguration1* configuration)
{
    // Ask the OS runtime for its own version; do not select a different SDK globally.
    const auto* version = reinterpret_cast<const UINT*>(GetProcAddress(GetModuleHandleW(L"D3D12Core.dll"), "D3D12SDKVersion"));
    Require(version != nullptr, "system-core-version-export");
    char directory[MAX_PATH]{};
    const UINT length = GetSystemDirectoryA(directory, MAX_PATH);
    Require(length > 0 && length < MAX_PATH, "system-runtime-path");
    const std::string path = std::string(directory) + "\\";
    ComPtr<ID3D12DeviceFactory> factory;
    Hr(configuration->CreateDeviceFactory(*version, path.c_str(), IID_PPV_ARGS(&factory)), "create-independent-factory");
    std::printf("{\"factorySdkVersion\":%u}\n", *version);
    return factory;
}

HRESULT GetD3D12Interface(REFCLSID clsid, REFIID iid, void** output)
{
    const FARPROC address = GetProcAddress(GetModuleHandleW(L"d3d12.dll"), "D3D12GetInterface");
    PFN_D3D12_GET_INTERFACE function = nullptr;
    static_assert(sizeof(function) == sizeof(address));
    std::memcpy(&function, &address, sizeof(function));
    Require(function != nullptr, "system-get-interface-export");
    return function(clsid, iid, output);
}

void ConcurrentFactories(ID3D12SDKConfiguration1* configuration, LUID expected)
{
    const auto* version = reinterpret_cast<const UINT*>(GetProcAddress(GetModuleHandleW(L"D3D12Core.dll"), "D3D12SDKVersion"));
    char directory[MAX_PATH]{};
    const UINT length = GetSystemDirectoryA(directory, MAX_PATH);
    Require(version != nullptr && length > 0 && length < MAX_PATH, "concurrent-runtime-inputs");
    const std::string path = std::string(directory) + "\\";
    constexpr unsigned workerCount = 4;
    constexpr unsigned factoriesPerWorker = 4;
    std::atomic<unsigned> completed{0}, failures{0};
    std::vector<std::thread> workers;
    for (unsigned worker = 0; worker < workerCount; ++worker)
        workers.emplace_back([&] {
            for (unsigned iteration = 0; iteration < factoriesPerWorker; ++iteration)
            {
                ComPtr<ID3D12DeviceFactory> factory;
                ComPtr<ID3D12Device> device;
                HRESULT result = configuration->CreateDeviceFactory(*version, path.c_str(), IID_PPV_ARGS(&factory));
                if (SUCCEEDED(result)) result = factory->SetFlags(D3D12_DEVICE_FACTORY_FLAG_DISALLOW_STORING_NEW_DEVICE_AS_SINGLETON);
                if (SUCCEEDED(result)) result = factory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
                if (FAILED(result) || device == nullptr || LuidValue(device->GetAdapterLuid()) != LuidValue(expected)) ++failures;
                ++completed;
            }
        });
    for (auto& worker : workers) worker.join();
    std::printf("{\"concurrentFactoryAttempts\":%u,\"concurrentFactoryFailures\":%u}\n", completed.load(), failures.load());
    Require(completed == workerCount * factoriesPerWorker && failures == 0, "concurrent-factory-creation-and-device-selection");
}

int FactoryRun(const wchar_t* providerPath, const wchar_t* policyPath)
{
    SetErrorMode(32771);
    SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", nullptr);
    ComPtr<IDXGIFactory4> dxgi;
    Hr(CreateDXGIFactory1(IID_PPV_ARGS(&dxgi)), "factory-probe-dxgi");
    ComPtr<ID3D12Device> held;
    Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&held)), "factory-probe-existing-device");
    const LUID heldLuid = held->GetAdapterLuid();
    ComPtr<ID3D12SDKConfiguration1> baselineConfiguration;
    Hr(GetD3D12Interface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&baselineConfiguration)), "baseline-sdk-configuration");
    auto baselineFactory = CreateDeviceFactory(baselineConfiguration.Get());
    const auto independent = D3D12_DEVICE_FACTORY_FLAG_DISALLOW_STORING_NEW_DEVICE_AS_SINGLETON;
    Hr(baselineFactory->SetFlags(independent), "baseline-independent-flags");
    ComPtr<ID3D12Device> baselineDevice;
    Hr(baselineFactory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&baselineDevice)), "baseline-independent-device");
    Require(baselineDevice.Get() != held.Get(), "baseline-factory-device-is-independent");
    void* invalid = nullptr;
    const HRESULT invalidIid = baselineFactory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, UnsupportedInterface, &invalid);
    Require(FAILED(invalidIid) && invalid == nullptr, "factory-baseline-invalid-iid");
    const HRESULT invalidFeature = baselineFactory->CreateDevice(nullptr, static_cast<D3D_FEATURE_LEVEL>(0x7fffffff), __uuidof(ID3D12Device), nullptr);
    Require(FAILED(invalidFeature), "factory-baseline-invalid-feature");
    ComPtr<IDXGIAdapter> warp;
    Hr(dxgi->EnumWarpAdapter(IID_PPV_ARGS(&warp)), "factory-probe-warp");
    ComPtr<ID3D12Device> baselineWarp;
    Hr(baselineFactory->CreateDevice(warp.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&baselineWarp)), "factory-baseline-warp");

    HMODULE provider = LoadLibraryExW(providerPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    Require(provider != nullptr, "factory-production-provider-loaded");
    auto configure = StatusExport(provider, "ResourceManagerGpuPlacementConfigure");
    WritePolicy(policyPath, "SystemDefaultGpu\n");
    Require(configure != nullptr && (configure(const_cast<wchar_t*>(policyPath)) & 7) == 7, "factory-provider-ready");
    // Exercise a base interface followed by QueryInterface, not only the newest IID.
    ComPtr<ID3D12SDKConfiguration> baseConfiguration;
    Hr(GetD3D12Interface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&baseConfiguration)), "base-sdk-configuration");
    ComPtr<ID3D12SDKConfiguration1> configuration;
    Hr(baseConfiguration.As(&configuration), "query-sdk-configuration1");
    ComPtr<IUnknown> baseIdentity, derivedIdentity;
    Hr(baseConfiguration.As(&baseIdentity), "base-configuration-identity");
    Hr(configuration.As(&derivedIdentity), "derived-configuration-identity");
    Require(baseIdentity.Get() == derivedIdentity.Get(), "configuration-com-identity-preserved");
    ConcurrentFactories(configuration.Get(), heldLuid);
    auto factory = CreateDeviceFactory(configuration.Get());
    Hr(factory->SetFlags(independent), "factory-set-independent-flags");
    Require(factory->GetFlags() == independent, "factory-flags-preserved");
    ComPtr<IUnknown> identity;
    Hr(factory.As(&identity), "factory-iunknown");
    ComPtr<ID3D12DeviceFactory> queried;
    Hr(identity.As(&queried), "factory-query-back");
    Require(queried.Get() == factory.Get(), "factory-com-identity-preserved");
    ComPtr<ID3D12Device> defaultDevice;
    Hr(factory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&defaultDevice)), "factory-default-device");
    CheckLuid("Factory-default", baselineDevice->GetAdapterLuid(), defaultDevice->GetAdapterLuid());
    Require(defaultDevice.Get() != held.Get(), "factory-keeps-independent-device-semantics");
    Readback12(defaultDevice.Get());
    unsigned hardwareAdapters = 0;
    for (UINT index = 0; index < 64; ++index)
    {
        ComPtr<IDXGIAdapter1> adapter;
        const HRESULT enumerated = dxgi->EnumAdapters1(index, &adapter);
        if (enumerated == DXGI_ERROR_NOT_FOUND) break;
        Hr(enumerated, "factory-enumerate-adapter");
        DXGI_ADAPTER_DESC1 description{};
        Hr(adapter->GetDesc1(&description), "factory-describe-adapter");
        if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
        Target(policyPath, description.AdapterLuid);
        ComPtr<ID3D12Device> device;
        Hr(factory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)), "factory-target-device");
        CheckLuid("Factory-null-adapter", description.AdapterLuid, device->GetAdapterLuid());
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D12, device->GetAdapterLuid());
        Readback12(device.Get());
        ComPtr<ID3D12Device> explicitDevice;
        ComPtr<IDXGIAdapter1> first;
        Hr(dxgi->EnumAdapters1(0, &first), "factory-explicit-input");
        Hr(factory->CreateDevice(first.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&explicitDevice)), "factory-explicit-device");
        CheckLuid("Factory-explicit-adapter", description.AdapterLuid, explicitDevice->GetAdapterLuid());
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D12, explicitDevice->GetAdapterLuid());
        const auto beforeSupport = GpuObservationProbe::Read(provider);
        Require(factory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), nullptr) == S_FALSE,
            "factory-null-output-preserves-s-false");
        CheckUnchanged(provider, beforeSupport);
        CheckLuid("Factory-held-device-not-moved", heldLuid, held->GetAdapterLuid());
        Require(factory->GetFlags() == independent, "factory-flags-still-independent");
        ComPtr<ID3D12Device> software;
        Hr(factory->CreateDevice(warp.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&software)), "factory-warp-device");
        CheckLuid("Factory-WARP-not-redirected", baselineWarp->GetAdapterLuid(), software->GetAdapterLuid());
        ++hardwareAdapters;
    }
    Require(hardwareAdapters > 0, "factory-hardware-tested");
    const auto beforeFailures = GpuObservationProbe::Read(provider);
    invalid = nullptr;
    Require(factory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, UnsupportedInterface, &invalid) == invalidIid && invalid == nullptr,
        "factory-invalid-iid-hresult-preserved");
    Require(factory->CreateDevice(nullptr, static_cast<D3D_FEATURE_LEVEL>(0x7fffffff), __uuidof(ID3D12Device), nullptr) == invalidFeature,
        "factory-invalid-feature-hresult-preserved");
    Target(policyPath, LUID{0xffffffff, 0x7fffffff});
    invalid = reinterpret_cast<void*>(1);
    Require(factory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), &invalid) == DXGI_ERROR_NOT_FOUND && invalid == nullptr,
        "factory-missing-target-clears-output-no-fallback");
    CheckUnchanged(provider, beforeFailures);
    WritePolicy(policyPath, "SystemDefaultGpu\n");
    Readback12(held.Get());
    factory.Reset();
    queried.Reset();
    identity.Reset();
    configuration->FreeUnusedSDKs();
    auto recreatedFactory = CreateDeviceFactory(configuration.Get());
    Hr(recreatedFactory->SetFlags(independent), "recreated-factory-flags");
    ComPtr<ID3D12Device> recreated;
    Hr(recreatedFactory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&recreated)), "recreated-factory-device");
    Readback12(recreated.Get());
    const auto shared = static_cast<D3D12_DEVICE_FACTORY_FLAGS>(D3D12_DEVICE_FACTORY_FLAG_ALLOW_RETURNING_EXISTING_DEVICE |
        D3D12_DEVICE_FACTORY_FLAG_ALLOW_RETURNING_INCOMPATIBLE_EXISTING_DEVICE);
    Hr(recreatedFactory->SetFlags(shared), "factory-shared-device-flags");
    Require(recreatedFactory->GetFlags() == shared, "factory-changed-flags-preserved");
    ComPtr<ID3D12Device> sharedDevice;
    Hr(recreatedFactory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&sharedDevice)), "factory-shared-device");
    // ALLOW_RETURNING_EXISTING_DEVICE permits reuse; it does not promise a specific pointer.
    std::printf("{\"factoryAllowsExistingDevice\":true,\"reusedHeldDevice\":%s}\n", sharedDevice.Get() == held.Get() ? "true" : "false");
    CheckLuid("Factory-allow-existing-device", heldLuid, sharedDevice->GetAdapterLuid());
    Readback12(sharedDevice.Get());
    std::printf("{\"passed\":true,\"mode\":\"factory\",\"checks\":%u,\"hardwareAdapters\":%u,\"pixelsPerReadback\":256}\n", checks, hardwareAdapters);
    return 0;
}

int Run(const wchar_t* providerPath, const wchar_t* policyPath)
{
    SetErrorMode(32771);
    SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", nullptr);
    ComPtr<IDXGIFactory4> factory;
    Hr(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "create-factory");
    std::vector<Adapter> adapters;
    for (UINT index = 0; index < 64; ++index)
    {
        Adapter adapter;
        const HRESULT enumerated = factory->EnumAdapters1(index, &adapter.dxgi);
        if (enumerated == DXGI_ERROR_NOT_FOUND) break;
        Hr(enumerated, "enumerate-adapter");
        Hr(adapter.dxgi->GetDesc1(&adapter.description), "describe-adapter");
        if ((adapter.description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
        const HRESULT supported = D3D12CreateDevice(adapter.dxgi.Get(), D3D_FEATURE_LEVEL_11_0,
            __uuidof(ID3D12Device), nullptr);
        std::printf("{\"adapterIndex\":%u,\"vendorId\":%u,\"deviceId\":%u,\"d3d12ProbeHresult\":%ld}\n",
            index, adapter.description.VendorId, adapter.description.DeviceId, static_cast<long>(supported));
        if (SUCCEEDED(supported)) adapters.push_back(std::move(adapter));
    }
    Require(!adapters.empty(), "at-least-one-real-d3d12-hardware-adapter");
    ComPtr<ID3D12Device> held;
    Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&held)), "baseline-d3d12-device");
    const LUID heldLuid = held->GetAdapterLuid();
    Readback12(held.Get());
    ComPtr<IDXGIAdapter> warp;
    Hr(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp)), "enumerate-warp");
    ComPtr<ID3D12Device> baselineWarp;
    Hr(D3D12CreateDevice(warp.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&baselineWarp)), "baseline-warp");
    const LUID warpLuid = baselineWarp->GetAdapterLuid();
    ComPtr<ID3D11Device> baseline11;
    ComPtr<ID3D11DeviceContext> baseline11Context;
    Hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
        &baseline11, nullptr, &baseline11Context), "baseline-d3d11-device");
    const LUID default11Luid = Luid11(baseline11.Get());
    ComPtr<ID3D11Device> baselineWarp11;
    Hr(D3D11CreateDevice(warp.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
        &baselineWarp11, nullptr, nullptr), "baseline-d3d11-explicit-warp");
    const LUID warp11Luid = Luid11(baselineWarp11.Get());
    void* invalidOutput = nullptr;
    const HRESULT unsupportedIid = D3D12CreateDevice(adapters.front().dxgi.Get(), D3D_FEATURE_LEVEL_11_0,
        UnsupportedInterface, &invalidOutput);
    Require(FAILED(unsupportedIid) && invalidOutput == nullptr, "baseline-unsupported-iid-rejected");
    const HRESULT unsupportedFeature = D3D12CreateDevice(adapters.front().dxgi.Get(),
        static_cast<D3D_FEATURE_LEVEL>(0x7fffffff), __uuidof(ID3D12Device), nullptr);
    Require(FAILED(unsupportedFeature), "baseline-unsupported-feature-rejected");

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"ResourceManager.Direct3DShimProbe";
    Require(RegisterClassW(&windowClass) != 0, "register-owned-hidden-window");
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE, windowClass.lpszClassName, L"GPU shim probe", WS_POPUP,
        0, 0, Width, Height, nullptr, nullptr, windowClass.hInstance, nullptr);
    Require(window != nullptr, "owned-window-remains-hidden");
    DXGI_SWAP_CHAIN_DESC swapDescription{};
    swapDescription.BufferDesc.Width = Width;
    swapDescription.BufferDesc.Height = Height;
    swapDescription.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapDescription.SampleDesc.Count = 1;
    swapDescription.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapDescription.BufferCount = 1;
    swapDescription.OutputWindow = window;
    swapDescription.Windowed = TRUE;
    const std::array<D3D11CallResult, 4> invalidBaselines = {
        CallD3D11(adapters.front().dxgi.Get(), D3D_DRIVER_TYPE_HARDWARE, nullptr),
        CallD3D11(adapters.front().dxgi.Get(), D3D_DRIVER_TYPE_HARDWARE, &swapDescription),
        CallD3D11(nullptr, D3D_DRIVER_TYPE_UNKNOWN, nullptr),
        CallD3D11(nullptr, D3D_DRIVER_TYPE_UNKNOWN, &swapDescription)
    };
    const char* baselineNames[] = {"device-invalid-hardware-adapter", "swap-invalid-hardware-adapter",
        "device-invalid-unknown-null", "swap-invalid-unknown-null"};
    for (size_t index = 0; index < invalidBaselines.size(); ++index)
    {
        const auto& baseline = invalidBaselines[index];
        std::printf("{\"d3d11InvalidBaseline\":\"%s\",\"hresult\":%ld,\"outputStates\":[%u,%u,%u]}\n",
            baselineNames[index], static_cast<long>(baseline.result), static_cast<unsigned>(baseline.outputs[0]),
            static_cast<unsigned>(baseline.outputs[1]), static_cast<unsigned>(baseline.outputs[2]));
        Require(baseline.result == E_INVALIDARG, "unhooked-invalid-adapter-driver-baseline");
    }

    // Load the actual production MinHook provider only after collecting unhooked baselines.
    HMODULE provider = LoadLibraryExW(providerPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    Require(provider != nullptr, "production-provider-loaded");
    auto configure = StatusExport(provider, "ResourceManagerGpuPlacementConfigure");
    auto status = StatusExport(provider, "ResourceManagerGpuPlacementGetStatus");
    Require(configure != nullptr && status != nullptr, "provider-exports");
    Require((status(nullptr) & 6) == 6, "all-three-production-hooks-active");
    ComPtr<ID3D12Device> unconfigured;
    Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&unconfigured)), "unconfigured-device");
    CheckLuid("D3D12-no-policy", heldLuid, unconfigured->GetAdapterLuid());
    WritePolicy(policyPath, "SystemDefaultGpu\n");
    Require((configure(const_cast<wchar_t*>(policyPath)) & 7) == 7, "policy-configured");
    ComPtr<ID3D12Device> defaultDevice;
    Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&defaultDevice)), "default-policy-device");
    CheckLuid("D3D12-default-policy", heldLuid, defaultDevice->GetAdapterLuid());
    ComPtr<ID3D11Device> default11;
    Hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
        &default11, nullptr, nullptr), "default-d3d11-device");
    CheckLuid("D3D11-default-policy", default11Luid, Luid11(default11.Get()));

    unsigned boundaryCases = 0, boundaryFailures = 0, crossAdapterRequests = 0;
    auto check11 = [&](const char* name, IDXGIAdapter* adapter, D3D_DRIVER_TYPE driver, bool withSwap,
        HRESULT expected, LUID expectedLuid = LUID{}, const std::array<InterfaceOutputState, 3>* outputs = nullptr) {
        ++boundaryCases;
        const bool passed = CheckD3D11Request(name, adapter, driver, withSwap ? &swapDescription : nullptr,
            expected, expectedLuid, outputs);
        if (!passed) ++boundaryFailures;
        return passed;
    };
    for (const Adapter& adapter : adapters)
    {
        const LUID expected = adapter.description.AdapterLuid;
        Target(policyPath, expected);
        ComPtr<ID3D12Device> redirected;
        Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&redirected)), "redirect-default-d3d12");
        CheckLuid("D3D12-null-adapter", expected, redirected->GetAdapterLuid());
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D12, redirected->GetAdapterLuid());
        Readback12(redirected.Get());
        ComPtr<ID3D12Device> explicitDevice;
        Hr(D3D12CreateDevice(adapters.front().dxgi.Get(), D3D_FEATURE_LEVEL_11_0,
            IID_PPV_ARGS(&explicitDevice)), "redirect-explicit-d3d12");
        CheckLuid("D3D12-explicit-adapter", expected, explicitDevice->GetAdapterLuid());
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D12, explicitDevice->GetAdapterLuid());
        const auto beforeSupport = GpuObservationProbe::Read(provider);
        Require(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), nullptr) == S_FALSE,
            "null-output-preserves-s-false");
        CheckUnchanged(provider, beforeSupport);
        CheckLuid("D3D12-existing-device-not-moved", heldLuid, held->GetAdapterLuid());
        ComPtr<ID3D12Device> softwareDevice;
        Hr(D3D12CreateDevice(warp.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&softwareDevice)), "preserve-warp");
        CheckLuid("D3D12-WARP-not-redirected", warpLuid, softwareDevice->GetAdapterLuid());

        ComPtr<ID3D11Device> device11;
        ComPtr<ID3D11DeviceContext> context11;
        Hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
            &device11, nullptr, &context11), "redirect-d3d11-device");
        CheckLuid("D3D11CreateDevice", expected, Luid11(device11.Get()));
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D11, Luid11(device11.Get()));
        Readback11(device11.Get(), context11.Get());
        ComPtr<IDXGISwapChain> swapChain;
        ComPtr<ID3D11Device> swapDevice;
        ComPtr<ID3D11DeviceContext> swapContext;
        Hr(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &swapDescription, &swapChain, &swapDevice, nullptr, &swapContext), "redirect-d3d11-swap-chain");
        CheckLuid("D3D11CreateDeviceAndSwapChain", expected, Luid11(swapDevice.Get()));
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D11, Luid11(swapDevice.Get()));
        Readback11(swapDevice.Get(), swapContext.Get());
        swapChain.Reset();
        swapContext.Reset();
        swapDevice.Reset();
        ComPtr<ID3D11DeviceContext> contextOnly;
        Hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, nullptr, nullptr, &contextOnly), "context-only-output");
        ComPtr<ID3D11Device> contextDevice;
        contextOnly->GetDevice(&contextDevice);
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D11, Luid11(contextDevice.Get()));
        Readback11(contextDevice.Get(), contextOnly.Get());
        ComPtr<IDXGISwapChain> swapOnly;
        Hr(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &swapDescription, &swapOnly, nullptr, nullptr, nullptr), "swap-only-output");
        ComPtr<ID3D11Device> swapOnlyDevice;
        Hr(swapOnly->GetDevice(IID_PPV_ARGS(&swapOnlyDevice)), "swap-only-device");
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D11, Luid11(swapOnlyDevice.Get()));
        swapOnly.Reset();
        ComPtr<IUnknown> generic12;
        Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&generic12)), "generic-device-interface");
        ComPtr<ID3D12Device> genericDevice;
        Hr(generic12.As(&genericDevice), "generic-device-query");
        CheckObservation(provider, ResourceManagerGpuObservation::Api::D3D12, genericDevice->GetAdapterLuid());
        // Prefer a different caller adapter so a pass proves replacement, not identity routing.
        const auto& caller = LuidValue(expected) == LuidValue(adapters.front().description.AdapterLuid)
            ? adapters.back() : adapters.front();
        const bool devicePassed = check11("device-target-found-explicit", caller.dxgi.Get(), D3D_DRIVER_TYPE_UNKNOWN,
            false, S_OK, expected);
        const bool swapPassed = check11("swap-target-found-explicit", caller.dxgi.Get(), D3D_DRIVER_TYPE_UNKNOWN,
            true, S_OK, expected);
        const bool different = LuidValue(caller.description.AdapterLuid) != LuidValue(expected);
        if (different) crossAdapterRequests += static_cast<unsigned>(devicePassed) + static_cast<unsigned>(swapPassed);
        std::printf("{\"d3d11ExplicitCaller\":\"%016llx\",\"targetLuid\":\"%016llx\",\"differentAdapters\":%s,"
            "\"devicePassed\":%s,\"swapPassed\":%s}\n",
            static_cast<unsigned long long>(LuidValue(caller.description.AdapterLuid)),
            static_cast<unsigned long long>(LuidValue(expected)), different ? "true" : "false",
            devicePassed ? "true" : "false", swapPassed ? "true" : "false");
    }
    Target(policyPath, adapters.front().description.AdapterLuid);
    check11("device-explicit-warp", warp.Get(), D3D_DRIVER_TYPE_UNKNOWN, false, S_OK, warp11Luid);
    check11("swap-explicit-warp", warp.Get(), D3D_DRIVER_TYPE_UNKNOWN, true, S_OK, warp11Luid);
    check11("device-warp-driver", nullptr, D3D_DRIVER_TYPE_WARP, false, S_OK, warp11Luid);
    check11("swap-warp-driver", nullptr, D3D_DRIVER_TYPE_WARP, true, S_OK, warp11Luid);
    check11(baselineNames[0], adapters.front().dxgi.Get(), D3D_DRIVER_TYPE_HARDWARE, false,
        invalidBaselines[0].result, {}, &invalidBaselines[0].outputs);
    check11(baselineNames[1], adapters.front().dxgi.Get(), D3D_DRIVER_TYPE_HARDWARE, true,
        invalidBaselines[1].result, {}, &invalidBaselines[1].outputs);
    check11(baselineNames[2], nullptr, D3D_DRIVER_TYPE_UNKNOWN, false, invalidBaselines[2].result, {}, &invalidBaselines[2].outputs);
    check11(baselineNames[3], nullptr, D3D_DRIVER_TYPE_UNKNOWN, true, invalidBaselines[3].result, {}, &invalidBaselines[3].outputs);
    invalidOutput = nullptr;
    Require(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, UnsupportedInterface, &invalidOutput) == unsupportedIid
        && invalidOutput == nullptr, "unsupported-iid-hresult-preserved");
    Require(D3D12CreateDevice(nullptr, static_cast<D3D_FEATURE_LEVEL>(0x7fffffff), __uuidof(ID3D12Device), nullptr)
        == unsupportedFeature, "minimum-feature-level-and-hresult-preserved");
    LUID missing{0xffffffff, 0x7fffffff};
    for (const Adapter& adapter : adapters)
        Require(LuidValue(missing) != LuidValue(adapter.description.AdapterLuid), "negative-target-not-present");
    Target(policyPath, missing);
    check11("device-missing-target", nullptr, D3D_DRIVER_TYPE_HARDWARE, false, DXGI_ERROR_NOT_FOUND);
    check11("swap-missing-target", nullptr, D3D_DRIVER_TYPE_HARDWARE, true, DXGI_ERROR_NOT_FOUND);
    check11("device-explicit-missing-target", adapters.front().dxgi.Get(), D3D_DRIVER_TYPE_UNKNOWN, false, DXGI_ERROR_NOT_FOUND);
    check11("swap-explicit-missing-target", adapters.front().dxgi.Get(), D3D_DRIVER_TYPE_UNKNOWN, true, DXGI_ERROR_NOT_FOUND);
    check11("device-missing-target-warp", warp.Get(), D3D_DRIVER_TYPE_UNKNOWN, false, S_OK, warp11Luid);
    check11("swap-missing-target-warp", warp.Get(), D3D_DRIVER_TYPE_UNKNOWN, true, S_OK, warp11Luid);
    check11("device-missing-target-warp-driver", nullptr, D3D_DRIVER_TYPE_WARP, false, S_OK, warp11Luid);
    check11("swap-missing-target-warp-driver", nullptr, D3D_DRIVER_TYPE_WARP, true, S_OK, warp11Luid);
    invalidOutput = nullptr;
    Require(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), &invalidOutput)
        == DXGI_ERROR_NOT_FOUND && invalidOutput == nullptr, "missing-exact-target-does-not-fallback");
    WritePolicy(policyPath, "SystemDefaultGpu\n");
    check11("device-withdraw-default", nullptr, D3D_DRIVER_TYPE_HARDWARE, false, S_OK, default11Luid);
    check11("swap-withdraw-default", nullptr, D3D_DRIVER_TYPE_HARDWARE, true, S_OK, default11Luid);
    check11("device-withdraw-explicit", adapters.back().dxgi.Get(), D3D_DRIVER_TYPE_UNKNOWN, false, S_OK,
        adapters.back().description.AdapterLuid);
    check11("swap-withdraw-explicit", adapters.back().dxgi.Get(), D3D_DRIVER_TYPE_UNKNOWN, true, S_OK,
        adapters.back().description.AdapterLuid);
    CheckLuid("D3D11-existing-device-not-moved", default11Luid, Luid11(baseline11.Get()));
    Readback11(baseline11.Get(), baseline11Context.Get());
    ComPtr<ID3D12Device> withdrawn12;
    Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&withdrawn12)), "withdrawn-default-d3d12");
    CheckLuid("D3D12-withdraw-default", heldLuid, withdrawn12->GetAdapterLuid());
    Readback12(withdrawn12.Get());
    Readback12(held.Get());
    Require(DestroyWindow(window) != FALSE, "owned-hidden-window-destroyed");
    UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);
    Require(boundaryCases == 20 + 2 * adapters.size() && boundaryFailures == 0, "d3d11-boundary-contract");
    const bool crossAdapterVerified = adapters.size() > 1 && crossAdapterRequests == 2 * adapters.size();
    std::printf("{\"passed\":true,\"checks\":%u,\"hardwareAdapters\":%zu,\"crossAdapterVerified\":%s,"
        "\"d3d11CrossAdapterRequests\":%u,\"pixelsPerReadback\":256,\"d3d11BoundaryCases\":%u,\"d3d11BoundaryFailures\":%u}\n",
        checks, adapters.size(), crossAdapterVerified ? "true" : "false", crossAdapterRequests, boundaryCases, boundaryFailures);
    return 0;
}

int StartupChild(const wchar_t* expectedText)
{
    wchar_t* end = nullptr;
    const uint64_t expectedValue = std::wcstoull(expectedText, &end, 16);
    Require(end != expectedText && *end == L'\0', "startup-target-argument");
    const LUID expected{static_cast<DWORD>(expectedValue), static_cast<LONG>(expectedValue >> 32)};
    HMODULE provider = GetModuleHandleW(L"ResourceManager.GpuPlacementShim.dll");
    Require(provider != nullptr, "startup-provider-present-at-main-entry");
    auto status = StatusExport(provider, "ResourceManagerGpuPlacementGetStatus");
    Require(status != nullptr && (status(nullptr) & 7) == 7, "startup-all-hooks-and-policy-ready-before-first-device");
    ComPtr<ID3D12Device> device;
    Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)), "first-startup-d3d12-device");
    CheckLuid("D3D12-first-startup-device", expected, device->GetAdapterLuid());
    Readback12(device.Get());
    ComPtr<ID3D11Device> device11;
    ComPtr<ID3D11DeviceContext> context11;
    Hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
        &device11, nullptr, &context11), "first-startup-d3d11-device");
    CheckLuid("D3D11-first-startup-device", expected, Luid11(device11.Get()));
    Readback11(device11.Get(), context11.Get());
    ComPtr<ID3D12SDKConfiguration1> configuration;
    Hr(GetD3D12Interface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&configuration)), "startup-sdk-configuration");
    auto factory = CreateDeviceFactory(configuration.Get());
    Hr(factory->SetFlags(D3D12_DEVICE_FACTORY_FLAG_DISALLOW_STORING_NEW_DEVICE_AS_SINGLETON), "startup-factory-independent-flags");
    ComPtr<ID3D12Device> factoryDevice;
    Hr(factory->CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&factoryDevice)), "first-startup-factory-device");
    CheckLuid("Factory-first-startup-device", expected, factoryDevice->GetAdapterLuid());
    Readback12(factoryDevice.Get());
    std::printf("{\"passed\":true,\"mode\":\"startup-child\",\"checks\":%u}\n", checks);
    return 0;
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3 && !(argc == 4 && std::wcscmp(argv[1], L"--factory") == 0))
    {
        std::fprintf(stderr, "Usage: Direct3DShimProbe <absolute-provider-dll> <owned-policy-file>\n");
        return 2;
    }
    try
    {
        if (argc == 4) return FactoryRun(argv[2], argv[3]);
        return std::wcscmp(argv[1], L"--startup-child") == 0 ? StartupChild(argv[2]) : Run(argv[1], argv[2]);
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "Probe failed: %s\n", error.what());
        return 1;
    }
}
