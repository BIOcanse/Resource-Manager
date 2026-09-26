#pragma once

// Included inside the Provider after its shared COM/MinHook helpers.
namespace D3D9Provider
{
using Create9 = IDirect3D9*(WINAPI*)(UINT);
using Create9Ex = ResourceManagerD3D9::Create9Ex;
using CreateDevice = HRESULT(STDMETHODCALLTYPE*)(IDirect3D9*, UINT, D3DDEVTYPE, HWND,
    DWORD, D3DPRESENT_PARAMETERS*, IDirect3DDevice9**);
using CreateDeviceEx = HRESULT(STDMETHODCALLTYPE*)(IDirect3D9Ex*, UINT, D3DDEVTYPE, HWND,
    DWORD, D3DPRESENT_PARAMETERS*, D3DDISPLAYMODEEX*, IDirect3DDevice9Ex**);

Create9 realCreate9 = nullptr;
Create9Ex realCreate9Ex = nullptr;
HMODULE moduleReference = nullptr;
INIT_ONCE initialization = INIT_ONCE_STATIC_INIT;
INIT_ONCE placementInitialization = INIT_ONCE_STATIC_INIT;
volatile LONG ready = 0;
volatile LONG hooksReady = 0;
bool placementPrepared = false;
SRWLOCK creationLock = SRWLOCK_INIT;
ResourceManagerD3D9::HybridEnumeration hybrid;
std::unordered_map<void**, void*> createMethods, createExMethods, queryMethods;
thread_local bool insideNativeEntry = false;

struct NativeEntry
{
    NativeEntry() { insideNativeEntry = true; }
    ~NativeEntry() { insideNativeEntry = false; }
};

class FactoryAccess
{
    UINT original_ = 0, expected_ = 0;
    bool readable_ = false, conflict_ = false, restored_ = false;
public:
    FactoryAccess()
    {
        AcquireSRWLockExclusive(&creationLock);
        readable_ = hybrid.Read(original_);
        expected_ = original_;
    }
    ~FactoryAccess() { Restore(); ReleaseSRWLockExclusive(&creationLock); }
    UINT Original() const { return original_; }
    bool Set(UINT mode)
    {
        if (!readable_ || conflict_ || InterlockedCompareExchange(&ready, 0, 0) != 1) return false;
        UINT current = 0;
        const bool changed = mode == expected_
            ? hybrid.Read(current) && current == expected_
            : hybrid.TryChange(expected_, mode);
        if (!changed) { conflict_ = true; InterlockedExchange(&ready, -1); return false; }
        expected_ = mode;
        return true;
    }
    bool Restore()
    {
        if (restored_) return !conflict_;
        restored_ = true;
        if (!conflict_ && expected_ != original_ && !hybrid.TryChange(expected_, original_))
        {
            conflict_ = true;
            InterlockedExchange(&ready, -1);
        }
        return !conflict_;
    }
};

template<typename Read>
HRESULT WithFactoryIdentities(IDirect3D9* factory, const Read& read)
{
    FactoryAccess access;
    HRESULT result = D3DERR_NOTAVAILABLE;
    const UINT modes[]{access.Original(), 1, 4};
    for (size_t index = 0; index < 3; ++index)
    {
        if (index != 0 && modes[index] == modes[0]) continue;
        if (index != 0 && !access.Set(modes[index])) break;
        result = read(factory, realCreate9Ex);
        if (result != D3DERR_NOTAVAILABLE) break;
    }
    return access.Restore() ? result : D3DERR_NOTAVAILABLE;
}

bool ContainsTarget(IDirect3D9* factory, LUID target)
{
    ResourceManagerD3D9::AdapterIdentities identities(factory);
    if (FAILED(identities.Open(realCreate9Ex))) return false;
    const UINT count = factory->GetAdapterCount();
    if (count > ResourceManagerD3D9::MaximumAdapterCount) return false;
    for (UINT index = 0; index < count; ++index)
    {
        LUID actual{};
        if (SUCCEEDED(identities.Luid(index, actual)) && SameLuid(actual, target)) return true;
    }
    return false;
}

template<typename Factory, typename Create>
HRESULT MakeFactory(UINT sdk, Factory** output, const Create& create)
{
    FactoryAccess access;
    HRESULT result = create(sdk, output);
    if (FAILED(result) || !output || !*output) return result;
    const auto policy = ReadPolicyFile();
    if (policy.mode == GpuShimPolicyMode::Default) return result;
    if (InterlockedCompareExchange(&ready, 0, 0) != 1)
    {
        (*output)->Release(); *output = nullptr;
        return D3DERR_NOTAVAILABLE;
    }

    Microsoft::WRL::ComPtr<IDXGIAdapter1> target;
    target.Attach(SelectAdapter(policy));
    DXGI_ADAPTER_DESC1 description{};
    if (!target || FAILED(target->GetDesc1(&description)))
    {
        (*output)->Release(); *output = nullptr;
        return D3DERR_NOTAVAILABLE;
    }
    bool found = ContainsTarget(*output, description.AdapterLuid);
    const UINT modes[]{1, 4};
    for (const UINT mode : modes)
    {
        if (found) break;
        if (mode == access.Original()) continue;
        if (!access.Set(mode)) break;
        (*output)->Release(); *output = nullptr;
        result = create(sdk, output);
        if (FAILED(result) || !*output) break;
        found = ContainsTarget(*output, description.AdapterLuid);
    }
    if (!access.Restore() || !found)
    {
        if (*output) { (*output)->Release(); *output = nullptr; }
        return FAILED(result) ? result : D3DERR_NOTAVAILABLE;
    }
    return result;
}

void ObserveDevice(HRESULT result, IDirect3DDevice9* device, D3DDEVTYPE requestedType) noexcept
{
    if (FAILED(result) || !device) return;
    auto identity = ResourceManagerGpuObservation::Identity::Unavailable;
    LUID actual{};
    try
    {
        D3DDEVICE_CREATION_PARAMETERS parameters{};
        Microsoft::WRL::ComPtr<IDirect3D9> parent;
        if (requestedType == D3DDEVTYPE_HAL && SUCCEEDED(device->GetCreationParameters(&parameters))
            && parameters.DeviceType == D3DDEVTYPE_HAL && SUCCEEDED(device->GetDirect3D(parent.GetAddressOf())) && parent)
        {
            const HRESULT identified = WithFactoryIdentities(parent.Get(), [&](IDirect3D9* factory, Create9Ex createEx) {
                ResourceManagerD3D9::AdapterIdentities identities(factory);
                const HRESULT opened = identities.Open(createEx);
                return FAILED(opened) ? opened : identities.Luid(parameters.AdapterOrdinal, actual);
            });
            if (SUCCEEDED(identified)) identity = ResourceManagerGpuObservation::Identity::Adapter;
        }
    }
    catch (...) { }
    g_deviceObservations.Publish(ResourceManagerGpuObservation::Api::D3D9, identity, actual);
}

template<typename Device, typename Create>
HRESULT MakeDevice(IDirect3D9* factory, UINT adapter, D3DDEVTYPE type, DWORD flags, Device** output, const Create& create)
{
    UINT selected = adapter;
    const auto policy = ReadPolicyFile();
    if (type == D3DDEVTYPE_HAL && policy.mode != GpuShimPolicyMode::Default)
    {
        if (InterlockedCompareExchange(&ready, 0, 0) != 1)
        {
            if (output) *output = nullptr;
            return D3DERR_NOTAVAILABLE;
        }
        const HRESULT selection = WithFactoryIdentities(factory, [&](IDirect3D9* original, Create9Ex createEx) {
            return ResourceManagerD3D9::SelectAdapterOrdinal(original, adapter, type, flags, policy, createEx, selected);
        });
        if (FAILED(selection)) { if (output) *output = nullptr; return selection; }
    }
    const HRESULT result = create(selected);
    ObserveDevice(result, output ? *output : nullptr, type);
    return result;
}

HRESULT Attach(HRESULT result, REFIID iid, void** output);

HRESULT STDMETHODCALLTYPE QueryInterface(IUnknown* object, REFIID iid, void** output)
{
    const auto original = reinterpret_cast<FactoryQueryInterfaceFn>(OriginalFactoryMethod(object, queryMethods));
    if (!original) { if (output) *output = nullptr; return E_UNEXPECTED; }
    return Attach(original(object, iid, output), iid, output);
}

HRESULT STDMETHODCALLTYPE HookCreateDevice(IDirect3D9* factory, UINT adapter, D3DDEVTYPE type, HWND window,
    DWORD flags, D3DPRESENT_PARAMETERS* parameters, IDirect3DDevice9** output)
{
    const auto original = reinterpret_cast<CreateDevice>(OriginalFactoryMethod(factory, createMethods));
    if (!original) { if (output) *output = nullptr; return E_UNEXPECTED; }
    if (insideNativeEntry) return original(factory, adapter, type, window, flags, parameters, output);
    NativeEntry entry;
    if (!parameters || !output || adapter >= factory->GetAdapterCount())
        return original(factory, adapter, type, window, flags, parameters, output);
    try { return MakeDevice(factory, adapter, type, flags, output, [&](UINT selected) {
        return original(factory, selected, type, window, flags, parameters, output);
    }); }
    catch (...) { if (output) *output = nullptr; return E_FAIL; }
}

HRESULT STDMETHODCALLTYPE HookCreateDeviceEx(IDirect3D9Ex* factory, UINT adapter, D3DDEVTYPE type, HWND window,
    DWORD flags, D3DPRESENT_PARAMETERS* parameters, D3DDISPLAYMODEEX* display, IDirect3DDevice9Ex** output)
{
    const auto original = reinterpret_cast<CreateDeviceEx>(OriginalFactoryMethod(factory, createExMethods));
    if (!original) { if (output) *output = nullptr; return E_UNEXPECTED; }
    if (insideNativeEntry) return original(factory, adapter, type, window, flags, parameters, display, output);
    NativeEntry entry;
    if (!parameters || !output || adapter >= factory->GetAdapterCount())
        return original(factory, adapter, type, window, flags, parameters, display, output);
    try { return MakeDevice(factory, adapter, type, flags, output, [&](UINT selected) {
        return original(factory, selected, type, window, flags, parameters, display, output);
    }); }
    catch (...) { if (output) *output = nullptr; return E_FAIL; }
}

HRESULT Attach(HRESULT result, REFIID iid, void** output)
{
    if (FAILED(result) || !output || !*output) return result;
    const bool extended = IsEqualGUID(iid, __uuidof(IDirect3D9Ex));
    const bool classic = IsEqualGUID(iid, __uuidof(IDirect3D9));
    if (!extended && !classic && !IsEqualGUID(iid, __uuidof(IUnknown))) return result;
    auto* object = static_cast<IUnknown*>(*output);
    bool installed = true;
    if (extended || classic)
        installed = InstallFactoryMethod(object, 16, reinterpret_cast<void*>(&HookCreateDevice), createMethods);
    if (extended && installed)
        installed = InstallFactoryMethod(object, 20, reinterpret_cast<void*>(&HookCreateDeviceEx), createExMethods);
    if (installed) installed = InstallFactoryMethod(object, 0, reinterpret_cast<void*>(&QueryInterface), queryMethods);
    if (!installed)
    {
        InterlockedExchange(&ready, -1);
        object->Release(); *output = nullptr;
        return E_FAIL;
    }
    return result;
}

IDirect3D9* WINAPI HookCreate9(UINT sdk)
{
    if (insideNativeEntry) return realCreate9(sdk);
    if (ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D9)
        || InterlockedCompareExchange(&ready, 0, 0) == 0) return realCreate9(sdk);
    NativeEntry entry;
    IDirect3D9* output = nullptr;
    try
    {
        HRESULT result = MakeFactory(sdk, &output, [](UINT version, IDirect3D9** value) {
            *value = realCreate9(version); return *value ? S_OK : D3DERR_NOTAVAILABLE;
        });
        result = Attach(result, __uuidof(IDirect3D9), reinterpret_cast<void**>(&output));
        return SUCCEEDED(result) ? output : nullptr;
    }
    catch (...) { if (output) output->Release(); return nullptr; }
}

