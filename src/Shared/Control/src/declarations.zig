//! Declaration tables for one process: what the generator emits from
//! src/Shared/StateItems/state-items.json (step 5a) and what the controller
//! and a process's control tree file read (step 5b).
//!
//! This is the contract between steps 5a and 5b. Step 5b places this file at
//! src/Shared/Control/src/declarations.zig and exports it from the rm_control
//! module as `declarations`. Step 5a's generated files import it as
//! `@import("rm_control").declarations`. Change it only through Claude.

pub const BlockUid = u32;
pub const ItemUid = u32;
pub const ConditionUid = u32;

/// Own: the owner writes the value. Reference: the owner publishes into a
/// cell it binds while publishing and unbinds before it stops.
pub const Storage = enum { own, reference };

/// bool: the controller holds the value, and conditions may read it.
/// host: the value is an object of the host language (for example a settings
/// document) and stays with its owner there; the controller keeps only whether
/// the item has a value (or why not) and its events. Conditions cannot read it.
/// The generator maps registry type "bool" to bool and every other type to host.
pub const ValueType = enum { bool, host };

/// Event: the owner's change point wakes readers. Poll: the controller's timer
/// asks the owner to check at an interval the host passes (step 6).
pub const Trigger = enum { event, poll };

pub const Block = struct {
    uid: BlockUid,
    /// Present only in builds with names.
    name: ?[]const u8,
};

pub const Item = struct {
    uid: ItemUid,
    storage: Storage,
    value_type: ValueType,
    /// One value per block; the runtime key is item UID plus block UID.
    per_block: bool,
    trigger: Trigger,
    /// The owning block, or null when the owner is a component that is not a block.
    owner_block: ?BlockUid,
    name: ?[]const u8,
};

/// One item a condition reads. For a per-block item, `block` names the block;
/// inside a per-block condition, null means the block of this condition instance.
/// For an item that is not per-block, `block` is null.
pub const ItemRead = struct {
    item: ItemUid,
    block: ?BlockUid,
};

pub const Condition = struct {
    uid: ConditionUid,
    /// The block whose decision node owns this condition (the program root is a
    /// block too). Null for a per-block condition, whose uses the control tree file declares.
    node: ?BlockUid,
    per_block: bool,
    items: []const ItemRead,
    /// Lower conditions of the same node that this condition reads.
    conditions: []const ConditionUid,
    /// Direct children whose four state this condition reads.
    children: []const BlockUid,
    /// For a top condition ("should run"), the direct child it decides; null for
    /// a lower condition. A per-block condition is always a top condition: it
    /// decides the block of its instance and leaves this null.
    decides: ?BlockUid,
    name: ?[]const u8,
};

pub const Registry = struct {
    /// The registry version (hash of the canonical registry file).
    version: []const u8,
    /// The process these tables belong to, as in the registry ("backend", "ui").
    process: []const u8,
    blocks: []const Block,
    items: []const Item,
    conditions: []const Condition,
};
