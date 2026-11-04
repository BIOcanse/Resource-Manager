//! Optional development facts. Resource selection remains the application's job.
pub const Counters = extern struct {
    new_allocations: u64 = 0,
    same_epoch_releases: u64 = 0,
    same_epoch_allocation_bytes: u64 = 0,
    subpage_allocations: u64 = 0,
    subpage_allocation_bytes: u64 = 0,
};

pub const Birth = struct {
    epoch: u64 = 0,
    allocation_bytes: u64 = 0,
};
