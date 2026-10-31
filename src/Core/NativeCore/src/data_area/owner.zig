//! The region owner in the service (backend_data_area §12 step 1).
//!
//! At run-plan compile every data-writing owner's position declaration is
//! passed to `apply`. The owner computes both regions' layouts (plan.zig),
//! creates an unnamed section per non-empty region and initializes it with
//! SharedSection. A writer gets a `Binding` (region, its global source and
//! position indexes, the layout generation they belong to); a reader in the
//! service gets a `ReadView` (region, the ascending sources it reads). Neither
//! holds the region: at each write or read round the caller `acquire`s the
//! region's current address, length and mapping epoch and `release`s it after.
//!
//! Declarations are fixed for a plan. When they change, `apply` is called with
//! the new set: a region whose layout is unchanged keeps its section, epoch and
//! bindings; a changed region gets a new section once every borrow of the old
//! one has ended. The old one is written ended, zeroed and unmapped, and the
//! region's epoch and layout generation advance. A failed or timed-out apply
//! leaves the current regions as they were.
//!
//! `stop` is service stop (§7): no new borrows, every source of both regions
//! written ended, then each region unmapped once its borrows end. A writer's own
//! stop never ends or unmaps a region.
const std = @import("std");
const policy = @import("../configuration/NativePolicy.g.zig");
const windows = std.os.windows;
const ntdll = windows.ntdll;
const ss = @import("rm_shared_section");
const plan = @import("plan.zig");
const Section = @import("section.zig").Section;

extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;

pub const Settings = extern struct {
    /// How long `apply` and `stop` wait for a replaced or stopped region's
    /// borrows to end before refusing with `Borrowed`. Zero: do not wait.
    /// The native fallback and host default both come from the same declaration.
    borrow_drain_timeout_ms: u32 = policy.data_area_borrow_drain_timeout_ms,
    reserved: u32 = 0,
};

/// A writer's non-owning binding. Valid while its region's layout generation
/// is unchanged; `acquire` refuses it with `StaleBinding` after a rebuild.
pub const Binding = extern struct {
    region: plan.RegionClass,
    source_base: u32,
    source_count: u32,
    position_base: u32,
    position_count: u32,
    reserved: u32 = 0,
    layout_generation: u64,

    /// The global index of the declaration's local source `local`.
    pub fn source(self: Binding, local: u32) ?u32 {
        return if (local < self.source_count) self.source_base + local else null;
    }
    /// The global index of the declaration's local position `local`.
    pub fn position(self: Binding, local: u32) ?u32 {
        return if (local < self.position_count) self.position_base + local else null;
    }
};

/// A reader's non-owning view: the region and how many ascending global source
/// indexes were written to the caller's buffer.
pub const ReadView = extern struct {
    region: plan.RegionClass = .normal,
    source_count: u32 = 0,
    layout_generation: u64 = 0,
};

/// One borrow of a region's current mapping, from `acquire` to `release`.
pub const Borrow = extern struct {
    region_address: ?[*]align(8) u8 = null,
    byte_length: u64 = 0,
    /// Stable while this mapping is the region's; advances when it is replaced.
    epoch: u64 = 0,
    layout_generation: u64 = 0,
    region: plan.RegionClass = .normal,
    reserved: u32 = 0,

    pub fn bytes(self: Borrow) []align(8) u8 {
        const base = self.region_address orelse return &.{};
        return base[0..@intCast(self.byte_length)];
    }
};

pub const Error = plan.Error || error{
    SectionFailed,
    /// A borrow did not end within the drain timeout.
    Borrowed,
    Stopped,
    /// The binding or view belongs to an earlier layout; register again.
    StaleBinding,
    /// The region is being replaced or stopped.
    Replacing,
    /// No declaration targets the region, so it has no section.
    Absent,
    MixedRegions,
    BufferTooSmall,
    NotBorrowed,
    NotStopped,
};

const Region = struct {
    section: ?Section = null,
    layout: plan.RegionLayout = .{},
    epoch: u64 = 0,
    layout_generation: u64 = 0,
    /// Changed only under `state_lock`, with atomic stores, so a drain can wait
    /// on it without the lock.
    borrows: u32 = 0,
    /// No new borrows: set while the region is replaced or stopped.
    closing: bool = false,
    /// Written ended by `stop`.
    ended: bool = false,
};

