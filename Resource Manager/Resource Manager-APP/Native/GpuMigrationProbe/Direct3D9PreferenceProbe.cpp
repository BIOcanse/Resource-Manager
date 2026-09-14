#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <d3d9.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include "../GpuPlacementShim/Direct3D9AdapterSelection.h"
#include "../GpuPlacementShim/Direct3D9HybridEnumeration.h"

using Microsoft::WRL::ComPtr;
namespace {
constexpr UINT Side = 32;
constexpr DWORD Flags = D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE;
constexpr wchar_t PreferenceKey[] = L"Software\\Microsoft\\DirectX\\UserGpuPreferences";
std::string role = "controller";
unsigned drawCount = 0;

void Check(bool value, const char* operation) { if (!value) throw std::runtime_error(operation); }
void Hr(HRESULT result, const char* operation) {
    if (FAILED(result)) { std::fprintf(stderr, "%s: 0x%08lx\n", operation, static_cast<unsigned long>(result)); throw std::runtime_error(operation); }
}
std::string Utf8(const std::wstring& value) {
    int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    Check(count > 0 || value.empty(), "utf8-length");
    std::string result(count, '\0');
    Check(WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), count, nullptr, nullptr) == count, "utf8-convert");
    return result;
}
std::string J(const std::string& text) {
    std::string result = "\"";
    for (unsigned char ch : text) {
        if (ch == '\\' || ch == '"') result += '\\';
        if (ch < 32) { char out[7]{}; std::snprintf(out, sizeof(out), "\\u%04x", ch); result += out; }
        else result += static_cast<char>(ch);
    }
    return result + '"';
}
std::string Hex(uint64_t value) { char out[17]{}; std::snprintf(out, sizeof(out), "%016llx", static_cast<unsigned long long>(value)); return out; }
uint64_t Luid(LUID value) { return (static_cast<uint64_t>(static_cast<uint32_t>(value.HighPart)) << 32) | value.LowPart; }
std::string IdentityFields(HRESULT result, LUID value) {
    Check(result == S_OK || result == D3DERR_NOTAVAILABLE, "expected-identity-result");
    return "\"luid\":" + (result == S_OK ? J(Hex(Luid(value))) : "null")
        + ",\"luidHresult\":" + std::to_string(static_cast<int32_t>(result));
}
uint64_t Birth(HANDLE process) {
    FILETIME created{}, exited{}, kernel{}, user{};
    Check(GetProcessTimes(process, &created, &exited, &kernel, &user) != FALSE, "native-process-times");
    return (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}
void Emit(const std::string& fields) {
    const auto line = "{\"role\":" + J(role) + ",\"pid\":" + std::to_string(GetCurrentProcessId()) + "," + fields + "}\n";
    DWORD written = 0;
    Check(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), line.data(), static_cast<DWORD>(line.size()), &written, nullptr) && written == line.size(), "write-json-line");
}
std::wstring ModulePath() {
    wchar_t path[32768]{};
    DWORD count = GetModuleFileNameW(nullptr, path, 32768);
    Check(count > 0 && count < 32768, "module-path");
    return std::wstring(path, count);
}
UINT ApplyHybridMode(UINT mode, const char* phase) {
    Check(mode == 0 || mode == 1 || mode == 3 || mode == 4, "inspected-hybrid-mode");
    HMODULE module = GetModuleHandleW(L"d3d9.dll"); Check(module != nullptr, "loaded-d3d9");
    wchar_t system[32768]{}, path[32768]{};
    UINT count = GetSystemDirectoryW(system, 32768); Check(count > 0 && count < 32768, "system-directory");
    DWORD length = GetModuleFileNameW(module, path, 32768); Check(length > 0 && length < 32768, "d3d9-path");
    const auto expected = std::wstring(system, count) + L"\\d3d9.dll";
    Check(_wcsicmp(path, expected.c_str()) == 0, "system-d3d9-only");
    auto base = reinterpret_cast<const unsigned char*>(module);
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    Check(dos->e_magic == IMAGE_DOS_SIGNATURE && dos->e_lfanew > 0 && dos->e_lfanew < 4096, "d3d9-dos-header");
    const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    Check(nt->Signature == IMAGE_NT_SIGNATURE && nt->FileHeader.Machine == IMAGE_FILE_MACHINE_AMD64
        && nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC && nt->OptionalHeader.SizeOfImage > 0x19b088, "inspected-x64-image");
    const auto address = GetProcAddress(module, MAKEINTRESOURCEA(16));
    Check(address && reinterpret_cast<const unsigned char*>(address) == base + 0xb5130, "inspected-entry-rva");
    const unsigned char instructions[]{0x89, 0x0d, 0x4e, 0x5f, 0x0e, 0x00, 0xc3};
    Check(std::memcmp(base + 0xb5130, instructions, sizeof(instructions)) == 0, "complete-entry-bytes");
    MEMORY_BASIC_INFORMATION code{}, data{};
    Check(VirtualQuery(base + 0xb5130, &code, sizeof(code)) == sizeof(code)
        && code.AllocationBase == module && code.Type == MEM_IMAGE && code.State == MEM_COMMIT
        && code.Protect == PAGE_EXECUTE_READ, "entry-image-execute-read");
    Check(VirtualQuery(base + 0x19b084, &data, sizeof(data)) == sizeof(data)
        && data.AllocationBase == module && data.Type == MEM_IMAGE && data.State == MEM_COMMIT
        && (data.Protect == PAGE_READWRITE || data.Protect == PAGE_WRITECOPY), "entry-target-image-data");
    const auto value = reinterpret_cast<const volatile UINT*>(base + 0x19b084);
    const UINT before = *value;
    Check(before == 0 || before == 1 || before == 3 || before == 4, "known-original-hybrid-mode-before-write");
    void (WINAPI *call)(UINT) = nullptr;
    static_assert(sizeof(call) == sizeof(address)); std::memcpy(&call, &address, sizeof(call));
    call(mode);
    Check(*value == mode, "entry-wrote-requested-mode");
    Emit("\"event\":\"hybrid-entry\",\"phase\":" + J(phase) + ",\"mode\":" + std::to_string(mode)
        + ",\"before\":" + std::to_string(before) + ",\"after\":" + std::to_string(*value)
        + ",\"entryRva\":741680,\"targetRva\":1683588,\"codeProtect\":" + std::to_string(code.Protect)
        + ",\"dataProtectBefore\":" + std::to_string(data.Protect) + ",\"module\":" + J(Utf8(path)) + ",\"bytes\":\"890d4e5f0e00c3\"");
    return before;
}
class Handle {
    HANDLE value_ = nullptr;
public:
    Handle() = default;
    explicit Handle(HANDLE value) : value_(value) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
    HANDLE Get() const { return value_; }
    HANDLE* Out() { Check(!value_, "handle-already-held"); return &value_; }
};
D3DPRESENT_PARAMETERS Parameters(HWND window) {
    D3DPRESENT_PARAMETERS p{};
    p.BackBufferWidth = Side; p.BackBufferHeight = Side; p.BackBufferFormat = D3DFMT_A8R8G8B8;
    p.BackBufferCount = 1; p.SwapEffect = D3DSWAPEFFECT_DISCARD; p.hDeviceWindow = window;
    p.Windowed = TRUE; p.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    return p;
}
struct Factory {
    bool extended;
    ComPtr<IDirect3D9> api;
    ComPtr<IDirect3D9Ex> ex;
    ComPtr<IDirect3DDevice9> device;
    explicit Factory(bool isEx) : extended(isEx) { Open(); }
    const char* Name() const { return extended ? "D3D9Ex" : "D3D9"; }
    void Open() {
        device.Reset(); api.Reset(); ex.Reset();
        if (extended) { Hr(Direct3DCreate9Ex(D3D_SDK_VERSION, &ex), "create9ex"); api = ex; }
        else { api.Attach(Direct3DCreate9(D3D_SDK_VERSION)); Check(api.Get() != nullptr, "create9"); }
    }
    void Enumerate(const std::string& phase) {
        ResourceManagerD3D9::AdapterIdentities ids(api.Get());
        Hr(ids.Open(Direct3DCreate9Ex), "factory-identities");
        UINT count = api->GetAdapterCount();
        Check(count > 0 && count <= 64, "bounded-adapters");
        for (UINT index = 0; index < count; ++index) {
            D3DADAPTER_IDENTIFIER9 info{}; LUID id{};
            Hr(api->GetAdapterIdentifier(index, 0, &info), "adapter-description"); const HRESULT identified = ids.Luid(index, id);
            Emit("\"event\":\"adapter\",\"phase\":" + J(phase) + ",\"api\":" + J(Name()) + ",\"count\":" + std::to_string(count)
                + ",\"ordinal\":" + std::to_string(index) + "," + IdentityFields(identified, id) + ",\"vendor\":" + std::to_string(info.VendorId)
                + ",\"description\":" + J(info.Description) + ",\"gdiDevice\":" + J(info.DeviceName));
        }
    }
    void Create(HWND window) {
        device.Reset(); auto p = Parameters(window);
        if (extended) { ComPtr<IDirect3DDevice9Ex> d; Hr(ex->CreateDeviceEx(0, D3DDEVTYPE_HAL, window, Flags, &p, nullptr, &d), "create-device-ex"); device = d; }
        else Hr(api->CreateDevice(0, D3DDEVTYPE_HAL, window, Flags, &p, &device), "create-device");
    }
    void Reset(HWND window, const std::string& phase) {
        auto p = Parameters(window); HRESULT result;
        if (extended) { ComPtr<IDirect3DDevice9Ex> d; Hr(device.As(&d), "device-as-ex"); result = d->ResetEx(&p, nullptr); }
        else result = device->Reset(&p);
        Emit("\"event\":\"reset\",\"phase\":" + J(phase) + ",\"api\":" + J(Name()) + ",\"hresult\":" + std::to_string(static_cast<int32_t>(result)));
        Hr(result, "reset-device");
    }
    void AssertOldFactoryRejectsNewTarget() {
        ComPtr<IDirect3D9Ex> fresh; Hr(Direct3DCreate9Ex(D3D_SDK_VERSION, &fresh), "fresh-target-factory");
        D3DADAPTER_IDENTIFIER9 oldInfo{}, newInfo{};
        Hr(api->GetAdapterIdentifier(0, 0, &oldInfo), "old-hardware-info");
        Hr(fresh->GetAdapterIdentifier(0, 0, &newInfo), "new-hardware-info");
        Check(oldInfo.VendorId == 0x10de && newInfo.VendorId == 0x1002, "real-hybrid-hardware-split");
        Check(std::strcmp(oldInfo.DeviceName, newInfo.DeviceName) == 0, "real-same-gdi-different-hardware");
        ResourceManagerGpuPolicy::GpuShimPolicy policy;
        policy.mode = ResourceManagerGpuPolicy::GpuShimPolicyMode::TargetLuid;
        Hr(fresh->GetAdapterLUID(0, &policy.targetLuid), "new-target-luid");
        UINT selected = 99;
        const HRESULT result = ResourceManagerD3D9::SelectAdapterOrdinal(api.Get(), 0, D3DDEVTYPE_HAL, Flags, policy, Direct3DCreate9Ex, selected);
        Emit("\"event\":\"old-factory-target-rejection\",\"api\":" + J(Name()) + ",\"hresult\":" + std::to_string(static_cast<int32_t>(result))
            + ",\"selected\":" + std::to_string(selected) + ",\"oldVendor\":" + std::to_string(oldInfo.VendorId)
            + ",\"newVendor\":" + std::to_string(newInfo.VendorId) + ",\"sameGdiName\":true");
        Check(result == D3DERR_NOTAVAILABLE && selected == 0, "old-factory-must-not-alias-new-target");
    }
    void Render(const std::string& phase, bool save, bool requireNvidia) {
        D3DDEVICE_CREATION_PARAMETERS p{}; Hr(device->GetCreationParameters(&p), "creation-parameters");
        ComPtr<IDirect3D9> parent; Hr(device->GetDirect3D(&parent), "device-parent");
        D3DADAPTER_IDENTIFIER9 info{}; Hr(parent->GetAdapterIdentifier(p.AdapterOrdinal, 0, &info), "actual-adapter");
        if (requireNvidia) Check(info.VendorId == 0x10de, "hot-start-must-really-be-nvidia");
        ComPtr<IDirect3DSurface9> original, target, readback;
        Hr(device->GetRenderTarget(0, &original), "original-target");
        Hr(device->CreateRenderTarget(Side, Side, D3DFMT_A8R8G8B8, D3DMULTISAMPLE_NONE, 0, FALSE, &target, nullptr), "render-target");
        Hr(device->CreateOffscreenPlainSurface(Side, Side, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, nullptr), "readback-surface");
        Hr(device->SetRenderTarget(0, target.Get()), "set-target");
        D3DVIEWPORT9 viewport{0, 0, Side, Side, 0, 1}; Hr(device->SetViewport(&viewport), "viewport");
        Hr(device->SetRenderState(D3DRS_LIGHTING, FALSE), "no-lighting");
        Hr(device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE), "no-cull");
        Hr(device->SetRenderState(D3DRS_ZENABLE, FALSE), "no-depth");
        Hr(device->SetRenderState(D3DRS_DITHERENABLE, FALSE), "no-dither");
        Hr(device->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1), "diffuse-color");
        Hr(device->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_DIFFUSE), "diffuse-arg");
        Hr(device->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1), "diffuse-alpha");
        Hr(device->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_DIFFUSE), "diffuse-alpha-arg");
        const unsigned char red = static_cast<unsigned char>(32 + (++drawCount % 160));
        struct Vertex { float x, y, z, rhw; DWORD color; };
        DWORD color = D3DCOLOR_ARGB(255, red, 128, 191);
        Vertex vertices[]{{-.5f, -.5f, .5f, 1, color}, {Side * 2.f, -.5f, .5f, 1, color}, {-.5f, Side * 2.f, .5f, 1, color}};
        Hr(device->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1, 0), "clear-target");
        Hr(device->SetFVF(D3DFVF_XYZRHW | D3DFVF_DIFFUSE), "set-fvf");
        Hr(device->BeginScene(), "begin-scene"); Hr(device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, vertices, sizeof(Vertex)), "draw-triangle"); Hr(device->EndScene(), "end-scene");
        Hr(device->GetRenderTargetData(target.Get(), readback.Get()), "gpu-readback");
        D3DLOCKED_RECT locked{}; Hr(readback->LockRect(&locked, nullptr, D3DLOCK_READONLY), "lock-pixels");
        std::vector<unsigned char> pixels(Side * Side * 4);
        bool valid = locked.pBits && locked.Pitch >= static_cast<int>(Side * 4);
        if (valid) for (UINT y = 0; y < Side; ++y) std::memcpy(pixels.data() + y * Side * 4, static_cast<unsigned char*>(locked.pBits) + y * locked.Pitch, Side * 4);
        Hr(readback->UnlockRect(), "unlock-pixels"); Hr(device->SetRenderTarget(0, original.Get()), "restore-target");
        for (size_t i = 0; i < pixels.size(); i += 4) valid &= pixels[i] == 191 && pixels[i+1] == 128 && pixels[i+2] == red && pixels[i+3] == 255;
        Check(valid, "all-triangle-pixels-match");
        if (save) {
            ResourceManagerD3D9::AdapterIdentities ids(parent.Get()); Hr(ids.Open(Direct3DCreate9Ex), "device-identities");
            LUID id{}; const HRESULT identified = ids.Luid(p.AdapterOrdinal, id);
            const std::string suffix = "." + phase + "." + Name() + ".bgra";
            std::wstring path = ModulePath() + L"." + std::to_wstring(GetCurrentProcessId()) + std::wstring(suffix.begin(), suffix.end());
            Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
            Check(file.Get() != INVALID_HANDLE_VALUE, "new-pixel-file"); DWORD written = 0;
            Check(WriteFile(file.Get(), pixels.data(), static_cast<DWORD>(pixels.size()), &written, nullptr) && written == pixels.size(), "save-pixels");
            Emit("\"event\":\"device\",\"phase\":" + J(phase) + ",\"api\":" + J(Name()) + "," + IdentityFields(identified, id)
                + ",\"vendor\":" + std::to_string(info.VendorId) + ",\"ordinal\":" + std::to_string(p.AdapterOrdinal) + ",\"deviceType\":" + std::to_string(p.DeviceType)
                + ",\"drawCount\":" + std::to_string(drawCount) + ",\"pixelCount\":1024,\"red\":" + std::to_string(red) + ",\"pixels\":" + J(Utf8(path)));
        }
    }
};

