#pragma once

#include <cstdint>
#include <stdexcept>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace resource_manager_adapter {

inline constexpr std::uint8_t snapshot_schema_version = 11;

enum class AdapterResourceTier : std::uint8_t {
    vram = 0,
    physical_memory = 1,
    virtual_memory = 2,
};

enum class AdapterResourceKind : std::uint8_t {
    primary_data = 0,
    cache = 1,
    index = 2,
    model_weights = 3,
    media_resource = 4,
    editing_document_state = 5,
    staging_buffer = 6,
    temporary_compute_memory = 7,
    runtime_overhead = 8,
    render_surface = 9,
    texture = 10,
    render_buffer = 11,
    compute_buffer = 12,
};

enum class AdapterResourceRecoveryKind : std::uint8_t {
    disk_copy = 0,
    built_data = 1,
    live_state = 2,
};

enum class AdapterResourceGranularity : std::uint8_t {
    fully_loaded = 0,
    partial_usable = 1,
    not_applicable = 2,
};

namespace action_route {
inline constexpr std::uint8_t manager_direct = 0;
inline constexpr std::uint8_t adapter_handler = 1;
inline constexpr std::uint8_t max = adapter_handler;
}

namespace surface_state {
inline constexpr std::uint8_t foreground_focused = 0;
inline constexpr std::uint8_t foreground_unfocused = 1;
inline constexpr std::uint8_t background_window = 2;
inline constexpr std::uint8_t tray_background = 3;
inline constexpr std::uint8_t pure_background = 4;
}

namespace action_mask {
inline constexpr std::uint8_t none = 0;
inline constexpr std::uint8_t discard = 1u << 0u;
inline constexpr std::uint8_t trim = 1u << 1u;
inline constexpr std::uint8_t move_down = 1u << 2u;
inline constexpr std::uint8_t move_up = 1u << 3u;
inline constexpr std::uint8_t all = discard | trim | move_down | move_up;
}

namespace frontend_demand_mask {
inline constexpr std::uint8_t none = 0;
inline constexpr std::uint8_t required_now = 1u << 0u;
inline constexpr std::uint8_t ready_soon = 1u << 1u;
inline constexpr std::uint8_t preload_eager = 1u << 2u;
inline constexpr std::uint8_t preload_opportunistic = 1u << 3u;
inline constexpr std::uint8_t all = required_now | ready_soon | preload_eager | preload_opportunistic;
}

enum class AdapterResourceActionStatus : std::uint8_t {
    completed = 0,
    resource_not_found = 1,
    action_not_supported = 2,
    invalid_request = 3,
    resource_busy = 4,
    failed = 5,
};

struct AdapterResourceActionRequest {
    std::uint64_t request_id;
    std::uint64_t resource_key;
    std::uint8_t action;
    std::uint8_t flags;
};

struct AdapterResourceActionResult {
    std::uint64_t request_id;
    std::uint64_t resource_key;
    std::uint8_t action;
    AdapterResourceActionStatus status;
    AdapterResourceTier previous_tier;
    AdapterResourceTier current_tier;
    std::uint64_t released_bytes;
    std::uint64_t resident_bytes;
    std::uint16_t detail_code;
};

inline AdapterResourceActionResult completed_action_result(
    const AdapterResourceActionRequest& request,
    AdapterResourceTier previous_tier,
    AdapterResourceTier current_tier,
    std::uint64_t released_bytes,
    std::uint64_t resident_bytes,
    std::uint16_t detail_code = 0) {
    return AdapterResourceActionResult{
        request.request_id,
        request.resource_key,
        request.action,
        AdapterResourceActionStatus::completed,
        previous_tier,
        current_tier,
        released_bytes,
        resident_bytes,
        detail_code,
    };
}

inline AdapterResourceActionResult action_error_result(
    const AdapterResourceActionRequest& request,
    AdapterResourceActionStatus status,
    std::uint16_t detail_code = 0) {
    return AdapterResourceActionResult{
        request.request_id,
        request.resource_key,
        request.action,
        status,
        AdapterResourceTier::vram,
        AdapterResourceTier::vram,
        0,
        0,
        detail_code,
    };
}

using AdapterResourceActionHandler = AdapterResourceActionResult (*)(void*, const AdapterResourceActionRequest&);

