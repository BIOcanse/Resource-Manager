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
//! stop never ends or unmaps a region. Dynamic desktop selections are in this
//! same Owner and use its locks/borrow counters. Each selection has a service T
//! and isolated external W; retireDesktop removes only that selection.
const std = @import("std");
const policy = @import("../configuration/NativePolicy.g.zig");
const windows = std.os.windows;
const ntdll = windows.ntdll;
const ss = @import("rm_shared_section");
const plan = @import("plan.zig");
const Section = @import("section.zig").Section;
pub const directory = @import("directory.zig");

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

/// Non-owning token for one committed selection. The existing connection
/// identity is part of the token; numeric session/PID reuse is insufficient.
pub const DesktopSelection = extern struct {
    trusted: plan.RegionReference,
    writer: plan.RegionReference,
    publisher: plan.SelectionIdentity,
};

pub const PhysicalBinding = struct {
    reference: plan.RegionReference,
    region_class: plan.RegionClass,
    source_base: u32,
    source_count: u32,
    position_base: u32,
    position_count: u32,

    pub fn source(self: PhysicalBinding, local: u32) ?u32 {
        return if (local < self.source_count) self.source_base + local else null;
    }

    pub fn position(self: PhysicalBinding, local: u32) ?u32 {
        return if (local < self.position_count) self.position_base + local else null;
    }
};

pub const PhysicalBorrow = struct {
    reference: plan.RegionReference,
    /// Non-owning platform handle, usable only inside this local borrow.
    /// H duplicates the required access while held; never sends this value.
    section_handle: windows.HANDLE,
    region_address: [*]align(8) u8,
    byte_length: usize,

    pub fn bytes(self: PhysicalBorrow) []align(8) u8 {
        return self.region_address[0..self.byte_length];
    }
};

