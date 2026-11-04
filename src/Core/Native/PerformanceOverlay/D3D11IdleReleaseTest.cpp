#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include "IdleReleaseTest.h"
#include <new>

using namespace IdleReleaseTest;

namespace
{
const GUID LifetimeGuid = {0x8df9e651, 0x24a0, 0x43bb, {0x97, 0xa8, 0x88, 0xdd, 0x07, 0x03, 0x36, 0xf1}};
volatile LONG liveTextures = 0, createdTextures = 0, destroyedTextures = 0, markerFailures = 0;
ID3D11Device* trackedDevice = nullptr;
using CreateTextureFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*,
    const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
CreateTextureFn originalCreateTexture = nullptr;

class TextureLifetime final : public IUnknown
{
    volatile LONG references_ = 1;
public:
    TextureLifetime() { InterlockedIncrement(&liveTextures); }
    ~TextureLifetime() { InterlockedDecrement(&liveTextures); InterlockedIncrement(&destroyedTextures); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (!IsEqualIID(id, __uuidof(IUnknown))) return E_NOINTERFACE;
        *output = this;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references_); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const LONG references = InterlockedDecrement(&references_);
        if (!references) delete this;
        return references;
    }
};

HRESULT STDMETHODCALLTYPE TrackTexture(ID3D11Device* device, const D3D11_TEXTURE2D_DESC* desc,
    const D3D11_SUBRESOURCE_DATA* data, ID3D11Texture2D** output)
{
    const bool overlayTexture = device == trackedDevice && desc && data && output
        && desc->Width == Width && desc->Height == Height && desc->Format == DXGI_FORMAT_B8G8R8A8_UNORM
        && desc->BindFlags == D3D11_BIND_SHADER_RESOURCE && desc->Usage == D3D11_USAGE_DEFAULT;
    const SIZE_T before = overlayTexture ? PrivateCommit() : 0;
    const SIZE_T mappedBefore = overlayTexture ? MappedCommit() : 0;
    const HRESULT result = originalCreateTexture(device, desc, data, output);
    if (overlayTexture && SUCCEEDED(result) && *output)
    {
        auto* lifetime = new (std::nothrow) TextureLifetime;
        if (!lifetime) InterlockedIncrement(&markerFailures);
        else
        {
            if (FAILED((*output)->SetPrivateDataInterface(LifetimeGuid, lifetime)))
                InterlockedIncrement(&markerFailures);
            else InterlockedIncrement(&createdTextures);
            // The resource's private-data reference is now the marker's only owner.
            lifetime->Release();
        }
        const SIZE_T after = PrivateCommit();
        const SIZE_T mappedAfter = MappedCommit();
        std::printf("D3D11 overlay_texture_created nominal_bytes=%u creation_commit_delta=%lld creation_mapped_delta=%lld live=%ld\n",
            Width * Height * 4, static_cast<long long>(after) - static_cast<long long>(before),
            static_cast<long long>(mappedAfter) - static_cast<long long>(mappedBefore),
            InterlockedCompareExchange(&liveTextures, 0, 0));
    }
    return result;
}

void PatchTextureMethod(ID3D11Device* device, bool install)
{
    void** methods = *reinterpret_cast<void***>(device);
    DWORD protection = 0;
    Check(VirtualProtect(methods + 5, sizeof(void*), PAGE_EXECUTE_READWRITE, &protection),
        "Texture lifetime vtable protection failed");
    if (install)
    {
        originalCreateTexture = reinterpret_cast<CreateTextureFn>(methods[5]);
        trackedDevice = device;
        InterlockedExchangePointer(reinterpret_cast<void* volatile*>(methods + 5), reinterpret_cast<void*>(&TrackTexture));
    }
    else
    {
        InterlockedExchangePointer(reinterpret_cast<void* volatile*>(methods + 5), reinterpret_cast<void*>(originalCreateTexture));
        trackedDevice = nullptr;
    }
    DWORD ignored = 0;
    VirtualProtect(methods + 5, sizeof(void*), protection, &ignored);
}
}

struct Renderer
{
    Window window;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IDXGISwapChain* chain = nullptr;
    ID3D11Texture2D* buffer = nullptr;
    ID3D11RenderTargetView* target = nullptr;

    // RuntimeRead: Fixed
    Renderer()
    {
        LoadOverlay();
        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferDesc.Width = Width;
        desc.BufferDesc.Height = Height;
        desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 1;
        desc.OutputWindow = window.handle;
        desc.Windowed = TRUE;
        desc.SwapEffect = DXGI_SWAP_EFFECT_SEQUENTIAL;
        Check(SUCCEEDED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            &desc, &chain, &device, nullptr, &context)), "D3D11 WARP device/swapchain creation failed");
        Check(SUCCEEDED(chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&buffer))),
            "GetBuffer failed");
        Check(SUCCEEDED(device->CreateRenderTargetView(buffer, nullptr, &target)), "CreateRenderTargetView failed");
        PatchTextureMethod(device, true);
    }

    ~Renderer()
    {
        if (context) { context->ClearState(); context->Flush(); }
        if (device && trackedDevice == device) PatchTextureMethod(device, false);
        Drop(target); Drop(buffer); Drop(chain); Drop(context); Drop(device);
    }

    void Frame()
    {
        const FLOAT black[4] = {0, 0, 0, 1};
        context->ClearRenderTargetView(target, black);
        Check(SUCCEEDED(chain->Present(0, 0)), "D3D11 Present failed");
    }

    // RuntimeRead: Sampled
    void CheckPixel(uint32_t expected)
    {
        D3D11_TEXTURE2D_DESC desc{};
        buffer->GetDesc(&desc);
        desc.BindFlags = 0;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;
        ID3D11Texture2D* staging = nullptr;
        Check(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &staging)), "Create readback texture failed");
        context->CopyResource(staging, buffer);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        const HRESULT result = context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
        uint32_t actual = 0;
        if (SUCCEEDED(result))
        {
            actual = *reinterpret_cast<const uint32_t*>(static_cast<const uint8_t*>(mapped.pData)
                + size_t(Height / 2) * mapped.RowPitch + Width / 2 * 4);
            context->Unmap(staging, 0);
        }
        Drop(staging);
        std::printf("D3D11 pixel expected=%08x actual=%08x\n", expected, actual);
        Check(SUCCEEDED(result) && actual == expected, "D3D11 overlay pixel readback mismatch");
        Check(InterlockedCompareExchange(&markerFailures, 0, 0) == 0, "Overlay texture lifetime marker installation failed");
        Check(InterlockedCompareExchange(&liveTextures, 0, 0) >= 1,
            "Enabled overlay has no tagged shader-resource texture");
    }

    void CheckDisabled()
    {
        const LONG live = InterlockedCompareExchange(&liveTextures, 0, 0);
        std::printf("D3D11 overlay_texture_lifetime disabled_live=%ld created=%ld destroyed=%ld\n", live,
            InterlockedCompareExchange(&createdTextures, 0, 0), InterlockedCompareExchange(&destroyedTextures, 0, 0));
        Check(InterlockedCompareExchange(&markerFailures, 0, 0) == 0, "Overlay texture lifetime marker installation failed");
        Check(live == 0, "Disabled overlay still owns a shader-resource texture");
    }
};

int main()
{
    try
    {
        Renderer renderer;
        // The private-data marker proves texture COM destruction independently of the WARP allocator's cached commit.
        Run(renderer, "D3D11-WARP", +[](Renderer& current) { current.CheckDisabled(); });
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "D3D11 FAIL: %s (Win32 error %lu)\n", error.what(), GetLastError());
        return 1;
    }
}
