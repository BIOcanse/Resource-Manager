#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <d3d11on12.h>
#include <dxgi1_4.h>
#include <d3dcompiler.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <MinHook.h>
#include "SharedSection.h"
#include <algorithm>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

#ifndef GL_BGRA
#define GL_BGRA 0x80E1
#endif
#ifndef GL_CONTEXT_PROFILE_MASK
#define GL_CONTEXT_PROFILE_MASK 0x9126
#define GL_CONTEXT_CORE_PROFILE_BIT 0x00000001
#endif

namespace
{
template <typename T> void Drop(T*& value) { if (value) { value->Release(); value = nullptr; } }
OverlaySection::Reader section;
std::mutex installMutex;
std::mutex d3dMutex;
std::mutex glMutex;
bool installed = false;

using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using ResizeFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using Resize1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT, const UINT*, IUnknown* const*);
using SwapFn = BOOL(WINAPI*)(HDC);
using CreateSwapChainFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
using CreateForHwndFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, HWND,
    const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
using CreateForCoreFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, IUnknown*,
    const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
using CreateForCompositionFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*,
    const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
PresentFn originalPresent = nullptr;
Present1Fn originalPresent1 = nullptr;
ResizeFn originalResize = nullptr;
Resize1Fn originalResize1 = nullptr;
SwapFn originalSwap = nullptr;
CreateSwapChainFn originalCreateSwapChain = nullptr;
CreateForHwndFn originalCreateForHwnd = nullptr;
CreateForCoreFn originalCreateForCore = nullptr;
CreateForCompositionFn originalCreateForComposition = nullptr;

struct D3D12Chain
{
    ID3D12CommandQueue* queue = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    ID3D11On12Device* on12 = nullptr;
    std::vector<ID3D11Texture2D*> buffers;
    ~D3D12Chain() { Clear(); Drop(queue); Drop(on12); Drop(context); Drop(device); }
    void Clear()
    {
        if (context) { context->ClearState(); context->Flush(); }
        for (auto*& buffer : buffers) Drop(buffer);
        buffers.clear();
    }
};
// The DLL stays loaded for the target's lifetime. COM cleanup from static destructors
// runs under the loader lock at process exit and can deadlock the graphics runtime.
auto& d3d12Chains = *new std::unordered_map<IDXGISwapChain*, std::unique_ptr<D3D12Chain>>;

struct D3DResources
{
    ID3D11Device* device = nullptr;
    ID3D11VertexShader* vertex = nullptr;
    ID3D11PixelShader* pixel = nullptr;
    ID3D11Buffer* constants = nullptr;
    ID3D11SamplerState* sampler = nullptr;
    ID3D11BlendState* blend = nullptr;
    ID3D11DepthStencilState* depth = nullptr;
    ID3D11RasterizerState* raster = nullptr;
    ID3D11Texture2D* texture = nullptr;
    ID3D11ShaderResourceView* textureView = nullptr;
    uint64_t uploadedGeneration = 0;
    ~D3DResources() { Clear(); }
    void Clear()
    {
        Drop(textureView); Drop(texture); Drop(raster); Drop(depth); Drop(blend);
        Drop(sampler); Drop(constants); Drop(pixel); Drop(vertex); Drop(device);
        uploadedGeneration = 0;
    }
};
D3DResources& d3d = *new D3DResources;

const char* VertexSource = R"(
cbuffer OverlaySize : register(b0) { float4 size; };
struct Output { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Output main(uint id : SV_VertexID) {
    float2 xy = float2((id == 1 || id == 4 || id == 5) ? size.x : 0,
                       (id == 2 || id == 3 || id == 5) ? size.y : 0);
    Output result;
    result.position = float4(xy.x / size.z * 2 - 1, 1 - xy.y / size.w * 2, 0, 1);
    result.uv = xy / size.xy;
    return result;
})";
const char* PixelSource = R"(
Texture2D bitmap : register(t0);
SamplerState pointSampler : register(s0);
float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    return bitmap.Sample(pointSampler, uv);
})";

bool PrepareD3D(ID3D11Device* device)
{
    if (d3d.device == device && d3d.vertex && d3d.pixel && d3d.constants && d3d.sampler
        && d3d.blend && d3d.depth && d3d.raster) return true;
    d3d.Clear();
    d3d.device = device;
    device->AddRef();
    HMODULE compiler = LoadLibraryExW(L"d3dcompiler_47.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!compiler) return false;
    using CompileFn = HRESULT(WINAPI*)(LPCVOID, SIZE_T, LPCSTR, const D3D_SHADER_MACRO*, ID3DInclude*,
        LPCSTR, LPCSTR, UINT, UINT, ID3DBlob**, ID3DBlob**);
    CompileFn compile = nullptr;
    const FARPROC compilerAddress = GetProcAddress(compiler, "D3DCompile");
    static_assert(sizeof(compile) == sizeof(compilerAddress));
    std::memcpy(&compile, &compilerAddress, sizeof(compile));
    ID3DBlob *vs = nullptr, *ps = nullptr, *errors = nullptr;
    bool good = compile
        && SUCCEEDED(compile(VertexSource, std::strlen(VertexSource), nullptr, nullptr, nullptr,
            "main", "vs_4_0", 0, 0, &vs, &errors));
    Drop(errors);
    if (good) good = SUCCEEDED(compile(PixelSource, std::strlen(PixelSource), nullptr, nullptr, nullptr,
        "main", "ps_4_0", 0, 0, &ps, &errors));
    Drop(errors);
    if (good) good = SUCCEEDED(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &d3d.vertex))
        && SUCCEEDED(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &d3d.pixel));
    Drop(vs); Drop(ps);
    FreeLibrary(compiler);
    if (!good) return false;

    D3D11_BUFFER_DESC cb{};
    cb.ByteWidth = 16;
    cb.Usage = D3D11_USAGE_DEFAULT;
    cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    D3D11_SAMPLER_DESC sampler{};
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX;
    D3D11_BLEND_DESC blend{};
    blend.RenderTarget[0].BlendEnable = TRUE;
    blend.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
    blend.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    blend.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    blend.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    blend.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    blend.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    blend.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    D3D11_DEPTH_STENCIL_DESC depth{};
    depth.DepthEnable = FALSE;
    depth.StencilEnable = FALSE;
    D3D11_RASTERIZER_DESC raster{};
    raster.FillMode = D3D11_FILL_SOLID;
    raster.CullMode = D3D11_CULL_NONE;
    raster.DepthClipEnable = TRUE;
    good = SUCCEEDED(device->CreateBuffer(&cb, nullptr, &d3d.constants))
        && SUCCEEDED(device->CreateSamplerState(&sampler, &d3d.sampler))
        && SUCCEEDED(device->CreateBlendState(&blend, &d3d.blend))
        && SUCCEEDED(device->CreateDepthStencilState(&depth, &d3d.depth))
        && SUCCEEDED(device->CreateRasterizerState(&raster, &d3d.raster));
    if (!good) d3d.Clear();
    return good;
}

