//! A process's fixed control tree and its compile-time checks.
const d = @import("declarations.zig");
const types = @import("types.zig");

pub const Block = struct {
    uid: d.BlockUid,
    parent: ?d.BlockUid,
    inputs: []const d.BlockUid = &.{},
    /// Development export metadata; the controller never reads this field.
    launches_process: ?[]const u8 = null,
};
pub const PerBlockUse = struct {
    node: d.BlockUid,
    condition: d.ConditionUid,
    child: d.BlockUid,
};
pub const Compute = types.Compute;
pub const Tree = struct {
    program: d.BlockUid,
    blocks: []const Block,
    uses: []const PerBlockUse,
    computes: []const Compute,
};

pub fn blockIndex(registry: d.Registry, uid: d.BlockUid) ?usize {
    for (registry.blocks, 0..) |block, index| if (block.uid == uid) return index;
    return null;
}
pub fn itemIndex(registry: d.Registry, uid: d.ItemUid) ?usize {
    for (registry.items, 0..) |item, index| if (item.uid == uid) return index;
    return null;
}
pub fn conditionIndex(registry: d.Registry, uid: d.ConditionUid) ?usize {
    for (registry.conditions, 0..) |condition, index| if (condition.uid == uid) return index;
    return null;
}
pub fn treeBlockIndex(tree: Tree, uid: d.BlockUid) ?usize {
    for (tree.blocks, 0..) |block, index| if (block.uid == uid) return index;
    return null;
}
pub fn isChild(tree: Tree, parent: d.BlockUid, child: d.BlockUid) bool {
    const index = treeBlockIndex(tree, child) orelse return false;
    return tree.blocks[index].parent == parent;
}
pub fn conditionCount(registry: d.Registry, tree: Tree, node: d.BlockUid) usize {
    var count: usize = 0;
    for (registry.conditions) |condition| if (!condition.per_block and condition.node == node) {
        count += 1;
    };
    for (tree.uses) |use| if (use.node == node) {
        count += 1;
    };
    return count;
}
pub fn instanceCount(registry: d.Registry, tree: Tree) usize {
    var count = tree.uses.len;
    for (registry.conditions) |condition| if (!condition.per_block) {
        count += 1;
    };
    return count;
}

