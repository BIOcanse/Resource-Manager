#include <windows.h>
#include <cstdio>
#include <atomic>
#include <thread>
#include <vector>

bool rejectProtection = false;
BOOL WINAPI ProbeVirtualProtect(void* address, SIZE_T size, DWORD protection, DWORD* previous)
{
    if (rejectProtection) { SetLastError(ERROR_ACCESS_DENIED); return FALSE; }
    return VirtualProtect(address, size, protection, previous);
}

// Compile the real implementation, substituting only the failing OS operation.
#define VirtualProtect ProbeVirtualProtect
#include "../GpuPlacementShim/ResourceManagerGpuPlacementShim.cpp"
#undef VirtualProtect

namespace
{
struct View
{
    void** table;
    ULONG references = 1;
    View* queried = nullptr;
    View* identity = nullptr;
    HRESULT queryResult = S_OK;
};

View* acquisition = nullptr;
View* factoryAcquisition = nullptr;
View* deviceAcquisition = nullptr;
unsigned passed = 0, failed = 0;
FactoryCreateDeviceFn nextFactoryMethod = nullptr;
unsigned delegationDepth = 0, delegationCalls = 0;

void Check(bool value, const char* name)
{
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
    if (value) ++passed; else ++failed;
}

HRESULT STDMETHODCALLTYPE Query(View* object, REFIID iid, void** output)
{
    if (output == nullptr) return E_POINTER;
    *output = nullptr;
    if (FAILED(object->queryResult)) return object->queryResult;
    View* result = IsEqualGUID(iid, __uuidof(IUnknown)) ? object->identity : object->queried;
    if (result == nullptr) return E_NOINTERFACE;
    ++result->references;
    *output = result;
    return S_OK;
}

ULONG STDMETHODCALLTYPE Add(View* object) { return ++object->references; }
ULONG STDMETHODCALLTYPE Release(View* object) { return --object->references; }
HRESULT STDMETHODCALLTYPE NativeDevice(View*, IUnknown*, D3D_FEATURE_LEVEL, REFIID, void** output)
{
    if (deviceAcquisition)
    {
        if (!output) return S_FALSE;
        ++deviceAcquisition->references;
        *output = deviceAcquisition;
        return S_OK;
    }
    if (output != nullptr) *output = nullptr;
    return E_ABORT;
}
HRESULT WINAPI NativeGlobalDevice(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void** output)
{
    return NativeDevice(nullptr, nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(IUnknown), output);
}
HRESULT STDMETHODCALLTYPE LaterDeviceHook(ID3D12DeviceFactory* factory, IUnknown* adapter, D3D_FEATURE_LEVEL level, REFIID iid, void** output)
{
    ++delegationCalls;
    if (++delegationDepth > 1) { --delegationDepth; return E_UNEXPECTED; }
    const HRESULT result = nextFactoryMethod(factory, adapter, level, iid, output);
    --delegationDepth;
    return result;
}
HRESULT STDMETHODCALLTYPE NativeFactory(View*, UINT, const char*, REFIID, void** output)
{
    ++factoryAcquisition->references;
    *output = factoryAcquisition;
    return S_OK;
}
HRESULT WINAPI NativeGetInterface(REFCLSID, REFIID, void** output)
{
    ++acquisition->references;
    *output = acquisition;
    return S_OK;
}

void InitializeTable(void** table, bool factory)
{
    table[0] = reinterpret_cast<void*>(&Query);
    table[1] = reinterpret_cast<void*>(&Add);
    table[2] = reinterpret_cast<void*>(&Release);
    table[factory ? FactoryCreateDeviceSlot : ConfigurationCreateFactorySlot] =
        factory ? reinterpret_cast<void*>(&NativeDevice) : reinterpret_cast<void*>(&NativeFactory);
}

void** NewTable(bool factory)
{
    auto* table = static_cast<void**>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (table == nullptr) std::abort();
    InitializeTable(table, factory);
    return table;
}

void CheckObservations(View& factory, View& unknown)
{
    using namespace ResourceManagerGpuObservation;
    Snapshot before;
    Check(ResourceManagerGpuPlacementReadDeviceObservations(&before) == 1, "observation-export-readable");
    unknown.queryResult = E_NOINTERFACE;
    deviceAcquisition = &unknown;
    g_realD3D12CreateDevice = NativeGlobalDevice;
    void* output = nullptr;
    const auto count = before.api[static_cast<uint32_t>(Api::D3D12)].returnedDeviceCount;
    Check(HookedD3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(IUnknown), &output) == S_OK &&
        output == &unknown && unknown.references == 2, "identity-query-failure-preserves-success-and-caller-reference");
    static_cast<IUnknown*>(output)->Release();
    output = nullptr;
    Check(HookedFactoryCreateDevice(reinterpret_cast<ID3D12DeviceFactory*>(&factory), nullptr, D3D_FEATURE_LEVEL_11_0,
        __uuidof(IUnknown), &output) == S_OK && output == &unknown, "factory-identity-query-failure-preserves-success");
    static_cast<IUnknown*>(output)->Release();
    Snapshot after;
    ResourceManagerGpuPlacementReadDeviceObservations(&after);
    const auto& actual = after.api[static_cast<uint32_t>(Api::D3D12)];
    Check(actual.returnedDeviceCount == count + 2 && actual.identity == Identity::Unavailable && actual.adapterLuid == 0 &&
        unknown.references == 1, "unknown-device-fact-with-no-reference-leak");
    Check(HookedD3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), nullptr) == S_FALSE,
        "support-only-result-preserved");
    deviceAcquisition = nullptr;
    Check(HookedD3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), &output) == E_ABORT,
        "creation-failure-preserved-with-observer");
    Snapshot noDevice;
    ResourceManagerGpuPlacementReadDeviceObservations(&noDevice);
    Check(std::memcmp(&after, &noDevice, sizeof(after)) == 0, "support-and-failure-do-not-publish-device-facts");

    Store store;
    Snapshot snapshot;
    Check(store.CopyTo(nullptr) == 0 && store.CopyTo(&snapshot) == 1 && snapshot.api[0].returnedDeviceCount == 0,
        "observation-read-does-not-create-facts");
    snapshot.version = 99;
    const auto invalid = snapshot;
    Check(store.CopyTo(&snapshot) == 0 && std::memcmp(&invalid, &snapshot, sizeof(snapshot)) == 0,
        "invalid-read-buffer-unchanged");
    snapshot = {};
    store.Publish(Api::D3D11, Identity::Adapter, {7, -1});
    store.Publish(Api::D3D11, Identity::Unavailable);
    store.CopyTo(&snapshot);
    Check(snapshot.api[0].returnedDeviceCount == 2 && snapshot.api[0].identity == Identity::Unavailable && snapshot.api[0].adapterLuid == 0,
        "unknown-replaces-previous-adapter");
    std::atomic<bool> torn{false};
    std::vector<std::thread> writers;
    for (unsigned worker = 0; worker < 4; ++worker) writers.emplace_back([&] {
        for (unsigned i = 0; i < 1000; ++i)
        {
            store.Publish(Api::D3D12, i % 2 ? Identity::Unavailable : Identity::Adapter, {0x12345678, -1});
            Snapshot read;
            store.CopyTo(&read);
            const auto& value = read.api[1];
            const bool complete = (value.identity == Identity::Adapter && value.adapterLuid == 0xffffffff12345678ull) ||
                (value.identity == Identity::Unavailable && value.adapterLuid == 0);
            if (!complete || value.reserved != 0)
                torn = true;
        }
    });
    for (auto& writer : writers) writer.join();
    store.CopyTo(&snapshot);
    Check(!torn && snapshot.api[1].returnedDeviceCount == 4000 && snapshot.api[0].returnedDeviceCount == 2 &&
        snapshot.api[2].returnedDeviceCount == 0, "concurrent-facts-complete-and-api-independent");
}
}

