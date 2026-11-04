#include <windows.h>
#include <dbgeng.h>
#include <dbghelp.h>
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>
#include <io.h>
#include <fcntl.h>

// These engine option bits are fixed by Windows SDK 10.0.26100 DbgEng.h.
// MinGW-w64's current projection omits their names, while retaining the API.
#ifndef DEBUG_ENGOPT_DISABLE_MANAGED_SUPPORT
#define DEBUG_ENGOPT_DISABLE_MANAGED_SUPPORT 0x00004000
#endif
#ifndef DEBUG_ENGOPT_DISABLE_EXECUTION_COMMANDS
#define DEBUG_ENGOPT_DISABLE_EXECUTION_COMMANDS 0x00010000
#endif
#ifndef DEBUG_ENGOPT_DISABLESQM
#define DEBUG_ENGOPT_DISABLESQM 0x00080000
#endif

// COM declarations and structures come from the platform's DbgEng header.
// Wire primitives are written individually; native COM structs never cross this ABI.
template<class T> struct Com final {
    T* value = nullptr;
    ~Com() { if (value) value->Release(); }
    T* operator->() const { return value; }
    Com() = default; Com(const Com&) = delete; Com& operator=(const Com&) = delete;
};
struct Library final { HMODULE value = nullptr; ~Library() { if (value) FreeLibrary(value); } };
template<class T> static void put(T value) { (void)fwrite(&value, sizeof(value), 1, stdout); }
static void prefix(HRESULT status) { put<uint32_t>(0x524D4441); put<uint32_t>(1); put<int32_t>(status); }
static void text(const std::string& value) {
    put<uint32_t>(static_cast<uint32_t>(value.size()));
    if (!value.empty()) (void)fwrite(value.data(), 1, value.size(), stdout);
}
static std::string utf8(const wchar_t* value) {
    const int n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1 || n > 2049) return {};
    std::string result(static_cast<size_t>(n), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, result.data(), n, nullptr, nullptr) != n) return {};
    result.resize(static_cast<size_t>(n - 1)); return result;
}
struct Frame final { DEBUG_STACK_FRAME raw{}; ULONG64 displacement = 0; std::string symbol, module; };

