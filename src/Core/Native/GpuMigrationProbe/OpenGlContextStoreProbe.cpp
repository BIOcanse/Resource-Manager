#include "../GpuPlacementShim/OpenGlContextDispatch.h"
#include <cstdio>
#include <stdexcept>
#include <thread>
#include <vector>

using namespace ResourceManagerOpenGl;
namespace {
ContextStore store;
HGLRC callbackHandle{};
DHGLRC nextToken = 1;
unsigned checks{}, createCalls{}, deleteCalls{}, wrongDeleteCalls{};
bool failCreate{}, failDelete{}, callbackRead{};
void Check(bool value, const char* name)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
    if (!value) throw std::runtime_error(name);
}
DHGLRC APIENTRY Create(HDC, int)
{
    ++createCalls;
    if (callbackHandle) callbackRead = store.Find(callbackHandle) != nullptr;
    SetLastError(1234);
    return failCreate ? 0 : nextToken++;
}
BOOL APIENTRY Delete(DHGLRC token)
{
    ++deleteCalls;
    bool found = false;
    // An ICD may query on its worker while the calling thread waits for that worker.
    std::thread reader([&] { const auto record = store.Find(callbackHandle); found = record && record->token.load() == token; });
    reader.join();
    callbackRead = found;
    SetLastError(1235);
    return !failDelete;
}
BOOL APIENTRY WrongDelete(DHGLRC) { ++wrongDeleteCalls; return FALSE; }
}
int main()
{
    SetErrorMode(32771);
    try {
        IcdExports driver;
        driver.createLayerContext = &Create;
        driver.deleteContext = &Delete;
        const HMODULE module = GetModuleHandleW(nullptr);
        Check(store.Count() == 0 && !store.Find(nullptr), "empty-store");
        Check(!store.Create(nullptr, driver, nullptr, 0) && GetLastError() == ERROR_INVALID_PARAMETER && createCalls == 0, "invalid-module-before-create");
        Check(!store.Create(module, {}, nullptr, 0) && GetLastError() == ERROR_INVALID_PARAMETER && createCalls == 0, "missing-entry-before-create");
        failCreate = true;
        Check(!store.Create(module, driver, nullptr, 0) && GetLastError() == 1234 && store.Count() == 0, "native-create-failure-removes-prepared-record");
        failCreate = false;
        auto first = store.Create(module, driver, nullptr, 0);
        Check(first && GetLastError() == 1234 && store.Count() == 1 && first->token.load() == 1, "create-publishes-real-token");
        callbackHandle = PublicHandle(first);
        auto second = store.Create(module, driver, nullptr, 0);
        Check(second && callbackRead && store.Find(callbackHandle) == first, "native-create-can-query-existing-record");
        Check(first->module == module && first->driver.deleteContext == &Delete, "immutable-original-driver");
        driver.deleteContext = &WrongDelete;
        Check(first->owner.CaptureCurrent(), "capture-bound-thread");
        const unsigned before = deleteCalls;
        Check(!store.DestroyUnbound(first) && GetLastError() == ERROR_BUSY && deleteCalls == before && store.Count() == 2, "bound-record-not-destroyed");
        first->owner.Reset();
        ContextStore other;
        Check(!other.DestroyUnbound(first) && GetLastError() == ERROR_INVALID_HANDLE && deleteCalls == before, "other-store-cannot-destroy-record");
        failDelete = true;
        callbackRead = false;
        Check(!store.DestroyUnbound(first) && GetLastError() == 1235 && callbackRead && first->token.load() == 1 && store.Find(callbackHandle) == first, "failed-delete-retains-live-record-and-callback-lookup");
        failDelete = false;
        const auto handle = callbackHandle;
        std::weak_ptr<ContextRecord> weak = first;
        auto borrowed = store.Find(handle);
        Check(store.DestroyUnbound(first) && GetLastError() == 1235 && callbackRead && wrongDeleteCalls == 0, "successful-delete-uses-original-driver");
        Check(first->token.load() == 0 && !store.Find(handle) && store.Count() == 1, "successful-delete-removes-index");
        Check(!store.DestroyUnbound(first) && GetLastError() == ERROR_INVALID_HANDLE, "deleted-reference-not-deleted-twice");
        first.reset();
        Check(!weak.expired() && borrowed->token.load() == 0, "borrowed-record-survives-index-removal");
        borrowed.reset();
        Check(weak.expired(), "last-reference-frees-record");
        callbackHandle = PublicHandle(second);
        Check(store.DestroyUnbound(second), "second-context-cleanup");
        second.reset();
        driver.deleteContext = &Delete;
        std::vector<ContextReference> live;
        for (unsigned i = 0; i < 128; ++i) live.push_back(store.Create(module, driver, nullptr, 0));
        Check(store.Count() == 128, "more-than-six-simultaneous-contexts");
        bool fresh = true;
        for (auto& record : live) {
            fresh = fresh && record && !record->modernResolved && !record->modern.glCreateShader && !record->owner.Id();
            callbackHandle = PublicHandle(record);
            if (!store.DestroyUnbound(record)) throw std::runtime_error("bulk-delete");
        }
        Check(fresh && store.Count() == 0, "independent-fresh-dispatch-and-complete-retirement");
        live.clear();
        for (unsigned i = 0; i < 128; ++i) {
            auto record = store.Create(module, driver, nullptr, 0);
            if (!record) throw std::runtime_error("repeated-create");
            callbackHandle = PublicHandle(record);
            if (!store.DestroyUnbound(record)) throw std::runtime_error("repeated-delete");
        }
        Check(store.Count() == 0, "cumulative-create-delete-has-no-six-slot-limit");
        Check(createCalls == 259 && deleteCalls == 259 && wrongDeleteCalls == 0, "exact-native-call-accounting");
        std::printf("{\"passed\":true,\"checks\":%u,\"simultaneous\":128,\"repeated\":128,\"createCalls\":%u,\"deleteCalls\":%u,\"gpuCreated\":false}\n", checks, createCalls, deleteCalls);
        return 0;
    } catch (const std::exception& error) {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