bool UploadD3D(ID3D11Device* device, ID3D11DeviceContext* context, const OverlaySection::Bitmap& bitmap)
{
    if (d3d.uploadedGeneration == bitmap.generation && d3d.textureView) return true;
    Drop(d3d.textureView);
    Drop(d3d.texture);
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = bitmap.width;
    desc.Height = bitmap.height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA source{};
    source.pSysMem = bitmap.pixels.data();
    source.SysMemPitch = bitmap.stride;
    if (FAILED(device->CreateTexture2D(&desc, &source, &d3d.texture))
        || FAILED(device->CreateShaderResourceView(d3d.texture, nullptr, &d3d.textureView))) return false;
    d3d.uploadedGeneration = bitmap.generation;
    (void)context;
    return true;
}

struct D3DState
{
    ID3D11RenderTargetView* targets[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};
    ID3D11DepthStencilView* depthTarget = nullptr;
    ID3D11BlendState* blend = nullptr;
    FLOAT blendFactor[4]{};
    UINT sampleMask = 0;
    ID3D11DepthStencilState* depth = nullptr;
    UINT stencilReference = 0;
    ID3D11RasterizerState* raster = nullptr;
    D3D11_VIEWPORT viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE]{};
    UINT viewportCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    ID3D11InputLayout* layout = nullptr;
    D3D11_PRIMITIVE_TOPOLOGY topology{};
    ID3D11Buffer* vertexBuffer = nullptr;
    UINT vertexStride = 0, vertexOffset = 0;
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11GeometryShader* gs = nullptr;
    ID3D11HullShader* hs = nullptr;
    ID3D11DomainShader* ds = nullptr;
    ID3D11ClassInstance* vsClasses[256]{};
    ID3D11ClassInstance* psClasses[256]{};
    ID3D11ClassInstance* gsClasses[256]{};
    ID3D11ClassInstance* hsClasses[256]{};
    ID3D11ClassInstance* dsClasses[256]{};
    UINT vsCount = 256, psCount = 256, gsCount = 256, hsCount = 256, dsCount = 256;
    ID3D11Buffer* vsBuffer = nullptr;
    ID3D11ShaderResourceView* psTexture = nullptr;
    ID3D11SamplerState* psSampler = nullptr;
    ID3D11Predicate* predicate = nullptr;
    BOOL predicateValue = FALSE;

    explicit D3DState(ID3D11DeviceContext* context)
    {
        context->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, targets, &depthTarget);
        context->OMGetBlendState(&blend, blendFactor, &sampleMask);
        context->OMGetDepthStencilState(&depth, &stencilReference);
        context->RSGetState(&raster);
        context->RSGetViewports(&viewportCount, viewports);
        context->IAGetInputLayout(&layout);
        context->IAGetPrimitiveTopology(&topology);
        context->IAGetVertexBuffers(0, 1, &vertexBuffer, &vertexStride, &vertexOffset);
        context->VSGetShader(&vs, vsClasses, &vsCount);
        context->PSGetShader(&ps, psClasses, &psCount);
        context->GSGetShader(&gs, gsClasses, &gsCount);
        context->HSGetShader(&hs, hsClasses, &hsCount);
        context->DSGetShader(&ds, dsClasses, &dsCount);
        context->VSGetConstantBuffers(0, 1, &vsBuffer);
        context->PSGetShaderResources(0, 1, &psTexture);
        context->PSGetSamplers(0, 1, &psSampler);
        context->GetPredication(&predicate, &predicateValue);
    }

    void Restore(ID3D11DeviceContext* context)
    {
        context->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, targets, depthTarget);
        context->OMSetBlendState(blend, blendFactor, sampleMask);
        context->OMSetDepthStencilState(depth, stencilReference);
        context->RSSetState(raster);
        context->RSSetViewports(viewportCount, viewportCount ? viewports : nullptr);
        context->IASetInputLayout(layout);
        context->IASetPrimitiveTopology(topology);
        context->IASetVertexBuffers(0, 1, &vertexBuffer, &vertexStride, &vertexOffset);
        context->VSSetShader(vs, vsClasses, vsCount);
        context->PSSetShader(ps, psClasses, psCount);
        context->GSSetShader(gs, gsClasses, gsCount);
        context->HSSetShader(hs, hsClasses, hsCount);
        context->DSSetShader(ds, dsClasses, dsCount);
        context->VSSetConstantBuffers(0, 1, &vsBuffer);
        context->PSSetShaderResources(0, 1, &psTexture);
        context->PSSetSamplers(0, 1, &psSampler);
        context->SetPredication(predicate, predicateValue);
        for (auto*& target : targets) Drop(target);
        Drop(depthTarget); Drop(blend); Drop(depth); Drop(raster); Drop(layout); Drop(vertexBuffer);
        Drop(vs); Drop(ps); Drop(gs); Drop(hs); Drop(ds);
        for (UINT i = 0; i < vsCount; ++i) Drop(vsClasses[i]);
        for (UINT i = 0; i < psCount; ++i) Drop(psClasses[i]);
        for (UINT i = 0; i < gsCount; ++i) Drop(gsClasses[i]);
        for (UINT i = 0; i < hsCount; ++i) Drop(hsClasses[i]);
        for (UINT i = 0; i < dsCount; ++i) Drop(dsClasses[i]);
        Drop(vsBuffer); Drop(psTexture); Drop(psSampler); Drop(predicate);
    }
};