static HRESULT analyze(const wchar_t* enginePath, const wchar_t* dumpPath,
    const wchar_t* symbolPath, ULONG timeout, ULONG maximumFrames) {
    Library engine;
    engine.value = LoadLibraryExW(enginePath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!engine.value) return HRESULT_FROM_WIN32(GetLastError());
    using Create = HRESULT(WINAPI*)(REFIID, PVOID*);
    const auto create = std::bit_cast<Create>(GetProcAddress(engine.value, "DebugCreate"));
    if (!create) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    Com<IDebugClient4> client; Com<IDebugControl> control; Com<IDebugSymbols3> symbols; Com<IDebugAdvanced> advanced;
    HRESULT hr = create(__uuidof(IDebugClient4), reinterpret_cast<void**>(&client.value));
    if (FAILED(hr)) return hr;
    hr = client->QueryInterface(__uuidof(IDebugControl), reinterpret_cast<void**>(&control.value));
    if (FAILED(hr)) return hr;
    hr = client->QueryInterface(__uuidof(IDebugSymbols3), reinterpret_cast<void**>(&symbols.value));
    if (FAILED(hr)) return hr;
    // Advanced context availability is optional, never replaced by synthetic context.
    const auto advancedHr = client->QueryInterface(__uuidof(IDebugAdvanced), reinterpret_cast<void**>(&advanced.value));
    hr = control->SetEngineOptions(DEBUG_ENGOPT_DISALLOW_NETWORK_PATHS | DEBUG_ENGOPT_DISALLOW_SHELL_COMMANDS
        | DEBUG_ENGOPT_DISABLE_EXECUTION_COMMANDS | DEBUG_ENGOPT_DISABLE_MANAGED_SUPPORT | DEBUG_ENGOPT_DISABLESQM);
    if (FAILED(hr)) return hr;
    // Explicit local-only symbol path and empty image path prevent environment policy injection.
    hr = symbols->SetSymbolPathWide(symbolPath); if (FAILED(hr)) return hr;
    hr = symbols->SetImagePathWide(L""); if (FAILED(hr)) return hr;
    hr = symbols->SetSymbolOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS
        | SYMOPT_IGNORE_NT_SYMPATH | SYMOPT_SECURE);
    if (FAILED(hr)) return hr;
    hr = client->OpenDumpFileWide(dumpPath, 0); if (FAILED(hr)) return hr;
    hr = control->WaitForEvent(DEBUG_WAIT_DEFAULT, timeout);
    if (hr != S_OK) return hr == S_FALSE ? HRESULT_FROM_WIN32(ERROR_TIMEOUT) : hr;

    ULONG processor = 0, code = 0; ULONG64 bug[4]{};
    (void)control->GetActualProcessorType(&processor);
    ULONG debugClass = 0, qualifier = 0;
    const auto typeHr = control->GetDebuggeeType(&debugClass, &qualifier);
    // ReadBugCheckData is meaningful only for a kernel dump; user dumps stay unavailable.
    const auto bugHr = SUCCEEDED(typeHr) && debugClass == DEBUG_CLASS_KERNEL
        ? control->ReadBugCheckData(&code, &bug[0], &bug[1], &bug[2], &bug[3]) : E_NOTIMPL;
    DEBUG_LAST_EVENT_INFO_EXCEPTION exception{}; ULONG eventType = 0, pid = 0, tid = 0, extraUsed = 0;
    auto exceptionHr = control->GetLastEventInformation(&eventType, &pid, &tid, &exception,
        static_cast<ULONG>(sizeof(exception)), &extraUsed, nullptr, 0, nullptr);
    if (SUCCEEDED(exceptionHr) && (eventType != DEBUG_EVENT_EXCEPTION || extraUsed != sizeof(exception)
        || exception.ExceptionRecord.NumberParameters > EXCEPTION_MAXIMUM_PARAMETERS)) exceptionHr = E_NOTIMPL;
    CONTEXT context{};
    static_assert(sizeof(CONTEXT) == 1232, "Managed decoder requires the Windows SDK AMD64 CONTEXT size.");
    context.ContextFlags = CONTEXT_ALL;
    const auto contextHr = processor == IMAGE_FILE_MACHINE_AMD64 && advancedHr == S_OK
        ? advanced->GetThreadContext(&context, static_cast<ULONG>(sizeof(context))) : E_NOTIMPL;
    DEBUG_STACK_FRAME raw[64]{}; ULONG filled = 0;
    const auto stackHr = control->GetStackTrace(0, 0, 0, raw, maximumFrames, &filled);
    if (stackHr != S_OK) filled = 0;
    if (filled > maximumFrames) return E_UNEXPECTED;
    std::vector<Frame> frames; frames.reserve(filled);
    for (ULONG i = 0; i < filled; ++i) {
        Frame frame; frame.raw = raw[i];
        wchar_t name[512]{}; ULONG used = 0;
        if (symbols->GetNameByOffsetWide(raw[i].InstructionOffset, name, 512, &used, &frame.displacement) == S_OK
            && used > 1 && used <= 512) frame.symbol = utf8(name);
        ULONG moduleIndex = 0; ULONG64 moduleBase = 0;
        if (SUCCEEDED(symbols->GetModuleByOffset(raw[i].InstructionOffset, 0, &moduleIndex, &moduleBase))) {
            wchar_t module[512]{}; ULONG moduleUsed = 0;
            if (symbols->GetModuleNameStringWide(DEBUG_MODNAME_MODULE, moduleIndex, moduleBase,
                module, 512, &moduleUsed) == S_OK && moduleUsed > 1 && moduleUsed <= 512)
                frame.module = utf8(module);
        }
        frames.push_back(std::move(frame));
    }
    // Finish reading and end the session before writing one complete bounded result.
    (void)client->EndSession(DEBUG_END_PASSIVE);
    prefix(S_OK);
    put<int32_t>(bugHr); put<int32_t>(exceptionHr); put<int32_t>(contextHr); put<int32_t>(stackHr);
    put<uint32_t>(processor); put<uint32_t>(code);
    for (const auto parameter : bug) put<uint64_t>(parameter);
    const bool hasException = exceptionHr == S_OK;
    put<uint32_t>(pid); put<uint32_t>(tid);
    put<uint32_t>(hasException ? exception.ExceptionRecord.ExceptionCode : 0);
    put<uint32_t>(hasException ? exception.ExceptionRecord.ExceptionFlags : 0);
    put<uint64_t>(hasException ? exception.ExceptionRecord.ExceptionAddress : 0);
    put<uint32_t>(hasException ? (exception.FirstChance != 0 ? 1 : 0) : 0);
    const ULONG parameterCount = hasException ? exception.ExceptionRecord.NumberParameters : 0;
    put<uint32_t>(parameterCount);
    for (ULONG i = 0; i < parameterCount; ++i) put<uint64_t>(exception.ExceptionRecord.ExceptionInformation[i]);
    put<uint32_t>(contextHr == S_OK ? static_cast<uint32_t>(sizeof(context)) : 0);
    if (contextHr == S_OK) (void)fwrite(&context, 1, sizeof(context), stdout);
    put<uint32_t>(filled);
    for (const auto& frame : frames) {
        put<uint64_t>(frame.raw.InstructionOffset); put<uint64_t>(frame.raw.ReturnOffset);
        put<uint64_t>(frame.raw.FrameOffset); put<uint64_t>(frame.raw.StackOffset);
        put<uint64_t>(frame.displacement); text(frame.symbol); text(frame.module);
    }
    return S_OK;
}
int wmain(int argc, wchar_t** argv) {
    (void)_setmode(_fileno(stdout), _O_BINARY);
    if (argc != 6) { prefix(E_INVALIDARG); return 0; }
    wchar_t* end = nullptr;
    const auto timeout = wcstoul(argv[4], &end, 10);
    if (!end || *end || timeout < 100 || timeout > 120000) { prefix(E_INVALIDARG); return 0; }
    const auto frames = wcstoul(argv[5], &end, 10);
    if (!end || *end || frames < 1 || frames > 64) { prefix(E_INVALIDARG); return 0; }
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) { prefix(initialized); return 0; }
    // C# owns the hard overall deadline. The engine WaitForEvent has the same finite bound.
    HRESULT result = E_FAIL;
    try { result = analyze(argv[1], argv[2], argv[3], timeout, frames); }
    catch (...) { result = E_FAIL; }
    if (FAILED(result)) prefix(result);
    CoUninitialize();
    return ferror(stdout) ? 1 : 0;
}