HRESULT WINAPI HookCreate9Ex(UINT sdk, IDirect3D9Ex** output)
{
    if (insideNativeEntry) return realCreate9Ex(sdk, output);
    if (ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D9)
        || InterlockedCompareExchange(&ready, 0, 0) == 0) return realCreate9Ex(sdk, output);
    if (!output) return realCreate9Ex(sdk, output);
    NativeEntry entry;
    *output = nullptr;
    try
    {
        const HRESULT result = MakeFactory(sdk, output, realCreate9Ex);
        return Attach(result, __uuidof(IDirect3D9Ex), reinterpret_cast<void**>(output));
    }
    catch (...) { if (*output) { (*output)->Release(); *output = nullptr; } return E_FAIL; }
}

BOOL CALLBACK InitializeOnce(PINIT_ONCE, PVOID parameter, PVOID*)
{
    const auto module = static_cast<HMODULE>(parameter);
    InterlockedExchange(&hooksReady, -1);
    try
    {
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(module), &moduleReference)) return TRUE;
        HMODULE provider{};
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&InitializeOnce), &provider)) return TRUE;
        const auto classic = reinterpret_cast<void*>(GetProcAddress(moduleReference, "Direct3DCreate9"));
        const auto extended = reinterpret_cast<void*>(GetProcAddress(moduleReference, "Direct3DCreate9Ex"));
        // Both original entry points must exist before any thread can enter either hook.
        if (MH_CreateHook(classic, reinterpret_cast<void*>(&HookCreate9), reinterpret_cast<void**>(&realCreate9)) == MH_OK
            && MH_CreateHook(extended, reinterpret_cast<void*>(&HookCreate9Ex), reinterpret_cast<void**>(&realCreate9Ex)) == MH_OK
            && MH_QueueEnableHook(classic) == MH_OK && MH_QueueEnableHook(extended) == MH_OK && MH_ApplyQueued() == MH_OK)
            InterlockedExchange(&hooksReady, 1);
    }
    catch (...) { InterlockedExchange(&hooksReady, -1); }
    return TRUE;
}