bool RenderD3D(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* buffer,
    const OverlaySection::Bitmap& bitmap, uint32_t width, uint32_t height)
{
    if (!PrepareD3D(device) || !UploadD3D(device, context, bitmap)) return false;
    ID3D11RenderTargetView* target = nullptr;
    if (FAILED(device->CreateRenderTargetView(buffer, nullptr, &target))) return false;
    D3DState state(context);
    const float sizes[4] = { float(bitmap.width), float(bitmap.height), float(width), float(height) };
    context->UpdateSubresource(d3d.constants, 0, nullptr, sizes, 0, 0);
    D3D11_VIEWPORT viewport{ 0, 0, float(width), float(height), 0, 1 };
    context->OMSetRenderTargets(1, &target, nullptr);
    context->OMSetBlendState(d3d.blend, nullptr, 0xffffffff);
    context->OMSetDepthStencilState(d3d.depth, 0);
    context->RSSetState(d3d.raster);
    context->RSSetViewports(1, &viewport);
    context->IASetInputLayout(nullptr);
    ID3D11Buffer* noBuffer = nullptr;
    UINT zero = 0;
    context->IASetVertexBuffers(0, 1, &noBuffer, &zero, &zero);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(d3d.vertex, nullptr, 0);
    context->PSSetShader(d3d.pixel, nullptr, 0);
    context->GSSetShader(nullptr, nullptr, 0);
    context->HSSetShader(nullptr, nullptr, 0);
    context->DSSetShader(nullptr, nullptr, 0);
    context->VSSetConstantBuffers(0, 1, &d3d.constants);
    context->PSSetShaderResources(0, 1, &d3d.textureView);
    context->PSSetSamplers(0, 1, &d3d.sampler);
    context->SetPredication(nullptr, FALSE);
    context->Draw(6, 0);
    state.Restore(context);
    Drop(target);
    section.Drew(bitmap.generation);
    return true;
}

bool PrepareD3D12Chain(IDXGISwapChain* chain, D3D12Chain& resources)
{
    if (resources.on12 && !resources.buffers.empty()) return true;
    if (!resources.queue) return false;
    if (!resources.on12)
    {
        ID3D12Device* device12 = nullptr;
        if (FAILED(resources.queue->GetDevice(__uuidof(ID3D12Device), reinterpret_cast<void**>(&device12)))) return false;
        HMODULE d3d11Module = GetModuleHandleW(L"d3d11.dll");
        using CreateOn12Fn = HRESULT(WINAPI*)(IUnknown*, UINT, const D3D_FEATURE_LEVEL*, UINT,
            IUnknown* const*, UINT, UINT, ID3D11Device**, ID3D11DeviceContext**, D3D_FEATURE_LEVEL*);
        const FARPROC address = d3d11Module ? GetProcAddress(d3d11Module, "D3D11On12CreateDevice") : nullptr;
        CreateOn12Fn createOn12 = nullptr;
        static_assert(sizeof(createOn12) == sizeof(address));
        std::memcpy(&createOn12, &address, sizeof(createOn12));
        IUnknown* queues[] = { resources.queue };
        const HRESULT result = createOn12 ? createOn12(device12, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, queues, 1, 0, &resources.device, &resources.context, nullptr) : E_NOINTERFACE;
        Drop(device12);
        if (FAILED(result) || !resources.device || !resources.context
            || FAILED(resources.device->QueryInterface(__uuidof(ID3D11On12Device), reinterpret_cast<void**>(&resources.on12)))) return false;
    }
    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(chain->GetDesc(&desc))) return false;
    resources.buffers.reserve(desc.BufferCount);
    for (UINT i = 0; i < desc.BufferCount; ++i)
    {
        ID3D12Resource* buffer12 = nullptr;
        ID3D11Texture2D* wrapped = nullptr;
        D3D11_RESOURCE_FLAGS flags{};
        flags.BindFlags = D3D11_BIND_RENDER_TARGET;
        const bool good = SUCCEEDED(chain->GetBuffer(i, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&buffer12)))
            && SUCCEEDED(resources.on12->CreateWrappedResource(buffer12, &flags,
                D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_PRESENT,
                __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&wrapped)));
        Drop(buffer12);
        if (!good) { Drop(wrapped); resources.Clear(); return false; }
        resources.buffers.push_back(wrapped);
    }
    for (auto* buffer : resources.buffers)
    {
        ID3D11Resource* base = buffer;
        resources.on12->ReleaseWrappedResources(&base, 1);
    }
    resources.context->Flush();
    return true;
}

