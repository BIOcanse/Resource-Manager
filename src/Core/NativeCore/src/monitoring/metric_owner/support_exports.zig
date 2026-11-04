//! Independent support ABI. Parent anchors these exports; no product switch.
const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const history = @import("history.zig");
const checkpoint = @import("persistence.zig");
const vector = @import("vector_inputs.zig");
fn ownerOf(pointer: ?*st.Owner) ?*st.Owner {
    const o = pointer orelse return null;
    return if (o.magic == st.Owner.live_magic) o else null;
}
fn done(o: ?*st.Owner, code: p.Code, f: ?*p.Failure, required: u64, limit: u64) u32 {
    if (f) |out| out.* = if (code == .ok) .{} else if (o) |owner| if (owner.failure.code == code) owner.failure else .{ .code = code, .required = required, .limit = limit } else .{ .code = code, .required = required, .limit = limit };
    return @intFromEnum(code);
}
fn failure(o: *st.Owner, err: anyerror, f: ?*p.Failure, required: u64, limit: u64) u32 {
    const code: p.Code = switch (err) {
        error.Capacity, error.GenerationCapacity => .capacity_exceeded,
        error.Workspace => .workspace_unavailable,
        error.Shared => .shared_section,
        error.Stale => .stale_identity,
        else => .invalid_contract,
    };
    _ = o.fail(code, if (err == error.Workspace) .work else if (err == error.GenerationCapacity) .generation else if (err == error.Capacity) .payload else .binding, if (err == error.GenerationCapacity) 0 else required, if (err == error.GenerationCapacity) 0 else limit);
    return done(o, code, f, required, limit);
}
fn bytes(pointer: ?[*]u8, length: u64) ?[]align(8) u8 {
    const ptr = pointer orelse return if (length == 0) @as([]align(8) u8, &.{}) else null;
    if (@intFromPtr(ptr) % 8 != 0 or length > std.math.maxInt(usize)) return null;
    return @as([*]align(8) u8, @alignCast(ptr))[0..@intCast(length)];
}
pub export fn rm_metric_owner_history_work_size(requirements: u32, samples: u32) u64 {
    return history.configureWorkSize(requirements, samples);
}
pub export fn rm_metric_owner_history_mixed_work_size(requirements: u32, samples: u32, text_bytes: u32) u64 {
    return history.mixedWorkSize(requirements, samples, text_bytes);
}
pub export fn rm_metric_owner_history_configure_mixed(owner: ?*st.Owner, binding: ?*const history.MixedBinding, requirements: ?[*]const history.MixedRequirement, count: u32, text: ?*const vector.Text, work: ?[*]u8, length: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    var accepted = false;
    defer if (!accepted) o.detach();
    o.failure = .{};
    if (length > o.config.work_limit) return done(o, o.fail(.capacity_exceeded, .work, length, o.config.work_limit), f, length, o.config.work_limit);
    const b = binding orelse return done(null, .invalid_contract, f, 0, 0);
    const names = text orelse return done(null, .invalid_contract, f, 0, 0);
    if (count != 0 and requirements == null) return done(null, .invalid_contract, f, 0, 0);
    const scratch = bytes(work, length) orelse return done(null, .invalid_contract, f, 0, 0);
    history.configureMixed(o, b.*, if (requirements) |ptr| ptr[0..count] else &.{}, names.*, scratch, utc) catch |err| {
        if (o.failure.code == .capacity_exceeded or o.failure.code == .workspace_unavailable) return done(o, o.failure.code, f, o.failure.required, o.failure.limit);
        return failure(o, err, f, 0, length);
    };
    accepted = true;
    return done(o, .ok, f, 0, 0);
}
pub export fn rm_metric_owner_history_commit_mixed(owner: ?*st.Owner, bindings: ?[*]const history.Binding, binding_count: u32, mixed: ?[*]const history.MixedBinding, mixed_count: u32, completed: ?[*]const u32, count: u32, token: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    defer o.detach();
    if (binding_count > o.result_count or mixed_count > o.result_count or binding_count + mixed_count > o.result_count or (binding_count != 0 and bindings == null) or (mixed_count != 0 and mixed == null) or count > o.metric_count or (count != 0 and completed == null)) return done(null, .invalid_contract, f, 0, 0);
    return done(o, history.commitMixed(o, if (bindings) |ptr| ptr[0..binding_count] else &.{}, if (mixed) |ptr| ptr[0..mixed_count] else &.{}, if (completed) |ptr| ptr[0..count] else &.{}, token, utc), f, 0, 0);
}
pub const GpuHistoryLocation = extern struct {
    source_id: u64 = 0,
    identity: u64 = 0,
    column_identity_sequence: u64 = 0,
    row: u32 = 0,
    public_status: u32 = 0,
    attempt_ms: i64 = 0,
    numeric_bits: u64 = 0,
};
pub export fn rm_metric_owner_history_gpu_location(owner: ?*st.Owner, text: ?*const vector.Text, address: vector.Ref, out: ?*GpuHistoryLocation, f: ?*p.Failure) u32 {
    if (out) |target| target.* = .{};
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    // Read-only metadata lookup must not abort an in-flight native plan.
    const target = out orelse return done(null, .invalid_contract, f, 0, 0);
    const names = text orelse return done(null, .invalid_contract, f, 0, 0);
    if (names.reserved != 0 or names.bytes == null or address.length == 0 or @as(u64, address.offset) + address.length > names.length) return done(null, .invalid_contract, f, 0, 0);
    const reader = vector.HistoryReader.init(o) catch |err| return failure(o, err, f, 0, 0);
    const row = (reader.find(names.bytes.?[address.offset..][0..address.length], 0) catch |err| return failure(o, err, f, 0, 0)) orelse return done(o, o.fail(.stale_identity, .binding, 0, 0), f, 0, 0);
    const value = reader.value(row);
    target.* = .{ .source_id = reader.table.binding.source_id, .identity = reader.identity(row), .column_identity_sequence = reader.identitySequence(), .row = row, .public_status = value.public_status, .attempt_ms = value.attempt, .numeric_bits = value.bits };
    return done(o, .ok, f, 0, 0);
}
pub export fn rm_metric_owner_history_gpu_prepared_location(owner: ?*st.Owner, token: u64, text: ?*const vector.Text, address: vector.Ref, out: ?*GpuHistoryLocation, f: ?*p.Failure) u32 {
    if (out) |target| target.* = .{};
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    var accepted = false;
    defer if (!accepted) o.detach();
    const target = out orelse return done(null, .invalid_contract, f, 0, 0);
    const names = text orelse return done(null, .invalid_contract, f, 0, 0);
    if (names.reserved != 0 or names.bytes == null or address.length == 0 or @as(u64, address.offset) + address.length > names.length) return done(null, .invalid_contract, f, 0, 0);
    const definition = (vector.historyCandidate(o, token, names.bytes.?[address.offset..][0..address.length], 0) catch |err| return failure(o, err, f, 0, 0)) orelse return done(o, o.fail(.stale_identity, .binding, 0, 0), f, 0, 0);
    // Sequence/status/value remain ZERO: this is a prepared definition, never
    // a fabricated published column sequence or numeric sample.
    target.* = .{ .source_id = definition.source_id, .identity = definition.identity, .row = definition.row };
    accepted = true;
    return done(o, .ok, f, 0, 0);
}
pub export fn rm_metric_owner_history_configure(owner: ?*st.Owner, binding: ?*const history.Binding, requirements: ?[*]const history.Requirement, count: u32, work: ?[*]u8, length: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    o.detach();
    defer o.detach();
    if (length > o.config.work_limit) return done(o, o.fail(.capacity_exceeded, .work, length, o.config.work_limit), f, length, o.config.work_limit);
    const b = binding orelse return done(null, .invalid_contract, f, 0, 0);
    if (count != 0 and requirements == null) return done(null, .invalid_contract, f, 0, 0);
    const scratch = bytes(work, length) orelse return done(null, .invalid_contract, f, 0, 0);
    history.configure(o, b.*, if (requirements) |ptr| ptr[0..count] else &.{}, scratch, utc) catch |err| return failure(o, err, f, 0, length);
    return done(o, .ok, f, 0, 0);
}
pub export fn rm_metric_owner_history_commit(owner: ?*st.Owner, binding: ?*const history.Binding, completed: ?[*]const u32, count: u32, token: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    defer o.detach();
    const b = binding orelse return done(null, .invalid_contract, f, 0, 0);
    if (count > o.metric_count or (count != 0 and completed == null)) return done(null, .invalid_contract, f, count, o.metric_count);
    return done(o, history.commit(o, b.*, if (completed) |ptr| ptr[0..count] else &.{}, token, utc), f, 0, 0);
}
pub export fn rm_metric_owner_history_commit_grouped(owner: ?*st.Owner, bindings: ?[*]const history.Binding, binding_count: u32, completed: ?[*]const u32, count: u32, token: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    defer o.detach();
    if (binding_count > o.result_count or (binding_count != 0 and bindings == null) or count > o.metric_count or (count != 0 and completed == null)) return done(null, .invalid_contract, f, 0, 0);
    return done(o, history.commitMany(o, if (bindings) |ptr| ptr[0..binding_count] else &.{}, if (completed) |ptr| ptr[0..count] else &.{}, token, utc), f, 0, 0);
}
pub export fn rm_metric_owner_checkpoint_size(owner: ?*st.Owner, image_size: ?*u64, work_size: ?*u64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    defer o.detach();
    const image = image_size orelse return done(null, .invalid_contract, f, 0, 0);
    const work = work_size orelse return done(null, .invalid_contract, f, 0, 0);
    image.* = 0;
    work.* = 0;
    image.* = checkpoint.encodedSize(o) catch |err| return failure(o, err, f, 0, 0);
    work.* = checkpoint.restoreWorkSize(o, image.*);
    return done(o, .ok, f, 0, 0);
}
pub export fn rm_metric_owner_checkpoint_encode(owner: ?*st.Owner, destination: ?[*]u8, capacity: u64, written: ?*u64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    defer o.detach();
    const target = written orelse return done(null, .invalid_contract, f, 0, 0);
    target.* = 0;
    const ptr = destination orelse return done(null, .invalid_contract, f, 0, 0);
    if (capacity > std.math.maxInt(usize)) return done(null, .invalid_contract, f, 0, 0);
    const required = checkpoint.encodedSize(o) catch |err| return failure(o, err, f, 0, capacity);
    target.* = checkpoint.encode(o, ptr[0..@intCast(capacity)]) catch |err| return failure(o, err, f, required, capacity);
    return done(o, .ok, f, 0, 0);
}
pub export fn rm_metric_owner_checkpoint_prepare(owner: ?*st.Owner, image: ?[*]const u8, image_length: u64, work: ?[*]u8, work_length: u64, out: ?*checkpoint.Prepared, f: ?*p.Failure) u32 {
    if (out) |target| target.* = .{};
    const o = ownerOf(owner) orelse return done(null, .closed, f, 0, 0);
    o.detach();
    defer o.detach();
    const target = out orelse return done(null, .invalid_contract, f, 0, 0);
    target.* = .{};
    const input = image orelse return done(null, .invalid_contract, f, 0, 0);
    if (image_length > std.math.maxInt(usize)) return done(null, .invalid_contract, f, 0, 0);
    const scratch = bytes(work, work_length) orelse return done(null, .invalid_contract, f, 0, 0);
    if (work_length > o.config.work_limit) return done(o, o.fail(.capacity_exceeded, .work, work_length, o.config.work_limit), f, work_length, o.config.work_limit);
    checkpoint.prepare(o, input[0..@intCast(image_length)], scratch, target) catch |err| return failure(o, err, f, checkpoint.restoreWorkSize(o, image_length), work_length);
    return done(o, .ok, f, 0, 0);
}
pub export fn rm_metric_owner_checkpoint_publish(owner: ?*st.Owner, prepared: ?*checkpoint.Prepared, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse {
        if (prepared) |target| target.* = .{};
        return done(null, .closed, f, 0, 0);
    };
    o.detach();
    defer o.detach();
    const q = prepared orelse return done(null, .invalid_contract, f, 0, 0);
    if (q.work_length > o.config.work_limit) {
        const supplied = q.work_length;
        q.* = .{};
        return done(o, o.fail(.capacity_exceeded, .work, supplied, o.config.work_limit), f, supplied, o.config.work_limit);
    }
    checkpoint.publish(o, q, utc) catch |err| return failure(o, err, f, 0, 0);
    return done(o, .ok, f, 0, 0);
}
pub fn exportAnchor() void {
    _ = &rm_metric_owner_history_work_size;
    _ = &rm_metric_owner_history_mixed_work_size;
    _ = &rm_metric_owner_history_configure_mixed;
    _ = &rm_metric_owner_history_commit_mixed;
    _ = &rm_metric_owner_history_gpu_location;
    _ = &rm_metric_owner_history_gpu_prepared_location;
    _ = &rm_metric_owner_history_configure;
    _ = &rm_metric_owner_history_commit;
    _ = &rm_metric_owner_history_commit_grouped;
    _ = &rm_metric_owner_checkpoint_size;
    _ = &rm_metric_owner_checkpoint_encode;
    _ = &rm_metric_owner_checkpoint_prepare;
    _ = &rm_metric_owner_checkpoint_publish;
}
comptime {
    std.debug.assert(@sizeOf(GpuHistoryLocation) == 48);
}
test {
    _ = @import("support_tests.zig");
}
