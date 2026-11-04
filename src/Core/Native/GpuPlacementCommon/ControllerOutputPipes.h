#pragma once
#include <windows.h>
#include <cstdio>
#include <cwchar>
#include <fcntl.h>
#include <io.h>
#include <string>

namespace ResourceManagerGpuController {
inline bool Redirect(FILE* stream, const wchar_t* name) {
    const std::wstring pipe(name);
    if (pipe.rfind(L"ResourceManager.GpuController.", 0) != 0 || pipe.find_first_of(L"\\/") != std::wstring::npos)
        return false;
    // CREATE_NO_WINDOW supplies no inherited standard handles; first bind the CRT stream to a valid descriptor.
    if (!_wfreopen(L"NUL", L"wb", stream)) return false;
    HANDLE handle = CreateFileW((L"\\\\.\\pipe\\" + pipe).c_str(), FILE_WRITE_DATA | SYNCHRONIZE,
        0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    const int descriptor = _open_osfhandle(reinterpret_cast<intptr_t>(handle), _O_WRONLY | _O_BINARY);
    if (descriptor < 0) { CloseHandle(handle); return false; }
    const bool redirected = _dup2(descriptor, _fileno(stream)) == 0;
    _close(descriptor);
    if (redirected) { clearerr(stream); setvbuf(stream, nullptr, _IONBF, 0); }
    return redirected;
}
// Optional suffix preserves the existing owned rehearsal CLI. No inherited handles cross sessions.
inline bool ConfigureOutput(int& argc, wchar_t** argv) {
    if (argc < 4 || std::wcscmp(argv[argc - 3], L"--resource-manager-output-pipes") != 0) return true;
    const bool okay = Redirect(stdout, argv[argc - 2]) && Redirect(stderr, argv[argc - 1]);
    argc -= 3;
    return okay;
}
}