void DrawD3D(IDXGISwapChain* chain)
{
    std::lock_guard<std::mutex> guard(d3dMutex);
    DXGI_SWAP_CHAIN_DESC chainDesc{};
    if (FAILED(chain->GetDesc(&chainDesc))) return;
    IDXGISwapChain3* chain3 = nullptr;
    const UINT index = SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3)))
        ? chain3->GetCurrentBackBufferIndex() : 0;
    Drop(chain3);
    ID3D11Device* device = nullptr;
    if (FAILED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&device))))
    {
        auto found = d3d12Chains.find(chain);
        if (found == d3d12Chains.end() || !PrepareD3D12Chain(chain, *found->second))
        {
            ID3D12Resource* buffer12 = nullptr;
            if (SUCCEEDED(chain->GetBuffer(index, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&buffer12))))
            {
                const auto desc = buffer12->GetDesc();
                section.ObserveFrame(chainDesc.OutputWindow, static_cast<uint32_t>(desc.Width), desc.Height);
            }
            Drop(buffer12);
            return;
        }
        auto& resources = *found->second;
        if (index >= resources.buffers.size()) return;
        ID3D11Texture2D* wrapped = resources.buffers[index];
        D3D11_TEXTURE2D_DESC desc{};
        wrapped->GetDesc(&desc);
        auto bitmap = section.Frame(chainDesc.OutputWindow, desc.Width, desc.Height);
        if (!bitmap) return;
        ID3D11Resource* base = wrapped;
        resources.on12->AcquireWrappedResources(&base, 1);
        RenderD3D(resources.device, resources.context, wrapped, *bitmap, desc.Width, desc.Height);
        resources.on12->ReleaseWrappedResources(&base, 1);
        resources.context->Flush();
        return;
    }
    ID3D11Texture2D* buffer = nullptr;
    if (FAILED(chain->GetBuffer(index, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&buffer))))
    { device->Release(); return; }
    D3D11_TEXTURE2D_DESC bufferDesc{};
    buffer->GetDesc(&bufferDesc);
    auto bitmap = section.Frame(chainDesc.OutputWindow, bufferDesc.Width, bufferDesc.Height);
    if (!bitmap) { Drop(buffer); Drop(device); return; }
    ID3D11DeviceContext* context = nullptr;
    device->GetImmediateContext(&context);
    if (context) RenderD3D(device, context, buffer, *bitmap, bufferDesc.Width, bufferDesc.Height);
    Drop(buffer); Drop(context); Drop(device);
}

struct GlCoreFns
{
    PFNGLCREATESHADERPROC createShader = nullptr;
    PFNGLSHADERSOURCEPROC shaderSource = nullptr;
    PFNGLCOMPILESHADERPROC compileShader = nullptr;
    PFNGLGETSHADERIVPROC getShaderiv = nullptr;
    PFNGLDELETESHADERPROC deleteShader = nullptr;
    PFNGLCREATEPROGRAMPROC createProgram = nullptr;
    PFNGLATTACHSHADERPROC attachShader = nullptr;
    PFNGLLINKPROGRAMPROC linkProgram = nullptr;
    PFNGLGETPROGRAMIVPROC getProgramiv = nullptr;
    PFNGLDELETEPROGRAMPROC deleteProgram = nullptr;
    PFNGLUSEPROGRAMPROC useProgram = nullptr;
    PFNGLGETUNIFORMLOCATIONPROC getUniformLocation = nullptr;
    PFNGLUNIFORM4FPROC uniform4f = nullptr;
    PFNGLUNIFORM1IPROC uniform1i = nullptr;
    PFNGLGENVERTEXARRAYSPROC genVertexArrays = nullptr;
    PFNGLBINDVERTEXARRAYPROC bindVertexArray = nullptr;
    PFNGLACTIVETEXTUREPROC activeTexture = nullptr;
    PFNGLBLENDFUNCSEPARATEPROC blendFuncSeparate = nullptr;
    PFNGLBLENDEQUATIONSEPARATEPROC blendEquationSeparate = nullptr;
    PFNGLBINDFRAMEBUFFERPROC bindFramebuffer = nullptr;
    PFNGLBINDBUFFERPROC bindBuffer = nullptr;
};
struct GlTexture
{
    GLuint name = 0;
    uint64_t generation = 0;
    GLuint program = 0;
    GLuint vao = 0;
    GLint sizeLocation = -1;
    GLint bitmapLocation = -1;
    GlCoreFns core;
};
std::unordered_map<HGLRC, GlTexture> glTextures;
using DeleteContextFn = BOOL(WINAPI*)(HGLRC);
DeleteContextFn originalDeleteContext = nullptr;

template <typename T> bool GlProc(T& output, const char* name)
{
    const PROC address = wglGetProcAddress(name);
    if (!address || address == reinterpret_cast<PROC>(1) || address == reinterpret_cast<PROC>(2)
        || address == reinterpret_cast<PROC>(3) || address == reinterpret_cast<PROC>(-1)) return false;
    static_assert(sizeof(output) == sizeof(address));
    std::memcpy(&output, &address, sizeof(output));
    return true;
}

