//! Service binding only: select one original owner call and interpret its
//! nullable result / delete boolean. No HTTP, settings, authorization or state.
pub const contract = @import("../contracts/crash_reports.zig");

pub fn visitResult(result: ?*const contract.ViewResult, context: ?*anyopaque, sink: ?*const contract.view.Sink) i32 {
    const input = result orelse return contract.Code.invalid;
    if (@backingInt(input.operation) > @backingInt(contract.Operation.delete) or @backingInt(input.outcome) > @backingInt(contract.Outcome.not_found)) return contract.Code.invalid;
    if (input.outcome == .not_found) return contract.Code.success;
    return contract.view.visit(@backingInt(input.operation), input.source, context, sink);
}

pub fn invoke(operation: u32, target: ?[*]const u16, target_length: usize, context: ?*anyopaque, owner: ?*const contract.Owner, result: ?*contract.Result) i32 {
    const output = result orelse return contract.Code.invalid;
    // A failed call cannot expose a previous borrowed result.
    output.* = .{ .operation = .read, .outcome = .not_found, .value = null };
    if (operation > @backingInt(contract.Operation.delete)) return contract.Code.invalid;
    if (target == null and target_length != 0) return contract.Code.invalid;
    const callbacks = owner orelse return contract.Code.invalid;
    const selected: contract.Operation = @fromBackingInt(@intCast(operation));
    var value: ?*anyopaque = null;
    var deleted: u32 = 0;
    const code = switch (selected) {
        .read => callbacks.read(context, &value),
        .package => callbacks.package(context, target, target_length, &value),
        .acknowledge => callbacks.acknowledge(context, target, target_length, &value),
        .delete => callbacks.delete(context, target, target_length, &deleted),
    };
    if (code != 0) return contract.Code.callback;
    if (selected == .delete and deleted > 1) return contract.Code.invalid;
    output.* = .{
        .operation = selected,
        // Read is always a successful query, including an empty list. The
        // original non-nullable Read contract does not use null as not-found.
        .outcome = if (selected == .read or (if (selected == .delete) deleted != 0 else value != null)) .success else .not_found,
        .value = value,
    };
    return contract.Code.success;
}