/// Validation uses bounded loops and Kahn's queue-free topological scan. There
/// is no recursive traversal, including during compile-time evaluation.
pub fn validate(comptime registry: d.Registry, comptime tree: Tree) void {
    @setEvalBranchQuota(10_000_000);
    if (registry.blocks.len == 0 or tree.blocks.len != registry.blocks.len)
        @compileError("control tree must contain every registered block exactly once");
    for (registry.blocks, 0..) |block, index| {
        if (block.uid == 0) @compileError("registered block UID must be positive");
        for (registry.blocks[0..index]) |earlier| if (earlier.uid == block.uid)
            @compileError("registered block UID must be unique");
        var count: usize = 0;
        for (tree.blocks) |entry| if (entry.uid == block.uid) {
            count += 1;
        };
        if (count != 1) @compileError("control tree must contain every registered block exactly once");
    }
    var roots: usize = 0;
    for (tree.blocks) |block| {
        if (blockIndex(registry, block.uid) == null)
            @compileError("control tree contains an unregistered block");
        if (block.parent) |parent| {
            if (treeBlockIndex(tree, parent) == null)
                @compileError("control tree parent is not a registered block");
        } else {
            roots += 1;
            if (block.uid != tree.program) @compileError("control tree root must be the program block");
        }
        for (block.inputs) |input| if (blockIndex(registry, input) == null)
            @compileError("control tree input is not a registered block");
    }
    if (roots != 1 or treeBlockIndex(tree, tree.program) == null)
        @compileError("control tree must have exactly one program root");
    var reached = [_]bool{false} ** tree.blocks.len;
    reached[treeBlockIndex(tree, tree.program).?] = true;
    var reached_count: usize = 1;
    for (0..tree.blocks.len) |_| {
        for (tree.blocks, 0..) |block, index| {
            if (reached[index]) continue;
            if (reached[treeBlockIndex(tree, block.parent.?).?]) {
                reached[index] = true;
                reached_count += 1;
            }
        }
    }
    if (reached_count != tree.blocks.len) @compileError("control tree parents contain a cycle");

    for (registry.items, 0..) |item, index| {
        if (item.uid == 0 or blockIndex(registry, item.uid) != null)
            @compileError("registry UIDs must be positive and unique across kinds");
        for (registry.items[0..index]) |earlier| if (earlier.uid == item.uid)
            @compileError("registry UIDs must be positive and unique across kinds");
        if (item.owner_block) |owner| if (blockIndex(registry, owner) == null)
            @compileError("item owner is not a registered block");
    }
    for (registry.conditions, 0..) |condition, index| {
        if (condition.uid == 0 or blockIndex(registry, condition.uid) != null or itemIndex(registry, condition.uid) != null)
            @compileError("registry UIDs must be positive and unique across kinds");
        for (registry.conditions[0..index]) |earlier| if (earlier.uid == condition.uid)
            @compileError("registry UIDs must be positive and unique across kinds");
        if (condition.per_block) {
            if (condition.node != null or condition.decides != null)
                @compileError("per-block condition cannot have a fixed node or child");
        } else {
            if (condition.node == null or blockIndex(registry, condition.node.?) == null)
                @compileError("condition owning node is not a registered block");
            if (condition.decides) |child| if (blockIndex(registry, child) == null)
                @compileError("top condition must decide a registered block");
        }
        for (condition.items) |read| {
            const item_index = itemIndex(registry, read.item) orelse
                @compileError("condition reads an unregistered item");
            const item = registry.items[item_index];
            if (item.value_type == .host)
                @compileError("conditions cannot read host-valued items");
            if (!item.per_block and read.block != null)
                @compileError("non-per-block item read cannot name a block");
            if (item.per_block and read.block == null and !condition.per_block)
                @compileError("per-block item read must name a block");
            if (read.block) |block| if (blockIndex(registry, block) == null)
                @compileError("item read names an unregistered block");
        }
        for (condition.conditions) |lower| {
            const lower_index = conditionIndex(registry, lower) orelse
                @compileError("condition reads an unregistered condition");
            if (registry.conditions[lower_index].per_block)
                @compileError("lower condition must have a fixed owning node");
            if (!condition.per_block and registry.conditions[lower_index].node != condition.node)
                @compileError("condition reads a lower condition from another node");
        }
        var compute_count: usize = 0;
        for (tree.computes) |compute| if (compute.condition == condition.uid) {
            compute_count += 1;
        };
        if (compute_count != 1) @compileError("every condition must have exactly one compute function");
    }
    for (tree.computes) |compute| if (conditionIndex(registry, compute.condition) == null)
        @compileError("compute function names an unregistered condition");
    for (tree.uses, 0..) |use, index| {
        const condition_index = conditionIndex(registry, use.condition) orelse
            @compileError("per-block use names an unregistered condition");
        if (!registry.conditions[condition_index].per_block)
            @compileError("per-block use requires a per-block condition");
        if (!isChild(tree, use.node, use.child))
            @compileError("top condition must decide a direct child of its node");
        for (tree.uses[0..index]) |earlier| if (earlier.node == use.node and earlier.condition == use.condition and earlier.child == use.child)
            @compileError("per-block condition instance must be unique");
    }
    for (tree.blocks) |node| {
        if (conditionCount(registry, tree, node.uid) > 64)
            @compileError("decision node has more than 64 condition instances");
        for (registry.conditions) |condition| {
            if (condition.per_block or condition.node != node.uid) continue;
            if (condition.decides) |child| if (!isChild(tree, node.uid, child))
                @compileError("top condition must decide a direct child of its node");
            for (condition.children) |child| if (!isChild(tree, node.uid, child))
                @compileError("condition state read must name a direct child");
        }
        for (tree.uses) |use| {
            if (use.node != node.uid) continue;
            const condition = registry.conditions[conditionIndex(registry, use.condition).?];
            for (condition.children) |child| if (!isChild(tree, node.uid, child))
                @compileError("condition state read must name a direct child");
            for (condition.conditions) |lower| if (registry.conditions[conditionIndex(registry, lower).?].node != node.uid)
                @compileError("condition reads a lower condition from another node");
        }
        for (tree.blocks) |child| {
            if (child.parent != node.uid) continue;
            var top_count: usize = 0;
            for (registry.conditions) |condition| if (!condition.per_block and condition.node == node.uid and condition.decides == child.uid) {
                top_count += 1;
            };
            for (tree.uses) |use| if (use.node == node.uid and use.child == child.uid) {
                top_count += 1;
            };
            if (top_count != 1) @compileError("every direct child must have exactly one top condition");
        }
        // Fixed lower conditions are the only possible cycle participants:
        // per-block instances are always tops and cannot be read as lowers.
        var ordered = [_]bool{false} ** registry.conditions.len;
        var own_count: usize = 0;
        var ordered_count: usize = 0;
        for (registry.conditions) |condition| if (!condition.per_block and condition.node == node.uid) {
            own_count += 1;
        };
        for (0..own_count) |_| {
            for (registry.conditions, 0..) |condition, index| {
                if (ordered[index] or condition.per_block or condition.node != node.uid) continue;
                var ready = true;
                for (condition.conditions) |lower| if (!ordered[conditionIndex(registry, lower).?]) {
                    ready = false;
                };
                if (ready) {
                    ordered[index] = true;
                    ordered_count += 1;
                }
            }
        }
        if (ordered_count != own_count) @compileError("decision node conditions contain a cycle");
    }
}