bool PrepareGlCore(GlTexture& texture)
{
    if (texture.program && texture.vao) return true;
    auto& gl = texture.core;
#define LOAD_GL(field, name) if (!GlProc(gl.field, name)) return false
    LOAD_GL(createShader, "glCreateShader"); LOAD_GL(shaderSource, "glShaderSource");
    LOAD_GL(compileShader, "glCompileShader"); LOAD_GL(getShaderiv, "glGetShaderiv");
    LOAD_GL(deleteShader, "glDeleteShader"); LOAD_GL(createProgram, "glCreateProgram");
    LOAD_GL(attachShader, "glAttachShader"); LOAD_GL(linkProgram, "glLinkProgram");
    LOAD_GL(getProgramiv, "glGetProgramiv"); LOAD_GL(deleteProgram, "glDeleteProgram");
    LOAD_GL(useProgram, "glUseProgram"); LOAD_GL(getUniformLocation, "glGetUniformLocation");
    LOAD_GL(uniform4f, "glUniform4f"); LOAD_GL(uniform1i, "glUniform1i");
    LOAD_GL(genVertexArrays, "glGenVertexArrays"); LOAD_GL(bindVertexArray, "glBindVertexArray");
    LOAD_GL(activeTexture, "glActiveTexture"); LOAD_GL(blendFuncSeparate, "glBlendFuncSeparate");
    LOAD_GL(blendEquationSeparate, "glBlendEquationSeparate");
    LOAD_GL(bindFramebuffer, "glBindFramebuffer"); LOAD_GL(bindBuffer, "glBindBuffer");
#undef LOAD_GL
    constexpr const char* vertexSource = R"(#version 330 core
uniform vec4 size;
out vec2 uv;
void main() {
    vec2 xy = vec2((gl_VertexID == 1 || gl_VertexID == 4 || gl_VertexID == 5) ? size.x : 0.0,
                   (gl_VertexID == 2 || gl_VertexID == 3 || gl_VertexID == 5) ? size.y : 0.0);
    gl_Position = vec4(xy.x / size.z * 2.0 - 1.0, 1.0 - xy.y / size.w * 2.0, 0.0, 1.0);
    uv = xy / size.xy;
})";
    constexpr const char* fragmentSource = R"(#version 330 core
in vec2 uv;
uniform sampler2D bitmap;
out vec4 color;
void main() { color = texture(bitmap, uv); })";
    GLuint vertex = gl.createShader(GL_VERTEX_SHADER);
    GLuint fragment = gl.createShader(GL_FRAGMENT_SHADER);
    if (!vertex || !fragment) return false;
    gl.shaderSource(vertex, 1, &vertexSource, nullptr);
    gl.compileShader(vertex);
    gl.shaderSource(fragment, 1, &fragmentSource, nullptr);
    gl.compileShader(fragment);
    GLint vertexReady = GL_FALSE, fragmentReady = GL_FALSE, linked = GL_FALSE;
    gl.getShaderiv(vertex, GL_COMPILE_STATUS, &vertexReady);
    gl.getShaderiv(fragment, GL_COMPILE_STATUS, &fragmentReady);
    GLuint program = 0;
    if (vertexReady && fragmentReady)
    {
        program = gl.createProgram();
        gl.attachShader(program, vertex);
        gl.attachShader(program, fragment);
        gl.linkProgram(program);
        gl.getProgramiv(program, GL_LINK_STATUS, &linked);
    }
    gl.deleteShader(vertex); gl.deleteShader(fragment);
    if (!linked) { if (program) gl.deleteProgram(program); return false; }
    texture.program = program;
    texture.sizeLocation = gl.getUniformLocation(program, "size");
    texture.bitmapLocation = gl.getUniformLocation(program, "bitmap");
    gl.genVertexArrays(1, &texture.vao);
    return texture.vao && texture.sizeLocation >= 0 && texture.bitmapLocation >= 0;
}

