//! Pure projection over a scoped stable current read. No sampling, timeout,
//! grant, selection, current cache or process enumeration is owned here.
const m = @import("model.zig");

pub const Process = extern struct {
    process_id: i32,
    /// Actual verified session from the process-instance reader; -1 is unknown.
    session_id: i32,
    creation_file_time_utc: i64,
};

pub const RuntimeState = enum(u32) {
    unavailable = 0,
    foreground_focused = 1,
    foreground_unfocused = 2,
    background_window = 3,
    background_process = 4,
};

pub const Fact = extern struct {
    /// Kept separate: an incomplete desktop can verify a helper without
    /// yielding its own fact, as in the original InheritHelperStates path.
    verified: u32 = 0,
    available: u32 = 0,
    runtime_state: RuntimeState = .unavailable,
    foreground_focused: u32 = 0,
    has_visible_window: u32 = 0,
    has_background_window: u32 = 0,
};

// Windows/.NET FILETIME conversion bounds, not configurable timing policy.
const maximum_file_time: i64 = 2650467743999999999;
const file_time_epoch_ticks: i64 = 504911232000000000;

fn sameInstance(row: m.Window, process: Process) bool {
    return row.creation_file_time_utc == 0 or row.creation_file_time_utc == process.creation_file_time_utc;
}

/// frame is nullable for initial/no current/loss. R must validate a stable W
/// copy against T and recheck the full selection tuple before AND after this
/// synchronous read. A borrowed live W pointer is not a valid input.
pub fn current(process: Process, frame: ?*const m.Frame, windows: m.Windows, now_utc_ticks: i64) Fact {
    if (process.process_id <= 0 or process.creation_file_time_utc <= 0 or
        process.creation_file_time_utc > maximum_file_time or process.session_id < 0) return .{};
    if (process.session_id == 0) return .{
        .verified = 1,
        .available = 1,
        .runtime_state = .background_process,
    };
    const sample = frame orelse return .{};
    if (!windows.valid(sample.window_count) or sample.publisher.session_id != process.session_id or sample.foreground_known != 1 or
        process.creation_file_time_utc + file_time_epoch_ticks > now_utc_ticks) return .{};
    var found: ?m.Window = null;
    var i: u32 = 0;
    while (i < sample.window_count) : (i += 1) {
        const row = windows.at(i) orelse return .{};
        if (row.process_id == process.process_id) {
            found = row;
            break;
        }
    }
    const foreground = sample.foreground_present == 1 and sample.foreground.process_id == process.process_id;
    if (found) |row| if (!sameInstance(row, process)) return .{};
    if (foreground and !sameInstance(sample.foreground, process)) return .{};
    // Instance/session checks pass before the incomplete/no-row decision.
    if (sample.complete == 0 and !foreground and found == null) return .{ .verified = 1 };
    const visible = foreground or (if (found) |row| row.has_visible_window == 1 else false);
    const background = if (found) |row| row.has_background_window == 1 else false;
    return .{
        .verified = 1,
        .available = 1,
        .runtime_state = if (foreground) .foreground_focused else if (visible) .foreground_unfocused else if (background) .background_window else .background_process,
        .foreground_focused = @intFromBool(foreground),
        .has_visible_window = @intFromBool(visible),
        .has_background_window = @intFromBool(background),
    };
}
