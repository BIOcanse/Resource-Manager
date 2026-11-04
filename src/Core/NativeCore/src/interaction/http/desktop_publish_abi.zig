const publisher = @import("desktop_publish.zig");

pub export fn rm_desktop_publisher_begin(state: *publisher.State, verified: u32, session_id: i32, admitted: u32, rejection_status: i32, context: ?*anyopaque, callbacks: *const publisher.Callbacks) callconv(.c) i32 {
    return publisher.begin(state, verified != 0, session_id, admitted != 0, rejection_status, context, callbacks);
}

pub export fn rm_desktop_publisher_read(state: *publisher.State, options: *const publisher.Options, length: u64, completed: u32, canceled: u32, context: ?*anyopaque, callbacks: *const publisher.Callbacks, consumed: *u64) callconv(.c) i32 {
    return publisher.read(state, options, length, completed != 0, canceled != 0, context, callbacks, consumed);
}

pub export fn rm_desktop_publisher_end(state: *publisher.State, context: ?*anyopaque, callbacks: *const publisher.Callbacks) callconv(.c) i32 {
    return publisher.end(state, context, callbacks);
}
