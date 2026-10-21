#include <windows.h>
#include <cstdint>
#include <cstdlib>

// Only the finite SDK fixture loads this module. Contract checks use the real
// module; initialization fails after the managed buffer has been allocated.
static HMODULE actual;
static uint32_t initializationCalls;
extern "C" __declspec(dllexport) int rm_test_load_actual(const wchar_t* path)
{
    if (actual) return 0;
    actual = LoadLibraryW(path);
    return actual ? 1 : 0;
}
extern "C" __declspec(dllexport) uint32_t rm_test_initialization_calls() { return initializationCalls; }
#define FORWARD(result, name) \
    extern "C" __declspec(dllexport) result name() { \
        auto function = reinterpret_cast<result (*)()>(GetProcAddress(actual, #name)); \
        if (!function) std::abort(); \
        return function(); \
    }
FORWARD(uint32_t, rm_private_resource_ledger_abi_version)
FORWARD(uint32_t, rm_private_resource_ledger_config_size)
FORWARD(uint32_t, rm_private_resource_ledger_snapshot_input_size)
FORWARD(uint32_t, rm_private_resource_ledger_resource_input_size)
FORWARD(uint32_t, rm_private_resource_ledger_resource_view_size)
FORWARD(uint32_t, rm_private_resource_ledger_action_feedback_size)
FORWARD(uint32_t, rm_private_resource_ledger_summary_size)
FORWARD(uint64_t, rm_private_resource_ledger_layout_fingerprint)
FORWARD(uint32_t, rm_private_resource_ledger_state_alignment)
FORWARD(uint32_t, rm_resource_scheduler_abi_version)
FORWARD(uint32_t, rm_resource_scheduler_config_size)
FORWARD(uint32_t, rm_resource_scheduler_plan_request_size)
FORWARD(uint32_t, rm_resource_scheduler_capacity_size)
FORWARD(uint32_t, rm_resource_scheduler_target_size)
FORWARD(uint32_t, rm_resource_scheduler_private_resource_size)
FORWARD(uint32_t, rm_resource_scheduler_pending_action_size)
FORWARD(uint32_t, rm_resource_scheduler_candidate_size)
FORWARD(uint32_t, rm_resource_scheduler_selection_size)
FORWARD(uint32_t, rm_resource_scheduler_resource_scratch_size)
FORWARD(uint32_t, rm_resource_scheduler_execution_authority_size)
FORWARD(uint32_t, rm_resource_scheduler_fact_batch_input_size)
FORWARD(uint32_t, rm_resource_scheduler_reservation_request_size)
FORWARD(uint32_t, rm_resource_scheduler_reservation_token_size)
FORWARD(uint32_t, rm_resource_scheduler_revalidation_input_size)
FORWARD(uint32_t, rm_resource_scheduler_revalidation_output_size)
FORWARD(uint32_t, rm_resource_scheduler_feedback_request_size)
FORWARD(uint32_t, rm_resource_scheduler_action_feedback_size)
FORWARD(uint32_t, rm_resource_scheduler_feedback_output_size)
FORWARD(uint64_t, rm_resource_scheduler_layout_fingerprint)
FORWARD(uint32_t, rm_resource_scheduler_state_alignment)
#define REQUIRED_SIZE(name) \
    extern "C" __declspec(dllexport) int name(uint32_t capacity, uint64_t* size) { \
        auto function = reinterpret_cast<int (*)(uint32_t, uint64_t*)>(GetProcAddress(actual, #name)); \
        if (!function) std::abort(); \
        return function(capacity, size); \
    }
REQUIRED_SIZE(rm_private_resource_ledger_state_required_size)
REQUIRED_SIZE(rm_resource_scheduler_state_required_size)
extern "C" __declspec(dllexport) int rm_resource_scheduler_required_candidate_capacity(uint32_t count, uint32_t* capacity)
{
    auto function = reinterpret_cast<int (*)(uint32_t, uint32_t*)>(
        GetProcAddress(actual, "rm_resource_scheduler_required_candidate_capacity"));
    if (!function) std::abort();
    return function(count, capacity);
}
static int failInitialization(void* state, uint64_t size)
{
    if (!state || size == 0) std::abort();
    static_cast<volatile unsigned char*>(state)[0] = 0x5a;
    ++initializationCalls;
    return 1;
}
extern "C" __declspec(dllexport) int rm_private_resource_ledger_state_initialize(
    void* state, uint64_t size, const void*) { return failInitialization(state, size); }
extern "C" __declspec(dllexport) int rm_resource_scheduler_state_initialize(
    void* state, uint64_t size, uint32_t, uint64_t) { return failInitialization(state, size); }