void DrawGlCore(HGLRC current, const OverlaySection::Bitmap& bitmap, uint32_t width, uint32_t height)
{
    std::lock_guard<std::mutex> guard(glMutex);
    auto& texture = glTextures[current];
    if (texture.name && !glIsTexture(texture.name)) texture = {};
    if (!PrepareGlCore(texture)) return;
    auto& gl = texture.core;
    GLint oldProgram = 0, oldVao = 0, oldFramebuffer = 0, oldActive = 0;
    GLint oldTexture = 0, oldUnpack = 0, oldRow = 0, oldPixelBuffer = 0;
    GLint oldViewport[4]{}, oldPolygon[2]{};
    GLint oldBlend[6]{};
    GLboolean oldColorMask[4]{};
    glGetIntegerv(GL_CURRENT_PROGRAM, &oldProgram);
    glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &oldVao);
    glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &oldFramebuffer);
    glGetIntegerv(GL_ACTIVE_TEXTURE, &oldActive);
    gl.activeTexture(GL_TEXTURE0);
    glGetIntegerv(GL_TEXTURE_BINDING_2D, &oldTexture);
    glGetIntegerv(GL_UNPACK_ALIGNMENT, &oldUnpack);
    glGetIntegerv(GL_UNPACK_ROW_LENGTH, &oldRow);
    glGetIntegerv(GL_PIXEL_UNPACK_BUFFER_BINDING, &oldPixelBuffer);
    glGetIntegerv(GL_VIEWPORT, oldViewport);
    glGetIntegerv(GL_POLYGON_MODE, oldPolygon);
    glGetIntegerv(GL_BLEND_SRC_RGB, &oldBlend[0]);
    glGetIntegerv(GL_BLEND_DST_RGB, &oldBlend[1]);
    glGetIntegerv(GL_BLEND_SRC_ALPHA, &oldBlend[2]);
    glGetIntegerv(GL_BLEND_DST_ALPHA, &oldBlend[3]);
    glGetIntegerv(GL_BLEND_EQUATION_RGB, &oldBlend[4]);
    glGetIntegerv(GL_BLEND_EQUATION_ALPHA, &oldBlend[5]);
    glGetBooleanv(GL_COLOR_WRITEMASK, oldColorMask);
    const GLboolean wasBlend = glIsEnabled(GL_BLEND), wasDepth = glIsEnabled(GL_DEPTH_TEST);
    const GLboolean wasStencil = glIsEnabled(GL_STENCIL_TEST), wasScissor = glIsEnabled(GL_SCISSOR_TEST);
    const GLboolean wasCull = glIsEnabled(GL_CULL_FACE), wasDiscard = glIsEnabled(GL_RASTERIZER_DISCARD);
    const GLboolean wasAlphaCoverage = glIsEnabled(GL_SAMPLE_ALPHA_TO_COVERAGE);
    const GLboolean wasLogic = glIsEnabled(GL_COLOR_LOGIC_OP);
    gl.bindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
    if (!texture.name) glGenTextures(1, &texture.name);
    glBindTexture(GL_TEXTURE_2D, texture.name);
    if (texture.generation != bitmap.generation)
    {
        glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        glPixelStorei(GL_UNPACK_ROW_LENGTH, bitmap.stride / 4);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, bitmap.width, bitmap.height,
            0, GL_BGRA, GL_UNSIGNED_BYTE, bitmap.pixels.data());
        if (glGetError() == GL_NO_ERROR) texture.generation = bitmap.generation;
    }
    if (texture.generation == bitmap.generation)
    {
        gl.bindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
        glViewport(0, 0, width, height);
        glPolygonMode(GL_FRONT_AND_BACK, GL_FILL);
        glDisable(GL_DEPTH_TEST); glDisable(GL_STENCIL_TEST); glDisable(GL_SCISSOR_TEST);
        glDisable(GL_CULL_FACE); glDisable(GL_RASTERIZER_DISCARD);
        glDisable(GL_SAMPLE_ALPHA_TO_COVERAGE); glDisable(GL_COLOR_LOGIC_OP);
        glEnable(GL_BLEND);
        glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
        gl.blendFuncSeparate(GL_ONE, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        gl.blendEquationSeparate(GL_FUNC_ADD, GL_FUNC_ADD);
        gl.useProgram(texture.program);
        gl.uniform4f(texture.sizeLocation, bitmap.width, bitmap.height, width, height);
        gl.uniform1i(texture.bitmapLocation, 0);
        gl.bindVertexArray(texture.vao);
        glDrawArrays(GL_TRIANGLES, 0, 6);
        if (glGetError() == GL_NO_ERROR) section.Drew(bitmap.generation);
    }
    gl.bindVertexArray(oldVao);
    gl.useProgram(oldProgram);
    gl.bindFramebuffer(GL_DRAW_FRAMEBUFFER, oldFramebuffer);
    glBindTexture(GL_TEXTURE_2D, oldTexture);
    gl.activeTexture(oldActive);
    gl.bindBuffer(GL_PIXEL_UNPACK_BUFFER, oldPixelBuffer);
    glPixelStorei(GL_UNPACK_ALIGNMENT, oldUnpack);
    glPixelStorei(GL_UNPACK_ROW_LENGTH, oldRow);
    glViewport(oldViewport[0], oldViewport[1], oldViewport[2], oldViewport[3]);
    glPolygonMode(GL_FRONT_AND_BACK, oldPolygon[0]);
    gl.blendFuncSeparate(oldBlend[0], oldBlend[1], oldBlend[2], oldBlend[3]);
    gl.blendEquationSeparate(oldBlend[4], oldBlend[5]);
    glColorMask(oldColorMask[0], oldColorMask[1], oldColorMask[2], oldColorMask[3]);
    if (wasBlend) glEnable(GL_BLEND); else glDisable(GL_BLEND);
    if (wasDepth) glEnable(GL_DEPTH_TEST); else glDisable(GL_DEPTH_TEST);
    if (wasStencil) glEnable(GL_STENCIL_TEST); else glDisable(GL_STENCIL_TEST);
    if (wasScissor) glEnable(GL_SCISSOR_TEST); else glDisable(GL_SCISSOR_TEST);
    if (wasCull) glEnable(GL_CULL_FACE); else glDisable(GL_CULL_FACE);
    if (wasDiscard) glEnable(GL_RASTERIZER_DISCARD); else glDisable(GL_RASTERIZER_DISCARD);
    if (wasAlphaCoverage) glEnable(GL_SAMPLE_ALPHA_TO_COVERAGE); else glDisable(GL_SAMPLE_ALPHA_TO_COVERAGE);
    if (wasLogic) glEnable(GL_COLOR_LOGIC_OP); else glDisable(GL_COLOR_LOGIC_OP);
}