int main()
{
    SetErrorMode(32771);
    SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", nullptr);
    View configA{NewTable(false)}, configB{NewTable(false)};
    View factoryA{NewTable(true)}, factoryB{NewTable(true)};
    configA.queried = configB.queried = &configB;
    configA.identity = configB.identity = &configA;
    factoryA.queried = factoryB.queried = &factoryB;
    factoryA.identity = factoryB.identity = &factoryA;
    acquisition = &configA;
    factoryAcquisition = &factoryA;
    g_realD3D12GetInterface = &NativeGetInterface;
    InterlockedExchange(&g_hooksEnabled, 1);

    ID3D12SDKConfiguration1* configuration = nullptr;
    Check(SUCCEEDED(HookedD3D12GetInterface(CLSID_D3D12SDKConfiguration, __uuidof(ID3D12SDKConfiguration1),
        reinterpret_cast<void**>(&configuration))), "direct-configuration-acquisition");
    Check(configA.table[ConfigurationCreateFactorySlot] == reinterpret_cast<void*>(&HookedCreateDeviceFactory),
        "patch-caller-visible-configuration-not-extra-query-view");
    ID3D12DeviceFactory* factory = nullptr;
    Check(SUCCEEDED(configuration->CreateDeviceFactory(616, "untouched", __uuidof(ID3D12DeviceFactory),
        reinterpret_cast<void**>(&factory))), "direct-factory-acquisition");
    Check(factoryA.table[FactoryCreateDeviceSlot] == reinterpret_cast<void*>(&HookedFactoryCreateDevice),
        "patch-caller-visible-factory-not-extra-query-view");
    ID3D12DeviceFactory* queriedFactory = nullptr;
    Check(SUCCEEDED(factory->QueryInterface(__uuidof(ID3D12DeviceFactory), reinterpret_cast<void**>(&queriedFactory))) &&
        queriedFactory == reinterpret_cast<ID3D12DeviceFactory*>(&factoryB), "different-factory-interface-view-retained");
    Check(factoryB.table[FactoryCreateDeviceSlot] == reinterpret_cast<void*>(&HookedFactoryCreateDevice),
        "query-result-factory-view-also-patched");
    IUnknown* firstIdentity = nullptr;
    IUnknown* secondIdentity = nullptr;
    factory->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(&firstIdentity));
    queriedFactory->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(&secondIdentity));
    Check(firstIdentity == secondIdentity, "tearoff-canonical-iunknown-retained");
    firstIdentity->Release(); secondIdentity->Release(); queriedFactory->Release(); factory->Release(); configuration->Release();

    // The requested interface is valid even when an unnecessary second QI would fail.
    View direct{NewTable(false)};
    direct.identity = direct.queried = &direct;
    direct.queryResult = E_OUTOFMEMORY;
    acquisition = &direct;
    configuration = nullptr;
    Check(SUCCEEDED(HookedD3D12GetInterface(CLSID_D3D12SDKConfiguration, __uuidof(ID3D12SDKConfiguration1),
        reinterpret_cast<void**>(&configuration))), "no-redundant-query-allocation");
    Check(direct.table[ConfigurationCreateFactorySlot] == reinterpret_cast<void*>(&HookedCreateDeviceFactory),
        "valid-direct-interface-not-bypassed-on-extra-query-error");
    void* output = reinterpret_cast<void*>(1);
    Check(configuration->QueryInterface(__uuidof(ID3D12SDKConfiguration1), &output) == E_OUTOFMEMORY && output == nullptr,
        "actual-caller-query-error-is-unchanged");
    configuration->Release();

    View base{NewTable(false)}, derived{NewTable(false)};
    base.queried = derived.queried = &derived;
    base.identity = derived.identity = &base;
    acquisition = &base;
    IUnknown* baseInterface = nullptr;
    Check(SUCCEEDED(HookedD3D12GetInterface(CLSID_D3D12SDKConfiguration, __uuidof(IUnknown),
        reinterpret_cast<void**>(&baseInterface))), "iunknown-acquisition");
    configuration = nullptr;
    Check(SUCCEEDED(baseInterface->QueryInterface(__uuidof(ID3D12SDKConfiguration1), reinterpret_cast<void**>(&configuration))),
        "later-query-configuration-view");
    Check(derived.table[ConfigurationCreateFactorySlot] == reinterpret_cast<void*>(&HookedCreateDeviceFactory),
        "later-query-configuration-view-patched");
    configuration->Release(); baseInterface->Release();

    View rejected{NewTable(true)};
    rejectProtection = true;
    Check(!InstallFactoryMethod(reinterpret_cast<IUnknown*>(&rejected), FactoryCreateDeviceSlot,
        reinterpret_cast<void*>(&HookedFactoryCreateDevice), g_factoryMethods), "actual-install-failure-returned");
    rejectProtection = false;
    Check(rejected.table[FactoryCreateDeviceSlot] == reinterpret_cast<void*>(&NativeDevice), "failed-install-keeps-original-method");
    Check((ReadStatus() & StatusHooksEnabled) == 0, "failure-clears-hook-health");
    InitializeProviderCore(false);
    Check((ReadStatus() & StatusHooksEnabled) == 0, "later-export-initialization-cannot-erase-failure");
    View conflict{NewTable(true)};
    Check(InstallFactoryMethod(reinterpret_cast<IUnknown*>(&conflict), FactoryCreateDeviceSlot,
        reinterpret_cast<void*>(&HookedFactoryCreateDevice), g_factoryMethods), "initial-method-hook-for-coexistence");
    nextFactoryMethod = reinterpret_cast<FactoryCreateDeviceFn>(conflict.table[FactoryCreateDeviceSlot]);
    conflict.table[FactoryCreateDeviceSlot] = reinterpret_cast<void*>(&LaterDeviceHook);
    Check(!InstallFactoryMethod(reinterpret_cast<IUnknown*>(&conflict), FactoryCreateDeviceSlot,
        reinterpret_cast<void*>(&HookedFactoryCreateDevice), g_factoryMethods), "later-hook-is-not-recursively-rechained");
    Check(conflict.table[FactoryCreateDeviceSlot] == reinterpret_cast<void*>(&LaterDeviceHook), "other-hook-is-not-overwritten");
    output = nullptr;
    const auto invokeConflict = reinterpret_cast<FactoryCreateDeviceFn>(conflict.table[FactoryCreateDeviceSlot]);
    Check(invokeConflict(reinterpret_cast<ID3D12DeviceFactory*>(&conflict), nullptr, D3D_FEATURE_LEVEL_11_0,
        __uuidof(ID3D12Device), &output) == E_ABORT && delegationCalls == 1, "existing-delegating-chain-still-calls-original-once");
    CheckObservations(factoryA, direct);
    for (View* view : {&configA, &configB, &factoryA, &factoryB, &direct, &base, &derived})
        Check(view->references == 1, "no-extra-com-reference-retained");
    for (View* view : {&configA, &configB, &factoryA, &factoryB, &direct, &base, &derived, &rejected, &conflict})
        VirtualFree(view->table, 0, MEM_RELEASE);
    std::printf("{\"mode\":\"factory-contract\",\"passed\":%s,\"checks\":%u,\"failedChecks\":%u}\n",
        failed == 0 ? "true" : "false", passed + failed, failed);
    return failed == 0 ? 0 : 1;
}