bool EnsureEntryHooks()
{
    // An absent API does not consume the one-time installation.
    const auto module = GetModuleHandleW(L"d3d9.dll");
    if (!module) return false;
    InitOnceExecuteOnce(&initialization, InitializeOnce, module, nullptr);
    return InterlockedCompareExchange(&hooksReady, 0, 0) == 1;
}
BOOL CALLBACK PreparePlacementOnce(PINIT_ONCE, PVOID, PVOID*)
{
    try { placementPrepared = hybrid.Open(moduleReference); }
    catch (...) { placementPrepared = false; }
    return TRUE;
}
enum class PlacementPreparation { NotLoaded, Prepared, Failed };
PlacementPreparation PreparePlacement()
{
    if (!GetModuleHandleW(L"d3d9.dll")) return PlacementPreparation::NotLoaded;
    if (!EnsureEntryHooks()) return PlacementPreparation::Failed;
    InitOnceExecuteOnce(&placementInitialization, PreparePlacementOnce, nullptr, nullptr);
    return placementPrepared ? PlacementPreparation::Prepared : PlacementPreparation::Failed;
}
// Called only with the original policy publication lock held.
void PublishPlacement(PlacementPreparation preparation)
{
    if (preparation == PlacementPreparation::Prepared) InterlockedCompareExchange(&ready, 1, 0);
}
}