void WorkerMain(HANDLE done, bool requireNvidia, UINT hybridMode) {
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"RM D3D9 Preference Probe", WS_POPUP, 0, 0, Side, Side, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Check(window != nullptr, "own-hidden-window");
    {
        Factory classic(false), extended(true);
        classic.Create(window); extended.Create(window);
        for (auto* f : {&classic, &extended}) { f->Enumerate("initial"); f->Render("initial", true, requireNvidia); }
        Emit("\"event\":\"ready\",\"hwnd\":" + std::to_string(reinterpret_cast<uintptr_t>(window)));
        Check(SetEvent(done), "ready-event");
        std::string command; auto started = GetTickCount64(); auto nextDraw = started;
        bool quitting = false;
        while (!quitting && GetTickCount64() - started < 70000) {
            MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
            DWORD available = 0; Check(PeekNamedPipe(GetStdHandle(STD_INPUT_HANDLE), nullptr, 0, nullptr, &available, nullptr), "peek-command");
            if (available) {
                char ch = 0; DWORD read = 0; Check(ReadFile(GetStdHandle(STD_INPUT_HANDLE), &ch, 1, &read, nullptr) && read == 1, "read-command");
                if (ch != '\n') { command += ch; Check(command.size() < 64, "command-bound"); continue; }
                if (command == "quit") quitting = true;
                else {
                    Check(command == "hold" || command == "changed" || command == "hybrid" || command == "reset" || command == "recreate-old" || command == "recreate-new", "known-command");
                    if (command == "hybrid") ApplyHybridMode(hybridMode, "while-rendering");
                    for (auto* f : {&classic, &extended}) {
                        if (command == "hybrid") f->AssertOldFactoryRejectsNewTarget();
                        if (command == "reset") f->Reset(window, command);
                        if (command == "recreate-old") f->Create(window);
                        if (command == "recreate-new") { f->Open(); f->Create(window); }
                        f->Enumerate(command + "-old-factory"); f->Render(command, true, false);
                        if (command == "changed" || command == "hybrid") { Factory fresh(f->extended); fresh.Enumerate(command + "-new-factory"); }
                    }
                }
                Emit("\"event\":\"command-complete\",\"phase\":" + J(command));
                Check(SetEvent(done), "completion-event"); command.clear();
            }
            if (!quitting && GetTickCount64() >= nextDraw) { classic.Render("steady", false, false); extended.Render("steady", false, false); nextDraw = GetTickCount64() + 250; }
            Sleep(2);
        }
        Check(quitting, "worker-wall-bound");
    }
    Check(!IsWindowVisible(window) && GetForegroundWindow() != window, "window-remained-hidden");
    Check(DestroyWindow(window) && !IsWindow(window), "window-destroyed");
    Emit("\"event\":\"worker-exit\",\"passed\":true,\"drawCount\":" + std::to_string(drawCount) + ",\"hwndDestroyed\":true");
}