pub const Owner = struct {
    allocator: std.mem.Allocator,
    settings: Settings,
    /// Serializes `apply`, `stop` and `deinit` for their whole run.
    change_lock: windows.SRWLOCK = windows.SRWLOCK_INIT,
    /// Guards region state and placements; held only briefly.
    state_lock: windows.SRWLOCK = windows.SRWLOCK_INIT,
    regions: [plan.region_count]Region = .{ .{}, .{} },
    placements: []plan.Placement = &.{},
    stopped: bool = false,

    pub fn init(allocator: std.mem.Allocator, settings: Settings) Owner {
        return .{ .allocator = allocator, .settings = settings };
    }

    /// Lays out and (re)builds the regions for `declarations`. On any error the
    /// current regions, bindings and epochs are unchanged.
    pub fn apply(self: *Owner, declarations: []const plan.Declaration, now_unix_ms: i64, failure: *plan.ApplyFailure) Error!void {
        lock(&self.change_lock);
        defer unlock(&self.change_lock);
        failure.* = .{};
        if (self.stopped) return error.Stopped;
        var next = try plan.compute(self.allocator, declarations, failure);
        defer next.deinit(self.allocator);

        var changed = [_]bool{ false, false };
        for (self.regions, next.regions, 0..) |region, layout, r| changed[r] = !region.layout.eql(layout);

        var created = [_]?Section{ null, null };
        errdefer for (created) |maybe| if (maybe) |section| section.destroy();
        for (next.regions, 0..) |layout, r| {
            if (!changed[r] or layout.isEmpty()) continue;
            failure.region = @intCast(r);
            const section = Section.create(layout.byte_length, &failure.win32_error) catch return error.SectionFailed;
            created[r] = section;
            ss.initialize(
                section.view,
                plan.dataKind(.fromIndex(r)),
                @intCast(layout.sources.len),
                @intCast(layout.positions.len),
                layout.data_capacity,
                layout.sources,
                layout.positions,
            ) catch |err| {
                failure.shared_section_failure = @intFromEnum(ss.failureOf(err));
                return error.DeclarationInvalid;
            };
        }
        failure.* = .{};

        // A replaced mapping waits for every writer and reader borrow to end.
        self.setClosing(changed, true);
        for (&self.regions, 0..) |*region, r| {
            if (!changed[r] or region.section == null) continue;
            if (!self.drain(region)) {
                self.setClosing(changed, false);
                failure.region = @intCast(r);
                return error.Borrowed;
            }
        }

        var replaced = [_]?Section{ null, null };
        {
            lock(&self.state_lock);
            defer unlock(&self.state_lock);
            for (&self.regions, 0..) |*region, r| {
                if (!changed[r]) continue;
                replaced[r] = region.section;
                region.section = created[r];
                created[r] = null;
                region.layout.deinit(self.allocator);
                region.layout = next.regions[r];
                next.regions[r] = .{};
                region.epoch += 1;
                region.layout_generation += 1;
                region.closing = false;
            }
            if (self.placements.len != 0) self.allocator.free(self.placements);
            self.placements = next.placements;
            next.placements = &.{};
        }
        // §8 step 4: the old block's sources are written ended and its data
        // zeroed, then the owner closes its mapping. Nothing borrows it now.
        for (replaced) |maybe| {
            const old = maybe orelse continue;
            // The region was initialized here; a header a writer overwrote is
            // still unmapped, so a refusal changes nothing.
            ss.end(old.view, now_unix_ms) catch {};
            old.destroy();
        }
    }

    /// The binding of declaration `declaration` in the last applied plan.
    pub fn binding(self: *Owner, declaration: u32) Error!Binding {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        if (self.stopped) return error.Stopped;
        if (declaration >= self.placements.len) return error.InvalidArgument;
        const placement = self.placements[declaration];
        return .{
            .region = placement.region,
            .source_base = placement.source_base,
            .source_count = placement.source_count,
            .position_base = placement.position_base,
            .position_count = placement.position_count,
            .layout_generation = self.regions[placement.region.index().?].layout_generation,
        };
    }

    /// A reader's view of the sources of `declarations`, all in one region.
    /// Writes the ascending global source indexes to `sources`; `view` always
    /// gets the count, so a `BufferTooSmall` caller learns the size it needs.
    pub fn readView(self: *Owner, declarations: []const u32, sources: []u32, view: *ReadView) Error!void {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        view.* = .{};
        if (self.stopped) return error.Stopped;
        if (declarations.len == 0) return error.InvalidArgument;
        var region: ?plan.RegionClass = null;
        var total: u64 = 0;
        for (declarations) |declaration| {
            if (declaration >= self.placements.len) return error.InvalidArgument;
            const placement = self.placements[declaration];
            if (region) |class| {
                if (class != placement.region) return error.MixedRegions;
            } else region = placement.region;
            total += placement.source_count;
        }
        if (total > std.math.maxInt(u32)) return error.InvalidArgument;
        view.* = .{ .region = region.?, .source_count = @intCast(total), .layout_generation = self.regions[region.?.index().?].layout_generation };
        if (total > sources.len) return error.BufferTooSmall;
        var count: usize = 0;
        for (declarations) |declaration| {
            const placement = self.placements[declaration];
            var local: u32 = 0;
            while (local < placement.source_count) : (local += 1) {
                sources[count] = placement.source_base + local;
                count += 1;
            }
        }
        std.mem.sort(u32, sources[0..count], {}, std.sort.asc(u32));
        // SharedSection selections take strictly ascending sources; a repeated
        // declaration would repeat its sources.
        var i: usize = 1;
        while (i < count) : (i += 1) if (sources[i] == sources[i - 1]) return error.InvalidArgument;
    }

    /// Borrows the current mapping of `region` for a binding or view of
    /// `layout_generation`. Never blocks.
    pub fn acquire(self: *Owner, region: plan.RegionClass, layout_generation: u64) Error!Borrow {
        const r = region.index() orelse return error.InvalidArgument;
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        if (self.stopped) return error.Stopped;
        const state = &self.regions[r];
        const section = state.section orelse return error.Absent;
        if (state.closing) return error.Replacing;
        if (layout_generation != state.layout_generation) return error.StaleBinding;
        if (state.borrows == std.math.maxInt(u32)) return error.InvalidArgument;
        @atomicStore(u32, &state.borrows, state.borrows + 1, .release);
        return .{
            .region_address = section.view.ptr,
            .byte_length = section.view.len,
            .epoch = state.epoch,
            .layout_generation = state.layout_generation,
            .region = region,
        };
    }

    pub fn release(self: *Owner, borrow: *const Borrow) Error!void {
        const r = borrow.region.index() orelse return error.InvalidArgument;
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        const state = &self.regions[r];
        if (state.section == null or state.epoch != borrow.epoch or state.borrows == 0) return error.NotBorrowed;
        const left = state.borrows - 1;
        @atomicStore(u32, &state.borrows, left, .release);
        if (left == 0 and state.closing) ntdll.RtlWakeAddressAll(&state.borrows);
    }

    /// Service stop. Callable again after `Borrowed` to finish unmapping.
    pub fn stop(self: *Owner, now_unix_ms: i64) Error!void {
        lock(&self.change_lock);
        defer unlock(&self.change_lock);
        {
            lock(&self.state_lock);
            defer unlock(&self.state_lock);
            self.stopped = true;
            for (&self.regions) |*state| {
                if (state.section != null) state.closing = true;
            }
        }
        // §7: every source of both regions is written ended. A round still open
        // fails at its end, and readers see ended before the unmap.
        for (&self.regions) |*state| {
            const section = state.section orelse continue;
            if (state.ended) continue;
            ss.end(section.view, now_unix_ms) catch {};
            state.ended = true;
        }
        var borrowed = false;
        for (&self.regions) |*state| {
            if (state.section == null) continue;
            if (!self.drain(state)) {
                borrowed = true;
                continue;
            }
            var section: Section = undefined;
            {
                lock(&self.state_lock);
                defer unlock(&self.state_lock);
                section = state.section.?;
                state.section = null;
                state.epoch += 1;
                state.layout.deinit(self.allocator);
            }
            section.destroy();
        }
        if (borrowed) return error.Borrowed;
    }

    /// Frees the owner's memory. A region still mapped refuses: `NotStopped`
    /// before `stop`, `Borrowed` while a stopped region is still borrowed.
    pub fn deinit(self: *Owner) Error!void {
        lock(&self.change_lock);
        defer unlock(&self.change_lock);
        for (self.regions) |state| if (state.section != null) return if (self.stopped) error.Borrowed else error.NotStopped;
        for (&self.regions) |*state| state.layout.deinit(self.allocator);
        if (self.placements.len != 0) self.allocator.free(self.placements);
        self.placements = &.{};
    }

    fn setClosing(self: *Owner, which: [plan.region_count]bool, closing: bool) void {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        for (&self.regions, which) |*state, selected| {
            if (selected and state.section != null) state.closing = closing;
        }
    }

    /// Waits, at most the drain timeout, for the region's borrows to end.
    // RuntimeRead: Sampled
    fn drain(self: *Owner, state: *Region) bool {
        const deadline = GetTickCount64() + self.settings.borrow_drain_timeout_ms;
        while (true) {
            var observed = @atomicLoad(u32, &state.borrows, .acquire);
            if (observed == 0) return true;
            const now = GetTickCount64();
            if (now >= deadline) return false;
            // Relative timeout in 100 ns units.
            const wait: windows.LARGE_INTEGER = -@as(i64, @intCast(deadline - now)) * 10_000;
            _ = ntdll.RtlWaitOnAddress(&state.borrows, &observed, @sizeOf(u32), &wait);
        }
    }
};

fn lock(l: *windows.SRWLOCK) void {
    ntdll.RtlAcquireSRWLockExclusive(l);
}
fn unlock(l: *windows.SRWLOCK) void {
    ntdll.RtlReleaseSRWLockExclusive(l);
}
