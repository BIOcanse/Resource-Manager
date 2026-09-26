#include "resource_manager_adapter/adapter_resource_ledger.hpp"

#include <cassert>
#include <vector>

namespace {

resource_manager_adapter::AdapterResourceActionResult handle_action(
    void* context,
    const resource_manager_adapter::AdapterResourceActionRequest& request) {
    auto* calls = static_cast<int*>(context);
    *calls += 1;
    return resource_manager_adapter::completed_action_result(
        request,
        resource_manager_adapter::AdapterResourceTier::physical_memory,
        resource_manager_adapter::AdapterResourceTier::virtual_memory,
        4096,
        0);
}

}

int main() {
    using namespace resource_manager_adapter;

    auto first = adapter_resource_key("physical-memory.sample");
    auto second = adapter_resource_key("physical-memory.sample");
    assert(first != 0);
    assert(first == second);

    TieredResourceEntry entry{
        adapter_resource_key("physical-memory.sample-cache"),
        1,
        4096,
        AdapterResourceTier::physical_memory,
        AdapterResourceKind::cache,
        AdapterResourceRecoveryKind::disk_copy,
        AdapterResourceGranularity::partial_usable,
        action_mask::move_up,
        action_route::adapter_handler,
        0,
        frontend_demand_mask::required_now,
    };
    validate_tiered_resource_entry(entry);
    assert(entry.resource_id == 1);
    assert(entry.frontend_demand_mask == frontend_demand_mask::required_now);

    int calls = 0;
    AdapterResourceActionDispatcher dispatcher;
    dispatcher.register_handler(AdapterResourceActionRegistration{
        entry.resource_key,
        action_mask::discard | action_mask::trim,
        &calls,
        handle_action,
    });

    auto result = dispatcher.execute(AdapterResourceActionRequest{
        0,
        entry.resource_key,
        action_mask::discard,
        0,
    });
    assert(calls == 1);
    assert(result.request_id == 0);
    assert(result.status == AdapterResourceActionStatus::completed);
    assert(result.released_bytes == 4096);

    auto compound = dispatcher.execute(AdapterResourceActionRequest{
        8,
        entry.resource_key,
        action_mask::discard | action_mask::trim,
        0,
    });
    assert(compound.status == AdapterResourceActionStatus::invalid_request);
    assert(calls == 1);
}
