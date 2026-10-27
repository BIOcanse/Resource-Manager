const control = @import("rm_control");
const d = control.declarations;
const t = control.tree;
const types = control.types;

pub const Case = enum {
    unknown_parent,
    unknown_input,
    missing_block,
    duplicate_block,
    multiple_roots,
    parent_cycle,
    missing_top,
    duplicate_top,
    non_direct_top,
    non_block_top,
    over_limit,
    per_block_over_limit,
    condition_cycle,
    unknown_item,
    host_item_read,
    unknown_condition,
    missing_compute,
    duplicate_compute,
};
fn yes(_: *types.Input) anyerror!bool {
    return true;
}
const blocks = [_]d.Block{ .{ .uid = 1, .name = null }, .{ .uid = 2, .name = null } };
const tree_blocks = [_]t.Block{ .{ .uid = 1, .parent = null }, .{ .uid = 2, .parent = 1 } };
const top = d.Condition{ .uid = 10, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = 2, .name = null };
const compute = t.Compute{ .condition = 10, .function = yes };
const item = d.Item{ .uid = 3, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null };
fn manyConditions() [65]d.Condition {
    var result: [65]d.Condition = undefined;
    for (&result, 0..) |*entry, index| {
        entry.* = top;
        entry.uid = @intCast(10 + index);
        entry.decides = if (index == 0) 2 else null;
    }
    return result;
}
fn manyComputes() [65]t.Compute {
    var result: [65]t.Compute = undefined;
    for (&result, 0..) |*entry, index| {
        entry.* = compute;
        entry.condition = @intCast(10 + index);
    }
    return result;
}
fn manyBlocks() [66]d.Block {
    var result: [66]d.Block = undefined;
    for (&result, 0..) |*entry, index| entry.* = .{ .uid = @intCast(1 + index), .name = null };
    return result;
}
fn manyTreeBlocks() [66]t.Block {
    var result: [66]t.Block = undefined;
    for (&result, 0..) |*entry, index| entry.* = .{ .uid = @intCast(1 + index), .parent = if (index == 0) null else 1 };
    return result;
}
fn manyUses() [65]t.PerBlockUse {
    var result: [65]t.PerBlockUse = undefined;
    for (&result, 0..) |*entry, index| entry.* = .{ .node = 1, .condition = 100, .child = @intCast(2 + index) };
    return result;
}
pub fn check(comptime case: Case) void {
    const all_many_conditions = manyConditions();
    const all_many_computes = manyComputes();
    const all_many_blocks = manyBlocks();
    const all_many_tree_blocks = manyTreeBlocks();
    const all_many_uses = manyUses();
    var registry = d.Registry{ .version = "fixture", .process = "test", .blocks = &blocks, .items = &.{item}, .conditions = &.{top} };
    var tree = t.Tree{ .program = 1, .blocks = &tree_blocks, .uses = &.{}, .computes = &.{compute} };
    switch (case) {
        .unknown_parent => tree.blocks = &.{ tree_blocks[0], .{ .uid = 2, .parent = 99 } },
        .unknown_input => tree.blocks = &.{ tree_blocks[0], .{ .uid = 2, .parent = 1, .inputs = &.{99} } },
        .missing_block => tree.blocks = &.{tree_blocks[0]},
        .duplicate_block => tree.blocks = &.{ tree_blocks[0], tree_blocks[0] },
        .multiple_roots => tree.blocks = &.{ tree_blocks[0], .{ .uid = 2, .parent = null } },
        .parent_cycle => {
            registry.blocks = &.{ blocks[0], blocks[1], .{ .uid = 4, .name = null } };
            tree.blocks = &.{ tree_blocks[0], .{ .uid = 2, .parent = 4 }, .{ .uid = 4, .parent = 2 } };
        },
        .missing_top => {
            registry.conditions = &.{};
            tree.computes = &.{};
        },
        .duplicate_top => {
            var other = top;
            other.uid = 11;
            registry.conditions = &.{ top, other };
            tree.computes = &.{ compute, .{ .condition = 11, .function = yes } };
        },
        .non_direct_top => {
            var other = top;
            other.decides = 1;
            registry.conditions = &.{other};
        },
        .non_block_top => {
            var other = top;
            other.decides = 3;
            registry.conditions = &.{other};
        },
        .over_limit => {
            registry.conditions = &all_many_conditions;
            tree.computes = &all_many_computes;
        },
        .per_block_over_limit => {
            var template = top;
            template.uid = 100;
            template.node = null;
            template.per_block = true;
            template.decides = null;
            registry.items = &.{};
            registry.blocks = &all_many_blocks;
            registry.conditions = &.{template};
            tree.blocks = &all_many_tree_blocks;
            tree.uses = &all_many_uses;
            tree.computes = &.{.{ .condition = 100, .function = yes }};
        },
        .condition_cycle => {
            var first = top;
            first.conditions = &.{11};
            var second = top;
            second.uid = 11;
            second.decides = null;
            second.conditions = &.{10};
            registry.conditions = &.{ first, second };
            tree.computes = &.{ compute, .{ .condition = 11, .function = yes } };
        },
        .host_item_read => {
            var host_item = item;
            host_item.value_type = .host;
            registry.items = &.{host_item};
            var other = top;
            other.items = &.{.{ .item = host_item.uid, .block = null }};
            registry.conditions = &.{other};
        },
        .unknown_item => {
            var other = top;
            other.items = &.{.{ .item = 99, .block = null }};
            registry.conditions = &.{other};
        },
        .unknown_condition => {
            var other = top;
            other.conditions = &.{99};
            registry.conditions = &.{other};
        },
        .missing_compute => tree.computes = &.{},
        .duplicate_compute => tree.computes = &.{ compute, compute },
    }
    t.validate(registry, tree);
}

pub fn checkValidDataCycle() void {
    const extra = d.Block{ .uid = 4, .name = null };
    var second_top = top;
    second_top.uid = 11;
    second_top.decides = 4;
    const registry = d.Registry{ .version = "fixture", .process = "test", .blocks = &.{ blocks[0], blocks[1], extra }, .items = &.{item}, .conditions = &.{ top, second_top } };
    const tree = t.Tree{
        .program = 1,
        .blocks = &.{ tree_blocks[0], .{ .uid = 2, .parent = 1, .inputs = &.{4} }, .{ .uid = 4, .parent = 1, .inputs = &.{2} } },
        .uses = &.{},
        .computes = &.{ compute, .{ .condition = 11, .function = yes } },
    };
    t.validate(registry, tree);
}
