#include "OpenGlCallbackCapture.h"
#include "../GpuPlacementShim/OpenGlCallbackSourceCodec.h"
#include "../GpuPlacementCommon/WorkerPipeClient.h"
#include <new>

namespace
{
struct Request { uint32_t version, kind; LUID source; };
struct Reply { uint32_t version{1}, kind{2}, error{}, cleanupError{}; };
static_assert(sizeof(Request) == 16 && sizeof(Reply) == 16);
}

int wmain(int argc, wchar_t** argv)
{
    using namespace ResourceManagerOpenGl;
    using namespace ResourceManagerGpuWorker;
    try {
        Handle pipe, parent;
        const DWORD connection = ConnectParentPipe(argc, argv, pipe, parent);
        if (connection) return static_cast<int>(connection);
        BOOL inJob{};
        if (!IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) || !inJob) return ERROR_ACCESS_DENIED;
        Request request{};
        if (!ReadFrame(pipe.get(), request, sizeof(request)) || request.version != 1 || request.kind != 1)
            return ERROR_INVALID_DATA;
        IcdCallbackSource source;
        const auto acquired = AcquireIcdCallbackSource(request.source, source);
        Reply reply{1, 2, acquired.error, acquired.cleanupError};
        std::vector<unsigned char> encoded;
        if (acquired.Succeeded() && !EncodeIcdCallbackSource(source, encoded)) reply.error = GetLastError();
        std::vector<unsigned char> bytes(sizeof(reply));
        std::memcpy(bytes.data(), &reply, sizeof(reply));
        if (!reply.error && !reply.cleanupError) bytes.insert(bytes.end(), encoded.begin(), encoded.end());
        return WriteBytes(pipe.get(), bytes.data(), static_cast<uint32_t>(bytes.size())) ? 0 : ERROR_BROKEN_PIPE;
    } catch (const std::bad_alloc&) { return ERROR_NOT_ENOUGH_MEMORY; }
      catch (...) { return ERROR_UNHANDLED_EXCEPTION; }
}
