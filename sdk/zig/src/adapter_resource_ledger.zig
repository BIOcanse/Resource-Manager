const std = @import("std");

pub const snapshot_schema_version: u8 = 11;

pub const AdapterResourceTier = enum(u8) {
    vram = 0,
    physical_memory = 1,
    virtual_memory = 2,
};

pub const AdapterResourceKind = enum(u8) {
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

pub const AdapterResourceRecoveryKind = enum(u8) {
    disk_copy = 0,
    built_data = 1,
    live_state = 2,
};

pub const AdapterResourceGranularity = enum(u8) {
    fully_loaded = 0,
    partial_usable = 1,
    not_applicable = 2,
};

pub const AdapterResourceActionRoute = enum(u8) {
    manager_direct = 0,
    adapter_handler = 1,
};

pub const AdapterSoftwareSurfaceState = enum(u8) {
    foreground_focused = 0,
    foreground_unfocused = 1,
    background_window = 2,
    tray_background = 3,
    pure_background = 4,
};

pub const AdapterResourceActionMask = packed struct(u8) {
    discard: bool = false,
    trim: bool = false,
    move_down: bool = false,
    move_up: bool = false,
    reserved: u4 = 0,
};

pub const AdapterResourceDemandMask = packed struct(u8) {
    required_now: bool = false,
    ready_soon: bool = false,
    preload_eager: bool = false,
    preload_opportunistic: bool = false,
    reserved: u4 = 0,
};

pub const AdapterResourceActionStatus = enum(u8) {
    completed = 0,
    resource_not_found = 1,
    action_not_supported = 2,
    invalid_request = 3,
    resource_busy = 4,
    failed = 5,
};

pub const AdapterResourceActionRequest = extern struct {
    request_id: u64,
    resource_key: u64,
    action: AdapterResourceActionMask,
    flags: u8,
};

pub const AdapterResourceActionResult = extern struct {
    request_id: u64,
    resource_key: u64,
    action: AdapterResourceActionMask,
    status: AdapterResourceActionStatus,
    previous_tier: AdapterResourceTier,
    current_tier: AdapterResourceTier,
    released_bytes: u64,
    resident_bytes: u64,
    detail_code: u16,
};

pub fn completedActionResult(
    request: AdapterResourceActionRequest,
    previous_tier: AdapterResourceTier,
    current_tier: AdapterResourceTier,
    released_bytes: u64,
    resident_bytes: u64,
    detail_code: u16,
) AdapterResourceActionResult {
    return .{
        .request_id = request.request_id,
        .resource_key = request.resource_key,
        .action = request.action,
        .status = .completed,
        .previous_tier = previous_tier,
        .current_tier = current_tier,
        .released_bytes = released_bytes,
        .resident_bytes = resident_bytes,
        .detail_code = detail_code,
    };
}

pub fn actionErrorResult(
    request: AdapterResourceActionRequest,
    status: AdapterResourceActionStatus,
    detail_code: u16,
) AdapterResourceActionResult {
    return .{
        .request_id = request.request_id,
        .resource_key = request.resource_key,
        .action = request.action,
        .status = status,
        .previous_tier = .vram,
        .current_tier = .vram,
        .released_bytes = 0,
        .resident_bytes = 0,
        .detail_code = detail_code,
    };
}

pub const AdapterResourceActionHandler = *const fn (?*anyopaque, AdapterResourceActionRequest) AdapterResourceActionResult;

pub const AdapterResourceActionRegistration = struct {
    resource_key: u64,
    supported_actions: AdapterResourceActionMask,
    context: ?*anyopaque,
    handler: AdapterResourceActionHandler,
};

pub const AdapterResourceActionDispatcher = struct {
    registrations: std.AutoHashMap(u64, AdapterResourceActionRegistration),

    pub fn init(allocator: std.mem.Allocator) AdapterResourceActionDispatcher {
        return .{ .registrations = std.AutoHashMap(u64, AdapterResourceActionRegistration).init(allocator) };
    }

    pub fn deinit(self: *AdapterResourceActionDispatcher) void {
        self.registrations.deinit();
    }

    pub fn register(self: *AdapterResourceActionDispatcher, registration: AdapterResourceActionRegistration) !void {
        if (registration.resource_key == 0) return error.MissingResourceKey;
        if (actionMaskValue(registration.supported_actions) == 0 or registration.supported_actions.reserved != 0) {
            return error.UnknownActionBits;
        }
        try self.registrations.put(registration.resource_key, registration);
    }

    pub fn unregister(self: *AdapterResourceActionDispatcher, resource_key: u64) bool {
        return self.registrations.remove(resource_key);
    }

    pub fn execute(self: *const AdapterResourceActionDispatcher, request: AdapterResourceActionRequest) AdapterResourceActionResult {
        if (request.resource_key == 0 or !isSingleKnownAction(request.action)) {
            return actionErrorResult(request, .invalid_request, 0);
        }
        const registration = self.registrations.get(request.resource_key) orelse {
            return actionErrorResult(request, .resource_not_found, 0);
        };
        if ((actionMaskValue(registration.supported_actions) & actionMaskValue(request.action)) == 0) {
            return actionErrorResult(request, .action_not_supported, 0);
        }
        return registration.handler(registration.context, request);
    }
};

pub const TieredResourceEntry = extern struct {
    resource_key: u64,
    resource_id: u32,
    size_bytes: u64,
    tier: AdapterResourceTier,
    resource_kind: AdapterResourceKind,
    recovery_kind: AdapterResourceRecoveryKind,
    granularity: AdapterResourceGranularity,
    inapplicable_actions: AdapterResourceActionMask,
    action_route: AdapterResourceActionRoute,
    activity_score: u8,
    frontend_demand_mask: AdapterResourceDemandMask,
};

pub fn adapterResourceKey(stable_id: []const u8) !u64 {
    if (std.mem.trim(u8, stable_id, " \t\r\n").len == 0) {
        return error.EmptyStableId;
    }

    var hash: u64 = 14695981039346656037;
    for (stable_id) |byte| {
        hash ^= byte;
        hash *%= 1099511628211;
    }
    return if (hash == 0) 14695981039346656037 else hash;
}

pub fn validateTieredResourceEntry(entry: TieredResourceEntry) !void {
    if (entry.resource_key == 0) return error.MissingResourceKey;
    if (entry.resource_id == 0) return error.MissingResourceId;
    if (entry.inapplicable_actions.reserved != 0) return error.UnknownActionBits;
    if (entry.frontend_demand_mask.reserved != 0) return error.UnknownFrontendDemandBits;
    if (@intFromEnum(entry.action_route) > @intFromEnum(AdapterResourceActionRoute.adapter_handler)) return error.UnknownActionRoute;
}

fn actionMaskValue(action: AdapterResourceActionMask) u8 {
    return @as(u8, @bitCast(action));
}

fn isSingleKnownAction(action: AdapterResourceActionMask) bool {
    const value = actionMaskValue(action);
    return value != 0 and (value & (value - 1)) == 0 and action.reserved == 0;
}