class Worker {
    Handle process_, commands_, done_;
    DWORD pid_ = 0;
    bool stopped_ = false;
public:
    Worker(const std::wstring& exe, const wchar_t* name, bool requireNvidia, UINT hybridMode = 0, bool coldHybrid = false) {
        SECURITY_ATTRIBUTES sa{sizeof(sa), nullptr, TRUE}; Handle input;
        Check(CreatePipe(input.Out(), commands_.Out(), &sa, 0), "command-pipe");
        Check(SetHandleInformation(commands_.Get(), HANDLE_FLAG_INHERIT, 0), "parent-command-handle");
        *done_.Out() = CreateEventW(&sa, FALSE, FALSE, nullptr); Check(done_.Get() != nullptr, "completed-event");
        Handle out, error;
        Check(DuplicateHandle(GetCurrentProcess(), GetStdHandle(STD_OUTPUT_HANDLE), GetCurrentProcess(), out.Out(), 0, TRUE, DUPLICATE_SAME_ACCESS), "inherit-output");
        Check(DuplicateHandle(GetCurrentProcess(), GetStdHandle(STD_ERROR_HANDLE), GetCurrentProcess(), error.Out(), 0, TRUE, DUPLICATE_SAME_ACCESS), "inherit-error");
        HANDLE allowed[]{input.Get(), done_.Get(), out.Get(), error.Get()};
        SIZE_T bytes = 0; InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes); std::vector<unsigned char> storage(bytes);
        auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
        Check(InitializeProcThreadAttributeList(attributes, 1, 0, &bytes), "attribute-list");
        Check(UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, allowed, sizeof(allowed), nullptr, nullptr), "explicit-inherited-handles");
        STARTUPINFOEXW start{}; start.StartupInfo.cb = sizeof(start); start.StartupInfo.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
        start.StartupInfo.wShowWindow = SW_HIDE; start.StartupInfo.hStdInput = input.Get(); start.StartupInfo.hStdOutput = out.Get(); start.StartupInfo.hStdError = error.Get(); start.lpAttributeList = attributes;
        auto command = L"\"" + exe + L"\" --worker " + name + L" " + std::to_wstring(reinterpret_cast<uintptr_t>(done_.Get())) + (requireNvidia ? L" 1 " : L" 0 ")
            + std::to_wstring(hybridMode) + (coldHybrid ? L" 1" : L" 0");
        PROCESS_INFORMATION pi{};
        const BOOL created = CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &start.StartupInfo, &pi);
        DeleteProcThreadAttributeList(attributes); Check(created, "create-worker");
        *process_.Out() = pi.hProcess; Handle thread(pi.hThread); pid_ = pi.dwProcessId;
        BOOL inJob = FALSE; Check(IsProcessInJob(process_.Get(), nullptr, &inJob) && inJob, "child-in-owned-job");
        Emit("\"event\":\"child-start\",\"childPid\":" + std::to_string(pid_) + ",\"creationFileTime\":" + std::to_string(Birth(process_.Get())) + ",\"childRole\":" + J(Utf8(name)) + ",\"path\":" + J(Utf8(exe)));
        Check(ResumeThread(thread.Get()) == 1, "resume-owned-worker");
    }
    ~Worker() {
        if (process_.Get() && !stopped_ && WaitForSingleObject(process_.Get(), 0) == WAIT_TIMEOUT) {
            TerminateProcess(process_.Get(), 99); WaitForSingleObject(process_.Get(), 1000);
            std::fprintf(stderr, "abnormal-owned-worker-termination: %lu\n", pid_);
        }
    }
    void Wait() {
        HANDLE waits[]{process_.Get(), done_.Get()};
        Check(WaitForMultipleObjects(2, waits, FALSE, 10000) == WAIT_OBJECT_0 + 1, "worker-command-completion");
    }
    void Send(const char* text) {
        std::string command = std::string(text) + '\n'; DWORD written = 0;
        Check(WriteFile(commands_.Get(), command.data(), static_cast<DWORD>(command.size()), &written, nullptr) && written == command.size(), "send-command");
        Wait();
    }
    void Stop() {
        const char command[] = "quit\n"; DWORD written = 0;
        Check(WriteFile(commands_.Get(), command, sizeof(command) - 1, &written, nullptr) && written == sizeof(command) - 1, "send-quit");
        Check(WaitForSingleObject(process_.Get(), 10000) == WAIT_OBJECT_0, "worker-normal-exit");
        DWORD exit = 0; Check(GetExitCodeProcess(process_.Get(), &exit), "worker-exit-code"); stopped_ = true;
        Emit("\"event\":\"child-exit\",\"childPid\":" + std::to_string(pid_) + ",\"creationFileTime\":" + std::to_string(Birth(process_.Get())) + ",\"exitCode\":" + std::to_string(exit));
        Check(exit == 0, "worker-zero-exit");
    }
};
void SetPreference(const std::wstring& path, unsigned value) {
    HKEY key = nullptr; Check(RegCreateKeyExW(HKEY_CURRENT_USER, PreferenceKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) == ERROR_SUCCESS, "preference-key");
    auto data = L"GpuPreference=" + std::to_wstring(value) + L";";
    auto result = RegSetValueExW(key, path.c_str(), 0, REG_SZ, reinterpret_cast<const BYTE*>(data.c_str()), static_cast<DWORD>((data.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key); Check(result == ERROR_SUCCESS, "write-owned-preference");
    Emit("\"event\":\"preference-written\",\"path\":" + J(Utf8(path)) + ",\"value\":" + J(Utf8(data)));
}
void ControlMain() {
    auto own = ModulePath(); auto directory = own.substr(0, own.find_last_of(L"\\/"));
    auto a = directory + L"\\D3D9Preference-A.exe", b = directory + L"\\D3D9Preference-B.exe";
    SetPreference(a, 2); SetPreference(b, 1);
    { Worker first(a, L"cold-A", false), second(b, L"cold-B", false); first.Wait(); second.Wait(); Sleep(750); first.Send("hold"); second.Send("hold"); first.Stop(); second.Stop(); }
    SetPreference(a, 2); SetPreference(b, 2);
    { Worker first(a, L"hot-A", true), second(b, L"hot-B", true); first.Wait(); second.Wait(); Sleep(750);
      first.Send("hold"); second.Send("hold"); SetPreference(b, 1);
      for (const char* command : {"changed", "reset", "recreate-old", "recreate-new"}) { first.Send(command); second.Send(command); }
      first.Stop(); second.Stop(); }
    { Worker restarted(b, L"restarted-B", false); restarted.Wait(); restarted.Stop(); }
    Emit("\"event\":\"experiment-complete\",\"completed\":true,\"injectionPerformed\":false,\"presentCalls\":0");
}
void HybridControlMain(bool cold, UINT mode) {
    auto own = ModulePath(); auto directory = own.substr(0, own.find_last_of(L"\\/"));
    auto a = directory + L"\\D3D9Preference-A.exe", b = directory + L"\\D3D9Preference-B.exe";
    SetPreference(a, 2); SetPreference(b, 2);
    if (cold) {
        { Worker baseline(a, L"hybrid-baseline", true); baseline.Wait(); baseline.Stop(); }
        for (UINT candidate : {1u, 3u, 4u}) {
            auto name = L"hybrid-cold-" + std::to_wstring(candidate);
            Worker worker(b, name.c_str(), false, candidate, true); worker.Wait(); worker.Send("hold"); worker.Stop();
        }
    } else {
        Worker baseline(a, L"hybrid-control-A", true), target(b, L"hybrid-hot-B", true, mode);
        baseline.Wait(); target.Wait(); Sleep(750); baseline.Send("hold"); target.Send("hold");
        baseline.Send("changed"); target.Send("hybrid");
        for (const char* command : {"reset", "recreate-old", "recreate-new"}) { baseline.Send(command); target.Send(command); }
        baseline.Stop(); target.Stop();
    }
    Emit("\"event\":\"experiment-complete\",\"completed\":true,\"injectionPerformed\":false,\"presentCalls\":0,\"hybrid\":true");
}

UINT DeviceVendor(Factory& factory) {
    D3DDEVICE_CREATION_PARAMETERS parameters{};
    Hr(factory.device->GetCreationParameters(&parameters), "roundtrip-creation-parameters");
    ComPtr<IDirect3D9> parent; Hr(factory.device->GetDirect3D(&parent), "roundtrip-device-parent");
    D3DADAPTER_IDENTIFIER9 identity{};
    Hr(parent->GetAdapterIdentifier(parameters.AdapterOrdinal, 0, &identity), "roundtrip-hardware");
    return identity.VendorId;
}

void HybridRoundTripMain() {
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"RM D3D9 Round Trip Probe",
        WS_POPUP, 0, 0, Side, Side, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Check(window != nullptr, "own-roundtrip-window");
    UINT original = 0;
    bool restorePending = false;
    try {
        Factory initial[]{Factory(false), Factory(true)};
        UINT initialVendors[2]{};
        for (size_t index = 0; index < 2; ++index) {
            initial[index].Create(window);
            initial[index].Enumerate("roundtrip-initial");
            initial[index].Render("roundtrip-initial", true, false);
            initialVendors[index] = DeviceVendor(initial[index]);
        }
        // Keep the actual previous factory/device alive across each mode change.
        std::unique_ptr<Factory> previous[2];
        UINT previousVendors[2]{};
        const UINT modes[]{4, 1, 3, 4};
        for (size_t step = 0; step < 4; ++step) {
            const std::string phase = "roundtrip-" + std::to_string(step) + "-mode" + std::to_string(modes[step]);
            const UINT before = ApplyHybridMode(modes[step], phase.c_str());
            if (step == 0) { original = before; restorePending = true; }
            for (size_t index = 0; index < 2; ++index) {
                auto& old = initial[index];
                old.Reset(window, phase + "-reset");
                old.Render(phase + "-reset", true, false);
                Check(DeviceVendor(old) == initialVendors[index], "reset-keeps-original-hardware");
                old.Create(window);
                old.Render(phase + "-old-factory", true, false);
                Check(DeviceVendor(old) == initialVendors[index], "old-factory-keeps-original-hardware");
                if (previous[index]) {
                    previous[index]->Render(phase + "-previous-device", true, false);
                    Check(DeviceVendor(*previous[index]) == previousVendors[index], "existing-device-keeps-hardware");
                }
                auto fresh = std::make_unique<Factory>(index == 1);
                fresh->Create(window);
                fresh->Enumerate(phase + "-new-factory");
                fresh->Render(phase + "-new-factory", true, false);
                previousVendors[index] = DeviceVendor(*fresh);
                previous[index] = std::move(fresh);
            }
        }
        ApplyHybridMode(original, "roundtrip-restore-original");
        restorePending = false;
        for (size_t index = 0; index < 2; ++index) {
            previous[index]->Render("roundtrip-after-restore-existing", true, false);
            Check(DeviceVendor(*previous[index]) == previousVendors[index], "restore-does-not-move-existing-device");
            Factory restored(index == 1); restored.Create(window);
            restored.Enumerate("roundtrip-restored-new-factory");
            restored.Render("roundtrip-restored-new-factory", true, false);
            Emit("\"event\":\"roundtrip-restoration\",\"api\":" + J(restored.Name())
                + ",\"originalMode\":" + std::to_string(original) + ",\"initialVendor\":" + std::to_string(initialVendors[index])
                + ",\"restoredVendor\":" + std::to_string(DeviceVendor(restored))
                + ",\"factoryChoiceRestored\":" + (DeviceVendor(restored) == initialVendors[index] ? "true" : "false"));
            Check(DeviceVendor(restored) == initialVendors[index], "new-factory-original-hardware-restored");
        }
    } catch (...) {
        if (restorePending) {
            try { ApplyHybridMode(original, "roundtrip-failure-restore-original"); }
            catch (const std::exception& error) { std::fprintf(stderr, "roundtrip-restore-failed: %s\n", error.what()); }
        }
        DestroyWindow(window);
        throw;
    }
    Check(!IsWindowVisible(window) && GetForegroundWindow() != window, "roundtrip-window-remained-hidden");
    Check(DestroyWindow(window) && !IsWindow(window), "roundtrip-window-destroyed");
    Emit("\"event\":\"roundtrip-complete\",\"completed\":true,\"injectionPerformed\":false,\"presentCalls\":0,\"drawCount\":"
        + std::to_string(drawCount) + ",\"originalMode\":" + std::to_string(original) + ",\"hwndDestroyed\":true");
}

void HybridScopedFactoryMain(bool atomic = false) {
    ResourceManagerD3D9::HybridEnumeration value;
    if (atomic) {
        ResourceManagerD3D9::HybridEnumeration rejected;
        UINT untouched = 77;
        Check(!rejected.Open(nullptr) && !rejected.Read(untouched) && untouched == 77, "atomic-null-module-no-value");
        Check(!rejected.Open(GetModuleHandleW(L"kernel32.dll")), "atomic-non-d3d9-module-rejected");
        Check(value.Open(GetModuleHandleW(L"d3d9.dll")), "atomic-qualified-system-image");
        Check(!value.Open(GetModuleHandleW(L"d3d9.dll")), "atomic-no-second-initialization");
        UINT initial = 0, after = 0;
        Check(value.Read(initial), "atomic-read-initial");
        Check(!value.TryChange(initial, 2) && !value.TryChange(2, 4)
            && value.Read(after) && after == initial, "atomic-unknown-mode-does-not-write");
        const UINT other = initial == 3 ? 4 : 3;
        ApplyHybridMode(other, "atomic-external-writer");
        Check(!value.TryChange(initial, 4) && value.Read(after) && after == other, "atomic-stale-original-does-not-overwrite");
        Check(value.TryChange(other, initial) && value.Read(after) && after == initial, "atomic-test-writer-restored");
        Emit("\"event\":\"atomic-boundaries\",\"passed\":true,\"originalMode\":" + std::to_string(initial)
            + ",\"restoredMode\":" + std::to_string(after) + ",\"unknownModeRejected\":true,\"staleExpectedRejected\":true");
    }
    const auto apply = [&](UINT desired, const char* phase) {
        if (!atomic) return ApplyHybridMode(desired, phase);
        UINT before = 0, after = 0;
        Check(value.Read(before) && value.TryChange(before, desired) && value.Read(after) && after == desired, "qualified-atomic-mode-change");
        Emit("\"event\":\"hybrid-atomic\",\"phase\":" + J(phase) + ",\"mode\":" + std::to_string(desired)
            + ",\"before\":" + std::to_string(before) + ",\"after\":" + std::to_string(after));
        return before;
    };
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"RM D3D9 Scoped Factory Probe",
        WS_POPUP, 0, 0, Side, Side, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Check(window != nullptr, "own-scoped-window");
    bool changed = false;
    UINT original = 0;
    try {
        for (UINT seed : {0u, 1u, 3u, 4u}) {
            const std::string prefix = "scoped-original" + std::to_string(seed);
            const UINT beforeSeed = apply(seed, (prefix + "-seed").c_str());
            if (!changed) { original = beforeSeed; changed = true; }
            Factory baseline(true); baseline.Create(window); baseline.Render(prefix + "-baseline", true, false);
            const UINT baselineVendor = DeviceVendor(baseline);
            for (UINT target : {1u, 4u}) {
                for (bool isEx : {false, true}) {
                    const std::string phase = prefix + "-target" + std::to_string(target) + (isEx ? "-ex" : "-classic");
                    const UINT before = apply(target, (phase + "-enter").c_str());
                    Check(before == seed, "scoped-entry-retains-actual-original");
                    Factory factory(isEx);
                    apply(before, (phase + "-leave").c_str());
                    // Device creation happens only after the temporary factory choice was restored.
                    factory.Create(window); factory.Render(phase + "-device-after-restore", true, false);
                    const UINT expectedVendor = target == 1 ? 0x10de : 0x1002;
                    Check(DeviceVendor(factory) == expectedVendor, "scoped-factory-retains-requested-hardware");
                    Factory after(true); after.Create(window); after.Render(phase + "-ordinary-factory-after", true, false);
                    Check(DeviceVendor(after) == baselineVendor, "ordinary-factory-choice-unchanged-after-scope");
                    Emit("\"event\":\"scoped-factory\",\"phase\":" + J(phase) + ",\"api\":" + J(factory.Name())
                        + ",\"originalMode\":" + std::to_string(before) + ",\"targetMode\":" + std::to_string(target)
                        + ",\"targetVendor\":" + std::to_string(DeviceVendor(factory)) + ",\"baselineVendor\":" + std::to_string(baselineVendor)
                        + ",\"ordinaryVendorAfter\":" + std::to_string(DeviceVendor(after)));
                }
            }
        }
        apply(original, "scoped-restore-process-original"); changed = false;
    } catch (...) {
        if (changed) {
            try { apply(original, "scoped-failure-restore-process-original"); }
            catch (const std::exception& error) { std::fprintf(stderr, "scoped-restore-failed: %s\n", error.what()); }
        }
        DestroyWindow(window); throw;
    }
    Check(!IsWindowVisible(window) && GetForegroundWindow() != window, "scoped-window-remained-hidden");
    Check(DestroyWindow(window) && !IsWindow(window), "scoped-window-destroyed");
    Emit("\"event\":\"scoped-complete\",\"completed\":true,\"caseCount\":16,\"injectionPerformed\":false,\"presentCalls\":0,\"drawCount\":"
        + std::to_string(drawCount) + ",\"originalMode\":" + std::to_string(original) + ",\"hwndDestroyed\":true,\"atomic\":" + (atomic ? "true" : "false"));
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        if (argc == 7 && std::wcscmp(argv[1], L"--worker") == 0) role = Utf8(argv[2]);
        else Check((argc == 2 && (std::wcscmp(argv[1], L"--control") == 0 || std::wcscmp(argv[1], L"--hybrid-cold") == 0
            || std::wcscmp(argv[1], L"--hybrid-roundtrip") == 0 || std::wcscmp(argv[1], L"--hybrid-scoped") == 0
            || std::wcscmp(argv[1], L"--hybrid-atomic") == 0))
            || (argc == 3 && std::wcscmp(argv[1], L"--hybrid-hot") == 0), "explicit-mode");
        BOOL inJob = FALSE; Check(IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-job");
        Check((GetErrorMode() & 0x8003) == 0x8003, "non-dialog-parent-policy");
        Emit("\"event\":\"process\",\"creationFileTime\":" + std::to_string(Birth(GetCurrentProcess())) + ",\"errorMode\":" + std::to_string(GetErrorMode()) + ",\"path\":" + J(Utf8(ModulePath())));
        if (role == "controller") {
            if (std::wcscmp(argv[1], L"--control") == 0) ControlMain();
            else if (std::wcscmp(argv[1], L"--hybrid-roundtrip") == 0) HybridRoundTripMain();
            else if (std::wcscmp(argv[1], L"--hybrid-scoped") == 0) HybridScopedFactoryMain();
            else if (std::wcscmp(argv[1], L"--hybrid-atomic") == 0) HybridScopedFactoryMain(true);
            else if (argc == 2) HybridControlMain(true, 0);
            else { Check(std::wcscmp(argv[2], L"1") == 0 || std::wcscmp(argv[2], L"3") == 0 || std::wcscmp(argv[2], L"4") == 0, "hot-mode"); HybridControlMain(false, static_cast<UINT>(argv[2][0] - L'0')); }
        } else {
            wchar_t* end = nullptr; auto value = std::wcstoull(argv[3], &end, 10); Check(end && !*end && value, "completion-handle");
            Check(std::wcslen(argv[5]) == 1 && (argv[5][0] == L'0' || argv[5][0] == L'1' || argv[5][0] == L'3' || argv[5][0] == L'4'), "worker-hybrid-mode");
            auto mode = static_cast<UINT>(argv[5][0] - L'0');
            if (std::wcscmp(argv[6], L"1") == 0) ApplyHybridMode(mode, "before-factory");
            WorkerMain(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(value)), std::wcscmp(argv[4], L"1") == 0, mode);
        }
        return 0;
    } catch (const std::exception& error) { std::fprintf(stderr, "%s: %s (win32=%lu)\n", role.c_str(), error.what(), GetLastError()); return 1; }
}
