//! Declaration-only, bounded, allocation-free development export.
const std = @import("std");
const d = @import("declarations.zig");
const t = @import("tree.zig");
const Writer = @import("json.zig").Writer;

pub const Profile = struct { name: []const u8, tree: t.Tree };
pub const Error = error{ DevelopmentTableRequired, ParentConflict, InvalidDeclaration };

/// No bytes are written on validation failure. diagnostic borrows a declaration
/// name (or a fixed diagnostic); the caller displays it alongside the error.
/// Like json.Writer, an empty buffer measures the exact output length.
pub fn write(comptime registry: d.Registry, process: []const u8, profiles: []const Profile, out: *Writer, diagnostic: *?[]const u8) Error!void {
    diagnostic.* = null;
    for (registry.blocks) |b| if (b.name == null) return fail(diagnostic, "export needs the development declaration table", error.DevelopmentTableRequired);
    for (registry.items) |i| if (i.name == null) return fail(diagnostic, "export needs the development declaration table", error.DevelopmentTableRequired);
    for (registry.conditions) |c| if (c.name == null) return fail(diagnostic, "export needs the development declaration table", error.DevelopmentTableRequired);
    if (process.len == 0 or profiles.len == 0) return fail(diagnostic, "process and named profiles are required", error.InvalidDeclaration);
    var merged: [registry.blocks.len]?t.Block = @splat(null);
    const program = profiles[0].tree.program;
    for (profiles, 0..) |profile, pi| {
        if (profile.name.len == 0 or profile.tree.program != program) return fail(diagnostic, profile.name, error.InvalidDeclaration);
        for (profiles[0..pi]) |earlier| if (std.mem.eql(u8, profile.name, earlier.name)) return fail(diagnostic, profile.name, error.InvalidDeclaration);
        if (profile.tree.blocks.len > registry.blocks.len or profile.tree.uses.len > registry.blocks.len) return fail(diagnostic, profile.name, error.InvalidDeclaration);
        for (profile.tree.blocks, 0..) |block, bi| {
            const index = t.blockIndex(registry, block.uid) orelse return fail(diagnostic, profile.name, error.InvalidDeclaration);
            const name = registry.blocks[index].name.?;
            for (profile.tree.blocks[0..bi]) |earlier| if (earlier.uid == block.uid) return fail(diagnostic, name, error.InvalidDeclaration);
            if (merged[index]) |earlier| {
                if (earlier.parent != block.parent) return fail(diagnostic, name, error.ParentConflict);
            } else merged[index] = block;
            if (block.parent) |parent| {
                if (block.uid == program or t.treeBlockIndex(profile.tree, parent) == null) return fail(diagnostic, name, error.InvalidDeclaration);
            } else if (block.uid != program) return fail(diagnostic, name, error.InvalidDeclaration);
            for (block.inputs) |input| if (t.blockIndex(registry, input) == null) return fail(diagnostic, name, error.InvalidDeclaration);
            if (block.launches_process) |launched| if (launched.len == 0) return fail(diagnostic, name, error.InvalidDeclaration);
            var top_count: usize = 0;
            for (registry.conditions) |condition| if (!condition.per_block and condition.node == block.parent and condition.decides == block.uid) {
                top_count += 1;
            };
            for (profile.tree.uses) |use| if (use.child == block.uid) {
                const ci = t.conditionIndex(registry, use.condition) orelse return fail(diagnostic, name, error.InvalidDeclaration);
                if (!registry.conditions[ci].per_block or block.parent != use.node) return fail(diagnostic, name, error.InvalidDeclaration);
                top_count += 1;
            };
            if (top_count != @as(usize, if (block.parent == null) 0 else 1)) return fail(diagnostic, name, error.InvalidDeclaration);
            var cursor = block.uid;
            var reached = false;
            for (0..registry.blocks.len) |_| {
                const ancestor = profile.tree.blocks[t.treeBlockIndex(profile.tree, cursor) orelse return fail(diagnostic, name, error.InvalidDeclaration)];
                if (ancestor.parent) |parent| cursor = parent else {
                    reached = true;
                    break;
                }
            }
            if (!reached) return fail(diagnostic, name, error.InvalidDeclaration);
        }
        if (t.treeBlockIndex(profile.tree, program) == null) return fail(diagnostic, profile.name, error.InvalidDeclaration);
        for (profile.tree.uses) |use| if (t.treeBlockIndex(profile.tree, use.child) == null) return fail(diagnostic, profile.name, error.InvalidDeclaration);
    }
    for (merged, 0..) |block, index| if (block == null)
        return fail(diagnostic, registry.blocks[index].name.?, error.InvalidDeclaration);
    // Validate all named references before serializing any output. Computes are
    // deliberately neither validated nor called: they are not export inputs.
    for (registry.conditions) |condition| {
        for (condition.items) |read| {
            if (t.itemIndex(registry, read.item) == null) return fail(diagnostic, condition.name.?, error.InvalidDeclaration);
            if (read.block) |block| if (t.blockIndex(registry, block) == null) return fail(diagnostic, condition.name.?, error.InvalidDeclaration);
        }
        for (condition.conditions) |uid| if (t.conditionIndex(registry, uid) == null) return fail(diagnostic, condition.name.?, error.InvalidDeclaration);
        for (condition.children) |uid| if (t.blockIndex(registry, uid) == null) return fail(diagnostic, condition.name.?, error.InvalidDeclaration);
    }
    out.raw("{\"schemaVersion\":1,\"process\":");
    out.string(process);
    out.raw(",\"registryVersion\":");
    out.string(registry.version);
    out.raw(",\"program\":");
    out.number(program);
    out.raw(",\"profiles\":[");
    for (profiles, 0..) |profile, i| {
        comma(out, i);
        out.string(profile.name);
    }
    out.raw("],\"conditions\":[");
    for (registry.conditions, 0..) |condition, i| {
        comma(out, i);
        writeCondition(registry, condition, null, out);
    }
    out.raw("],\"blocks\":[");
    var written: usize = 0;
    for (merged, 0..) |entry, index| {
        const block = entry orelse continue;
        comma(out, written);
        written += 1;
        named(out, block.uid, registry.blocks[index].name.?);
        out.raw(",\"parent\":");
        optionalUid(out, block.parent);
        out.raw(",\"children\":[");
        var children: usize = 0;
        for (merged) |child_entry| if (child_entry) |child| {
            if (child.parent == block.uid) {
                comma(out, children);
                children += 1;
                out.number(child.uid);
            }
        };
        out.raw("],\"profiles\":[");
        var appearances: usize = 0;
        for (profiles) |profile| if (t.treeBlockIndex(profile.tree, block.uid) != null) {
            comma(out, appearances);
            appearances += 1;
            out.string(profile.name);
        };
        out.raw("],\"declarations\":[");
        appearances = 0;
        for (profiles) |profile| {
            const bi = t.treeBlockIndex(profile.tree, block.uid) orelse continue;
            const declared = profile.tree.blocks[bi];
            comma(out, appearances);
            appearances += 1;
            out.raw("{\"profile\":");
            out.string(profile.name);
            out.raw(",\"inputs\":[");
            for (declared.inputs, 0..) |uid, i| {
                comma(out, i);
                named(out, uid, registry.blocks[t.blockIndex(registry, uid).?].name.?);
                out.raw("}");
            }
            out.raw("],\"decidingCondition\":");
            var deciding: ?d.Condition = null;
            for (registry.conditions) |c| if (!c.per_block and c.node == declared.parent and c.decides == declared.uid) {
                deciding = c;
            };
            for (profile.tree.uses) |use| if (use.child == declared.uid) {
                deciding = registry.conditions[t.conditionIndex(registry, use.condition).?];
            };
            if (deciding) |c| writeCondition(registry, c, declared.uid, out) else out.raw("null");
            out.raw(",\"launchesProcess\":");
            if (declared.launches_process) |launch| out.string(launch) else out.raw("null");
            out.raw("}");
        }
        out.raw("]}");
    }
    out.raw("]}\n");
}

