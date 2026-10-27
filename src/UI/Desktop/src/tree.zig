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
        .{ .uid = B.MainRoot, .parent = B.Program },
        .{ .uid = B.TrayRoot, .parent = B.Program },
        .{ .uid = B.SingleInstance, .parent = B.MainRoot },
        .{ .uid = B.ForceTerminateHotkey, .parent = B.MainRoot },
        .{ .uid = B.TaskManagerReplacement, .parent = B.MainRoot },
        .{ .uid = B.BackendConnection, .parent = B.MainRoot },
        .{ .uid = B.WindowProcess, .parent = B.MainRoot },
        .{ .uid = B.DesktopObservation, .parent = B.BackendConnection, .inputs = &.{B.BackendConnection} },
        .{ .uid = B.Overlay, .parent = B.BackendConnection, .inputs = &.{B.BackendConnection} },
        .{ .uid = B.TrayNotifications, .parent = B.TrayRoot, .inputs = &.{B.BackendConnection} },
    },
    .uses = &.{
        .{ .node = B.MainRoot, .condition = C.AlwaysWanted, .child = B.SingleInstance },
        .{ .node = B.MainRoot, .condition = C.AlwaysWanted, .child = B.BackendConnection },
    },
    .computes = &.{
        .{ .condition = C.ProgramActive, .function = programActive },
        .{ .condition = C.MainTreeWanted, .function = programTreeWanted },
        .{ .condition = C.TrayTreeWanted, .function = programTreeWanted },
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
    // Both reads are explicit even when one operand settles the result.
    const started = try read(input, I.ProgramStarted);
    const ending = try read(input, I.ProgramShuttingDown);
    return started and !ending;
}
fn programTreeWanted(input: *Input) anyerror!bool {
    return input.condition(C.ProgramActive);
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

test "resident conditions require ready sessions, explicit switches and valid keys" {
    const std = @import("std");
    const Fixture = struct {
        values: [registry.items.len]control.types.Read = [_]control.types.Read{.not_published} ** registry.items.len,
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
    try std.testing.expect(!try programActive(&input));
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