/// Part of this Owner's region collection, never a separate source registry.
/// Its address stays stable while any borrow can refer to its counters.
const DesktopSlot = struct {
    trusted: Region = .{},
    writer: Region = .{},
    placements: []plan.Placement = &.{},
    publisher: plan.SelectionIdentity = .{ .session_id = 0, .process_id = 0, .process_start_time = 0, .connection_generation = 0 },
    selected: bool = false,
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
    desktop_slots: []DesktopSlot = &.{},
    stopped: bool = false,

    pub fn init(allocator: std.mem.Allocator, settings: Settings) Owner {
        return .{ .allocator = allocator, .settings = settings };
    }

    /// Capacity is an explicit host declaration, with no native fallback and
    /// no growth on select. The v1 initializer continues to expose only N/A.
    /// Large owner storage must use the host's registered allocator/workspace.
    pub fn initWithDesktopCapacity(allocator: std.mem.Allocator, settings: Settings, capacity: u32) Error!Owner {
        if (capacity != 0 and plan.selectionRegion(capacity - 1, .writer) == null) return error.InvalidArgument;
        var result = Owner.init(allocator, settings);
        if (capacity != 0) {
            result.desktop_slots = try allocator.alloc(DesktopSlot, capacity);
            @memset(result.desktop_slots, DesktopSlot{});
        }
        return result;
    }

    /// Select/replace only after the caller/connection owner has verified this
    /// publisher. This leaf checks binding consistency; it does not attest a
    /// caller or grant a handle. Replacement requires the exact prior token.
    /// Every failure keeps the old selection and T active, including Borrowed.
    pub fn selectDesktop(self: *Owner, publisher: plan.SelectionIdentity, declarations: []const plan.Declaration, data_kind: u32, expected: ?DesktopSelection, failure: *plan.ApplyFailure) Error!DesktopSelection {
        lock(&self.change_lock);
        defer unlock(&self.change_lock);
        failure.* = .{};
        if (self.stopped) return error.Stopped;
        if (!publisher.valid()) return error.InvalidArgument;
        var existing: ?usize = null;
        var vacant: ?usize = null;
        var i: usize = 0;
        while (i < self.desktop_slots.len) : (i += 1) {
            const slot = &self.desktop_slots[i];
            if (slot.selected and slot.publisher.session_id == publisher.session_id) existing = i;
            if (vacant == null and slot.trusted.section == null and slot.writer.section == null) vacant = i;
        }
        const index = if (existing) |at| at else vacant orelse return error.BufferTooSmall;
        const slot = &self.desktop_slots[index];
        if (expected) |token| {
            if (existing == null or !matches(slot, index, token)) return error.StaleBinding;
        } else if (existing != null) return error.InvalidArgument;
        if (slot.trusted.epoch == std.math.maxInt(u64) or slot.writer.epoch == std.math.maxInt(u64) or slot.trusted.layout_generation == std.math.maxInt(u64) or slot.writer.layout_generation == std.math.maxInt(u64)) return error.InvalidArgument;

        var next = try plan.computePhysical(self.allocator, .normal, declarations, failure);
        defer next.deinit(self.allocator);
        i = 0;
        while (i < next.layout.sources.len) : (i += 1) {
            const source = next.layout.sources[i];
            if (source.process_id != publisher.process_id or source.process_start_time != publisher.process_start_time) return error.InvalidArgument;
        }
        const trusted_ref = reference(&slot.trusted, plan.selectionRegion(index, .trusted).?, true);
        const writer_ref = reference(&slot.writer, plan.selectionRegion(index, .writer).?, true);
        failure.region = @backingInt(writer_ref.id);
        const writer = Section.create(next.layout.byte_length, &failure.win32_error) catch return error.SectionFailed;
        var writer_owned = true;
        defer if (writer_owned) writer.destroy();
        ss.initialize(writer.view, data_kind, @intCast(next.layout.sources.len), @intCast(next.layout.positions.len), next.layout.data_capacity, next.layout.sources, next.layout.positions) catch |err| {
            failure.shared_section_failure = @backingInt(ss.failureOf(err));
            return error.DeclarationInvalid;
        };
        const trusted_size = directory.requiredSize(next.layout) catch return error.RegionTooLarge;
        failure.region = @backingInt(trusted_ref.id);
        const trusted = Section.create(trusted_size, &failure.win32_error) catch return error.SectionFailed;
        var trusted_owned = true;
        defer if (trusted_owned) trusted.destroy();
        directory.initialize(trusted.view, trusted_ref, writer_ref, publisher, .normal, data_kind, next.layout) catch return error.InvalidArgument;

        self.closeDesktop(slot, true);
        if (!self.drain(&slot.trusted)) {
            self.closeDesktop(slot, false);
            return error.Borrowed;
        }
        if (!self.drain(&slot.writer)) {
            failure.region = @backingInt(writer_ref.id);
            self.closeDesktop(slot, false);
            return error.Borrowed;
        }
        var old_trusted: ?Section = null;
        var old_writer: ?Section = null;
        {
            lock(&self.state_lock);
            defer unlock(&self.state_lock);
            old_trusted = slot.trusted.section;
            old_writer = slot.writer.section;
            if (old_trusted) |old| directory.invalidate(old.view);
            slot.writer.layout.deinit(self.allocator);
            if (slot.placements.len != 0) self.allocator.free(slot.placements);
            slot.trusted = .{ .section = trusted, .epoch = trusted_ref.epoch, .layout_generation = trusted_ref.layout_generation };
            slot.writer = .{ .section = writer, .layout = next.layout, .epoch = writer_ref.epoch, .layout_generation = writer_ref.layout_generation };
            slot.placements = next.placements;
            next.layout = .{};
            next.placements = &.{};
            slot.publisher = publisher;
            slot.selected = true;
            trusted_owned = false;
            writer_owned = false;
        }
        if (old_trusted) |old| old.destroy();
        if (old_writer) |old| old.destroy();
        failure.* = .{};
        return .{ .trusted = trusted_ref, .writer = writer_ref, .publisher = publisher };
    }

    pub fn desktopSelection(self: *Owner, session_id: u32) Error!DesktopSelection {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        if (self.stopped) return error.Stopped;
        var i: usize = 0;
        while (i < self.desktop_slots.len) : (i += 1) {
            const slot = &self.desktop_slots[i];
            if (slot.selected and slot.publisher.session_id == session_id) return .{ .trusted = reference(&slot.trusted, plan.selectionRegion(i, .trusted).?, false), .writer = reference(&slot.writer, plan.selectionRegion(i, .writer).?, false), .publisher = slot.publisher };
        }
        return error.Absent;
    }

    pub fn desktopBinding(self: *Owner, selection: DesktopSelection, declaration: u32) Error!PhysicalBinding {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        if (self.stopped) return error.Stopped;
        const index = self.desktopIndex(selection.trusted.id) orelse return error.StaleBinding;
        const slot = &self.desktop_slots[index];
        if (!slot.selected or !matches(slot, index, selection)) return error.StaleBinding;
        if (declaration >= slot.placements.len) return error.InvalidArgument;
        const placement = slot.placements[declaration];
        return .{ .reference = selection.writer, .region_class = placement.region, .source_base = placement.source_base, .source_count = placement.source_count, .position_base = placement.position_base, .position_count = placement.position_count };
    }

    /// Obtain physical identity for an existing v1 service binding/view, using
    /// the very same static Region and borrow counter as acquire/release.
    pub fn serviceReference(self: *Owner, class: plan.RegionClass, generation: u64) Error!plan.RegionReference {
        const r = class.index() orelse return error.InvalidArgument;
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        if (self.stopped) return error.Stopped;
        const state = &self.regions[r];
        if (state.section == null) return error.Absent;
        if (state.layout_generation != generation) return error.StaleBinding;
        return reference(state, @fromBackingInt(@intCast(r)), false);
    }

    pub fn acquirePhysical(self: *Owner, ref: plan.RegionReference) Error!PhysicalBorrow {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        if (self.stopped) return error.Stopped;
        const state = self.physicalState(ref.id) orelse return error.Absent;
        if (ref.reserved != 0 or ref.epoch != state.epoch or ref.layout_generation != state.layout_generation) return error.StaleBinding;
        const section = state.section orelse return error.Absent;
        if (state.closing) return error.Replacing;
        if (state.borrows == std.math.maxInt(u32)) return error.InvalidArgument;
        @atomicStore(u32, &state.borrows, state.borrows + 1, .release);
        return .{ .reference = ref, .section_handle = section.handle, .region_address = section.view.ptr, .byte_length = section.view.len };
    }

    pub fn releasePhysical(self: *Owner, borrow: *const PhysicalBorrow) Error!void {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        const state = self.physicalState(borrow.reference.id) orelse return error.NotBorrowed;
        const section = state.section orelse return error.NotBorrowed;
        if (borrow.reference.reserved != 0 or state.epoch != borrow.reference.epoch or state.layout_generation != borrow.reference.layout_generation or state.borrows == 0 or section.handle != borrow.section_handle or section.view.ptr != borrow.region_address or section.view.len != borrow.byte_length) return error.NotBorrowed;
        @atomicStore(u32, &state.borrows, state.borrows - 1, .release);
        if (state.borrows == 0 and state.closing) ntdll.RtlWakeAddressAll(&state.borrows);
    }

    /// Source loss is irreversible: invalidate T and remove selection BEFORE
    /// returning (and thus before the caller sends loss). Borrowed only means
    /// local maps still need draining; retry with the same token after release.
    /// Never writes W, which an uncooperative old process may still modify.
    pub fn retireDesktop(self: *Owner, selection: DesktopSelection) Error!void {
        lock(&self.change_lock);
        defer unlock(&self.change_lock);
        const index = self.desktopIndex(selection.trusted.id) orelse return error.StaleBinding;
        const slot = &self.desktop_slots[index];
        if (slot.trusted.section == null or !matches(slot, index, selection)) return error.StaleBinding;
        try self.retireDesktopSlot(slot);
    }

    fn retireDesktopSlot(self: *Owner, slot: *DesktopSlot) Error!void {
        {
            lock(&self.state_lock);
            defer unlock(&self.state_lock);
            if (slot.trusted.section) |section| directory.invalidate(section.view);
            slot.selected = false;
            slot.trusted.closing = true;
            slot.writer.closing = true;
        }
        if (!self.drain(&slot.trusted) or !self.drain(&slot.writer)) return error.Borrowed;
        var trusted: ?Section = null;
        var writer: ?Section = null;
        {
            lock(&self.state_lock);
            defer unlock(&self.state_lock);
            trusted = slot.trusted.section;
            writer = slot.writer.section;
            slot.trusted.section = null;
            slot.writer.section = null;
            slot.writer.layout.deinit(self.allocator);
            if (slot.placements.len != 0) self.allocator.free(slot.placements);
            slot.placements = &.{};
        }
        if (trusted) |section| section.destroy();
        if (writer) |section| section.destroy();
    }

    fn closeDesktop(self: *Owner, slot: *DesktopSlot, closing: bool) void {
        lock(&self.state_lock);
        defer unlock(&self.state_lock);
        slot.trusted.closing = closing;
        slot.writer.closing = closing;
    }

    fn desktopIndex(self: *Owner, id: plan.RegionId) ?usize {
        const value = @backingInt(id);
        if (value < plan.region_count) return null;
        const index = (value - plan.region_count) / 2;
        return if (index < self.desktop_slots.len) index else null;
    }

    // Caller holds state_lock. Retiring slots remain addressable for release.
    fn physicalState(self: *Owner, id: plan.RegionId) ?*Region {
        const value = @backingInt(id);
        if (value < plan.region_count) return &self.regions[value];
        const index = self.desktopIndex(id) orelse return null;
        return if ((value - plan.region_count) % 2 == 0) &self.desktop_slots[index].trusted else &self.desktop_slots[index].writer;
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
        var checked: usize = 0;
        while (checked < self.regions.len) : (checked += 1) {
            const region = &self.regions[checked];
            if (changed[checked] and (region.epoch == std.math.maxInt(u64) or region.layout_generation == std.math.maxInt(u64))) return error.InvalidArgument;
        }

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
                failure.shared_section_failure = @backingInt(ss.failureOf(err));
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
            var i: usize = 0;
            while (i < self.desktop_slots.len) : (i += 1) {
                const slot = &self.desktop_slots[i];
                if (slot.trusted.section) |section| directory.invalidate(section.view);
                slot.selected = false;
                slot.trusted.closing = true;
                slot.writer.closing = true;
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
                state.epoch +|= 1; // Terminal owner: no future mapping can reuse it.
                state.layout.deinit(self.allocator);
            }
            section.destroy();
        }
        var i: usize = 0;
        while (i < self.desktop_slots.len) : (i += 1) {
            self.retireDesktopSlot(&self.desktop_slots[i]) catch |err| {
                if (err == error.Borrowed) borrowed = true else return err;
            };
        }
        if (borrowed) return error.Borrowed;
    }

    /// Frees the owner's memory. A region still mapped refuses: `NotStopped`
    /// before `stop`, `Borrowed` while a stopped region is still borrowed.
    pub fn deinit(self: *Owner) Error!void {
        lock(&self.change_lock);
        defer unlock(&self.change_lock);
        for (self.regions) |state| if (state.section != null) return if (self.stopped) error.Borrowed else error.NotStopped;
        var i: usize = 0;
        while (i < self.desktop_slots.len) : (i += 1) {
            const slot = &self.desktop_slots[i];
            if (slot.trusted.section != null or slot.writer.section != null) return if (self.stopped) error.Borrowed else error.NotStopped;
        }
        for (&self.regions) |*state| state.layout.deinit(self.allocator);
        if (self.placements.len != 0) self.allocator.free(self.placements);
        self.placements = &.{};
        if (self.desktop_slots.len != 0) self.allocator.free(self.desktop_slots);
        self.desktop_slots = &.{};
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

fn reference(state: *const Region, id: plan.RegionId, next: bool) plan.RegionReference {
    return .{ .id = id, .epoch = state.epoch + @as(u64, if (next) 1 else 0), .layout_generation = state.layout_generation + @as(u64, if (next) 1 else 0) };
}

fn matches(slot: *const DesktopSlot, index: usize, selection: DesktopSelection) bool {
    return std.meta.eql(selection.publisher, slot.publisher) and std.meta.eql(selection.trusted, reference(&slot.trusted, plan.selectionRegion(index, .trusted).?, false)) and std.meta.eql(selection.writer, reference(&slot.writer, plan.selectionRegion(index, .writer).?, false));
}

fn lock(l: *windows.SRWLOCK) void {
    ntdll.RtlAcquireSRWLockExclusive(l);
}
fn unlock(l: *windows.SRWLOCK) void {
    ntdll.RtlReleaseSRWLockExclusive(l);
}