fn fail(diagnostic: *?[]const u8, name: []const u8, err: Error) Error {
    diagnostic.* = name;
    return err;
}
fn comma(out: *Writer, index: usize) void {
    if (index != 0) out.raw(",");
}
fn named(out: *Writer, uid: u32, name: []const u8) void {
    out.raw("{\"uid\":");
    out.number(uid);
    out.raw(",\"name\":");
    out.string(name);
}
fn optionalUid(out: *Writer, uid: ?u32) void {
    if (uid) |value| out.number(value) else out.raw("null");
}
fn writeCondition(registry: d.Registry, condition: d.Condition, instance: ?u32, out: *Writer) void {
    named(out, condition.uid, condition.name.?);
    out.raw(",\"perBlock\":");
    out.boolean(condition.per_block);
    out.raw(",\"node\":");
    optionalUid(out, condition.node);
    out.raw(",\"decides\":");
    optionalUid(out, condition.decides);
    out.raw(",\"items\":[");
    for (condition.items, 0..) |read, i| {
        comma(out, i);
        const item = registry.items[t.itemIndex(registry, read.item).?];
        named(out, item.uid, item.name.?);
        const key = read.block orelse if (item.per_block) instance else null;
        out.raw(",\"block\":");
        if (key) |uid| {
            named(out, uid, registry.blocks[t.blockIndex(registry, uid).?].name.?);
            out.raw("}");
        } else out.raw("null");
        out.raw("}");
    }
    out.raw("],\"conditions\":[");
    for (condition.conditions, 0..) |uid, i| {
        comma(out, i);
        named(out, uid, registry.conditions[t.conditionIndex(registry, uid).?].name.?);
        out.raw("}");
    }
    out.raw("],\"children\":[");
    for (condition.children, 0..) |uid, i| {
        comma(out, i);
        named(out, uid, registry.blocks[t.blockIndex(registry, uid).?].name.?);
        out.raw("}");
    }
    out.raw("]}");
}

test {
    _ = @import("export_tree_tests.zig");
}
