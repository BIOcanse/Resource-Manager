// One synchronous launch. The caller owns service facts, identity verification,
// system actions and their failure details; this module retains none of them.
pub const ServiceState = extern struct {
    present: u8,
    running: u8,
};

pub const CallbackStatus = enum(u32) {
    success = 0,
    failure = 1,
};

pub const Result = enum(u32) {
    success = 0,
    callback_failure = 1,
    service_missing = 2,
    service_not_running = 3,
    invalid_arguments = 4,
};

pub const Callbacks = extern struct {
    context: ?*anyopaque,
    // Each read verifies the original service identity before exposing flags.
    // No managed record, path or error message is copied into this module.
    read_verified_service: *const fn (?*anyopaque, *ServiceState) callconv(.c) u32,
    start_service: *const fn (?*anyopaque) callconv(.c) u32,
    elevate_service: *const fn (?*anyopaque) callconv(.c) u32,
    launch_interface: *const fn (?*anyopaque) callconv(.c) u32,
};

pub fn start(elevated: bool, restart: bool, callbacks: *const Callbacks) Result {
    var state: ServiceState = undefined;
    if (callbacks.read_verified_service(callbacks.context, &state) != @backingInt(CallbackStatus.success)) return .callback_failure;
    if (!valid(state)) return .invalid_arguments;
    if (state.present == 0 or state.running == 0 or restart) {
        const status = if (elevated) callbacks.start_service(callbacks.context) else callbacks.elevate_service(callbacks.context);
        if (status != @backingInt(CallbackStatus.success)) return .callback_failure;
    }
    if (callbacks.read_verified_service(callbacks.context, &state) != @backingInt(CallbackStatus.success)) return .callback_failure;
    if (!valid(state)) return .invalid_arguments;
    if (state.present == 0) return .service_missing;
    if (state.running == 0) return .service_not_running;
    if (callbacks.launch_interface(callbacks.context) != @backingInt(CallbackStatus.success)) return .callback_failure;
    return .success;
}

fn valid(state: ServiceState) bool {
    return state.present <= 1 and state.running <= 1 and (state.present != 0 or state.running == 0);
}
