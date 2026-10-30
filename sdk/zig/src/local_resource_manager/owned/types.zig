//! Build declarations and typed results for the owned-memory ledger.
//! Policy values are supplied by the adopter's FreedomPoint projection.
const protocol = @import("../protocol.zig");
const release = @import("../release_check.zig");

pub const no_slot = @import("std").math.maxInt(u32);
pub const Mode = enum { unrestricted, normal, optimize };
pub const Protection = enum { concentrated, smooth };
pub const Attributes = struct {
    recoverability: protocol.Recoverability,
    recovery_cost_coefficient: u32,
    access_loss_impact: protocol.AccessLossImpact,
    pub fn safe(self: @This()) bool {
        return self.recoverability == .recoverable and self.access_loss_impact != .fatal;
    }
};
pub const AttributeUpdate = struct {
    recoverability: ?protocol.Recoverability = null,
    recovery_cost_coefficient: ?u32 = null,
    access_loss_impact: ?protocol.AccessLossImpact = null,
    pub fn apply(self: @This(), current: Attributes) Attributes {
        return .{ .recoverability = self.recoverability orelse current.recoverability, .recovery_cost_coefficient = self.recovery_cost_coefficient orelse current.recovery_cost_coefficient, .access_loss_impact = self.access_loss_impact orelse current.access_loss_impact };
    }
};
pub const Policy = struct {
    score_epochs: u8,
    idle_epochs: u8,
    protection: Protection,
    concentrated_trigger_percent: u8,
    concentrated_target_percent: u8,
    smooth_trigger_percent: u8,
    smooth_emergency_percent: u8,
    smooth_interval_epochs: u32,
    smooth_release_count: u32,
    tick_maximum_actions: u32,
    release_check: release.Policy,

    pub fn valid(self: @This(), width: u8) bool {
        if (self.score_epochs == 0 or self.score_epochs > width or self.idle_epochs > width or
            self.tick_maximum_actions == 0) return false;
        return switch (self.protection) {
            .concentrated => self.concentrated_trigger_percent < self.concentrated_target_percent and
                self.concentrated_target_percent <= 100,
            .smooth => self.smooth_emergency_percent < self.smooth_trigger_percent and
                self.smooth_trigger_percent <= 100 and self.smooth_interval_epochs != 0 and self.smooth_release_count != 0,
        };
    }
};
pub const Table = struct {
    capacity: u32,
    partition_capacity: u32,
    reserved_start: u32,
    reserved_count: u32,
    attributes: Attributes,
    policy: Policy,
};
pub const Config = struct {
    uid_width: u16,
    activity_width: u8,
    index_buckets: u32,
    development_allocation_statistics: bool = false,
    development_lease_analysis: bool = false,
    development_lease_analysis_capacity: u32 = 256,
    development_lease_analysis_write_watch_pages: u32 = 16384,
    use_window_cache: bool = false,
    use_window_cache_slots: u32 = 64,
};

pub const LeaseAnalysis = @import("lease_analysis.zig");
pub const LeaseAnalysisRecord = LeaseAnalysis.Record;
pub const LeaseAnalysisSummary = LeaseAnalysis.Summary;

pub fn For(comptime Uid: type) type {
    return struct {
        pub const Fixed = struct {
            table: u32,
            parent: ?Uid,
            ordinal: u32,
            uid: Uid,
            allocation_bytes: u64,
            attributes: ?Attributes,
        };
        pub const Target = union(enum) { table: u32, partition: Uid };
        pub const Lease = struct { slot: u32, allocation_number: u64, use_generation: u64 };
        pub const Borrow = struct { lease: Lease, region: []u8, allocation_number: u64 };
        pub const UseResult = union(enum) { in_memory: Borrow, not_in_memory, loading };
        pub const PushResult = union(enum) { in_memory: Borrow, newly_allocated: Borrow, loading };
        pub const UpdateResult = union(enum) { updated: ?[]u8, registration_only, in_use, force_released };
        pub const PartitionResult = struct { created: bool, allocation_number: u64 };
        pub const Notification = struct { uid: Uid, allocation_number: u64 };
        pub const Update = struct { allocation_bytes: ?u64 = null, attributes: ?AttributeUpdate = null };
    };
}
pub const TableView = struct { registered_bytes: u64, system_page_bytes: u64 };
pub const TickResult = struct { released: usize, scanned: usize };