void DrawGl(HDC dc)
{
    HGLRC current = wglGetCurrentContext();
    if (!current || wglGetCurrentDC() != dc) return;
    HWND window = WindowFromDC(dc);
    RECT bounds{};
    if (!window || !GetClientRect(window, &bounds) || bounds.right <= 0 || bounds.bottom <= 0) return;
    auto bitmap = section.Frame(window, bounds.right, bounds.bottom);
    if (!bitmap) return;
    const char* version = reinterpret_cast<const char*>(glGetString(GL_VERSION));
    if (version && version[0] >= '3' && version[0] <= '9')
    {
        GLint profile = 0;
        glGetIntegerv(GL_CONTEXT_PROFILE_MASK, &profile);
        if (profile & GL_CONTEXT_CORE_PROFILE_BIT)
        {
            DrawGlCore(current, *bitmap, bounds.right, bounds.bottom);
            return;
        }
    }
    std::lock_guard<std::mutex> guard(glMutex);
    auto& texture = glTextures[current];
    if (texture.name && !glIsTexture(texture.name)) texture = {};
    glPushAttrib(GL_ALL_ATTRIB_BITS);
    glPushClientAttrib(GL_CLIENT_ALL_ATTRIB_BITS);
    GLint oldMatrix = GL_MODELVIEW;
    glGetIntegerv(GL_MATRIX_MODE, &oldMatrix);
    if (!texture.name) glGenTextures(1, &texture.name);
    glBindTexture(GL_TEXTURE_2D, texture.name);
    if (texture.generation != bitmap->generation)
    {
        std::vector<uint8_t> packed;
        const uint8_t* pixels = bitmap->pixels.data();
        if (bitmap->stride != bitmap->width * 4)
        {
            packed.resize(size_t(bitmap->width) * bitmap->height * 4);
            for (uint32_t row = 0; row < bitmap->height; ++row)
                std::memcpy(packed.data() + size_t(row) * bitmap->width * 4,
                    pixels + size_t(row) * bitmap->stride, size_t(bitmap->width) * 4);
            pixels = packed.data();
        }
        glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, bitmap->width, bitmap->height,
            0, GL_BGRA, GL_UNSIGNED_BYTE, pixels);
        if (glGetError() == GL_NO_ERROR) texture.generation = bitmap->generation;
    }
    if (texture.generation == bitmap->generation)
    {
        glViewport(0, 0, bounds.right, bounds.bottom);
        glDisable(GL_DEPTH_TEST); glDisable(GL_STENCIL_TEST); glDisable(GL_SCISSOR_TEST);
        glDisable(GL_CULL_FACE); glDisable(GL_LIGHTING);
        glEnable(GL_BLEND); glEnable(GL_TEXTURE_2D);
        glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        glMatrixMode(GL_PROJECTION); glPushMatrix(); glLoadIdentity();
        glOrtho(0, bounds.right, 0, bounds.bottom, -1, 1);
        glMatrixMode(GL_MODELVIEW); glPushMatrix(); glLoadIdentity();
        glMatrixMode(GL_TEXTURE); glPushMatrix(); glLoadIdentity();
        glMatrixMode(GL_MODELVIEW);
        glColor4f(1, 1, 1, 1);
        const GLfloat top = GLfloat(bounds.bottom);
        const GLfloat left = GLfloat(bitmap->width);
        const GLfloat bottom = top - GLfloat(bitmap->height);
        glBegin(GL_QUADS);
        glTexCoord2f(0, 0); glVertex2f(0, top);
        glTexCoord2f(1, 0); glVertex2f(left, top);
        glTexCoord2f(1, 1); glVertex2f(left, bottom);
        glTexCoord2f(0, 1); glVertex2f(0, bottom);
        glEnd();
        const bool success = glGetError() == GL_NO_ERROR;
        glMatrixMode(GL_TEXTURE); glPopMatrix();
        glMatrixMode(GL_MODELVIEW); glPopMatrix();
        glMatrixMode(GL_PROJECTION); glPopMatrix();
        if (success) section.Drew(bitmap->generation);
    }
    glMatrixMode(oldMatrix);
    glPopClientAttrib();
    glPopAttrib();
}

void TrackD3D12Chain(IUnknown* source, IDXGISwapChain* chain)
{
    if (!source || !chain) return;
    ID3D12CommandQueue* queue = nullptr;
    if (FAILED(source->QueryInterface(__uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&queue)))) return;
    auto resources = std::make_unique<D3D12Chain>();
    resources->queue = queue;
    std::lock_guard<std::mutex> guard(d3dMutex);
    d3d12Chains[chain] = std::move(resources);
}

HRESULT STDMETHODCALLTYPE HookCreateSwapChain(IDXGIFactory* factory, IUnknown* source,
    DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** output)
{
    const HRESULT result = originalCreateSwapChain(factory, source, desc, output);
    if (SUCCEEDED(result) && output) TrackD3D12Chain(source, *output);
    return result;
}
HRESULT STDMETHODCALLTYPE HookCreateForHwnd(IDXGIFactory2* factory, IUnknown* source, HWND window,
    const DXGI_SWAP_CHAIN_DESC1* desc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreen,
    IDXGIOutput* output, IDXGISwapChain1** chain)
{
    const HRESULT result = originalCreateForHwnd(factory, source, window, desc, fullscreen, output, chain);
    if (SUCCEEDED(result) && chain) TrackD3D12Chain(source, *chain);
    return result;
}
HRESULT STDMETHODCALLTYPE HookCreateForCore(IDXGIFactory2* factory, IUnknown* source, IUnknown* window,
    const DXGI_SWAP_CHAIN_DESC1* desc, IDXGIOutput* output, IDXGISwapChain1** chain)
{
    const HRESULT result = originalCreateForCore(factory, source, window, desc, output, chain);
    if (SUCCEEDED(result) && chain) TrackD3D12Chain(source, *chain);
    return result;
}
HRESULT STDMETHODCALLTYPE HookCreateForComposition(IDXGIFactory2* factory, IUnknown* source,
    const DXGI_SWAP_CHAIN_DESC1* desc, IDXGIOutput* output, IDXGISwapChain1** chain)
{
    const HRESULT result = originalCreateForComposition(factory, source, desc, output, chain);
    if (SUCCEEDED(result) && chain) TrackD3D12Chain(source, *chain);
    return result;
}

HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* chain, UINT interval, UINT flags)
{
    if (!(flags & DXGI_PRESENT_TEST)) { try { DrawD3D(chain); } catch (...) {} }
    return originalPresent(chain, interval, flags);
}
HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* chain, UINT interval, UINT flags,
    const DXGI_PRESENT_PARAMETERS* parameters)
{
    if (!(flags & DXGI_PRESENT_TEST)) { try { DrawD3D(chain); } catch (...) {} }
    return originalPresent1(chain, interval, flags, parameters);
}
HRESULT STDMETHODCALLTYPE HookResize(IDXGISwapChain* chain, UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags)
{
    {
        std::lock_guard<std::mutex> guard(d3dMutex);
        const auto found = d3d12Chains.find(chain);
        if (found != d3d12Chains.end()) found->second->Clear();
    }
    return originalResize(chain, count, width, height, format, flags);
}
HRESULT STDMETHODCALLTYPE HookResize1(IDXGISwapChain3* chain, UINT count, UINT width, UINT height,
    DXGI_FORMAT format, UINT flags, const UINT* masks, IUnknown* const* queues)
{
    {
        std::lock_guard<std::mutex> guard(d3dMutex);
        const auto found = d3d12Chains.find(chain);
        if (found != d3d12Chains.end()) found->second->Clear();
    }
    return originalResize1(chain, count, width, height, format, flags, masks, queues);
}
BOOL WINAPI HookSwap(HDC dc)
{
    try { DrawGl(dc); } catch (...) {}
    return originalSwap(dc);
}
BOOL WINAPI HookDeleteContext(HGLRC context)
{
    {
        std::lock_guard<std::mutex> guard(glMutex);
        glTextures.erase(context);
    }
    return originalDeleteContext(context);
}

bool Hook(void* target, void* replacement, void** original)
{
    if (*original) return true;
    return target && MH_CreateHook(target, replacement, original) == MH_OK;
}

// DXGI can replace the code at Present's entry after the first real presentation.
// Patch the COM dispatch slot instead, leaving DXGI's mutable entry code alone.
bool PatchMethod(void** methods, size_t slot, void* replacement, void** original)
{
    if (!methods || !methods[slot]) return false;
    DWORD previous = 0;
    if (!VirtualProtect(methods + slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &previous)) return false;
    void* before = InterlockedExchangePointer(reinterpret_cast<void* volatile*>(methods + slot), replacement);
    DWORD ignored = 0;
    VirtualProtect(methods + slot, sizeof(void*), previous, &ignored);
    if (before != replacement) *original = before;
    return *original != nullptr;
}

bool Install()
{
    std::lock_guard<std::mutex> guard(installMutex);
    if (installed) return true;
    const MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) return false;
    const HMODULE gdi = GetModuleHandleW(L"gdi32.dll");
    const HMODULE gl = GetModuleHandleW(L"opengl32.dll");
    if (!gdi || !gl || !Hook(reinterpret_cast<void*>(GetProcAddress(gdi, "SwapBuffers")),
        reinterpret_cast<void*>(&HookSwap), reinterpret_cast<void**>(&originalSwap))) return false;
    if (!Hook(reinterpret_cast<void*>(GetProcAddress(gl, "wglDeleteContext")),
        reinterpret_cast<void*>(&HookDeleteContext), reinterpret_cast<void**>(&originalDeleteContext))) return false;

    HWND window = CreateWindowExW(WS_EX_NOACTIVATE, L"STATIC", L"", WS_POPUP,
        0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) return false;
    DXGI_SWAP_CHAIN_DESC desc{};
    desc.BufferDesc.Width = desc.BufferDesc.Height = 1;
    desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.OutputWindow = window;
    desc.Windowed = TRUE;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain* chain = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    HRESULT result = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
    if (FAILED(result)) result = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
    bool ready = SUCCEEDED(result) && chain;
    if (ready)
    {
        IDXGISwapChain3* chain3 = nullptr;
        ready = SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3)));
        if (ready)
        {
            void** methods = *reinterpret_cast<void***>(chain3);
            ready = PatchMethod(methods, 8, reinterpret_cast<void*>(&HookPresent), reinterpret_cast<void**>(&originalPresent))
                && PatchMethod(methods, 13, reinterpret_cast<void*>(&HookResize), reinterpret_cast<void**>(&originalResize))
                && PatchMethod(methods, 22, reinterpret_cast<void*>(&HookPresent1), reinterpret_cast<void**>(&originalPresent1))
                && PatchMethod(methods, 39, reinterpret_cast<void*>(&HookResize1), reinterpret_cast<void**>(&originalResize1));
        }
        Drop(chain3);
    }
    Drop(chain); Drop(context); Drop(device);
    DestroyWindow(window);
    IDXGIFactory4* factory = nullptr;
    if (ready && SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory))))
    {
        void** methods = *reinterpret_cast<void***>(factory);
        ready = PatchMethod(methods, 10, reinterpret_cast<void*>(&HookCreateSwapChain),
                    reinterpret_cast<void**>(&originalCreateSwapChain))
            && PatchMethod(methods, 15, reinterpret_cast<void*>(&HookCreateForHwnd),
                    reinterpret_cast<void**>(&originalCreateForHwnd))
            && PatchMethod(methods, 16, reinterpret_cast<void*>(&HookCreateForCore),
                    reinterpret_cast<void**>(&originalCreateForCore))
            && PatchMethod(methods, 24, reinterpret_cast<void*>(&HookCreateForComposition),
                    reinterpret_cast<void**>(&originalCreateForComposition));
    }
    Drop(factory);
    if (!ready || MH_EnableHook(MH_ALL_HOOKS) != MH_OK) return false;
    installed = true;
    return true;
}

DWORD WINAPI InstallWorker(void*) { Install(); return 0; }
}

extern "C" __declspec(dllexport) BOOL WINAPI ResourceManagerPerformanceOverlayInitialize()
{
    return Install() ? TRUE : FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        HANDLE worker = CreateThread(nullptr, 0, InstallWorker, nullptr, 0, nullptr);
        if (worker) CloseHandle(worker);
    }
    return TRUE;
}