struct AdapterResourceActionRegistration {
    std::uint64_t resource_key;
    std::uint8_t supported_actions;
    void* context;
    AdapterResourceActionHandler handler;
};

class AdapterResourceActionDispatcher {
public:
    void register_handler(AdapterResourceActionRegistration registration) {
        if (registration.resource_key == 0) {
            throw std::invalid_argument("resource_key must be nonzero.");
        }
        if (registration.supported_actions == action_mask::none || (registration.supported_actions & ~action_mask::all) != 0) {
            throw std::invalid_argument("supported_actions contains unknown bits.");
        }
        if (registration.handler == nullptr) {
            throw std::invalid_argument("handler is required.");
        }
        registrations_[registration.resource_key] = registration;
    }

    bool unregister_handler(std::uint64_t resource_key) {
        return registrations_.erase(resource_key) > 0;
    }

    AdapterResourceActionResult execute(const AdapterResourceActionRequest& request) const {
        if (request.resource_key == 0 || !is_single_known_action(request.action)) {
            return action_error_result(request, AdapterResourceActionStatus::invalid_request);
        }
        const auto found = registrations_.find(request.resource_key);
        if (found == registrations_.end()) {
            return action_error_result(request, AdapterResourceActionStatus::resource_not_found);
        }
        const auto& registration = found->second;
        if ((registration.supported_actions & request.action) == 0) {
            return action_error_result(request, AdapterResourceActionStatus::action_not_supported);
        }
        return registration.handler(registration.context, request);
    }

private:
    static bool is_single_known_action(std::uint8_t action) {
        return action != 0 && (action & (action - 1u)) == 0 && (action & ~action_mask::all) == 0;
    }

    std::unordered_map<std::uint64_t, AdapterResourceActionRegistration> registrations_;
};

struct TieredResourceEntry {
    std::uint64_t resource_key;
    std::uint32_t resource_id;
    std::uint64_t size_bytes;
    AdapterResourceTier tier;
    AdapterResourceKind resource_kind;
    AdapterResourceRecoveryKind recovery_kind;
    AdapterResourceGranularity granularity;
    std::uint8_t inapplicable_actions;
    std::uint8_t action_route;
    std::uint8_t activity_score;
    std::uint8_t frontend_demand_mask;
};

inline std::uint64_t adapter_resource_key(std::string_view stable_id) {
    auto begin = stable_id.find_first_not_of(" \t\r\n");
    if (begin == std::string_view::npos) {
        throw std::invalid_argument("Stable id is required.");
    }
    auto end = stable_id.find_last_not_of(" \t\r\n");
    stable_id = stable_id.substr(begin, end - begin + 1);

    std::uint64_t hash = 14695981039346656037ull;
    for (unsigned char value : stable_id) {
        hash ^= static_cast<std::uint64_t>(value);
        hash *= 1099511628211ull;
    }
    return hash == 0 ? 14695981039346656037ull : hash;
}

inline void validate_tiered_resource_entry(const TieredResourceEntry& entry) {
    if (entry.resource_key == 0) {
        throw std::invalid_argument("Adapter resource entry requires a nonzero resource_key.");
    }
    if (entry.resource_id == 0) {
        throw std::invalid_argument("Adapter resource entry requires a nonzero resource_id.");
    }
    if (static_cast<std::uint8_t>(entry.resource_kind) > static_cast<std::uint8_t>(AdapterResourceKind::compute_buffer)) {
        throw std::invalid_argument("resource_kind is outside the known byte enum range.");
    }
    if ((entry.inapplicable_actions & ~action_mask::all) != 0) {
        throw std::invalid_argument("inapplicable_actions contains unknown bits.");
    }
    if (entry.action_route > action_route::max) {
        throw std::invalid_argument("action_route is outside the known byte enum range.");
    }
    if ((entry.frontend_demand_mask & ~frontend_demand_mask::all) != 0) {
        throw std::invalid_argument("frontend_demand_mask contains unknown bits.");
    }
}

static_assert(sizeof(AdapterResourceTier) == 1);
static_assert(sizeof(AdapterResourceKind) == 1);
static_assert(sizeof(AdapterResourceActionStatus) == 1);

}
