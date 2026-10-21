#include "../../src/Core/Native/GpuPlacementCommon/ControllerOutputPipes.h"
int wmain(int argc, wchar_t** argv) {
    if (!ResourceManagerGpuController::ConfigureOutput(argc, argv)) return 2;
    if (argc == 3 && std::wcscmp(argv[1], L"--await-recovery") == 0) {
        const std::wstring request = std::wstring(argv[2]) + L"/stop.recover";
        const auto deadline = GetTickCount64() + 10000;
        while (GetFileAttributesW(request.c_str()) == INVALID_FILE_ATTRIBUTES && GetTickCount64() < deadline) Sleep(20);
        if (GetFileAttributesW(request.c_str()) == INVALID_FILE_ATTRIBUTES) return 4;
        printf("owned-cleanup-request-observed\n");
        return 0;
    }
    DWORD session{};
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return 3;
    printf("session:%lu\n", session);
    for (int i = 1; i < argc; ++i) printf("argument:%ls\n", argv[i]);
    fprintf(stderr, "owned-probe-error-stream\n");
    return 0;
}
