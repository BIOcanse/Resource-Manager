//! Resident process control declarations. All UIDs come from the registry.
const control = @import("rm_control");
const generated = @import("desktop_state_items");
pub const registry = generated.registry;
pub const BlockUids = generated.BlockUids;
pub const ItemUids = generated.ItemUids;
pub const ConditionUids = generated.ConditionUids;
const B = BlockUids;
const I = ItemUids;
const C = ConditionUids;
const Input = control.types.Input;

pub const tree: control.tree.Tree = .{
    .program = B.Program,
    .blocks = &.{
        .{ .uid = B.Program, .parent = null },
        .{ .uid = B.Frontend, .parent = B.Program },
        .{ .uid = B.MainFrontend, .parent = B.Frontend },
        .{ .uid = B.Tray, .parent = B.Frontend },
        .{ .uid = B.SingleInstance, .parent = B.MainFrontend },
        .{ .uid = B.SystemIntegration, .parent = B.MainFrontend },
        .{ .uid = B.ForceTerminateHotkey, .parent = B.SystemIntegration },
        .{ .uid = B.TaskManagerReplacement, .parent = B.SystemIntegration },
        .{ .uid = B.BackendConnection, .parent = B.MainFrontend },
        .{ .uid = B.WindowProcess, .parent = B.MainFrontend, .launches_process = "window" },
        .{ .uid = B.DesktopObservation, .parent = B.BackendConnection, .inputs = &.{B.BackendConnection} },
        .{ .uid = B.Overlay, .parent = B.BackendConnection, .inputs = &.{B.BackendConnection} },
        .{ .uid = B.TrayNotifications, .parent = B.Tray, .inputs = &.{B.BackendConnection} },
    },
    .uses = &.{
        .{ .node = B.Frontend, .condition = C.AlwaysWanted, .child = B.MainFrontend },
        .{ .node = B.Frontend, .condition = C.AlwaysWanted, .child = B.Tray },
        .{ .node = B.MainFrontend, .condition = C.AlwaysWanted, .child = B.SingleInstance },
        .{ .node = B.MainFrontend, .condition = C.AlwaysWanted, .child = B.SystemIntegration },
        .{ .node = B.MainFrontend, .condition = C.AlwaysWanted, .child = B.BackendConnection },
    },
    .computes = &.{
        .{ .condition = C.ProgramActive, .function = programActive },
        .{ .condition = C.AlwaysWanted, .function = alwaysWanted },
        .{ .condition = C.ForceTerminateHotkeyWanted, .function = hotkeyWanted },
        .{ .condition = C.TaskManagerReplacementWanted, .function = taskManagerWanted },
        .{ .condition = C.WindowProcessWanted, .function = windowWanted },
        .{ .condition = C.DesktopObservationWanted, .function = desktopWanted },
        .{ .condition = C.OverlayWanted, .function = overlayWanted },
        .{ .condition = C.TrayNotificationsWanted, .function = notificationsWanted },
    },
};

pub const Engine = control.Controller(registry, tree);
/// The wrapper cannot report stopped until its complete branch has drained.
/// The native message loop and process scenarios share this exit guard.
pub fn mayExitMessageLoop(engine: *Engine, ending: bool) !bool {
    return ending and try engine.readState(B.Frontend) == .stopped;
}
pub const host_blocks = blk: {
    var result: [registry.blocks.len - 1]u32 = undefined;
    var index: usize = 0;
    for (registry.blocks) |block| {
        if (block.uid == B.Program) continue;
        result[index] = block.uid;
        index += 1;
    }
    break :blk result;
};

fn read(input: *Input, uid: u32) !bool {
    return (try input.read(.{ .item = uid })).valueOr(false);
}
fn programActive(input: *Input) anyerror!bool {
    // Shutdown is owned by Engine.shutdown(), which revokes the root permit.
    return read(input, I.ProgramStarted);
}
fn alwaysWanted(_: *Input) anyerror!bool {
    return true;
}
fn hotkeyWanted(input: *Input) anyerror!bool {
    const enabled = try read(input, I.ForceTerminateHotkeyEnabled);
    const valid = try read(input, I.ForceTerminateHotkeyValid);
    return enabled and valid;
}
fn taskManagerWanted(input: *Input) anyerror!bool {
    return read(input, I.TaskManagerReplacementEnabled);
}
fn windowWanted(input: *Input) anyerror!bool {
    return read(input, I.WindowWanted);
}
fn desktopWanted(input: *Input) anyerror!bool {
    return read(input, I.BackendSessionReady);
}
fn overlayWanted(input: *Input) anyerror!bool {
    const ready = try read(input, I.BackendSessionReady);
    const enabled = try read(input, I.OverlayEnabled);
    return ready and enabled;
}
fn notificationsWanted(input: *Input) anyerror!bool {
    const ready = try read(input, I.BackendSessionReady);
    const enabled = try read(input, I.NotificationsEnabled);
    return ready and enabled;
}

test "the resident tree covers every registered block and direct-child decision" {
    comptime control.tree.validate(registry, tree);
    try @import("std").testing.expectEqual(registry.blocks.len - 1, host_blocks.len);
    _ = Engine;
}

test "Program owns only Frontend and Frontend owns the two frontend branches" {
    const std = @import("std");
    var root_children: usize = 0;
    var frontend_children: usize = 0;
    for (tree.blocks) |block| {
        if (block.parent == B.Program) {
            root_children += 1;
            try std.testing.expectEqual(B.Frontend, block.uid);
        }
        if (block.parent == B.Frontend) {
            frontend_children += 1;
            try std.testing.expect(block.uid == B.MainFrontend or block.uid == B.Tray);
        }
    }
    try std.testing.expectEqual(@as(usize, 1), root_children);
    try std.testing.expectEqual(@as(usize, 2), frontend_children);
}

test "SystemIntegration owns exactly the two system integration blocks" {
    const std = @import("std");
    var children: usize = 0;
    for (tree.blocks) |block| {
        if (block.uid == B.SystemIntegration) try std.testing.expectEqual(B.MainFrontend, block.parent.?);
        if (block.parent != B.SystemIntegration) continue;
        children += 1;
        try std.testing.expect(block.uid == B.ForceTerminateHotkey or block.uid == B.TaskManagerReplacement);
    }
    try std.testing.expectEqual(@as(usize, 2), children);
}

test "resident conditions require ready sessions, explicit switches and valid keys" {
    const std = @import("std");
    const Fixture = struct {
        values: [registry.items.len]control.types.Read = @as([registry.items.len]control.types.Read, @splat(.not_published)),
        fn set(self: *@This(), uid: u32, value: control.types.Read) void {
            self.values[control.tree.itemIndex(registry, uid).?] = value;
        }
        fn readValue(context: *anyopaque, key: control.types.ItemKey) anyerror!control.types.Read {
            const self: *@This() = @ptrCast(@alignCast(context));
            return self.values[control.tree.itemIndex(registry, key.item).?];
        }
        fn noCondition(_: *anyopaque, _: u32) anyerror!bool {
            return error.UnexpectedConditionRead;
        }
        fn noChild(_: *anyopaque, _: u32) anyerror!control.types.BlockState {
            return error.UnexpectedChildRead;
        }
        fn input(self: *@This()) Input {
            return .{ .context = self, .block = null, .read_fn = readValue, .condition_fn = noCondition, .child_fn = noChild };
        }
    };
    var fixture: Fixture = .{};
    var input = fixture.input();
    try std.testing.expect(!try programActive(&input));
    try std.testing.expect(!try windowWanted(&input));
    try std.testing.expect(!try hotkeyWanted(&input));
    try std.testing.expect(!try taskManagerWanted(&input));
    try std.testing.expect(!try desktopWanted(&input));
    try std.testing.expect(!try overlayWanted(&input));
    try std.testing.expect(!try notificationsWanted(&input));
    fixture.set(I.ProgramStarted, .{ .value = true });
    try std.testing.expect(try programActive(&input));
    fixture.set(I.ProgramShuttingDown, .{ .value = true });
    try std.testing.expect(try programActive(&input));
    fixture.set(I.WindowWanted, .{ .value = true });
    try std.testing.expect(try windowWanted(&input));
    fixture.set(I.ForceTerminateHotkeyEnabled, .{ .value = true });
    try std.testing.expect(!try hotkeyWanted(&input));
    fixture.set(I.ForceTerminateHotkeyValid, .{ .value = true });
    try std.testing.expect(try hotkeyWanted(&input));
    fixture.set(I.TaskManagerReplacementEnabled, .{ .value = true });
    try std.testing.expect(try taskManagerWanted(&input));
    fixture.set(I.BackendSessionReady, .{ .value = true });
    try std.testing.expect(try desktopWanted(&input));
    try std.testing.expect(!try overlayWanted(&input));
    try std.testing.expect(!try notificationsWanted(&input));
    fixture.set(I.OverlayEnabled, .{ .value = true });
    fixture.set(I.NotificationsEnabled, .{ .value = true });
    try std.testing.expect(try overlayWanted(&input));
    try std.testing.expect(try notificationsWanted(&input));
    for ([_]control.types.Read{ .not_published, .owner_stopped, .read_failed, .{ .value = false } }) |absent| {
        fixture.set(I.BackendSessionReady, absent);
        try std.testing.expect(!try desktopWanted(&input));
        try std.testing.expect(!try overlayWanted(&input));
        try std.testing.expect(!try notificationsWanted(&input));
    }
}
