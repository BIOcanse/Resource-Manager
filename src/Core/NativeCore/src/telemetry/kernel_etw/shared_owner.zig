const policy = @import("../../configuration/NativePolicy.g.zig");
const std = @import("std");
const capture = @import("abi.zig");
const irq = @import("interrupt_abi.zig");
const trace = @import("trace_abi.zig");
const native = @import("consumer.zig");
const p = @import("protocol.zig");
const Budget = @import("budget.zig").Budget;
const Region = @import("region_allocator.zig").RegionAllocator;
const disk = @import("disk_io_owner.zig");
const network = @import("network_io_owner.zig");
const fileio = @import("fileio.zig");
const History = @import("thread_process_history.zig").History;
const PathMap = @import("path_mapping.zig").PathMap;
const Readers = @import("shared_readers.zig").State;
const io = @import("io_decode.zig");
const identity = @import("io_identity.zig");
const host = @import("host_allocator.zig");
const counters = @import("admission_counters.zig");
const admission = @import("shared_admission.zig");
const Mutex = struct {
    value: std.atomic.Mutex = .unlocked,
    fn lock(self: *Mutex) void {
        while (!self.value.tryLock()) std.atomic.spinLoopHint();
    }
    fn unlock(self: *Mutex) void {
        self.value.unlock();
    }
};
pub const abi_version: u32 = 0x0001_0000;
pub const Config = extern struct {
    size: u32 = @sizeOf(Config),
    version: u32 = abi_version,
    residency: p.Config,
    interrupts: irq.Config,
    byte_cap: u64,
    disk_byte_cap: u64,
    network_byte_cap: u64,
    file_byte_cap: u64,
    history_byte_cap: u64,
    maximum_disk_pairs: u32,
    maximum_disks: u32,
    maximum_cached_processes: u32,
    maximum_network_events: u32,
    maximum_network_processes: u32,
    maximum_file_entries: u32,
    maximum_file_versions: u32,
    maximum_history_entries: u32,
    maximum_path_units: u32,
    maximum_namespace_entries: u32,
    maximum_commands: u32,
    maximum_command_name_bytes: u32,
    publication_banks: u32,
    reserved: u32 = 0,
    window_ms: i64,
    retention_ms: i64,
    disk_bucket_ms: i64,
    started_tick_ms: i64,
    maximum_pending_events: u32,
    maximum_pending_path_units: u32,
    maximum_renderer_processes: u32,
    /// Network events the owner reserves before its first growth, capped by maximum_network_events.
    network_initial_events: u32,
};
pub const ProcessDisk = extern struct {
    pid: u32,
    flags: u32,
    birth: u64,
    read_bytes: u64,
    write_bytes: u64,
    read_rate: f64,
    write_rate: f64,
    read_count: u32,
    write_count: u32,
};
pub const Disk = extern struct { pid: u32, disk: u32, flags: u32, reserved: u32 = 0, birth: u64, read_bytes: u64, write_bytes: u64 };
pub const Network = extern struct {
    pid: u32,
    flags: u32,
    birth: u64,
    receive_bytes: u64,
    send_bytes: u64,
    receive_rate: f64,
    send_rate: f64,
    event_count: u32,
    external_event_count: u32,
    external_receive_bytes: u64,
    external_send_bytes: u64,
    external_receive_rate: f64,
    external_send_rate: f64,
};
pub const Notification = extern struct {
    kind: u32,
    flags: u32 = 0,
    qpc: i64,
    tick_ms: i64,
    pid: u32 = 0,
    tid: u32 = 0,
    key: u64 = 0,
    object: u64 = 0,
    base: u64 = 0,
    size: u64 = 0,
    path: ?[*]const u16 = null,
    path_len: u32 = 0,
    reserved: u32 = 0,
    time_filetime_100ns: i64 = 0,
    parent_pid: u32 = 0,
    reserved2: u32 = 0,
};
pub const FailureDetail = extern struct {
    result: i32 = 0,
    reserved: u32 = 0,
    error_name: ?[*]const u8 = null,
    error_name_length: u32 = 0,
    reserved2: u32 = 0,
    records: u64 = 0,
    qpc: i64 = 0,
};
pub const PublicationStage = enum(u32) {
    none = 0,
    io_scratch = 1,
    io_final = 2,
    notifications = 3,
    residency = 4,
    interrupts = 5,
    diagnostics = 6,
    renderer = 7,
    scratch_reset = 8,
};
pub const FailureAllocation = extern struct {
    publication_stage: u32 = 0,
    reserved: u32 = 0,
    allocation: @import("region_allocator.zig").AllocationFailure = .{},
};
pub const OwnerUsage = extern struct {
    kind: u32,
    flags: u32 = 0,
    current_bytes: u64,
    peak_bytes: u64,
    byte_cap: u64,
    failed_allocations: u64,
    capacity_failures: u64 = 0,
};
pub const View = extern struct {
    version: u64 = 0,
    through_tick_ms: i64 = 0,
    window_ms: i64 = 0,
    flags: u32 = 0,
    process_disk_count: u32 = 0,
    process_disk: ?[*]const ProcessDisk = null,
    disk_count: u32 = 0,
    known_disk_count: u32 = 0,
    disks: ?[*]const Disk = null,
    known_disks: ?[*]const u32 = null,
    network_count: u32 = 0,
    namespace_entries: u32 = 0,
    network: ?[*]const Network = null,
    file_events: u64 = 0,
    file_entries: u64 = 0,
    command_version: u64 = 0,
    skipped_publications: u64 = 0,
    region_usage: capture.RegionUsage = .{},
    owner_usage: [7]OwnerUsage = undefined,
    renderer_revisions: ?[*]const u64 = null,
    renderer_epoch: u64 = 0,
    notification_count: u32 = 0,
    reserved: u32 = 0,
    notifications: ?[*]const Notification = null,
    // Stream position of the first notification in this bank. Every output's
    // publication advances `version`, so only this tells a reader whether
    // notifications were published that it never saw.
    notification_first: u64 = 0,
    events_lost: u64 = 0,
    network_transition_version: u64 = 0,
    disk_transition_version: u64 = 0,
    diagnostics: @import("thread_diagnostics.zig").View = .{},
    residency: p.View = .{},
    interrupts: irq.View = .{},
};
pub const Lease = extern struct { view: ?*const View = null, token: u64 = 0 };
const CpuMetadata = struct {
    sequence: u64 = 0,
    events_lost: u64 = 0,
    applied_ticket: u64 = 0,
    revision: u64 = 0,
    semantic: u64 = 0,
};
pub const PublishedProgress = extern struct {
    bank_sequence: u64 = 0,
    cpu_sequence: u64 = 0,
    cpu_applied_ticket: u64 = 0,
    status: trace.TraceStatus = std.mem.zeroes(trace.TraceStatus),
};
const CpuBank = struct { arena: std.heap.ArenaAllocator, view: p.View = .{}, references: u32 = 0 };
const Bank = struct {
    arena: std.heap.ArenaAllocator,
    component_arenas: [9]std.heap.ArenaAllocator = undefined,
    component_references: [9]u32 = @as([9]u32, @splat(0)),
    view: View = .{},
    references: u32 = 0,
    cpu_index: ?usize = null,
    cpu_metadata: CpuMetadata = .{},
    published_progress: ?PublishedProgress = null,
    renderer_rows: []const RendererRow = &.{},
    available_outputs: u32 = 0,
};
const RendererRow = struct { slot: u32, revision: u64 };
pub const RendererStamp = extern struct { epoch: u64, events_lost: u64, revision: u64 };
pub const ResidencyLease = struct { view: p.View, sequence: u64, events_lost: u64, applied_ticket: u64, token: u64 };
pub const ResidencyRequest = struct { ticket: u64, pending: bool };
pub const AggregationDiagnostics = extern struct {
    qpc_frequency: i64, window_qpc: i64,
    maximum_active_pairs: u64, active_pair_count: u64, slots_per_window: u64, ring_slots: u64,
    pair_record_bytes: u64, window_storage_bytes: u64, maximum_window_storage_bytes: u64,
    current_process_count: u64, current_thread_count: u64, retained_process_identity_count: u64, retained_thread_identity_count: u64,
    continuity_epoch: i64, capacity_reset_count: i64,
    last_capacity_reset: @import("residency.zig").CapacityReset,
};
pub const InterruptMetadata = extern struct { session_generation: i64, owner_serial: u64, active_modules: u64, module_revision: u64 };
/// One ProcessTrace flow owns all mutable owners. Only immutable bank reference
/// counts and preallocated command staging are shared with host callers.
pub const Owner = struct {
    host_callbacks: ?host.Callbacks = null,
    host_accounting: [9]host.Accounting = undefined,
    host_contexts: [9][10]host.Context = undefined,
    allocation_fault: std.atomic.Value(u32) = .init(0),
    active_outputs: u32 = 127,
    retirement_outputs: std.atomic.Value(u32) = .init(127),
    pending_outputs: ?u32 = null,
    history_active: bool = true,
    paths_active: bool = true,
    identity_source: identity.Source,
    diagnostic_source_override: ?@import("thread_diagnostics.zig").Source = null,
    diagnostic_recorded_override: ?bool = null,
    legacy_usage_history: [7]OwnerUsage = undefined,
    consumer_counters: counters.Store,
    counter_commands: std.ArrayList(counters.Config) = .empty,
    aggregation_diagnostics: ?AggregationDiagnostics = null,
    interrupt_metadata: ?InterruptMetadata = null,
    interrupt_owner_serial: u64 = 0,
    region: Region,
    budget: Budget,
    config: Config,
    capture: *capture.Session,
    interrupts: *irq.Session,
    trace: ?*trace.Trace = null,
    disk: disk.Owner,
    network: network.Owner,
    files: fileio.FileIoState,
    history: History,
    paths: PathMap,
    readers: Readers,
    round: std.heap.ArenaAllocator,
    publication_round: std.heap.ArenaAllocator,
    pending_events: []Notification,
    pending_paths: []u16,
    pending_event_count: usize = 0,
    pending_path_count: usize = 0,
    // Notifications published so far; the next bank's notification_first.
    notification_sequence: u64 = 0,
    banks: []Bank,
    cpu_banks: []CpuBank,
    cpu_revision: std.atomic.Value(u64) = .init(1),
    cpu_semantic: std.atomic.Value(u64) = .init(1),
    cpu_sequence: u64 = 0,
    cpu_capacity_resets_seen: i64 = 0,
    cpu_initial_ready: bool = true,
    cpu_pending_ticket: ?u64 = null,
    cpu_invalidated: std.atomic.Value(bool) = .init(true),
    irq_processor_count_override: ?u32 = null,
    publication_mutex: Mutex = .{},
    current_bank: ?usize = null,
    queue_mutex: Mutex = .{},
    commands: []p.Input,
    command_tickets: []u64,
    command_names: []u8,
    processor_ids: []i32,
    command_count: usize = 0,
    command_name_count: usize = 0,
    pending_processor_count: ?usize = null,
    pending_enabled: ?u32 = null,
    desired_enabled: u32 = 1,
    applied_enabled: u32 = 1,
    pending_changes: std.atomic.Value(bool) = .init(false),
    next_command_ticket: u64 = 0,
    disk_transition_version: u64 = 0,
    disk_started_tick: i64,
    network_started_tick: i64,
    network_transition_version: u64 = 0,
    pending_tick: std.atomic.Value(i64) = .init(-1),
    published_version: u64 = 0,
    command_version: u64 = 0,
    skipped_publications: u64 = 0,
    file_events: u64 = 0,
    events_lost: std.atomic.Value(u64) = .init(0),
    failure: std.atomic.Value(i32) = .init(0),
    failure_detail: FailureDetail = .{},
    publication_stage: PublicationStage = .none,
    profile: @import("publication_profile.zig").Profile = .{},
    failure_allocation: FailureAllocation = .{},
    failure_usage: [7]OwnerUsage = undefined,
    failure_region_usage: capture.RegionUsage = .{},
    external_region_bytes: usize,
    publication_callback: ?*const fn (?*anyopaque) callconv(.c) void = null,
    publication_context: ?*anyopaque = null,
    renderer_revisions: []u64,
    renderer_epoch: u64 = 1,
    renderer_sparse: std.AutoHashMapUnmanaged(u32, u64) = .empty,

    pub fn has(self: *const Owner, mask: u32) bool { return self.active_outputs & mask != 0; }
    pub fn controlAllocator(self: *Owner) std.mem.Allocator { return self.outputAllocator(0, 0); }
    fn outputAllocator(self: *Owner, kind: usize, scope: usize) std.mem.Allocator {
        if (self.host_callbacks != null) return self.host_contexts[kind][scope].allocator();
        return self.budget.allocator();
    }
    fn publicationAllocator(self: *Owner, kind: usize, index: usize) std.mem.Allocator {
        if (self.host_callbacks != null) return self.outputAllocator(kind, index + 2);
        return self.budget.transientAllocator();
    }
    pub fn createWithAllocator(cfg: Config, callbacks: host.Callbacks, source: identity.Source) !*Owner {
        try callbacks.validate();
        try validateConfig(cfg);
        const root = callbacks.allocate.?(callbacks.context, 0, @sizeOf(Owner), @intFromEnum(std.mem.Alignment.fromByteUnits(@alignOf(Owner)))) orelse return error.HostAllocationFailed;
        const self: *Owner = @ptrCast(@alignCast(root));
        errdefer _ = callbacks.release.?(callbacks.context, 0, root, @sizeOf(Owner), @intFromEnum(std.mem.Alignment.fromByteUnits(@alignOf(Owner))));
        self.* = .{
            .host_callbacks = callbacks, .region = undefined, .budget = undefined, .config = cfg,
            .capture = undefined, .interrupts = undefined, .disk = undefined, .network = undefined,
            .files = undefined, .history = undefined, .paths = undefined, .readers = undefined,
            .identity_source = source, .consumer_counters = undefined,
            .round = undefined, .publication_round = undefined, .pending_events = &.{}, .pending_paths = &.{},
            .banks = &.{}, .cpu_banks = &.{}, .commands = &.{}, .command_tickets = &.{}, .command_names = &.{},
            .processor_ids = &.{}, .disk_started_tick = cfg.started_tick_ms, .network_started_tick = cfg.started_tick_ms,
            .external_region_bytes = 0, .renderer_revisions = &.{}, .active_outputs = 0, .retirement_outputs = .init(0), .history_active = false, .paths_active = false,
        };
        const caps = [_]u64{ cfg.byte_cap, cfg.residency.byte_cap, cfg.interrupts.byte_cap, cfg.disk_byte_cap, cfg.network_byte_cap, cfg.file_byte_cap, cfg.history_byte_cap, cfg.byte_cap, cfg.byte_cap };
        for (&self.host_accounting, caps, 0..) |*accounting, cap, kind| accounting.* = .{ .cap = @intCast(cap), .parent = if (kind == 0) null else &self.host_accounting[0] };
        const root_pages = std.mem.alignForward(usize, @sizeOf(Owner), callbacks.page_size);
        if (!self.host_accounting[0].admit(root_pages)) return error.OwnerByteCapExceeded;
        self.host_accounting[0].committedGrowth(root_pages);
        self.host_accounting[0].requestChange(0, @sizeOf(Owner));
        for (&self.host_contexts, 0..) |*contexts, kind| for (contexts, 0..) |*context, scope| {
            context.* = try host.Context.init(callbacks, @intCast(kind | (scope << 8)), &self.host_accounting[kind], &self.allocation_fault);
        };
        const a = self.controlAllocator();
        self.budget = .{ .backing = a, .cap = @intCast(cfg.byte_cap) };
        self.consumer_counters = try counters.Store.init(a, cfg.maximum_commands);
        errdefer self.consumer_counters.deinit();
        self.round = std.heap.ArenaAllocator.init(self.outputAllocator(8, 1));
        self.publication_round = std.heap.ArenaAllocator.init(self.outputAllocator(0, 1));
        self.banks = try a.alloc(Bank, cfg.publication_banks);
        errdefer a.free(self.banks);
        for (self.banks, 0..) |*bank, i| {
            bank.* = .{ .arena = std.heap.ArenaAllocator.init(self.publicationAllocator(0, i)) };
            for (&bank.component_arenas, 0..) |*arena, kind| arena.* = std.heap.ArenaAllocator.init(self.publicationAllocator(kind, i));
        }
        self.cpu_banks = try a.alloc(CpuBank, cfg.publication_banks);
        errdefer a.free(self.cpu_banks);
        for (self.cpu_banks, 0..) |*bank, i| bank.* = .{ .arena = std.heap.ArenaAllocator.init(self.publicationAllocator(1, i)) };
        self.processor_ids = try a.alloc(i32, cfg.residency.max_processors);
        errdefer a.free(self.processor_ids);
        for (self.processor_ids, 0..) |*id, i| id.* = @intCast(i);
        self.events_lost = .init(cfg.interrupts.events_lost);
        self.failure_usage = self.collectUsage();
        self.failure_region_usage = self.collectRegionUsage();
        return self;
    }
    fn validateConfig(cfg: Config) !void {
        // The shared owner configures processors through configure_processors only.
        if (cfg.residency.processor_count != 0 or cfg.residency.processors != null) return error.InvalidConfig;
        if (cfg.size != @sizeOf(Config) or cfg.version != abi_version or cfg.reserved != 0 or cfg.network_initial_events == 0 or cfg.maximum_renderer_processes == 0 or
            cfg.maximum_pending_events == 0 or cfg.maximum_pending_path_units == 0 or cfg.maximum_commands == 0 or cfg.maximum_command_name_bytes == 0 or cfg.publication_banks < 2 or cfg.publication_banks > 8 or
            cfg.maximum_namespace_entries == 0 or cfg.maximum_path_units == 0 or cfg.started_tick_ms < 0) return error.InvalidConfig;
    }
    /// Called only before ProcessTrace or on its command flow. No read acquires
    /// an output. Dependencies disappear with their final actual consumer.
    fn applyOutputs(self: *Owner, mask: u32) !void {
        try self.applyOutputTransition(mask);
        try self.checkAllocatorFault();
    }
    // Retirement must release ownership even after an earlier allocation fault.
    // Normal business transitions check the latch through applyOutputs above.
    fn applyOutputTransition(self: *Owner, mask: u32) !void {
        const old = self.active_outputs;
        const cfg = self.config;
        if (old == mask) return;
        // Mark transitions after successful construction; a failure terminates
        // this owner and its host reports availability, never a fallback.
        if (mask & 1 != 0 and old & 1 == 0) {
            self.capture = try capture.Session.create(self.outputAllocator(1, 0), &cfg.residency);
            self.capture.residency.profile = if (self.profile.enabled) &self.profile else null;
            self.capture.residency.setWindowAllocator(self.outputAllocator(1, 1)) catch |err| { self.capture.destroy(); return err; };
            self.readers = Readers.initCpu(self.outputAllocator(1, 0), self.capture, cfg.interrupts, self.processor_ids) catch |err| { self.capture.destroy(); return err; };
            self.readers.residency_enabled = self.applied_enabled & 1 != 0;
            self.readers.thread_diagnostics.recorded = self.diagnostic_recorded_override orelse (if (self.trace) |t| !t.consumer.realtime else false);
            if (self.diagnostic_source_override) |source| self.readers.thread_diagnostics.source = source;
            self.capture.active_traces = if (self.trace != null) 1 else 0;
            self.cpu_capacity_resets_seen = self.capture.residency.capacity_reset_count;
            self.active_outputs |= 1;
            self.invalidateCpu();
            self.cpu_initial_ready = true;
        }
        if (mask & 2 != 0 and old & 2 == 0) {
            self.interrupts = try irq.Session.create(self.outputAllocator(2, 0), &cfg.interrupts);
            self.interrupts.active_traces = if (self.trace != null) 1 else 0;
            self.active_outputs |= 2;
            self.interrupt_owner_serial = try std.math.add(u64, self.interrupt_owner_serial, 1);
        }
        if (mask & 4 != 0 and old & 4 == 0) {
            self.disk = try disk.Owner.init(self.outputAllocator(3, 0), .{ .byte_cap = @intCast(cfg.disk_byte_cap), .window_ms = cfg.window_ms, .retention_ms = cfg.retention_ms, .bucket_width_ms = cfg.disk_bucket_ms, .maximum_active_pairs = cfg.maximum_disk_pairs, .maximum_disks = cfg.maximum_disks, .maximum_cached_processes = cfg.maximum_cached_processes }, self.identity_source);
            self.active_outputs |= 4;
        }
        if (mask & 8 != 0 and old & 8 == 0) {
            self.network = try network.Owner.init(self.outputAllocator(4, 0), .{ .byte_cap = @intCast(cfg.network_byte_cap), .window_ms = cfg.window_ms, .retention_ms = cfg.retention_ms, .initial_event_capacity = @min(cfg.network_initial_events, cfg.maximum_network_events), .maximum_retained_events = cfg.maximum_network_events, .maximum_processes = cfg.maximum_network_processes }, self.identity_source);
            self.active_outputs |= 8;
        }
        if (mask & 16 != 0 and old & 16 == 0) {
            self.files = try fileio.FileIoState.init(self.outputAllocator(5, 0), .{ .byte_cap = @intCast(cfg.file_byte_cap), .max_entries = cfg.maximum_file_entries, .max_versions = cfg.maximum_file_versions, .max_name_units = cfg.maximum_path_units, .max_path_units = cfg.maximum_path_units }, if (self.trace != null and self.trace.?.consumer.realtime) .realtime else .recorded);
            self.active_outputs |= 16;
        }
        if (mask & 32 != 0 and old & 32 == 0) {
            self.renderer_sparse = .empty;
            if (self.host_callbacks == null) {
                self.renderer_revisions = try self.outputAllocator(7, 0).alloc(u64, cfg.maximum_renderer_processes);
                @memset(self.renderer_revisions, 0);
            }
            self.active_outputs |= 32;
        }
        if (mask & 64 != 0 and old & 64 == 0) self.active_outputs |= 64;
        const needs_history = mask & (1 | 4 | 8 | 16) != 0;
        if (needs_history and !self.history_active) {
            self.history = try History.init(self.outputAllocator(6, 0), cfg.maximum_history_entries, @intCast(cfg.history_byte_cap), self.trace != null and self.trace.?.consumer.realtime);
            self.history_active = true;
        }
        const needs_paths = mask & (2 | 16) != 0;
        if (needs_paths and !self.paths_active) {
            self.paths = try PathMap.initDeferredWithLimits(self.outputAllocator(0, 1), cfg.maximum_path_units, cfg.maximum_namespace_entries);
            self.paths_active = true;
        }
        if (mask & 1 == 0 and old & 1 != 0) {
            self.rememberLegacyBudget(1, &self.capture.budget, 0);
            self.readers.deinit(); self.capture.active_traces = 0; self.capture.destroy(); self.active_outputs &= ~@as(u32, 1);
        }
        if (mask & 2 == 0 and old & 2 != 0) { self.rememberLegacyBudget(2, &self.interrupts.budget, 0); if (self.trace) |t| t.deinitImageRound(); self.interrupts.active_traces = 0; self.interrupts.destroy(); self.active_outputs &= ~@as(u32, 2); }
        if (mask & 4 == 0 and old & 4 != 0) { self.rememberLegacyBudget(3, &self.disk.budget, self.disk.capacity_failures); self.disk.deinit(); self.active_outputs &= ~@as(u32, 4); }
        if (mask & 8 == 0 and old & 8 != 0) { self.rememberLegacyBudget(4, &self.network.budget, self.network.capacity_failures); self.network.deinit(); self.active_outputs &= ~@as(u32, 8); }
        if (mask & 16 == 0 and old & 16 != 0) { self.rememberLegacyBudget(5, &self.files.budget, 0); self.files.deinit(); self.active_outputs &= ~@as(u32, 16); }
        if (mask & 32 == 0 and old & 32 != 0) { self.renderer_sparse.deinit(self.outputAllocator(7, 0)); self.outputAllocator(7, 0).free(self.renderer_revisions); self.renderer_revisions = &.{}; self.active_outputs &= ~@as(u32, 32); }
        self.active_outputs = mask;
        if (!needs_history and self.history_active) { self.rememberLegacyBudget(6, &self.history.budget, 0); self.history.deinit(); self.history_active = false; }
        if (!needs_paths and self.paths_active) { self.paths.deinit(); self.paths_active = false; }
        if (mask & (16 | 64) == 0) {
            self.outputAllocator(8, 0).free(self.pending_events); self.outputAllocator(8, 0).free(self.pending_paths);
            self.pending_events = &.{}; self.pending_paths = &.{}; self.pending_event_count = 0; self.pending_path_count = 0;
        }
        if (self.has(1)) self.readers.interrupts = if (self.has(2)) self.interrupts else null;
        if (self.trace) |t| {
            t.owner = if (self.has(1)) self.capture else null;
            t.interrupts = if (self.has(2)) self.interrupts else null;
            t.paths = if (self.paths_active) &self.paths else null;
        }
        self.publication_mutex.lock();
        self.retirement_outputs.store(self.active_outputs, .release);
        for (self.banks) |*bank| bank.available_outputs &= mask;
        self.releaseInactiveComponents();
        self.publication_mutex.unlock();
        self.snapshotDiagnostics();
    }
    fn checkAllocatorFault(self: *const Owner) !void {
        if (self.allocatorFault()) |err| return err;
    }
    fn allocatorFault(self: *const Owner) ?anyerror {
        return switch (@as(host.Fault, @enumFromInt(self.allocation_fault.load(.acquire)))) {
            .none => null,
            .allocation => error.HostAllocationFailed,
            .remap => error.HostRemapFailed,
            .release => error.HostReleaseFailed,
            .byte_cap => error.OwnerByteCapExceeded,
            .invalid_resize => error.HostResizeContractViolation,
        };
    }
    fn failureReason(ctx: *anyopaque, err: anyerror) anyerror {
        return cast(ctx).allocatorFault() orelse err;
    }
    fn componentOutput(kind: usize) u32 { return switch (kind) { 1 => 1, 2 => 2, 3 => 4, 4 => 8, 5 => 16, 7 => 32, 8 => 16 | 64, else => 127 }; }
    /// Mutable children may retire while a different output is borrowed. Only
    /// references to this component delay returning its immutable Ledger pages.
    fn releaseInactiveComponents(self: *Owner) void {
        const retained = self.retirement_outputs.load(.acquire);
        for (self.banks) |*bank| {
            for (&bank.component_arenas, 0..) |*arena, kind| if (retained & componentOutput(kind) == 0 and bank.component_references[kind] == 0) { _ = arena.reset(.free_all); };
            if (retained & 1 == 0 and bank.component_references[1] == 0) self.dropCpuReference(bank);
        }
        if (retained & 1 == 0) for (self.cpu_banks) |*bank| if (bank.references == 0) { _ = bank.arena.reset(.free_all); };
    }
    pub fn setOutputs(self: *Owner, mask: u32) !void {
        if (mask & ~@as(u32, 127) != 0) return error.InvalidInput;
        self.queue_mutex.lock(); defer self.queue_mutex.unlock();
        if (self.trace) |t| if (t.consumer.processing.load(.acquire)) {
            self.pending_outputs = mask; self.pending_changes.store(true, .release); return;
        };
        try self.applyOutputs(mask);
    }
    /// Explicit fixture/SDK configuration, supplied before consumer processing.
    /// This is borrowed configuration, not child-owned state: the caller keeps
    /// its source context alive until this owner and all CPU children retire.
    pub fn configureDiagnosticSource(self: *Owner, source: ?@import("thread_diagnostics.zig").Source, recorded: bool) void {
        self.diagnostic_source_override = source;
        self.diagnostic_recorded_override = recorded;
        if (self.has(1)) {
            self.readers.thread_diagnostics.source = source orelse @import("thread_diagnostics.zig").windows;
            self.readers.thread_diagnostics.recorded = recorded;
        }
    }
    pub fn configureConsumerCounter(self: *Owner, config: counters.Config) !void {
        self.queue_mutex.lock(); defer self.queue_mutex.unlock();
        if (self.trace) |t| if (t.consumer.processing.load(.acquire)) {
            if (self.counter_commands.items.len >= self.config.maximum_commands) return error.CapacityExceeded;
            try self.counter_commands.append(self.controlAllocator(), config);
            self.pending_changes.store(true, .release); return;
        };
        try self.consumer_counters.configure(config);
    }
    /// Release allocates nothing, so stop and restart still prove it after an
    /// owner allocation fault: queued configurations for the token are dropped
    /// in place and its descriptor is frozen now, or marked for the next drain.
    pub fn retireConsumerCounter(self: *Owner, token: u64) !void {
        if (token == 0) return error.InvalidConfig;
        self.queue_mutex.lock(); defer self.queue_mutex.unlock();
        var index: usize = 0;
        while (index < self.counter_commands.items.len) {
            if (self.counter_commands.items[index].token == token) _ = self.counter_commands.orderedRemove(index) else index += 1;
        }
        if (self.trace) |t| if (t.consumer.processing.load(.acquire)) {
            try self.consumer_counters.requestRetire(token);
            self.pending_changes.store(true, .release); return;
        };
        try self.consumer_counters.retireToSnapshot(token);
    }
    pub fn takeRetiredConsumerCounter(self: *Owner, token: u64) ?counters.Frame {
        // Acknowledgment frees control-scope storage. Share the command
        // allocator's lock order (queue, then descriptor lookup), not its hot
        // event increments, which remain on the consumer flow without a lock.
        self.queue_mutex.lock(); defer self.queue_mutex.unlock();
        return self.consumer_counters.takeRetired(token);
    }
    pub fn queryResidencyDiagnostics(self: *Owner) !AggregationDiagnostics {
        self.publication_mutex.lock(); defer self.publication_mutex.unlock();
        return self.aggregation_diagnostics orelse error.NoPublication;
    }
    pub fn queryInterruptMetadata(self: *Owner) !InterruptMetadata {
        self.publication_mutex.lock(); defer self.publication_mutex.unlock();
        return self.interrupt_metadata orelse error.NoPublication;
    }
    pub fn queryRendererStamp(self: *Owner, pid: u32) !RendererStamp {
        if (pid == 0 or pid > std.math.maxInt(i32)) return error.InvalidInput;
        self.publication_mutex.lock(); defer self.publication_mutex.unlock();
        const bank = &self.banks[self.current_bank orelse return error.NoPublication];
        if (bank.available_outputs & 32 == 0) return error.NoPublication;
        const slot = (pid *% 2654435761) % self.config.maximum_renderer_processes;
        if (self.host_callbacks == null) return .{ .epoch = bank.view.renderer_epoch, .events_lost = bank.view.events_lost, .revision = bank.view.renderer_revisions.?[slot] };
        var low: usize = 0; var high = bank.renderer_rows.len;
        while (low < high) { const mid = low + (high - low) / 2; if (bank.renderer_rows[mid].slot < slot) low = mid + 1 else high = mid; }
        return .{ .epoch = bank.view.renderer_epoch, .events_lost = bank.view.events_lost, .revision = if (low < bank.renderer_rows.len and bank.renderer_rows[low].slot == slot) bank.renderer_rows[low].revision else 0 };
    }
    fn snapshotDiagnostics(self: *Owner) void {
        const aggregate: ?AggregationDiagnostics = if (self.has(1)) blk: {
            const d = self.capture.residency.getDiagnostics();
            break :blk .{ .qpc_frequency = d.qpc_frequency, .window_qpc = d.window_qpc, .maximum_active_pairs = d.maximum_active_pairs, .active_pair_count = d.active_pair_count, .slots_per_window = d.slots_per_window, .ring_slots = d.ring_slots, .pair_record_bytes = d.pair_record_bytes, .window_storage_bytes = d.window_storage_bytes, .maximum_window_storage_bytes = d.maximum_window_storage_bytes, .current_process_count = d.current_process_count, .current_thread_count = d.current_thread_count, .retained_process_identity_count = d.retained_process_identity_count, .retained_thread_identity_count = d.retained_thread_identity_count, .continuity_epoch = d.continuity_epoch, .capacity_reset_count = d.capacity_reset_count, .last_capacity_reset = d.last_capacity_reset };
        } else null;
        self.publication_mutex.lock();
        self.aggregation_diagnostics = aggregate;
        self.interrupt_metadata = if (self.has(2)) .{ .session_generation = @bitCast(self.interrupts.owner.generation), .owner_serial = self.interrupt_owner_serial, .active_modules = self.interrupts.owner.modules.len, .module_revision = self.interrupts.owner.modules.revision } else null;
        self.publication_mutex.unlock();
    }

    pub fn createExternal(span: []u8, cfg: Config, source: identity.Source) !*Owner {
        if (cfg.residency.processor_count != 0 or cfg.residency.processors != null) return error.InvalidConfig;
        if (cfg.size != @sizeOf(Config) or cfg.version != abi_version or cfg.reserved != 0 or cfg.network_initial_events == 0 or cfg.maximum_renderer_processes == 0 or cfg.byte_cap < span.len or
            cfg.maximum_pending_events == 0 or cfg.maximum_pending_path_units == 0 or cfg.maximum_commands == 0 or cfg.maximum_command_name_bytes == 0 or cfg.publication_banks < 2 or cfg.publication_banks > 8 or
            cfg.maximum_namespace_entries == 0 or cfg.maximum_path_units == 0 or cfg.started_tick_ms < 0) return error.InvalidConfig;
        const address = std.mem.alignForward(usize, @intFromPtr(span.ptr), @alignOf(Owner));
        const prefix = address - @intFromPtr(span.ptr);
        if (prefix > span.len or span.len - prefix < @sizeOf(Owner)) return error.InvalidConfig;
        const self: *Owner = @ptrFromInt(address);
        self.* = undefined;
        self.host_callbacks = null;
        self.active_outputs = 127;
        self.retirement_outputs = .init(127);
        self.pending_outputs = null;
        self.history_active = true;
        self.paths_active = true;
        self.identity_source = source;
        self.diagnostic_source_override = null;
        self.diagnostic_recorded_override = null;
        self.legacy_usage_history = emptyLegacyUsage(cfg);
        self.allocation_fault = .init(0);
        self.counter_commands = .empty;
        self.aggregation_diagnostics = null;
        self.interrupt_metadata = null;
        self.interrupt_owner_serial = 1;
        self.renderer_sparse = .empty;
        self.region = try Region.init(span[prefix + @sizeOf(Owner) ..]);
        errdefer self.region.deinit();
        self.budget = .{ .backing = self.region.allocator(), .transient_backing = self.region.transientAllocator(), .cap = @intCast(cfg.byte_cap), .current = @sizeOf(Owner), .peak = @sizeOf(Owner) };
        self.config = cfg;
        self.external_region_bytes = span.len;
        self.renderer_epoch = 1;
        self.publication_callback = null;
        self.publication_context = null;
        self.failure = .init(0);
        const a = self.budget.allocator();
        const transient = self.budget.transientAllocator();
        self.consumer_counters = try counters.Store.init(a, cfg.maximum_commands);
        errdefer self.consumer_counters.deinit();
        self.capture = try capture.Session.create(a, &cfg.residency);
        errdefer self.capture.destroy();
        self.interrupts = try irq.Session.create(a, &cfg.interrupts);
        errdefer self.interrupts.destroy();
        self.capture.budget.transient_backing = transient;
        self.interrupts.budget.transient_backing = transient;
        // Construction-only binding: this arena is still empty. Scratch stays
        // charged to IRQ and shared budgets while using high-end placement.
        std.debug.assert(self.interrupts.owner.publication_round.queryCapacity() == 0);
        self.interrupts.owner.publication_round.child_allocator = self.interrupts.budget.transientAllocator();
        self.disk = try disk.Owner.init(a, .{ .byte_cap = @intCast(cfg.disk_byte_cap), .window_ms = cfg.window_ms, .retention_ms = cfg.retention_ms, .bucket_width_ms = cfg.disk_bucket_ms, .maximum_active_pairs = cfg.maximum_disk_pairs, .maximum_disks = cfg.maximum_disks, .maximum_cached_processes = cfg.maximum_cached_processes }, source);
        errdefer self.disk.deinit();
        self.network = try network.Owner.init(a, .{ .byte_cap = @intCast(cfg.network_byte_cap), .window_ms = cfg.window_ms, .retention_ms = cfg.retention_ms, .initial_event_capacity = @min(cfg.network_initial_events, cfg.maximum_network_events), .maximum_retained_events = cfg.maximum_network_events, .maximum_processes = cfg.maximum_network_processes }, source);
        errdefer self.network.deinit();
        self.files = try fileio.FileIoState.init(a, .{ .byte_cap = @intCast(cfg.file_byte_cap), .max_entries = cfg.maximum_file_entries, .max_versions = cfg.maximum_file_versions, .max_name_units = cfg.maximum_path_units, .max_path_units = cfg.maximum_path_units }, .recorded);
        errdefer self.files.deinit();
        self.history = try History.init(a, cfg.maximum_history_entries, @intCast(cfg.history_byte_cap), false);
        errdefer self.history.deinit();
        self.paths = try PathMap.initDeferredWithLimits(a, cfg.maximum_path_units, cfg.maximum_namespace_entries);
        errdefer self.paths.deinit();
        self.processor_ids = try a.alloc(i32, cfg.residency.max_processors);
        errdefer a.free(self.processor_ids);
        for (self.processor_ids, 0..) |*id, i| id.* = @intCast(i);
        self.readers = try Readers.init(a, self.capture, self.interrupts, self.processor_ids);
        errdefer self.readers.deinit();
        std.debug.assert(self.readers.thread_diagnostics.round.queryCapacity() == 0);
        self.readers.thread_diagnostics.round.child_allocator = transient;
        self.commands = try a.alloc(p.Input, cfg.maximum_commands);
        errdefer a.free(self.commands);
        self.command_tickets = try a.alloc(u64, cfg.maximum_commands);
        errdefer a.free(self.command_tickets);
        self.command_names = try a.alloc(u8, cfg.maximum_command_name_bytes);
        errdefer a.free(self.command_names);
        self.renderer_revisions = try a.alloc(u64, cfg.maximum_renderer_processes);
        errdefer a.free(self.renderer_revisions);
        @memset(self.renderer_revisions, 0);
        self.pending_events = try a.alloc(Notification, cfg.maximum_pending_events);
        errdefer a.free(self.pending_events);
        self.pending_paths = try a.alloc(u16, cfg.maximum_pending_path_units);
        errdefer a.free(self.pending_paths);
        self.pending_event_count = 0;
        self.pending_path_count = 0;
        self.notification_sequence = 0;
        self.banks = try a.alloc(Bank, cfg.publication_banks);
        errdefer a.free(self.banks);
        for (self.banks) |*bank| {
            bank.* = .{ .arena = std.heap.ArenaAllocator.init(transient) };
            for (&bank.component_arenas) |*arena| arena.* = std.heap.ArenaAllocator.init(transient);
        }
        self.cpu_banks = try a.alloc(CpuBank, cfg.publication_banks);
        for (self.cpu_banks) |*bank| bank.* = .{ .arena = std.heap.ArenaAllocator.init(transient) };
        self.cpu_revision = .init(1);
        self.cpu_semantic = .init(1);
        self.cpu_sequence = 0;
        self.cpu_capacity_resets_seen = 0;
        self.cpu_initial_ready = true;
        self.cpu_pending_ticket = null;
        self.cpu_invalidated = .init(true);
        self.irq_processor_count_override = null;
        self.round = std.heap.ArenaAllocator.init(transient);
        self.publication_round = std.heap.ArenaAllocator.init(transient);
        self.trace = null;
        self.publication_mutex = .{};
        self.current_bank = null;
        self.queue_mutex = .{};
        self.command_count = 0;
        self.command_name_count = 0;
        self.pending_processor_count = null;
        self.pending_enabled = null;
        self.desired_enabled = 1;
        self.applied_enabled = 1;
        self.pending_changes = .init(false);
        self.next_command_ticket = 0;
        self.disk_transition_version = 0;
        self.disk_started_tick = cfg.started_tick_ms;
        self.network_started_tick = cfg.started_tick_ms;
        self.network_transition_version = 0;
        self.pending_tick = .init(-1);
        self.published_version = 0;
        self.command_version = 0;
        self.skipped_publications = 0;
        self.file_events = 0;
        self.events_lost = .init(cfg.interrupts.events_lost);
        self.failure_detail = .{};
        self.publication_stage = .none;
        self.profile = .{};
        self.failure_allocation = .{};
        self.failure_usage = self.collectUsage();
        self.failure_region_usage = self.collectRegionUsage();
        return self;
    }
    pub fn destroy(self: *Owner) !void {
        self.publication_mutex.lock();
        for (self.banks) |bank| if (bank.references != 0) {
            self.publication_mutex.unlock();
            return error.OutstandingLease;
        };
        self.publication_mutex.unlock();
        if (self.trace != null) return error.TraceOpen;
        if (self.host_callbacks) |callbacks| {
            try self.applyOutputTransition(0);
            const a = self.controlAllocator();
            for (self.banks) |*bank| { self.dropCpuReference(bank); bank.arena.deinit(); for (&bank.component_arenas) |*arena| arena.deinit(); }
            for (self.cpu_banks) |*bank| { std.debug.assert(bank.references == 0); bank.arena.deinit(); }
            self.round.deinit(); self.publication_round.deinit(); self.consumer_counters.deinit(); self.counter_commands.deinit(a);
            a.free(self.banks); a.free(self.cpu_banks); a.free(self.commands); a.free(self.command_tickets); a.free(self.command_names); a.free(self.processor_ids);
            for (&self.host_contexts) |*contexts| for (contexts) |*context| context.deinit();
            // Logical destruction transfers the final physical retirement to
            // the host scope even when Release reports a pending lease. Never
            // dereference or retry this root: a later host Flush may free it.
            _ = callbacks.release.?(callbacks.context, 0, self, @sizeOf(Owner), @intFromEnum(std.mem.Alignment.fromByteUnits(@alignOf(Owner))));
            return;
        }
        try self.applyOutputs(0);
        const a = self.budget.allocator();
        for (self.banks) |*bank| {
            self.dropCpuReference(bank);
            bank.arena.deinit();
            for (&bank.component_arenas) |*arena| arena.deinit();
        }
        for (self.cpu_banks) |*bank| {
            std.debug.assert(bank.references == 0);
            bank.arena.deinit();
        }
        a.free(self.cpu_banks);
        a.free(self.banks);
        self.round.deinit();
        self.publication_round.deinit();
        a.free(self.commands);
        a.free(self.command_tickets);
        a.free(self.command_names);
        self.consumer_counters.deinit();
        self.counter_commands.deinit(a);
        a.free(self.processor_ids);
        std.debug.assert(self.budget.current == @sizeOf(Owner));
        self.region.deinit();
        self.* = undefined;
    }
    pub fn hooks(self: *Owner) trace.Hooks {
        return .{ .context = self, .event = event, .raw = raw, .loss = loss, .loss_count = lossCount, .boundary = boundary, .thread = thread, .processor = processor, .enabled = enabled, .held = held, .admit = admit, .failed = failed, .creation = creation, .failure_reason = failureReason };
    }
    fn held(ctx: *anyopaque, interrupts: bool) bool { return cast(ctx).has(if (interrupts) 2 else 1); }
    fn admissionFacts(self: *Owner, qpc: i64) counters.Facts {
        return .{ .generation = @bitCast(self.config.interrupts.generation), .qpc = qpc, .observed_tick = if (self.trace) |t| t.eventTick(qpc) catch 0 else 0 };
    }
    fn admit(ctx: *anyopaque, value: native.decoder.Event) !void {
        const self = cast(ctx);
        var facts = self.admissionFacts(value.qpc);
        // The ImageLoad rundown can share a buffer with events from before its
        // keyword change. Apply queued outputs and bindings before admitting or
        // recording any image, not only at the buffer end.
        if (value.data == .image and self.pending_changes.load(.acquire)) try self.drain(facts.observed_tick);
        switch (value.data) {
            .cswitch => if (self.has(1)) {
                facts.mapped_processor = self.readers.resolveProcessor(value.processor) != null;
                facts.current_window_active = self.readers.residency_enabled;
                facts.current_window_start = self.capture.residency.context_switch_floor_qpc;
            },
            .interrupt => |v| {
                if (self.has(2)) facts.current_window_start = self.interrupts.owner.started_tick_ms;
                if (self.trace) |t| {
                    const metadata = t.consumer.metadata;
                    const anchor = if (metadata.realtime_clock) |sample| sample.midpoint() - @divTrunc(metadata.qpc_frequency, 10) else metadata.header_qpc orelse value.qpc;
                    const divisor: f64 = @floatFromInt(metadata.qpc_frequency);
                    facts.duration_ms = @as(f64, @floatFromInt(@max(value.qpc, anchor) - anchor)) * 1000.0 / divisor - @as(f64, @floatFromInt(@max(v.initial_qpc, anchor) - anchor)) * 1000.0 / divisor;
                }
            },
            .image => if (self.has(2)) { facts.current_window_start = self.interrupts.owner.started_tick_ms; },
            else => {},
        }
        admission.decoded(&self.consumer_counters, self.active_outputs, value, facts) catch |err| {
            if (self.trace) |t| t.invalidate(err) else self.recordFailure(-1);
        };
    }
    fn failed(ctx: *anyopaque, value: i32) void {
        cast(ctx).recordFailure(value);
    }
    /// Called on the single consumer flow after it has stopped mutating owner
    /// state. Release publishes one immutable diagnostic snapshot, without a
    /// bank allocation, even when the failing allocation exhausted its budget.
    pub fn recordFailure(self: *Owner, value: i32) void {
        if (self.failure.load(.acquire) != 0) return;
        self.failure_detail = .{ .result = value };
        self.failure_allocation = .{ .publication_stage = @intFromEnum(self.publication_stage), .allocation = if (self.host_callbacks == null) self.region.first_failure else .{} };
        if (self.trace) |t| {
            const name = if (t.failure_reason) |reason| @errorName(reason) else "";
            self.failure_detail.error_name = if (name.len == 0) null else name.ptr;
            self.failure_detail.error_name_length = @intCast(name.len);
            self.failure_detail.records = t.failure_records;
            self.failure_detail.qpc = t.failure_qpc;
        }
        self.failure_usage = self.collectUsage();
        self.failure_region_usage = self.collectRegionUsage();
        self.failure.store(value, .release);
    }
    fn creation(ctx: *anyopaque, pid: u32, qpc: i64) ?i64 {
        const self = cast(ctx);
        const t = self.trace orelse return null;
        // Recorded process identities belong to that capture. Present-day OS
        // PIDs must never enrich a historical process lifecycle.
        if (!t.consumer.realtime) return null;
        const event_time = t.consumer.metadata.qpcToFiletime(qpc) catch return null;
        if (!self.has(1 | 4 | 8)) return null;
        const lease = identity.Lease.open(self.identity_source, pid) orelse return null;
        defer lease.close();
        const birth = lease.creationFiletime() orelse return null;
        const final_birth = lease.creationFiletime() orelse return null;
        if (birth != final_birth or event_time < 0 or birth > @as(u64, @intCast(event_time))) return null;
        return std.math.cast(i64, birth);
    }
    fn cast(ctx: *anyopaque) *Owner {
        return @ptrCast(@alignCast(ctx));
    }
    fn event(ctx: *anyopaque, value: native.decoder.Event) !void {
        const self = cast(ctx);
        switch (value.data) {
            .process, .thread => if (self.has(1)) self.markCpuChanged(),
            .cswitch => if (self.has(1)) {
                const resets = self.capture.residency.capacity_reset_count;
                if (resets != self.cpu_capacity_resets_seen) {
                    self.cpu_capacity_resets_seen = resets;
                    self.invalidateCpu();
                    self.cpu_initial_ready = true;
                } else self.markCpuChanged();
            },
            else => {},
        }
        switch (value.data) {
            .process => |v| {
                if (self.has(4)) self.disk.processIdentityChanged(v.process_id);
                self.invalidateRenderer(v.process_id);
                if (self.has(64) and v.process_id > 4 and v.process_id <= std.math.maxInt(i32)) if (self.trace) |t| {
                    const wide = try std.unicode.utf8ToUtf16LeAlloc(self.round.allocator(), v.image_name);
                    defer _ = self.round.reset(.free_all);
                    try self.append(.{ .kind = if (v.lifecycle == .end or v.lifecycle == .rundown_end) 8 else 7, .flags = if (v.lifecycle == .rundown_start or v.lifecycle == .rundown_end) 1 else 0, .pid = v.process_id, .parent_pid = v.parent_id, .qpc = value.qpc, .tick_ms = try t.eventTick(value.qpc), .key = v.unique_key orelse 0 }, wide);
                };
            },
            .image => |v| if (self.trace) |t| {
                if (v.lifecycle != .rundown_end) self.invalidateRenderer(v.process_id);
                if (!self.has(16)) return;
                defer _ = self.round.reset(.free_all);
                if (v.path_utf16le.len % 2 != 0) return error.InvalidTraceRecord;
                const wide = try self.round.allocator().alloc(u16, v.path_utf16le.len / 2);
                for (wide, 0..) |*unit, i| unit.* = std.mem.readInt(u16, v.path_utf16le[i * 2 ..][0..2], .little);
                const mapped = try self.paths.map(self.round.allocator(), wide, self.config.maximum_path_units);
                try self.append(.{ .kind = if (v.lifecycle == .end or v.lifecycle == .rundown_end) 10 else 9, .flags = if (v.lifecycle == .rundown_start or v.lifecycle == .rundown_end) 1 else 0, .pid = v.process_id, .qpc = value.qpc, .tick_ms = try t.eventTick(value.qpc), .base = v.base, .size = v.size }, mapped);
            },
            .thread => |v| {
                const ended = v.lifecycle == .end or v.lifecycle == .rundown_end;
                if (self.history_active) { if (ended) try self.history.end(v.thread_id, v.process_id, value.qpc) else try self.history.start(v.thread_id, v.process_id); }
                if (self.has(1)) if (self.trace) |t| try self.readers.observeThread(v.process_id, v.thread_id, ended, try t.consumer.metadata.qpcToFiletime(value.qpc));
            },
            else => {},
        }
    }
    fn thread(ctx: *anyopaque, tid: u32, qpc: i64) ?u32 {
        const self = cast(ctx); return if (self.history_active) self.history.lookup(tid, qpc) else null;
    }
    fn processor(ctx: *anyopaque, index: u16) ?i32 {
        const self = cast(ctx); return if (self.has(1)) self.readers.resolveProcessor(index) else null;
    }
    fn enabled(ctx: *anyopaque, interrupts: bool) bool {
        const s = cast(ctx);
        return if (interrupts) s.has(2) else s.has(1) and s.readers.residency_enabled;
    }
    fn invalidateRenderer(self: *Owner, pid: u32) void {
        if (!self.has(32)) return;
        if (pid == 0 or pid > std.math.maxInt(i32)) return;
        if (self.host_callbacks != null) {
            const slot = (pid *% 2654435761) % self.config.maximum_renderer_processes;
            const previous = self.renderer_sparse.get(slot) orelse 0;
            if (previous == std.math.maxInt(i64)) { self.renderer_epoch = tryEpoch(self.renderer_epoch); self.renderer_sparse.clearAndFree(self.outputAllocator(7, 0)); }
            self.renderer_sparse.put(self.outputAllocator(7, 0), slot, if (previous == std.math.maxInt(i64)) 1 else previous + 1) catch |err| {
                if (self.trace) |t| t.invalidate(err) else self.recordFailure(-3);
            };
            return;
        }
        const slot = (pid *% 2654435761) % self.renderer_revisions.len;
        if (self.renderer_revisions[slot] == std.math.maxInt(i64)) {
            self.renderer_epoch = std.math.add(u64, self.renderer_epoch, 1) catch {
                self.failure.store(-1, .release);
                return;
            };
            @memset(self.renderer_revisions, 0);
        }
        self.renderer_revisions[slot] += 1;
    }
    fn loss(ctx: *anyopaque, tick: i64) void {
        // Bounded fixtures may explicitly mutate their capture loss fact.
        // Production Trace supplies the notice's count through lossCount.
        const self = cast(ctx);
        lossCount(ctx, tick, if (self.has(1)) self.capture.events_lost else self.events_lost.load(.acquire));
    }
    fn lossCount(ctx: *anyopaque, tick: i64, lost: u64) void {
        const self = cast(ctx);
        if (self.has(4)) self.disk.invalidate(tick);
        if (self.has(8)) self.network.invalidate(tick);
        self.events_lost.store(lost, .release);
        if (self.has(1)) {
            self.capture.events_lost = lost;
            self.capture.residency.resetAfterEventLoss() catch |err| {
                if (self.trace) |t| t.invalidate(err) else self.recordFailure(-1);
                return;
            };
            self.invalidateCpu();
        }
        if (self.has(2)) self.interrupts.owner.reset(tick, lost);
        self.cpu_initial_ready = true;
        self.renderer_epoch = tryEpoch(self.renderer_epoch);
        @memset(self.renderer_revisions, 0);
        if (self.host_callbacks != null and self.has(32)) self.renderer_sparse.clearAndFree(self.outputAllocator(7, 0));
        // Parser namespace/history survives loss just as TraceEvent does. Loss
        // invalidates measurement windows, not recorded identity evidence.
    }
    fn tryEpoch(current: u64) u64 {
        return if (current == std.math.maxInt(u64)) current else current + 1;
    }
    fn raw(ctx: *anyopaque, value: native.decoder.RawEvent) !void {
        const self = cast(ctx);
        admission.raw(&self.consumer_counters, self.active_outputs, value);
        if (self.paths_active) if (try @import("namespace.zig").decode(value)) |v| try @import("namespace.zig").apply(v, &self.paths);
        defer _ = self.round.reset(.free_all);
        const observed = if (self.has(16)) try self.files.observe(value, &self.paths, &self.history, self.round.allocator()) else null;
        if (observed) |file| {
            self.file_events +|= 1;
            if (self.trace) |t| {
                const tick = try t.eventTick(value.qpc);
                switch (file) {
                    .name => |v| try self.append(.{ .kind = if (v.opcode == 32) 2 else 3, .qpc = value.qpc, .tick_ms = tick, .key = v.file_key }, v.file_name),
                    .create => |v| try self.append(.{ .kind = 1, .qpc = value.qpc, .tick_ms = tick, .pid = v.process_id, .tid = v.thread_id, .object = v.file_object }, v.file_name),
                    .read_write => |v| if (v.kind == .write) {
                        try self.append(.{ .kind = 4, .qpc = value.qpc, .tick_ms = tick, .pid = v.process_id, .tid = v.thread_id, .key = v.file_key, .object = v.file_object, .size = v.io_size }, v.file_name);
                    },
                    .info => |v| if (v.kind != .set_info) {
                        try self.append(.{ .kind = if (v.kind == .rename) 5 else 6, .qpc = value.qpc, .tick_ms = tick, .pid = v.process_id, .tid = v.thread_id, .key = v.file_key, .object = v.file_object }, v.file_name);
                    },
                }
            }
        }
        if (!self.has(4 | 8)) return;
        const t = self.trace orelse return error.TraceNotOpen;
        const filetime = t.consumer.metadata.qpcToFiletime(value.qpc) catch null;
        const decoded = io.decode(value, if (filetime) |v| std.math.cast(u64, v) else null) catch |err| {
            _ = try admission.malformedIO(&self.consumer_counters, self.active_outputs, value, self.admissionFacts(value.qpc));
            return err;
        };
        if (decoded) |v| {
            const tick = try t.eventTick(value.qpc);
            switch (v) {
                .disk => |input| {
                    if (!self.has(4)) return;
                    if (self.pending_changes.load(.acquire)) try self.drain(tick);
                    var resolved = input;
                    if (input.process_id == null) {
                        if (input.thread_id) |tid| resolved.process_id = self.history.lookup(tid, value.qpc);
                    }
                    const observation = try admission.ioEvent(&self.consumer_counters, self.active_outputs, .{ .disk = resolved }, self.admissionFacts(value.qpc));
                    try self.disk.observe(resolved, tick);
                    if (self.disk.last_rejection == .before_process_birth) if (observation.disk) |disk_admission| self.consumer_counters.rejectObservation(disk_admission, .disk_transfer, .before_process_birth);
                },
                .network => |input| {
                    if (!self.has(8)) return;
                    if (self.pending_changes.load(.acquire)) try self.drain(tick);
                    var resolved = input;
                    if (input.process_id == null) if (input.thread_id) |tid| { resolved.process_id = self.history.lookup(tid, value.qpc); };
                    var facts = self.admissionFacts(value.qpc);
                    facts.at_capacity = self.network.event_count == self.network.config.maximum_retained_events;
                    facts.current_incomplete_through = self.network.incomplete_through_tick;
                    _ = try admission.ioEvent(&self.consumer_counters, self.active_outputs, .{ .network = resolved }, facts);
                    try self.network.observe(resolved, tick);
                },
            }
        }
    }
    fn append(self: *Owner, value: Notification, path: ?[]const u16) !void {
        const units = if (path) |v| v.len else 0;
        if (!self.has(16 | 64)) return;
        if (self.pending_event_count == self.config.maximum_pending_events or units > self.config.maximum_pending_path_units - self.pending_path_count) return error.CapacityExceeded;
        const a = self.outputAllocator(8, 0);
        if (self.pending_event_count == self.pending_events.len) {
            self.pending_events = try a.realloc(self.pending_events, @min(self.config.maximum_pending_events, @max(8, self.pending_event_count * 2)));
        }
        if (units > self.pending_paths.len - self.pending_path_count) {
            const old_address = @intFromPtr(self.pending_paths.ptr);
            self.pending_paths = try a.realloc(self.pending_paths, @min(self.config.maximum_pending_path_units, @max(self.pending_path_count + units, @max(policy.etw_initial_pending_path_units, self.pending_paths.len * 2))));
            for (self.pending_events[0..self.pending_event_count]) |*note| if (note.path) |old| { note.path = self.pending_paths.ptr + (@intFromPtr(old) - old_address) / @sizeOf(u16); };
        }
        var copy = value;
        if (self.trace) |t| copy.time_filetime_100ns = try t.consumer.metadata.qpcToFiletime(value.qpc);
        if (path) |v| {
            const dest = self.pending_paths[self.pending_path_count..][0..units];
            @memcpy(dest, v);
            copy.path = dest.ptr;
            copy.path_len = @intCast(units);
            self.pending_path_count += units;
        }
        self.pending_events[self.pending_event_count] = copy;
        self.pending_event_count += 1;
    }
    fn markCpuChanged(self: *Owner) void {
        _ = self.cpu_revision.fetchAdd(1, .release);
    }
    fn invalidateCpu(self: *Owner) void {
        self.cpu_invalidated.store(true, .release);
        _ = self.cpu_semantic.fetchAdd(1, .release);
        self.markCpuChanged();
    }
    /// Caller holds publication_mutex, or has joined and drained external reads.
    fn dropCpuReference(self: *Owner, bank: *Bank) void {
        if (bank.cpu_index) |index| {
            std.debug.assert(self.cpu_banks[index].references > 0);
            self.cpu_banks[index].references -= 1;
            bank.cpu_index = null;
            if (self.cpu_banks[index].references == 0) _ = self.cpu_banks[index].arena.reset(.free_all);
        }
    }
    // Called under publication_mutex only for a superseded bank, never the
    // non-current bank being constructed by the consumer outside that mutex.
    fn retireSupersededBank(self: *Owner, index: usize) void {
        const bank = &self.banks[index];
        if (bank.component_references[1] == 0) self.dropCpuReference(bank);
        for (&bank.component_arenas, bank.component_references) |*arena, references|
            if (references == 0) { _ = arena.reset(.free_all); };
        if (bank.references == 0) _ = bank.arena.reset(.free_all);
    }
    /// Only immutable banks/atomics are inspected from cold request callers.
    fn cpuBankValid(self: *const Owner, bank: *const Bank) bool {
        return bank.available_outputs & 1 != 0 and bank.cpu_metadata.sequence != 0 and !self.cpu_invalidated.load(.acquire) and
            bank.cpu_metadata.semantic == self.cpu_semantic.load(.acquire) and
            bank.cpu_metadata.events_lost == self.events_lost.load(.acquire);
    }
    /// queue_mutex protects pending configuration and tracked staging.
    fn hasQueuedCpuMutation(self: *const Owner) bool {
        if (self.pending_processor_count != null) return true;
        if (self.pending_enabled) |flags| if ((flags ^ self.applied_enabled) & 1 != 0) return true;
        for (self.commands[0..self.command_count]) |input| {
            if (input.kind <= 12 or input.kind == 16) return true;
        }
        return false;
    }
    pub fn requestResidency(self: *Owner) !ResidencyRequest {
        if (self.failure.load(.acquire) != 0) return error.Invalidated;
        self.queue_mutex.lock();
        defer self.queue_mutex.unlock();
        if (!self.has(1)) return error.NoPublication;
        var pending = self.cpu_pending_ticket orelse 0;
        for (self.commands[0..self.command_count], self.command_tickets[0..self.command_count]) |input, ticket| {
            if (input.kind == 20) pending = @max(pending, ticket);
        }
        if (pending != 0) return .{ .ticket = pending, .pending = true };
        self.publication_mutex.lock();
        if (self.current_bank) |index| {
            const bank = &self.banks[index];
            if (self.cpuBankValid(bank) and bank.cpu_metadata.revision == self.cpu_revision.load(.acquire) and !self.hasQueuedCpuMutation()) {
                const result: ResidencyRequest = .{ .ticket = bank.cpu_metadata.applied_ticket, .pending = false };
                self.publication_mutex.unlock();
                return result;
            }
        }
        self.publication_mutex.unlock();
        try self.ensureCommands(self.command_count + 1, self.command_name_count);
        const ticket = try std.math.add(u64, self.next_command_ticket, 1);
        self.commands[self.command_count] = .{ .kind = 20 };
        self.command_tickets[self.command_count] = ticket;
        self.next_command_ticket = ticket;
        self.command_count += 1;
        self.pending_changes.store(true, .release);
        return .{ .ticket = ticket, .pending = true };
    }
    /// Uses the same pure closed-boundary prelude as the exact snapshot core.
    fn cpuReadiness(self: *Owner) p.View {
        if (!self.has(1)) return .{};
        const metadata = self.capture.residency.snapshotMetadata();
        return .{ .frequency = metadata.qpc_frequency, .start_qpc = metadata.window_start_qpc, .through_qpc = metadata.measured_through_qpc, .time_ticks = metadata.measured_through orelse 0, .flags = @as(u32, if (metadata.is_complete) 1 else 0) | @as(u32, if (metadata.measured_through != null) 2 else 0) | @as(u32, if (metadata.boundaries_ready) 4 else 0), .current_bytes = self.capture.budget.current, .peak_bytes = self.capture.budget.peak };
    }
    pub fn submit(self: *Owner, inputs: []const p.Input) !void {
        _ = try self.stage(inputs, false);
    }
    pub fn submitTracked(self: *Owner, inputs: []const p.Input) !u64 {
        if (inputs.len == 0) return error.InvalidInput;
        return self.stage(inputs, true);
    }
    fn stage(self: *Owner, inputs: []const p.Input, tracked: bool) !u64 {
        self.queue_mutex.lock();
        defer self.queue_mutex.unlock();
        if (inputs.len > self.config.maximum_commands - self.command_count) return error.CapacityExceeded;
        var bytes: usize = 0;
        for (inputs) |input| {
            if (input.kind < 1 or input.kind > 21 or input.kind == 17 or (input.name_len != 0 and input.name == null)) return error.InvalidInput;
            if (input.kind == 18 and (input.flags == 0 or input.flags & ~@as(u32, 12) != 0 or input.time_ticks < 0)) return error.InvalidInput;
            if (input.kind == 19 and (input.unique == 0 or input.time_ticks <= 0 or input.qpc < input.time_ticks)) return error.InvalidInput;
            if (input.kind == 20 and (input.flags != 0 or input.qpc != 0 or input.time_ticks != 0 or input.pid != 0 or input.tid != 0 or
                input.old_pid != 0 or input.old_tid != 0 or input.processor != 0 or input.reserved != 0 or input.unique != 0 or
                input.start != 0 or input.name != null or input.name_len != 0 or input.reserved2 != 0)) return error.InvalidInput;
            if (input.kind == 21 and (input.pid <= 0 or input.flags != 0 or input.qpc != 0 or input.time_ticks != 0 or input.tid != 0 or
                input.old_pid != 0 or input.old_tid != 0 or input.processor != 0 or input.reserved != 0 or input.unique != 0 or
                input.start != 0 or input.name != null or input.name_len != 0 or input.reserved2 != 0)) return error.InvalidInput;
            bytes = try std.math.add(usize, bytes, input.name_len);
        }
        if (bytes > self.config.maximum_command_name_bytes - self.command_name_count) return error.CapacityExceeded;
        try self.ensureCommands(self.command_count + inputs.len, self.command_name_count + bytes);
        const final_ticket = if (tracked) try std.math.add(u64, self.next_command_ticket, @intCast(inputs.len)) else 0;
        for (inputs) |input| if (input.kind == 8 or input.kind == 9 or input.kind == 10 or input.kind == 16) self.invalidateCpu();
        for (inputs) |input| {
            var copy = input;
            if (input.name_len > 0) {
                const dest = self.command_names[self.command_name_count..][0..input.name_len];
                @memcpy(dest, input.name.?[0..input.name_len]);
                copy.name = dest.ptr;
                self.command_name_count += input.name_len;
            }
            if (tracked) self.next_command_ticket += 1;
            self.commands[self.command_count] = copy;
            self.command_tickets[self.command_count] = if (tracked) self.next_command_ticket else 0;
            self.command_count += 1;
        }
        if (inputs.len != 0) self.pending_changes.store(true, .release);
        return final_ticket;
    }
    fn ensureCommands(self: *Owner, count: usize, names: usize) !void {
        if (count > self.config.maximum_commands or names > self.config.maximum_command_name_bytes) return error.CapacityExceeded;
        const a = self.controlAllocator();
        if (count > self.commands.len or count > self.command_tickets.len) {
            const capacity = @min(self.config.maximum_commands, @max(count, @max(4, self.commands.len * 2)));
            if (count > self.commands.len) self.commands = try a.realloc(self.commands, capacity);
            if (count > self.command_tickets.len) self.command_tickets = try a.realloc(self.command_tickets, capacity);
        }
        if (names > self.command_names.len) {
            const address = @intFromPtr(self.command_names.ptr);
            const capacity = @min(self.config.maximum_command_name_bytes, @max(names, @max(64, self.command_names.len * 2)));
            self.command_names = try a.realloc(self.command_names, capacity);
            for (self.commands[0..self.command_count]) |*input| if (input.name_len != 0) {
                const offset = @intFromPtr(input.name.?) - address;
                input.name = self.command_names.ptr + offset;
            };
        }
    }
    /// Applied commands no longer need their staging: a host-backed owner returns
    /// buffers grown past the minimum (4 entries, 64 name bytes), so a seed burst
    /// does not keep its high-water mark. Region owners preallocate and keep theirs.
    fn releaseCommandBuffers(self: *Owner) void {
        if (self.host_callbacks == null) return;
        const a = self.controlAllocator();
        if (self.commands.len > 4 or self.command_tickets.len > 4) {
            a.free(self.commands);
            a.free(self.command_tickets);
            self.commands = &.{};
            self.command_tickets = &.{};
        }
        if (self.command_names.len > 64) {
            a.free(self.command_names);
            self.command_names = &.{};
        }
    }
    pub fn configure(self: *Owner, ids: []const i32, flags: u32) !void {
        if (ids.len == 0 or ids.len > self.processor_ids.len or flags & ~@as(u32, 1) != 0) return error.InvalidInput;
        for (ids, 0..) |id, i| {
            if (id < 0 or std.mem.indexOfScalar(i32, ids[0..i], id) != null) return error.InvalidInput;
        }
        self.queue_mutex.lock();
        defer self.queue_mutex.unlock();
        @memcpy(self.processor_ids[0..ids.len], ids);
        self.pending_processor_count = ids.len;
        self.invalidateCpu();
        self.pending_enabled = flags;
        self.desired_enabled = flags;
        self.pending_changes.store(true, .release);
    }
    pub fn configureProcessors(self: *Owner, ids: []const i32) !void {
        if (ids.len == 0 or ids.len > self.processor_ids.len) return error.InvalidInput;
        for (ids, 0..) |id, i| if (id < 0 or std.mem.indexOfScalar(i32, ids[0..i], id) != null) return error.InvalidInput;
        self.queue_mutex.lock();
        defer self.queue_mutex.unlock();
        @memcpy(self.processor_ids[0..ids.len], ids);
        self.pending_processor_count = ids.len;
        self.invalidateCpu();
        self.pending_changes.store(true, .release);
    }
    pub fn setEnabled(self: *Owner, mask: u32, value: u32) !void {
        if (mask & ~@as(u32, 1) != 0 or value & ~mask != 0) return error.InvalidInput;
        self.queue_mutex.lock();
        defer self.queue_mutex.unlock();
        if (mask & 1 != 0 and value & 1 != 0 and self.desired_enabled & 1 == 0) self.invalidateCpu();
        self.desired_enabled = (self.desired_enabled & ~mask) | value;
        self.pending_enabled = self.desired_enabled;
        self.pending_changes.store(true, .release);
    }
    fn applyEnabled(self: *Owner, flags: u32) !void {
        if (self.has(1)) {
            const resumed = flags & 1 != 0 and !self.readers.residency_enabled;
            const paused = flags & 1 == 0 and self.readers.residency_enabled;
            try self.readers.setResidencyEnabled(flags & 1 != 0);
            if (resumed) { self.markCpuChanged(); self.cpu_initial_ready = true; }
            if (paused) try self.capture.residency.releaseWindowStorage();
        }
        self.applied_enabled = flags;
    }
    pub fn drain(self: *Owner, _: i64) !void {
        self.queue_mutex.lock();
        defer self.queue_mutex.unlock();
        // Retirement first: it cannot fail, and no queued configuration names a
        // retiring token, so an allocation failure below never strands a release.
        self.consumer_counters.applyRetireRequests();
        if (self.pending_outputs) |outputs| { try self.applyOutputs(outputs); self.pending_outputs = null; }
        for (self.counter_commands.items) |config| try self.consumer_counters.configure(config);
        self.counter_commands.clearRetainingCapacity();
        if (self.pending_processor_count) |count| {
            if (self.has(1)) try self.readers.configureProcessors(self.processor_ids[0..count]);
            self.markCpuChanged();
            self.cpu_initial_ready = true;
            self.pending_processor_count = null;
        }
        if (self.pending_enabled) |flags| {
            try self.applyEnabled(flags);
            self.pending_enabled = null;
        }
        for (self.commands[0..self.command_count], self.command_tickets[0..self.command_count]) |*input, ticket| {
            if (input.kind == 18) {
                if (input.flags & 4 != 0 and self.has(4)) {
                    self.disk.resetCapture();
                    self.disk_started_tick = input.time_ticks;
                    self.disk_transition_version = ticket;
                }
                if (input.flags & 8 != 0 and self.has(8)) {
                    self.network.invalidate(input.time_ticks);
                    self.network_started_tick = input.time_ticks;
                    self.network_transition_version = ticket;
                }
            } else if (input.kind == 21) {
                self.irq_processor_count_override = @intCast(input.pid);
            } else if (input.kind == 20) {
                if (self.has(1)) self.cpu_pending_ticket = @max(self.cpu_pending_ticket orelse 0, ticket);
            } else if (input.kind == 15) {
                if (self.has(2)) self.interrupts.owner.bind(input.qpc, self.events_lost.load(.acquire));
            } else {
                if (self.has(1)) try self.readers.apply(input);
                if (input.kind <= 12 or input.kind == 16) self.markCpuChanged();
                if (input.kind == 8 or input.kind == 9 or input.kind == 10 or input.kind == 16) self.cpu_initial_ready = true;
            }
            if (ticket != 0) self.command_version = ticket;
        }
        self.pending_changes.store(false, .release);
        self.command_count = 0;
        self.command_name_count = 0;
        self.releaseCommandBuffers();
        self.snapshotDiagnostics();
        try self.checkAllocatorFault();
    }
    fn boundary(ctx: *anyopaque) !void {
        const self = cast(ctx);
        const t = self.trace orelse return;
        const requested = self.pending_tick.swap(-1, .acq_rel);
        const tick = if (requested >= 0) requested else try t.eventTick(t.last_event_qpc);
        try self.drain(tick);
        try self.publishBuffer(tick, false);
    }
    /// Explicit fixture/finite EOF publication forces the exact current CPU state.
    pub fn publish(self: *Owner, tick: i64) !void {
        try self.publishBuffer(tick, true);
    }
    pub fn finish(self: *Owner) !void {
        const t = self.trace orelse return;
        const requested = self.pending_tick.swap(-1, .acq_rel);
        const tick = if (requested >= 0) requested else try t.eventTick(t.last_event_qpc);
        try self.drain(tick);
        try self.publish(tick);
    }
    fn publishBuffer(self: *Owner, tick: i64, force_cpu: bool) !void {
        self.profile.begin(.bank_select);
        defer self.profile.end();
        self.publication_mutex.lock();
        var chosen: ?usize = null;
        for (self.banks, 0..) |bank, i| if ((self.current_bank == null or self.current_bank.? != i) and bank.references == 0) {
            chosen = i;
            break;
        };
        if (chosen) |index| {
            self.dropCpuReference(&self.banks[index]);
            self.profile.begin(.bank_reset);
            // Cold releases also retire inactive component arenas. Reset this
            // reusable header under the same mutex before building any active
            // component; its inactive arenas then remain empty throughout.
            _ = self.banks[index].arena.reset(.free_all);
            for (&self.banks[index].component_arenas) |*arena| _ = arena.reset(.free_all);
        }
        self.publication_mutex.unlock();
        const index = chosen orelse {
            self.skipped_publications +|= 1;
            return;
        };
        const bank = &self.banks[index];
        const da = bank.component_arenas[3].allocator();
        const na = bank.component_arenas[4].allocator();
        const notify_a = bank.component_arenas[8].allocator();
        const cpu_a = bank.component_arenas[1].allocator();
        const irq_a = bank.component_arenas[2].allocator();
        const renderer_a = bank.component_arenas[7].allocator();
        var disk_round = std.heap.ArenaAllocator.init(self.outputAllocator(3, 1));
        var network_round = std.heap.ArenaAllocator.init(self.outputAllocator(4, 1));
        var cpu_round = std.heap.ArenaAllocator.init(self.outputAllocator(1, 1));
        const disk_scratch = disk_round.allocator();
        const network_scratch = network_round.allocator();
        const scratch = cpu_round.allocator();
        var scratch_reset = false;
        defer if (!scratch_reset) {
            disk_round.deinit(); network_round.deinit(); cpu_round.deinit();
        };
        // Scratch capacity follows occupied owner state, never configured caps.
        // Only final ABI rows remain in the immutable publication bank.
        self.profile.begin(.io);
        self.publication_stage = .io_scratch;
        const dp = try disk_scratch.alloc(disk.ProcessRow, if (self.has(4)) self.disk.pairs.count() else 0);
        const dd = try disk_scratch.alloc(disk.DiskRow, if (self.has(4)) self.disk.pairs.count() else 0);
        const kd = try disk_scratch.alloc(u32, if (self.has(4)) self.disk.disks.count() else 0);
        const dw = if (self.has(4)) try self.disk.snapshot(tick, self.disk_started_tick, dp, dd, kd) else disk.Window{ .complete = false, .processes = &.{}, .disk_rows = &.{}, .disks = &.{}, .start_tick_ms = tick, .through_tick_ms = tick, .window_ms = self.config.window_ms, .process_revision = 0 };
        const nw = if (self.has(8)) try self.network.snapshotAllocated(network_scratch, tick, self.network_started_tick) else network.Window{ .complete = false, .rows = &.{}, .start_tick_ms = tick, .through_tick_ms = tick, .window_ms = self.config.window_ms };
        self.publication_stage = .io_final;
        const process_rows = try da.alloc(ProcessDisk, dw.processes.len);
        for (process_rows, dw.processes) |*out, row| out.* = .{ .pid = row.process_id, .flags = @as(u32, if (row.available) 1 else 0) | @as(u32, if (row.process_birth_filetime_100ns != null) 2 else 0), .birth = row.process_birth_filetime_100ns orelse 0, .read_bytes = row.read_bytes, .write_bytes = row.write_bytes, .read_rate = row.read_bytes_per_second, .write_rate = row.write_bytes_per_second, .read_count = row.read_operation_count, .write_count = row.write_operation_count };
        const disk_rows = try da.alloc(Disk, dw.disk_rows.len);
        for (disk_rows, dw.disk_rows) |*out, row| out.* = .{ .pid = row.process_id, .disk = row.disk_number, .flags = @as(u32, if (row.available) 1 else 0) | @as(u32, if (row.process_birth_filetime_100ns != null) 2 else 0), .birth = row.process_birth_filetime_100ns orelse 0, .read_bytes = row.read_bytes, .write_bytes = row.write_bytes };
        const network_rows = try na.alloc(Network, nw.rows.len);
        for (network_rows, nw.rows) |*out, row| out.* = .{ .pid = row.process_id, .flags = @as(u32, if (row.available) 1 else 0) | @as(u32, if (row.process_birth_filetime_100ns != null) 2 else 0), .birth = row.process_birth_filetime_100ns orelse 0, .receive_bytes = row.receive_bytes, .send_bytes = row.send_bytes, .receive_rate = row.receive_bytes_per_second, .send_rate = row.send_bytes_per_second, .event_count = row.event_count, .external_event_count = row.external_event_count, .external_receive_bytes = row.external_receive_bytes, .external_send_bytes = row.external_send_bytes, .external_receive_rate = row.external_receive_bytes_per_second, .external_send_rate = row.external_send_bytes_per_second };
        const known_disks = try da.dupe(u32, dw.disks);
        self.profile.begin(.notifications);
        self.publication_stage = .notifications;
        const notifications = try notify_a.dupe(Notification, self.pending_events[0..self.pending_event_count]);
        for (notifications) |*note| if (note.path) |ptr| {
            note.path = (try notify_a.dupe(u16, ptr[0..note.path_len])).ptr;
        };
        self.profile.begin(.residency);
        self.publication_stage = .residency;
        const revision = self.cpu_revision.load(.acquire);
        const semantic = self.cpu_semantic.load(.acquire);
        const loss_stamp = self.events_lost.load(.acquire);
        const readiness = self.cpuReadiness();
        const old_bank = if (self.current_bank) |old_index| &self.banks[old_index] else null;
        var cpu_index: ?usize = if (self.has(1)) (if (old_bank) |old| old.cpu_index else null) else null;
        var cpu_metadata: CpuMetadata = if (old_bank) |old| old.cpu_metadata else .{};
        var residency_view: p.View = if (self.has(1)) (if (old_bank) |old| old.view.residency else .{}) else .{};
        const full_cpu = self.has(1) and (self.readers.residency_enabled or self.host_callbacks == null) and (force_cpu or self.cpu_pending_ticket != null or (self.cpu_initial_ready and readiness.flags & 1 != 0));
        const warming_changed = self.has(1) and (self.readers.residency_enabled or self.host_callbacks == null) and (self.cpu_invalidated.load(.acquire) or cpu_metadata.sequence == 0 or
            cpu_metadata.semantic != semantic or cpu_metadata.events_lost != loss_stamp or
            (self.cpu_initial_ready and cpu_metadata.revision != revision));
        var cpu_installed = false;
        if (full_cpu) {
            var free_cpu: ?usize = null;
            self.publication_mutex.lock();
            for (self.cpu_banks, 0..) |component, i| if (component.references == 0) {
                free_cpu = i;
                break;
            };
            self.publication_mutex.unlock();
            // Each referenced CPU component belongs to a current/live aggregate
            // header; a reusable aggregate header therefore guarantees a slot.
            std.debug.assert(free_cpu != null);
            if (free_cpu) |component_index| {
                const component = &self.cpu_banks[component_index];
                _ = component.arena.reset(.free_all);
                if (self.profile.enabled) {
                    self.profile.view.cpu_pairs_bound +|= self.capture.residency.live_pairs;
                }
                component.view = try self.readers.publishResidency(component.arena.allocator(), scratch);
                if (self.profile.enabled) self.profile.view.cpu_rows +|= component.view.count;
                residency_view = component.view;
                cpu_index = component_index;
                cpu_installed = true;
            }
        } else if (warming_changed) {
            residency_view = readiness;
            residency_view.flags &= ~@as(u32, 1);
            cpu_index = null;
            cpu_installed = true;
        }
        if (cpu_installed) {
            self.cpu_sequence = try std.math.add(u64, self.cpu_sequence, 1);
            cpu_metadata = .{ .sequence = self.cpu_sequence, .events_lost = loss_stamp, .applied_ticket = self.command_version, .revision = revision, .semantic = semantic };
        }
        self.profile.begin(.interrupts);
        self.publication_stage = .interrupts;
        if (self.profile.enabled and self.has(2)) {
            self.profile.view.irq_identities +|= self.interrupts.owner.identity_len;
            self.profile.view.irq_cells +|= self.interrupts.owner.cell_len;
        }
        const interrupt_view = if (self.has(2)) try Readers.publishInterruptOwner(self.interrupts, irq_a, tick, self.interruptProcessorCount()) else irq.View{};
        self.published_version +|= 1;
        bank.view = .{ .version = self.published_version, .through_tick_ms = tick, .window_ms = self.config.window_ms, .flags = @as(u32, if (dw.complete) 1 else 0) | @as(u32, if (nw.complete) 2 else 0), .process_disk_count = @intCast(process_rows.len), .process_disk = process_rows.ptr, .disk_count = @intCast(disk_rows.len), .disks = disk_rows.ptr, .known_disk_count = @intCast(dw.disks.len), .known_disks = known_disks.ptr, .network_count = @intCast(network_rows.len), .network = network_rows.ptr, .namespace_entries = if (self.paths_active) @intCast(self.paths.count()) else 0, .file_events = self.file_events, .file_entries = if (self.has(16)) self.files.entryCount() else 0, .command_version = self.command_version, .skipped_publications = self.skipped_publications, .residency = residency_view, .interrupts = interrupt_view };
        self.profile.begin(.diagnostics);
        self.publication_stage = .diagnostics;
        if (self.has(1)) bank.view.diagnostics = try self.readers.publishDiagnostics(cpu_a);
        self.profile.begin(.renderer);
        self.publication_stage = .renderer;
        bank.renderer_rows = &.{};
        if (self.has(32)) {
            if (self.host_callbacks == null) bank.view.renderer_revisions = (try renderer_a.dupe(u64, self.renderer_revisions)).ptr else {
                const rows = try renderer_a.alloc(RendererRow, self.renderer_sparse.count());
                var iterator = self.renderer_sparse.iterator(); var row_index: usize = 0;
                while (iterator.next()) |entry| { rows[row_index] = .{ .slot = entry.key_ptr.*, .revision = entry.value_ptr.* }; row_index += 1; }
                std.sort.heap(RendererRow, rows, {}, struct { fn less(_: void, a: RendererRow, b: RendererRow) bool { return a.slot < b.slot; } }.less);
                bank.renderer_rows = rows;
            }
        }
        bank.view.renderer_epoch = self.renderer_epoch;
        bank.view.notifications = notifications.ptr;
        bank.view.notification_count = @intCast(notifications.len);
        bank.view.notification_first = self.notification_sequence;
        bank.view.events_lost = self.events_lost.load(.acquire);
        bank.view.network_transition_version = self.network_transition_version;
        bank.view.disk_transition_version = self.disk_transition_version;
        self.profile.begin(.scratch_reset);
        self.publication_stage = .scratch_reset;
        disk_round.deinit(); network_round.deinit(); cpu_round.deinit();
        scratch_reset = true;
        try self.checkAllocatorFault();
        self.profile.begin(.install);
        if (self.profile.enabled) {
            self.profile.view.publications +|= 1;
            self.profile.view.notifications +|= self.pending_event_count;
            self.profile.view.path_units +|= self.pending_path_count;
        }
        bank.view.owner_usage = self.collectUsage();
        bank.view.region_usage = self.collectRegionUsage();
        // Captured only on the consumer/publication flow, before its signal.
        // Cold readers never inspect mutable Trace counters or metadata.
        bank.published_progress = if (self.trace) |t| .{
            .bank_sequence = bank.view.version,
            .cpu_sequence = cpu_metadata.sequence,
            .cpu_applied_ticket = cpu_metadata.applied_ticket,
            .status = .{ .windows_status = t.consumer.last_windows_error, .owner_status = t.failure, .records = t.consumer.counters.records, .decoded = t.consumer.counters.decoded, .rejected = t.consumer.counters.rejected, .unhandled = t.consumer.counters.unhandled, .switches = t.switches, .events_lost = t.consumer.counters.events_lost, .buffers_lost = t.consumer.counters.buffers_lost, .qpc_frequency = t.consumer.metadata.qpc_frequency, .processor_count = t.consumer.metadata.processor_count, .os_buffer_size = t.consumer.metadata.os_buffer_size },
        } else null;
        self.queue_mutex.lock();
        self.publication_mutex.lock();
        const previous_bank = self.current_bank;
        bank.cpu_index = cpu_index;
        bank.cpu_metadata = cpu_metadata;
        if (cpu_index) |component_index| self.cpu_banks[component_index].references += 1;
        self.current_bank = index;
        bank.available_outputs = self.active_outputs;
        if (previous_bank) |old_index| self.retireSupersededBank(old_index);
        if (cpu_installed) {
            if (self.cpu_pending_ticket) |ticket| {
                if (cpu_metadata.applied_ticket >= ticket) self.cpu_pending_ticket = null;
            }
            if (residency_view.flags & 1 != 0) self.cpu_initial_ready = false;
            if (revision == self.cpu_revision.load(.acquire) and semantic == self.cpu_semantic.load(.acquire) and !self.hasQueuedCpuMutation()) {
                self.cpu_invalidated.store(false, .release);
            }
        }
        self.notification_sequence = bank.view.notification_first + bank.view.notification_count;
        self.pending_event_count = 0;
        self.pending_path_count = 0;
        self.publication_mutex.unlock();
        self.queue_mutex.unlock();
        self.publication_stage = .none;
        self.snapshotDiagnostics();
        self.profile.begin(.signal);
        if (self.publication_callback) |callback| callback(self.publication_context);
    }
    fn collectUsage(self: *const Owner) [7]OwnerUsage {
        if (self.host_callbacks != null) {
            var result: [7]OwnerUsage = undefined;
            for (&result, self.host_accounting[0..7], 0..) |*row, *accounting, kind| row.* = .{ .kind = @intCast(kind + 1), .current_bytes = accounting.committed.load(.acquire), .peak_bytes = accounting.peak.load(.acquire), .byte_cap = accounting.cap, .failed_allocations = accounting.failures.load(.acquire) };
            if (self.has(4)) result[3].capacity_failures = self.disk.capacity_failures;
            if (self.has(8)) result[4].capacity_failures = self.network.capacity_failures;
            return result;
        }
        var result = self.legacy_usage_history;
        result[0] = budgetUsage(1, &self.budget);
        if (self.has(1)) result[1] = mergedLegacyUsage(budgetUsage(2, &self.capture.budget), result[1]);
        if (self.has(2)) result[2] = mergedLegacyUsage(budgetUsage(3, &self.interrupts.budget), result[2]);
        if (self.has(4)) {
            var current = budgetUsage(4, &self.disk.budget); current.capacity_failures = self.disk.capacity_failures;
            result[3] = mergedLegacyUsage(current, result[3]);
        }
        if (self.has(8)) {
            var current = budgetUsage(5, &self.network.budget); current.capacity_failures = self.network.capacity_failures;
            result[4] = mergedLegacyUsage(current, result[4]);
        }
        if (self.has(16)) result[5] = mergedLegacyUsage(budgetUsage(6, &self.files.budget), result[5]);
        if (self.history_active) result[6] = mergedLegacyUsage(budgetUsage(7, &self.history.budget), result[6]);
        return result;
    }
    fn emptyLegacyUsage(cfg: Config) [7]OwnerUsage {
        var result: [7]OwnerUsage = undefined;
        const caps = [_]u64{ cfg.byte_cap, cfg.residency.byte_cap, cfg.interrupts.byte_cap, cfg.disk_byte_cap, cfg.network_byte_cap, cfg.file_byte_cap, cfg.history_byte_cap };
        for (&result, caps, 0..) |*row, cap, index| row.* = .{ .kind = @intCast(index + 1), .current_bytes = 0, .peak_bytes = 0, .byte_cap = cap, .failed_allocations = 0 };
        return result;
    }
    fn mergedLegacyUsage(current: OwnerUsage, retired: OwnerUsage) OwnerUsage {
        var result = current;
        result.peak_bytes = @max(current.peak_bytes, retired.peak_bytes);
        result.failed_allocations +|= retired.failed_allocations;
        result.capacity_failures +|= retired.capacity_failures;
        return result;
    }
    fn rememberLegacyBudget(self: *Owner, index: usize, budget: *const Budget, capacity_failures: u64) void {
        if (self.host_callbacks != null) return;
        var current = budgetUsage(@intCast(index + 1), budget);
        current.capacity_failures = capacity_failures;
        current.current_bytes = 0;
        self.legacy_usage_history[index] = mergedLegacyUsage(current, self.legacy_usage_history[index]);
    }
    fn collectRegionUsage(self: *const Owner) capture.RegionUsage {
        if (self.host_callbacks != null) {
            const total = &self.host_accounting[0];
            return .{ .reserved_bytes = total.committed.load(.acquire), .usable_bytes = total.committed.load(.acquire), .occupied_bytes = total.committed.load(.acquire), .peak_occupied_bytes = total.peak.load(.acquire), .requested_bytes = total.requested.load(.acquire), .peak_requested_bytes = total.requested_peak.load(.acquire), .failed_allocations = total.failures.load(.acquire) };
        }
        return .{ .reserved_bytes = self.external_region_bytes, .usable_bytes = self.region.capacity, .occupied_bytes = self.region.current, .peak_occupied_bytes = self.region.peak, .requested_bytes = self.region.requested_current, .peak_requested_bytes = self.region.requested_peak, .live_allocations = self.region.live_allocations, .failed_allocations = self.region.failures };
    }
    fn budgetUsage(kind: u32, value: *const Budget) OwnerUsage {
        return .{ .kind = kind, .current_bytes = value.current, .peak_bytes = value.peak, .byte_cap = value.cap, .failed_allocations = value.failures };
    }
    pub fn acquire(self: *Owner) !Lease {
        if (self.failure.load(.acquire) != 0) return error.Invalidated;
        self.publication_mutex.lock(); defer self.publication_mutex.unlock();
        const index = self.current_bank orelse return error.NoPublication;
        const bank = &self.banks[index];
        // The untyped legacy view promises every field. It cannot hand out a
        // partial production header containing retired component pointers.
        if (bank.available_outputs != 127) return error.NoPublication;
        return .{ .view = &bank.view, .token = try self.retainBank(bank, index, 127) };
    }
    pub fn acquireOutputs(self: *Owner, mask: u32) !Lease {
        if (mask & ~@as(u32, 127) != 0) return error.InvalidInput;
        if (self.failure.load(.acquire) != 0) return error.Invalidated;
        self.publication_mutex.lock();
        defer self.publication_mutex.unlock();
        const index = self.current_bank orelse return error.NoPublication;
        const bank = &self.banks[index];
        const actual = mask & bank.available_outputs;
        if (mask != 0 and actual == 0) return error.NoPublication;
        return .{ .view = &bank.view, .token = try self.retainBank(bank, index, actual) };
    }
    pub fn leasedOutputs(token: u64) u32 { const prefix = token >> 56; return if (prefix == 0) 127 else @intCast(prefix & 127); }
    fn retainBank(_: *Owner, bank: *Bank, index: usize, mask: u32) !u64 {
        if (bank.view.version >= @as(u64, 1) << 53) return error.Overflow;
        bank.references = try std.math.add(u32, bank.references, 1);
        for (&bank.component_references, 0..) |*references, kind| if (componentOutput(kind) & mask != 0) { references.* += 1; };
        const prefix = if (mask == 127) @as(u64, 0) else (@as(u64, 128) | mask) << 56;
        return prefix | (bank.view.version * 8 + index);
    }
    /// The immutable progress stays readable while the native flow is running.
    pub fn readPublishedProgress(self: *Owner) !PublishedProgress {
        const lease = try self.acquireOutputs(0);
        defer self.release(lease.token) catch unreachable;
        const index: usize = @intCast(lease.token % 8);
        return self.banks[index].published_progress orelse error.NoPublication;
    }
    /// IRQ capacity covers its own source scope, independently of CPU selection.
    fn interruptProcessorCount(self: *const Owner) u32 {
        if (self.irq_processor_count_override) |count| return count;
        if (self.trace) |t| if (t.consumer.metadata.processor_count != 0) return t.consumer.metadata.processor_count;
        return self.config.residency.max_processors;
    }
    pub fn acquireInterruptsAt(self: *Owner, required_ticket: u64) !Lease {
        if (self.failure.load(.acquire) != 0) return error.Invalidated;
        self.publication_mutex.lock();
        defer self.publication_mutex.unlock();
        const index = self.current_bank orelse return error.NoPublication;
        const bank = &self.banks[index];
        if (bank.available_outputs & 2 == 0) return error.NoPublication;
        if (bank.view.command_version < required_ticket or bank.view.interrupts.events_lost != self.events_lost.load(.acquire)) return error.NoPublication;
        return .{ .view = &bank.view, .token = try self.retainBank(bank, index, 2) };
    }
    pub fn acquireResidency(self: *Owner) !ResidencyLease {
        if (self.failure.load(.acquire) != 0) return error.Invalidated;
        self.publication_mutex.lock();
        defer self.publication_mutex.unlock();
        const index = self.current_bank orelse return error.NoPublication;
        const bank = &self.banks[index];
        if (!self.cpuBankValid(bank)) return error.NoPublication;
        return .{ .view = bank.view.residency, .sequence = bank.cpu_metadata.sequence, .events_lost = bank.cpu_metadata.events_lost, .applied_ticket = bank.cpu_metadata.applied_ticket, .token = try self.retainBank(bank, index, 1) };
    }
    pub fn release(self: *Owner, token: u64) !void {
        self.publication_mutex.lock();
        defer self.publication_mutex.unlock();
        const index: usize = @intCast(token % 8);
        const prefix = token >> 56;
        const mask: u32 = if (prefix == 0) 127 else @intCast(prefix & 127);
        const version = (token & 0x00ff_ffff_ffff_ffff) / 8;
        if (index >= self.banks.len or self.banks[index].view.version != version or self.banks[index].references == 0) return error.InvalidLease;
        self.banks[index].references -= 1;
        for (&self.banks[index].component_references, 0..) |*references, kind| if (componentOutput(kind) & mask != 0) {
            if (references.* == 0) return error.InvalidLease;
            references.* -= 1;
        };
        if (self.current_bank == null or self.current_bank.? != index) self.retireSupersededBank(index);
        self.releaseInactiveComponents();
    }
};
fn testConfig() Config {
    return .{ .residency = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 16, .max_processes = 16, .max_threads = 32, .max_processors = 2, .max_name_bytes = 4096, .byte_cap = 1024 * 1024 }, .interrupts = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 64, .max_slice_drivers = 128, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 }, .byte_cap = 4 * 1024 * 1024, .disk_byte_cap = 512 * 1024, .network_byte_cap = 512 * 1024, .file_byte_cap = 256 * 1024, .history_byte_cap = 256 * 1024, .maximum_disk_pairs = 32, .maximum_disks = 16, .maximum_cached_processes = 16, .maximum_network_events = 64, .maximum_network_processes = 16, .maximum_file_entries = 16, .maximum_file_versions = 64, .maximum_history_entries = 64, .maximum_path_units = 128, .maximum_namespace_entries = 16, .maximum_commands = 8, .maximum_command_name_bytes = 128, .publication_banks = 3, .window_ms = 2000, .retention_ms = 8000, .disk_bucket_ms = 100, .started_tick_ms = 0, .maximum_pending_events = 128, .maximum_pending_path_units = 4096, .maximum_renderer_processes = 4096, .network_initial_events = 256 };
}
fn absentOpen(_: ?*anyopaque, _: u32) ?*anyopaque {
    return null;
}
fn absentPid(_: ?*anyopaque, _: *anyopaque) ?u32 {
    return null;
}
fn absentBirth(_: ?*anyopaque, _: *anyopaque) ?u64 {
    return null;
}
fn absentClose(_: ?*anyopaque, _: *anyopaque) void {}
const absent_identity: identity.Source = .{ .open_process = absentOpen, .read_process_id = absentPid, .read_creation_filetime = absentBirth, .close_process = absentClose };
test "historical allocator faults fail work but never prevent full owner retirement" {
    var backing: host.TestHost = .{};
    const self = try Owner.createWithAllocator(testConfig(), backing.callbacks(), absent_identity);
    try self.setOutputs(1 | 2 | 8);
    try self.publish(0);
    self.allocation_fault.store(@intFromEnum(host.Fault.remap), .release);
    try std.testing.expectError(error.HostRemapFailed, self.checkAllocatorFault());
    try std.testing.expectError(error.HostRemapFailed, self.setOutputs(1 | 2 | 8));
    try self.destroy();
    for (backing.blocks) |block| try std.testing.expect(block.ptr == null);
}
test "backing callback refusal retains its typed cause and counts once before cleanup" {
    var backing: host.TestHost = .{};
    const self = try Owner.createWithAllocator(testConfig(), backing.callbacks(), absent_identity);
    defer { self.trace = null; self.destroy() catch unreachable; }
    try self.setOutputs(1);
    var t: trace.Trace = .{ .owner = self.capture, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .shared_hooks = self.hooks() };
    self.trace = &t;
    backing.fail_allocate_after = backing.alloc_calls;
    try std.testing.expectError(error.OutOfMemory, self.outputAllocator(1, 1).alloc(u8, 8000));
    t.invalidate(error.OutOfMemory);
    try std.testing.expect(t.failure_reason.? == error.HostAllocationFailed);
    try std.testing.expectEqualStrings("HostAllocationFailed", self.failure_detail.error_name.?[0..self.failure_detail.error_name_length]);
    try std.testing.expectEqual(@as(u64, 1), self.host_accounting[1].failures.load(.acquire));
    try std.testing.expectEqual(@as(u64, 1), self.host_accounting[0].failures.load(.acquire));
    // A later null caused by the latch is not another rejected callback.
    try std.testing.expectError(error.OutOfMemory, self.outputAllocator(1, 1).alloc(u8, 8000));
    try std.testing.expectEqual(@as(u64, 1), self.host_accounting[0].failures.load(.acquire));
}

test "an allocation fault never prevents retiring consumer counters with or without a running flow" {
    var backing: host.TestHost = .{};
    const self = try Owner.createWithAllocator(testConfig(), backing.callbacks(), absent_identity);
    defer { self.trace = null; self.destroy() catch unreachable; }
    try self.setOutputs(1);
    var t: trace.Trace = .{ .owner = self.capture, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .shared_hooks = self.hooks() };
    self.trace = &t;
    var config: counters.Config = .{ .token = 1, .generation = 1, .kind_mask = counters.all_kinds, .output_mask = 1, .flags = counters.active | counters.current_binding | counters.window_active, .window_start = 0, .incomplete_through = -1 };
    try self.configureConsumerCounter(config);
    config.token = 2;
    try self.configureConsumerCounter(config);
    t.consumer.processing.store(true, .release);
    config.token = 3;
    try self.configureConsumerCounter(config);
    // A refused allocation, as at a byte cap, latches every context of the owner.
    backing.fail_allocate_after = backing.alloc_calls;
    try std.testing.expectError(error.OutOfMemory, self.outputAllocator(1, 1).alloc(u8, 8000));
    try std.testing.expectError(error.OutOfMemory, self.controlAllocator().alloc(u8, 8));
    // Running flow: the live descriptor is marked, the queued one dropped in place.
    try self.retireConsumerCounter(2);
    try self.retireConsumerCounter(3);
    try std.testing.expectEqual(@as(usize, 0), self.counter_commands.items.len);
    try std.testing.expect(self.takeRetiredConsumerCounter(2) == null);
    try std.testing.expectEqual(counters.Frame{}, self.takeRetiredConsumerCounter(3).?);
    try std.testing.expectError(error.HostAllocationFailed, self.drain(0));
    try std.testing.expect(self.takeRetiredConsumerCounter(2) != null);
    // Stopped flow: frozen at once.
    t.consumer.processing.store(false, .release);
    try self.retireConsumerCounter(1);
    try std.testing.expect(self.takeRetiredConsumerCounter(1) != null);
}
test "superseded publication pages return at the last reader without another publication" {
    var backing: host.TestHost = .{};
    const self = try Owner.createWithAllocator(testConfig(), backing.callbacks(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.setOutputs(2);
    try self.interrupts.owner.upsertImage(100, 10, std.unicode.utf8ToUtf16LeStringLiteral("driver.sys"), 0);
    try self.interrupts.owner.observe(.dpc, 1, 100, 9999);
    try self.publish(10000);
    const old = try self.acquireOutputs(2);
    const old_index: usize = @intCast(old.token % 8);
    const old_scope = &self.host_contexts[2][old_index + 2];
    try std.testing.expect(old_scope.requested > 0);
    try self.publish(11000);
    try std.testing.expect(old_scope.requested > 0);
    try std.testing.expectEqual(@as(u64, 1), old.view.?.interrupts.event_count);
    try self.release(old.token);
    try std.testing.expectEqual(@as(usize, 0), old_scope.requested);
    const current = try self.acquireOutputs(2);
    try std.testing.expectEqual(@as(u64, 1), current.view.?.interrupts.event_count);
    const current_index: usize = @intCast(current.token % 8);
    try self.release(current.token);
    // The current publication has a real future-read use until replaced.
    try std.testing.expect(self.host_contexts[2][current_index + 2].requested > 0);
    try self.publish(12000);
    try std.testing.expectEqual(@as(usize, 0), self.host_contexts[2][current_index + 2].requested);
}

test "bounded region output transitions retire children once and keep inactive usage readable" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    var alive = true; defer if (alive) self.destroy() catch unreachable;
    try self.setOutputs(2);
    try self.publish(1000);
    const irq_only = try self.acquireOutputs(2);
    try std.testing.expectEqual(@as(u64, 0), irq_only.view.?.owner_usage[1].current_bytes);
    try std.testing.expect(irq_only.view.?.owner_usage[1].peak_bytes > 0);
    try std.testing.expectEqual(self.config.residency.byte_cap, irq_only.view.?.owner_usage[1].byte_cap);
    try self.release(irq_only.token);
    try self.setOutputs(8);
    try self.publish(2000);
    const network_only = try self.acquireOutputs(8);
    try std.testing.expectEqual(@as(u64, 0), network_only.view.?.owner_usage[2].current_bytes);
    try std.testing.expect(network_only.view.?.owner_usage[2].peak_bytes > 0);
    try std.testing.expect(self.history_active and !self.paths_active);
    try self.release(network_only.token);
    try self.setOutputs(0);
    try self.publish(3000);
    const stopped = try self.acquireOutputs(0);
    for (stopped.view.?.owner_usage[1..]) |row| try std.testing.expectEqual(@as(u64, 0), row.current_bytes);
    try self.release(stopped.token);
    try self.destroy(); alive = false;
}
test "bounded region CPU and renderer recreation preserve explicit diagnostic input" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    var alive = true; defer if (alive) self.destroy() catch unreachable;
    var clock: i64 = 777;
    var source = self.readers.thread_diagnostics.source;
    source.context = &clock;
    source.now = struct { fn read(context: ?*anyopaque) i64 { const value: *i64 = @ptrCast(@alignCast(context.?)); return value.*; } }.read;
    self.configureDiagnosticSource(source, false);
    try self.setOutputs(2);
    try self.setOutputs(1 | 2 | 32);
    try std.testing.expectEqual(@as(i64, 777), self.readers.thread_diagnostics.source.now(self.readers.thread_diagnostics.source.context));
    try std.testing.expect(!self.readers.thread_diagnostics.recorded);
    try std.testing.expectEqual(@as(usize, self.config.maximum_renderer_processes), self.renderer_revisions.len);
    self.invalidateRenderer(7);
    try self.publish(4000);
    const stamp = try self.queryRendererStamp(7);
    try std.testing.expectEqual(@as(u64, 1), stamp.revision);
    try self.setOutputs(0);
    try self.destroy(); alive = false;
}
test "handled CPU reset invalidates a complete current sample while its historical reference survives" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig(); cfg.residency.max_pairs = 1; cfg.residency.max_processors = 1;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    // A continuous window (one slot per window) keeps adding after its first W.
    try self.submit(&.{ .{ .kind = 9, .unique = 1 }, .{ .kind = 1, .pid = 7 }, .{ .kind = 2, .pid = 7, .tid = 8 }, .{ .kind = 2, .pid = 7, .tid = 9 } });
    try self.drain(0);
    try self.history.start(8, 7); try self.history.start(9, 7);
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t; defer self.trace = null;
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = .{ .qpc = 0, .processor = 0, .data = .{ .cswitch = .{ .new_thread_id = 8, .old_thread_id = 0 } } } }));
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = .{ .qpc = 1000, .processor = 0, .data = .{ .cswitch = .{ .new_thread_id = 8, .old_thread_id = 8 } } } }));
    try self.publish(1000);
    const historical = try self.acquireResidency(); defer self.release(historical.token) catch unreachable;
    try std.testing.expect(historical.view.flags & 1 != 0);
    try std.testing.expectEqual(@as(i64, 1000), historical.view.rows.?[0].duration);
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = .{ .qpc = 1000, .processor = 0, .data = .{ .cswitch = .{ .new_thread_id = 9, .old_thread_id = 8 } } } }));
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = .{ .qpc = 1500, .processor = 0, .data = .{ .cswitch = .{ .new_thread_id = 9, .old_thread_id = 9 } } } }));
    try std.testing.expectEqual(@as(i64, 1), self.capture.residency.capacity_reset_count);
    try std.testing.expectError(error.NoPublication, self.acquireResidency());
    const requested = try self.requestResidency(); try std.testing.expect(requested.pending);
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const warming = try self.acquireResidency(); defer self.release(warming.token) catch unreachable;
    try std.testing.expect(warming.view.flags & 1 == 0);
    try std.testing.expect(warming.applied_ticket >= requested.ticket);
    try std.testing.expectEqual(@as(i64, 1000), historical.view.rows.?[0].duration);
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = .{ .qpc = 2500, .processor = 0, .data = .{ .cswitch = .{ .new_thread_id = 9, .old_thread_id = 9 } } } }));
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const recovered = try self.acquireResidency(); defer self.release(recovered.token) catch unreachable;
    try std.testing.expect(recovered.view.flags & 1 != 0);
    try std.testing.expect(recovered.sequence > historical.sequence);
    try std.testing.expectEqual(@as(i64, 1000), recovered.view.rows.?[0].duration);
    try std.testing.expectEqual(@as(i32, 9), recovered.view.rows.?[0].tid);
    try std.testing.expectEqual(@as(i64, 1000), historical.view.rows.?[0].duration);
    try std.testing.expectEqual(@as(i32, 0), t.failure);
}
test "handled CPU capacity reset leaves IRQ disk network and FileIO on the same trace alive" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig(); cfg.residency.max_pairs = 1;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    try self.submit(&.{ .{ .kind = 1, .pid = 7 }, .{ .kind = 2, .pid = 7, .tid = 8 }, .{ .kind = 2, .pid = 7, .tid = 9 } });
    try self.drain(0);
    try self.history.start(8, 7); try self.history.start(9, 7);
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t; defer self.trace = null;
    const events = [_]native.decoder.Event{
        .{ .qpc = 0, .processor = 0, .data = .{ .cswitch = .{ .new_thread_id = 8, .old_thread_id = 0 } } },
        .{ .qpc = 0, .processor = 1, .data = .{ .cswitch = .{ .new_thread_id = 9, .old_thread_id = 0 } } },
        .{ .qpc = 1000, .processor = 0, .data = .{ .cswitch = .{ .new_thread_id = 8, .old_thread_id = 8 } } },
        .{ .qpc = 1000, .processor = 1, .data = .{ .cswitch = .{ .new_thread_id = 9, .old_thread_id = 9 } } },
    };
    for (events) |value| try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = value }));
    try std.testing.expectEqual(@as(i64, 1), self.capture.residency.capacity_reset_count);
    try std.testing.expectEqual(@as(u32, 1), self.capture.residency.last_capacity_reset.reason);
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = .{ .qpc = 1001, .processor = 0, .data = .{ .interrupt = .{ .kind = .dpc, .initial_qpc = 1000, .routine = 100 } } } }));
    // The64-bit layout contains two widened pointers before offset40.
    var disk_payload: [48]u8 = @splat(0);
    std.mem.writeInt(u32, disk_payload[8..12], 128, .little);
    var raw_event: native.decoder.RawEvent = .{ .provider = io.disk_io_task_guid, .opcode = 10, .version = 3, .pointer_size = 8, .qpc = 1001, .processor = 0, .header_process_id = 7, .header_thread_id = 8, .payload = &disk_payload };
    try std.testing.expect(trace.Trace.onUnhandled(&t, raw_event));
    var network_payload: [12]u8 = @splat(0);
    std.mem.writeInt(u32, network_payload[0..4], 7, .little);
    std.mem.writeInt(u32, network_payload[4..8], 64, .little); network_payload[8] = 8;
    raw_event.provider = io.tcp_ip_task_guid; raw_event.version = 1; raw_event.payload = &network_payload;
    try std.testing.expect(trace.Trace.onUnhandled(&t, raw_event));
    const text = std.unicode.utf8ToUtf16LeStringLiteral("Z:\\file");
    var file_payload: [8 + (text.len + 1) * 2]u8 = @splat(0);
    std.mem.writeInt(u64, file_payload[0..8], 99, .little);
    for (text, 0..) |unit, index| std.mem.writeInt(u16, file_payload[8 + index * 2 ..][0..2], unit, .little);
    // Opcode0 only seeds parser names and deliberately emits no owned event.
    // NameCreate32 exercises notification delivery as well as retained state.
    raw_event.provider = fileio.file_io_task_guid; raw_event.opcode = 32; raw_event.version = 0; raw_event.payload = &file_payload;
    try std.testing.expect(trace.Trace.onUnhandled(&t, raw_event));
    // Disk deliberately publishes no rows before its full2s capture window.
    // At3000 the post-reset event1001 remains in the closed[1000,3000] window.
    try self.publish(3000);
    const published = try self.acquire(); defer self.release(published.token) catch unreachable;
    try std.testing.expectEqual(@as(u32, 3), published.view.?.flags & 3);
    std.testing.expectEqual(@as(u64, 1), published.view.?.interrupts.event_count) catch |err| { std.debug.print("after handled reset: IRQ event count\n", .{}); return err; };
    std.testing.expectEqual(@as(u32, 1), published.view.?.disk_count) catch |err| { std.debug.print("after handled reset: disk row count\n", .{}); return err; };
    std.testing.expectEqual(@as(u32, 1), published.view.?.network_count) catch |err| { std.debug.print("after handled reset: network row count\n", .{}); return err; };
    std.testing.expectEqual(@as(u64, 1), published.view.?.file_events) catch |err| { std.debug.print("after handled reset: FileIO event count\n", .{}); return err; };
    try std.testing.expectEqual(@as(u64, 1), published.view.?.file_entries);
    try std.testing.expectEqual(@as(u32, 1), published.view.?.notification_count);
    try std.testing.expectEqual(@as(u32, 2), published.view.?.notifications.?[0].kind);
    try std.testing.expectEqual(@as(i32, 0), t.failure);
    try std.testing.expectEqual(@as(i32, 0), self.failure.load(.acquire));
}
test "host outputs allocate only their children and retiring IRQ waits only for IRQ references" {
    var backing: host.TestHost = .{};
    const self = try Owner.createWithAllocator(testConfig(), backing.callbacks(), absent_identity);
    errdefer self.destroy() catch {};
    try std.testing.expectEqual(@as(u32, 0), self.active_outputs);
    for (self.host_accounting[1..]) |*accounting| try std.testing.expectEqual(@as(usize, 0), accounting.committed.load(.acquire));
    try self.setOutputs(2);
    try std.testing.expect(!self.history_active and self.paths_active);
    try std.testing.expectEqual(@as(usize, 0), self.host_accounting[1].committed.load(.acquire));
    try std.testing.expectEqual(@as(usize, 0), self.pending_events.len);
    const path = [_]u16{ 'd', 'r', 'v' };
    try self.interrupts.owner.upsertImage(1000, 100, &path, 0);
    try self.interrupts.owner.observe(.dpc, 2, 1010, 100);
    try self.publish(1000);
    const metadata = try self.queryInterruptMetadata();
    try std.testing.expectEqual(@as(u64, 1), metadata.active_modules);
    const irq_lease = try self.acquireOutputs(2);
    const irq_events = irq_lease.view.?.interrupts.event_count;
    try std.testing.expectEqual(@as(u64, 1), irq_events);
    try self.setOutputs(8);
    try std.testing.expect(self.history_active and !self.paths_active);
    try std.testing.expectError(error.NoPublication, self.acquireOutputs(2));
    try std.testing.expect(self.host_accounting[2].committed.load(.acquire) > 0);
    try std.testing.expectEqual(irq_events, irq_lease.view.?.interrupts.event_count);
    try self.release(irq_lease.token);
    try std.testing.expectEqual(@as(usize, 0), self.host_accounting[2].committed.load(.acquire));
    try std.testing.expectEqual(@as(usize, 0), self.host_accounting[1].committed.load(.acquire));
    try std.testing.expectEqual(@as(usize, 0), self.host_accounting[7].committed.load(.acquire));
    try std.testing.expectEqual(@as(usize, 0), self.host_accounting[8].committed.load(.acquire));
    try self.setOutputs(2);
    try std.testing.expectError(error.NoPublication, self.acquireInterruptsAt(0));
    try std.testing.expect((try self.queryInterruptMetadata()).owner_serial > metadata.owner_serial);
    try self.setOutputs(0);
    try self.destroy();
    try std.testing.expect(backing.empty());
}

test "host CPU window storage returns while identities and a closed borrowed publication survive" {
    var backing: host.TestHost = .{};
    const self = try Owner.createWithAllocator(testConfig(), backing.callbacks(), absent_identity);
    errdefer self.destroy() catch {};
    try self.setOutputs(1);
    try self.submit(&.{ .{ .kind = 1, .pid = 7, .flags = 1, .start = 10, .name = "cpu".ptr, .name_len = 3 }, .{ .kind = 2, .pid = 7, .tid = 8 } });
    try self.drain(0);
    try self.capture.apply(&.{ .kind = 7, .qpc = 0, .time_ticks = 10000000, .processor = 0, .pid = 7, .tid = 8 });
    try self.capture.apply(&.{ .kind = 7, .qpc = 0, .time_ticks = 10000000, .processor = 1, .pid = 7, .tid = 8 });
    try self.capture.apply(&.{ .kind = 7, .qpc = 1000, .time_ticks = 20000000, .processor = 0, .pid = 7, .tid = 8, .old_pid = 7, .old_tid = 8 });
    try self.capture.apply(&.{ .kind = 7, .qpc = 1000, .time_ticks = 20000000, .processor = 1, .pid = 7, .tid = 8, .old_pid = 7, .old_tid = 8 });
    try self.publish(1000);
    const closed = try self.acquireResidency();
    try std.testing.expect(closed.view.count > 0);
    const duration = closed.view.rows.?[0].duration;
    try self.setEnabled(1, 0);
    try self.drain(1000);
    const diagnostics = try self.queryResidencyDiagnostics();
    try std.testing.expectEqual(@as(u64, 0), diagnostics.active_pair_count);
    try std.testing.expectEqual(@as(u64, 0), diagnostics.window_storage_bytes);
    try std.testing.expectEqual(@as(u64, 1), diagnostics.current_thread_count);
    try std.testing.expectEqual(@as(usize, 0), self.host_contexts[1][1].requested);
    try std.testing.expectEqual(duration, closed.view.rows.?[0].duration);
    try self.setOutputs(2);
    try std.testing.expect(self.host_accounting[1].committed.load(.acquire) > 0);
    try self.release(closed.token);
    try std.testing.expectEqual(@as(usize, 0), self.host_accounting[1].committed.load(.acquire));
    try self.destroy();
    try std.testing.expect(backing.empty());
}
test "shared immutable banks reuse a version and preserve leased rows until release" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.submit(&.{.{ .kind = 1, .pid = 7, .name = "first".ptr, .name_len = 5 }});
    try self.drain(0);
    try self.publish(2000);
    const first = try self.acquire();
    const again = try self.acquire();
    try std.testing.expectEqual(first.view, again.view);
    try std.testing.expectEqual(first.token, again.token);
    try self.release(again.token);
    try self.publish(3000);
    const second = try self.acquire();
    try self.publish(4000);
    const third = try self.acquire();
    try self.publish(5000);
    try std.testing.expectEqual(@as(u64, 1), self.skipped_publications);
    try std.testing.expectEqual(@as(i64, 2000), first.view.?.through_tick_ms);
    try std.testing.expectError(error.OutstandingLease, self.destroy());
    try self.release(first.token);
    try self.release(second.token);
    try self.release(third.token);
    try std.testing.expectError(error.InvalidLease, self.release(third.token));
    try self.publish(6000);
    const last = try self.acquire();
    defer self.release(last.token) catch unreachable;
    try std.testing.expectEqual(@as(i64, 6000), last.view.?.through_tick_ms);
}
test "shared namespace and recorded history exist without interrupt routing" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    const mapping = std.unicode.utf8ToUtf16LeStringLiteral("\\Device\\Recorded\\\x00Z:\\\x00");
    const raw_event: native.decoder.RawEvent = .{ .provider = @import("namespace.zig").sysconfig_task_guid, .opcode = 35, .version = 0, .pointer_size = 8, .qpc = 0, .processor = 0, .header_process_id = 0, .header_thread_id = 0, .payload = std.mem.sliceAsBytes(mapping) };
    // The namespace mutation precedes clock/I/O routing, even with no trace.
    try std.testing.expectError(error.TraceNotOpen, Owner.raw(self, raw_event));
    try std.testing.expect(self.paths.recorded);
    try std.testing.expect(!self.paths.local);
    const mapped = try self.paths.map(std.testing.allocator, std.unicode.utf8ToUtf16LeStringLiteral("\\Device\\Recorded\\x"), 128);
    defer std.testing.allocator.free(mapped);
    try std.testing.expectEqualSlices(u16, std.unicode.utf8ToUtf16LeStringLiteral("Z:\\x"), mapped);
    try Owner.event(self, .{ .qpc = 200, .processor = 0, .data = .{ .thread = .{ .lifecycle = .end, .process_id = 21, .thread_id = 7 } } });
    try std.testing.expectEqual(@as(?u32, 21), self.history.lookup(7, 100));
    try self.setEnabled(1, 0);
    try self.drain(0);
    try std.testing.expect(!self.readers.residency_enabled);
    try std.testing.expect(Owner.enabled(self, true));
}

test "shared notification copies are bounded and retain canonical text in a leased bank" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.maximum_pending_events = 1;
    cfg.maximum_pending_path_units = 16;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    var path = [_]u16{ 'C', ':', '\\', 'x' };
    try self.append(.{ .kind = 1, .qpc = 0, .tick_ms = 0, .pid = 7 }, &path);
    path[3] = 'y';
    try std.testing.expectError(error.CapacityExceeded, self.append(.{ .kind = 1, .qpc = 0, .tick_ms = 0 }, null));
    self.invalidateRenderer(7);
    try self.publish(2000);
    const first = try self.acquire();
    defer self.release(first.token) catch unreachable;
    try std.testing.expectEqual(@as(u32, 1), first.view.?.notification_count);
    const note = first.view.?.notifications.?[0];
    try std.testing.expectEqualSlices(u16, &.{ 'C', ':', '\\', 'x' }, note.path.?[0..note.path_len]);
    const slot = (7 *% @as(u32, 2654435761)) % cfg.maximum_renderer_processes;
    try std.testing.expectEqual(@as(u64, 1), first.view.?.renderer_revisions.?[slot]);
    self.invalidateRenderer(7);
    try self.publish(3000);
    try std.testing.expectEqual(@as(u64, 1), first.view.?.renderer_revisions.?[slot]);
    try std.testing.expectEqual(@as(usize, 0), self.pending_event_count);
}
test "notification stream positions count published notifications, not bank versions" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.append(.{ .kind = 7, .qpc = 0, .tick_ms = 0, .pid = 7 }, null);
    try self.append(.{ .kind = 7, .qpc = 0, .tick_ms = 0, .pid = 8 }, null);
    try self.publish(1000);
    const first = try self.acquire();
    const first_view = first.view.?.*;
    try self.release(first.token);
    try std.testing.expectEqual(@as(u64, 0), first_view.notification_first);
    try std.testing.expectEqual(@as(u32, 2), first_view.notification_count);
    // Banks without notifications advance the shared version but not the stream.
    try self.publish(2000);
    try self.publish(3000);
    try self.append(.{ .kind = 8, .qpc = 0, .tick_ms = 0, .pid = 7 }, null);
    try self.publish(4000);
    const last = try self.acquire();
    const last_view = last.view.?.*;
    try self.release(last.token);
    try std.testing.expect(last_view.version > first_view.version + 1);
    try std.testing.expectEqual(@as(u64, 2), last_view.notification_first);
    try std.testing.expectEqual(@as(u32, 1), last_view.notification_count);
    try std.testing.expectEqual(@as(u64, 3), self.notification_sequence);
}
test "empty shared IO publication uses occupied rows and releases temporary columns" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    // These are admission limits, not a request to allocate publication rows.
    // The old cap-sized network row allocation alone exceeded this region.
    cfg.maximum_network_processes = 65536;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    const before = self.region.requested_current;
    try self.publish(2000);
    const leased = try self.acquire();
    defer self.release(leased.token) catch unreachable;
    try std.testing.expectEqual(@as(u32, 0), leased.view.?.process_disk_count);
    try std.testing.expectEqual(@as(u32, 0), leased.view.?.disk_count);
    try std.testing.expectEqual(@as(u32, 0), leased.view.?.network_count);
    try std.testing.expect(self.region.requested_current - before < 128 * 1024);
    try std.testing.expectEqual(@as(usize, 0), self.round.queryCapacity());
}

test "shared public command admission includes IRQ reset and CPU window configuration" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.submit(&.{ .{ .kind = 15, .qpc = 1250 }, .{ .kind = 16, .qpc = 2000, .unique = 8 } });
    try self.drain(1300);
    try std.testing.expectEqual(@as(i64, 1250), self.interrupts.owner.started_tick_ms);
    try std.testing.expectEqual(@as(i64, 2000), self.capture.residency.window_qpc);
    try std.testing.expectEqual(@as(usize, 8), self.capture.residency.config.max_pairs);
}

test "network binding reset cannot consume a staged public IRQ reset" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    // Isolate dispatch from admission so both independent regressions are visible.
    self.commands[0] = .{ .kind = 15, .qpc = 1250 };
    self.command_count = 1;
    const ticket = try self.submitTracked(&.{.{ .kind = 18, .flags = 8, .time_ticks = 1300 }});
    try self.drain(1400);
    try std.testing.expectEqual(@as(i64, 1250), self.interrupts.owner.started_tick_ms);
    try std.testing.expectEqual(@as(usize, 0), self.network.event_count);
    try std.testing.expectEqual(ticket, self.network_transition_version);
}
test "tracked reader barrier preserves input fields and publishes only its applied ticket" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.publish(2000);
    const old = try self.acquire();
    defer self.release(old.token) catch unreachable;
    const ticket = try self.submitTracked(&.{ .{ .kind = 16, .qpc = 500, .unique = 16 }, .{ .kind = 9 } });
    try std.testing.expectEqual(@as(u64, 16), self.commands[0].unique);
    try std.testing.expectEqual(@as(u64, 0), old.view.?.command_version);
    try self.drain(2500);
    try self.publish(2500);
    const fresh = try self.acquire();
    defer self.release(fresh.token) catch unreachable;
    try std.testing.expectEqual(ticket, fresh.view.?.command_version);
    try std.testing.expectEqual(@as(u64, 0), old.view.?.command_version);
    try std.testing.expectEqual(@as(i64, 500), self.capture.residency.config.window_ms);
    self.failure.store(3, .release);
    try std.testing.expectError(error.Invalidated, self.acquire());
}

test "new IO holder clears prior disk state before next transfer and restarts coverage" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.disk.observe(.{ .process_id = 7, .thread_id = null, .bytes = 100, .disk_number = 2, .is_write = false, .qpc = 3000, .event_filetime_100ns = 1 }, 3000);
    try self.publish(3001);
    const old = try self.acquire();
    defer self.release(old.token) catch unreachable;
    try std.testing.expect(old.view.?.flags & 1 != 0);
    const reset = try self.submitTracked(&.{.{ .kind = 18, .flags = 4, .time_ticks = 4000 }});
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0 };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t;
    defer self.trace = null;
    var payload: [64]u8 = @splat(0);
    std.mem.writeInt(u32, payload[0..4], 4, .little);
    std.mem.writeInt(u32, payload[8..12], 200, .little);
    const raw_event: native.decoder.RawEvent = .{ .provider = io.disk_io_task_guid, .opcode = 10, .version = 3, .pointer_size = 8, .qpc = 4001, .processor = 0, .header_process_id = 7, .header_thread_id = 8, .payload = &payload };
    try Owner.raw(self, raw_event);
    try std.testing.expectEqual(@as(usize, 1), self.disk.pairs.count());
    try std.testing.expect(!self.disk.disks.contains(2) and self.disk.disks.contains(4));
    try std.testing.expectEqual(reset, self.disk_transition_version);
    try self.publish(5000);
    const warming = try self.acquire();
    try std.testing.expectEqual(reset, warming.view.?.disk_transition_version);
    try std.testing.expect(warming.view.?.flags & 1 == 0);
    try self.release(warming.token);
    try self.publish(6000);
    const complete = try self.acquire();
    defer self.release(complete.token) catch unreachable;
    try std.testing.expect(complete.view.?.flags & 1 != 0);
    try std.testing.expectEqual(@as(u64, 0), old.view.?.disk_transition_version);
}
test "new network holder resets capture and acknowledges fresh coverage" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.network.observe(.{ .process_id = 7, .bytes = 100, .direction = .send, .protocol = .tcp, .remote_address = .{ .ipv4 = .{ 8, 8, 8, 8 } }, .qpc = 1000, .event_filetime_100ns = 1 }, 1000);
    try std.testing.expectEqual(@as(usize, 1), self.network.event_count);
    const ticket = try self.submitTracked(&.{.{ .kind = 18, .flags = 8, .time_ticks = 2000 }});
    try self.drain(2000);
    try std.testing.expect(self.has(8));
    try std.testing.expectEqual(@as(usize, 0), self.network.event_count);
    try std.testing.expectEqual(ticket, self.network_transition_version);
    try self.publish(2000);
    const warming = try self.acquire();
    try std.testing.expect(warming.view.?.flags & 2 == 0);
    try self.release(warming.token);
    try self.publish(4001);
    const complete = try self.acquire();
    defer self.release(complete.token) catch unreachable;
    try std.testing.expect(complete.view.?.flags & 2 != 0);
    try std.testing.expectEqual(ticket, complete.view.?.network_transition_version);
}
test "shared raw-only buffers publish at latest raw QPC without a core event" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t;
    defer self.trace = null;
    const raw_unknown: native.decoder.RawEvent = .{ .provider = .{ .data1 = 0xdeadbeef, .data2 = 0, .data3 = 0, .data4 = @splat(0) }, .opcode = 0, .version = 0, .pointer_size = 8, .qpc = 5000, .processor = 0, .header_process_id = 0, .header_thread_id = 0, .payload = &.{} };
    try std.testing.expect(trace.Trace.onUnhandled(&t, raw_unknown));
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const first = try self.acquire();
    defer self.release(first.token) catch unreachable;
    try std.testing.expectEqual(@as(i64, 5000), first.view.?.through_tick_ms);
    var payload: [12]u8 = @splat(0);
    std.mem.writeInt(u32, payload[0..4], 7, .little);
    std.mem.writeInt(u32, payload[4..8], 100, .little);
    payload[8] = 8;
    const raw_io: native.decoder.RawEvent = .{ .provider = io.tcp_ip_task_guid, .opcode = 10, .version = 1, .pointer_size = 8, .qpc = 6000, .processor = 0, .header_process_id = 7, .header_thread_id = 8, .payload = &payload };
    try std.testing.expect(trace.Trace.onUnhandled(&t, raw_io));
    var older_unknown = raw_unknown;
    older_unknown.qpc = 5500;
    try std.testing.expect(trace.Trace.onUnhandled(&t, older_unknown));
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const second = try self.acquire();
    defer self.release(second.token) catch unreachable;
    try std.testing.expectEqual(@as(i64, 6000), second.view.?.through_tick_ms);
    try std.testing.expectEqual(@as(i64, 5000), first.view.?.through_tick_ms);
}
test "shared FileIO cap failure keeps its first cause through the next buffer" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    try self.paths.add(std.unicode.utf8ToUtf16LeStringLiteral("\\Device\\Recorded\\"), std.unicode.utf8ToUtf16LeStringLiteral("Z:\\"));
    self.files.budget.cap = self.files.budget.current + 1;
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    t.consumer.counters.records = 42;
    self.trace = &t;
    defer self.trace = null;
    const text = std.unicode.utf8ToUtf16LeStringLiteral("Z:\\x");
    var payload: [8 + (text.len + 1) * 2]u8 = @splat(0);
    std.mem.writeInt(u64, payload[0..8], 99, .little);
    for (text, 0..) |unit, index| std.mem.writeInt(u16, payload[8 + index * 2 ..][0..2], unit, .little);
    const raw_event: native.decoder.RawEvent = .{ .provider = fileio.file_io_task_guid, .opcode = 0, .version = 0, .pointer_size = 8, .qpc = 5000, .processor = 0, .header_process_id = 0, .header_thread_id = 0, .payload = &payload };
    try std.testing.expect(!trace.Trace.onUnhandled(&t, raw_event));
    try std.testing.expectEqual(@as(i32, 7), t.failure);
    try std.testing.expect(!trace.Trace.onBuffer(&t));
    // A failed buffer must not replace OutOfMemory with secondary Invalidated.
    try std.testing.expectEqual(@as(i32, 7), t.failure);
    try std.testing.expectEqual(@as(i32, 7), self.failure.load(.acquire));
    const boundary = @import("shared_abi.zig");
    var usage: [7]OwnerUsage = undefined;
    var count: u32 = 0;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_owner_usage(self, &usage, 7, &count));
    try std.testing.expectEqual(@as(u32, 7), count);
    try std.testing.expectEqual(@as(u32, 6), usage[5].kind);
    try std.testing.expectEqual(@as(u64, self.files.budget.cap), usage[5].byte_cap);
    try std.testing.expectEqual(@as(u64, 1), usage[5].failed_allocations);
    var region_usage: capture.RegionUsage = undefined;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_region_usage(self, &region_usage, @sizeOf(capture.RegionUsage)));
    try std.testing.expect(region_usage.peak_requested_bytes >= region_usage.requested_bytes);
    var detail: FailureDetail = undefined;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_failure_detail(self, &detail, @sizeOf(FailureDetail)));
    try std.testing.expectEqual(@as(i32, 7), detail.result);
    try std.testing.expectEqualStrings("OutOfMemory", detail.error_name.?[0..detail.error_name_length]);
    try std.testing.expectEqual(@as(u64, 42), detail.records);
    try std.testing.expectEqual(@as(i64, 5000), detail.qpc);
    self.trace = null;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_failure_detail(self, &detail, @sizeOf(FailureDetail)));
    try std.testing.expectEqual(@as(u64, 42), detail.records);
}
test "disk configuration query returns immutable construction limits before a bank exists" {
    const boundary = @import("shared_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.disk_bucket_ms = 73;
    cfg.maximum_disk_pairs = 17;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    try std.testing.expectError(error.NoPublication, self.acquire());
    var value: boundary.DiskConfiguration = undefined;
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(boundary.DiskConfiguration));
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_disk_config(self, &value, @sizeOf(boundary.DiskConfiguration)));
    try std.testing.expectEqual(@as(i64, 73), value.bucket_ms);
    try std.testing.expectEqual(@as(u32, 17), value.maximum_pairs);
    try std.testing.expectEqual(cfg.disk_byte_cap, value.byte_cap);
}

test "cold allocation failure query survives cleanup and owner invalidation" {
    const boundary = @import("shared_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    var observed: FailureAllocation = undefined;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_failure_allocation(self, &observed, @sizeOf(FailureAllocation)));
    try std.testing.expectEqualDeep(FailureAllocation{}, observed);
    const region_allocator = self.region.allocator();
    const pin = try region_allocator.alloc(u8, 128);
    const occupied = self.region.current;
    const requested = self.region.requested_current;
    self.publication_stage = .scratch_reset;
    try std.testing.expectError(error.OutOfMemory, region_allocator.alloc(u8, self.region.capacity));
    // Producer cleanup occurs before trace invalidation records the failure.
    region_allocator.free(pin);
    self.recordFailure(7);
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_failure_allocation(self, &observed, @sizeOf(FailureAllocation)));
    try std.testing.expectEqual(@as(u32, 8), observed.publication_stage);
    try std.testing.expectEqual(@as(u64, self.region.capacity), observed.allocation.request_bytes);
    try std.testing.expectEqual(@as(u64, occupied), observed.allocation.occupied_bytes);
    try std.testing.expectEqual(@as(u64, requested), observed.allocation.requested_bytes);
    try std.testing.expect(observed.allocation.largest_free_payload < observed.allocation.request_bytes);
    const first = observed;
    self.publication_stage = .none;
    self.recordFailure(3);
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_failure_allocation(self, &observed, @sizeOf(FailureAllocation)));
    try std.testing.expectEqualDeep(first, observed);
}

test "shared working pins stay below growing retained publication storage" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.residency.max_pairs = 512;
    cfg.residency.max_threads = 512;
    cfg.residency.max_processors = 1;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    const name = @as([512]u8, @splat('p'));
    try self.readers.apply(&.{ .kind = 1, .pid = 1, .flags = 1, .start = 10, .name = &name, .name_len = name.len });
    for (0..256) |index| try self.readers.apply(&.{ .kind = 2, .pid = 1, .tid = @intCast(index + 1) });
    for (0..256) |index| try self.readers.apply(&.{ .kind = 7, .pid = 1, .tid = @intCast(index + 1), .old_pid = if (index == 0) 0 else 1, .old_tid = @intCast(index), .processor = 0, .qpc = @intCast(index * 3), .time_ticks = @intCast(1000000 + index * 3) });
    try self.readers.apply(&.{ .kind = 7, .old_pid = 1, .old_tid = 256, .processor = 0, .qpc = 768, .time_ticks = 1000768 });
    try self.readers.apply(&.{ .kind = 7, .processor = 0, .qpc = 1000, .time_ticks = 1001000 });
    const cpu_allocator = self.capture.budget.allocator();
    var pins: [8][]u8 = undefined;
    var pin_count: usize = 0;
    defer for (pins[0..pin_count]) |pin| cpu_allocator.free(pin);
    var held: ?Lease = null;
    defer if (held) |lease| self.release(lease.token) catch unreachable;
    const path = @as([64]u16, @splat('x'));
    var roles_separated = true;
    for (0..pins.len) |iteration| {
        // Bank output grows while one earlier bank remains leased. These
        // native notifications and CPU rows pass through actual Owner.publish.
        for (0..(iteration + 1) * 2) |_| try self.append(.{ .kind = 1, .qpc = 1000, .tick_ms = 1000, .pid = 1 }, &path);
        try self.publish(@intCast(1000 + iteration));
        const lease = try self.acquire();
        errdefer self.release(lease.token) catch unreachable;
        const view = lease.view.?;
        try std.testing.expectEqual(@as(u32, 256), view.residency.count);
        try std.testing.expectEqual(@as(u32, @intCast((iteration + 1) * 2)), view.notification_count);
        try std.testing.expectEqualStrings(&name, view.residency.rows.?[0].name.?[0..view.residency.rows.?[0].name_len]);
        pins[pin_count] = try cpu_allocator.alloc(u8, 64);
        @memset(pins[pin_count], @intCast(iteration));
        roles_separated = roles_separated and @intFromPtr(pins[pin_count].ptr) < @intFromPtr(view.residency.rows.?);
        pin_count += 1;
        if (held) |previous| {
            const before = previous.view.?;
            try std.testing.expectEqual(@as(i64, 3), before.residency.rows.?[0].duration);
            try std.testing.expectEqualStrings(&name, before.residency.rows.?[0].name.?[0..before.residency.rows.?[0].name_len]);
            try self.release(previous.token);
        }
        held = lease;
    }
    for (pins, 0..) |pin, iteration| for (pin) |byte| try std.testing.expectEqual(@as(u8, @intCast(iteration)), byte);
    try std.testing.expectEqual(@as(u64, 0), self.region.failures);
    try std.testing.expect(roles_separated);
}

test "cold publication profile is opt-in and reports real complete publication phases" {
    const boundary = @import("shared_profile_abi.zig");
    const ProfileView = @import("publication_profile.zig").View;
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    var result: ProfileView = undefined;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_profile_query(self, &result, @sizeOf(ProfileView)));
    try std.testing.expectEqual(@as(u32, 0), result.enabled);
    try std.testing.expectEqual(@as(u64, 0), result.region.low_alloc_calls);
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_profile_enable(self, 1));
    try self.publish(1000);
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_profile_query(self, &result, @sizeOf(ProfileView)));
    try std.testing.expectEqual(@as(u32, 1), result.enabled);
    try std.testing.expect(result.qpc_frequency > 0);
    try std.testing.expectEqual(@as(u32, 11), result.phases_count);
    try std.testing.expectEqual(@as(u64, 1), result.publications);
    try std.testing.expectEqual(@as(u64, 0), result.clock_failures);
    for (result.phases) |phase| try std.testing.expectEqual(@as(u64, 1), phase.calls);
    try std.testing.expect(result.region.high_alloc_calls > 0);
    var cpu_detail: @import("publication_profile.zig").CpuDetail = undefined;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_profile_query_cpu_detail(self, &cpu_detail, @sizeOf(@import("publication_profile.zig").CpuDetail)));
    try std.testing.expectEqual(@as(u64, 1), cpu_detail.native_snapshot.calls);
    try std.testing.expectEqual(@as(u64, 1), cpu_detail.abi_conversion.calls);
    try std.testing.expectEqual(@as(u64, 4), cpu_detail.detail_clock_reads);
    try std.testing.expectEqual(@as(u64, 0), cpu_detail.detail_clock_failures);
    try std.testing.expect(boundary.rm_kernel_etw_shared_profile_enable(self, 0) != 0);
    const lease = try self.acquire();
    defer self.release(lease.token) catch unreachable;
    try std.testing.expectEqual(@as(i64, 1000), lease.view.?.through_tick_ms);
}

test "unrelated notification buffers reuse a complete CPU component without new snapshot" {
    const profile_boundary = @import("shared_profile_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.residency.max_processors = 1;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    try std.testing.expectEqual(@as(i32, 0), profile_boundary.rm_kernel_etw_shared_profile_enable(self, 1));
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks(), .last_event_qpc = 1000 };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t;
    defer self.trace = null;
    try self.submit(&.{ .{ .kind = 9 }, .{ .kind = 1, .pid = 7, .flags = 1, .start = 10, .name = "stable".ptr, .name_len = 6 }, .{ .kind = 2, .pid = 7, .tid = 8 }, .{ .kind = 7, .pid = 7, .tid = 8, .processor = 0 }, .{ .kind = 7, .old_pid = 7, .old_tid = 8, .processor = 0, .qpc = 1000, .time_ticks = 1001000 } });
    // This is the real buffer callback, not explicit publish/EOF force-all.
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const old = try self.acquire();
    defer self.release(old.token) catch unreachable;
    try std.testing.expect(old.view.?.residency.flags & 1 != 0);
    try std.testing.expectEqual(@as(u32, 1), old.view.?.residency.count);
    const before = self.profile.cpu_detail.native_snapshot.calls;
    try std.testing.expectEqual(@as(u64, 1), before);
    const path = std.unicode.utf8ToUtf16LeStringLiteral("Z:\\notification");
    for (0..8) |iteration| {
        const qpc: i64 = @intCast(1001 + iteration);
        try self.append(.{ .kind = 4, .qpc = qpc, .tick_ms = qpc, .pid = 7, .size = 1 }, path);
        t.last_event_qpc = qpc;
        try std.testing.expect(trace.Trace.onBuffer(&t));
        const current = try self.acquire();
        defer self.release(current.token) catch unreachable;
        try std.testing.expect(current.view.?.version > old.view.?.version);
        try std.testing.expectEqual(@as(u32, 1), current.view.?.notification_count);
        try std.testing.expectEqualSlices(u16, path, current.view.?.notifications.?[0].path.?[0..current.view.?.notifications.?[0].path_len]);
        try std.testing.expectEqual(@as(i64, 1000), current.view.?.residency.rows.?[0].duration);
        try std.testing.expectEqualStrings("stable", old.view.?.residency.rows.?[0].name.?[0..old.view.?.residency.rows.?[0].name_len]);
    }
    try std.testing.expectEqual(before, self.profile.cpu_detail.native_snapshot.calls);
}

test "public current CPU sample command20 is admitted and acknowledged on a buffer" {
    const boundary = @import("shared_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks(), .last_event_qpc = 1000 };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t;
    defer self.trace = null;
    const ticket = try self.submitTracked(&.{.{ .kind = 20 }});
    try std.testing.expect(trace.Trace.onBuffer(&t));
    var view: p.View = .{};
    var sequence: u64 = 0;
    var lost: u64 = 0;
    var applied: u64 = 0;
    var lease: u64 = 0;
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_acquire_residency(self, &view, &sequence, &lost, &applied, &lease));
    defer _ = boundary.rm_kernel_etw_shared_release(self, lease);
    try std.testing.expect(applied >= ticket and sequence > 0);
    try std.testing.expectEqual(@as(u64, 0), lost);
}

test "all pinned aggregate CPU versions reject new acquires after loss then recover" {
    const boundary = @import("shared_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.residency.max_processors = 1;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t;
    defer self.trace = null;
    try self.readers.apply(&.{ .kind = 1, .pid = 7, .flags = 1, .start = 10, .name = "leased".ptr, .name_len = 6 });
    try self.readers.apply(&.{ .kind = 2, .pid = 7, .tid = 8 });
    var held: [3]?Lease = .{ null, null, null };
    defer for (held) |value| if (value) |lease| self.release(lease.token) catch unreachable;
    for (&held, 0..) |*item, i| {
        const started: i64 = @intCast(i * 1000);
        const through = started + 1000;
        // One finite window per due, as the managed reader runs them.
        try self.readers.apply(&.{ .kind = 9, .qpc = started });
        try self.readers.apply(&.{ .kind = 7, .pid = 7, .tid = 8, .processor = 0, .qpc = started });
        try self.readers.apply(&.{ .kind = 7, .old_pid = 7, .old_tid = 8, .processor = 0, .qpc = through, .time_ticks = 1000000 + through });
        // Explicit fixture publication forces a distinct current CPU view.
        try self.publish(through);
        item.* = try self.acquire();
        try std.testing.expect(item.*.?.view.?.residency.flags & 1 != 0);
        try std.testing.expectEqual(through, item.*.?.view.?.residency.through_qpc);
        t.last_event_qpc = through;
    }
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .loss = .{ .source = .buffer_callback, .events_lost = 1, .buffers_lost = 0 } }));
    var view: p.View = .{ .count = 99 };
    var sequence: u64 = 99;
    var lost: u64 = 99;
    var applied: u64 = 99;
    var lease: u64 = 99;
    const status = boundary.rm_kernel_etw_shared_acquire_residency(self, &view, &sequence, &lost, &applied, &lease);
    if (status == 0) _ = boundary.rm_kernel_etw_shared_release(self, lease);
    try std.testing.expectEqual(@as(i32, 3), status);
    try std.testing.expectEqual(@as(u64, 0), lease);
    try std.testing.expectEqual(@as(u32, 0), view.count);
    try std.testing.expectEqual(@as(u64, 0), sequence);
    try std.testing.expectEqual(@as(u64, 0), held[0].?.view.?.events_lost);
    try std.testing.expectEqual(@as(i64, 1000), held[0].?.view.?.residency.rows.?[0].duration);
    try self.release(held[0].?.token);
    held[0] = null;
    try std.testing.expect(trace.Trace.onBuffer(&t));
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_acquire_residency(self, &view, &sequence, &lost, &applied, &lease));
    defer _ = boundary.rm_kernel_etw_shared_release(self, lease);
    try std.testing.expectEqual(@as(u64, 1), lost);
    try std.testing.expect(view.flags & 1 == 0);
}

test "current CPU requests coalesce and never borrow an unrelated command acknowledgment" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks(), .last_event_qpc = 2000 };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t;
    defer self.trace = null;
    try self.publish(2000);
    const first = try self.acquireResidency();
    try self.release(first.token);
    const irq_ticket = try self.submitTracked(&.{.{ .kind = 15, .qpc = 2000 }});
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const unchanged = try self.acquireResidency();
    try std.testing.expectEqual(first.sequence, unchanged.sequence);
    try std.testing.expectEqual(first.applied_ticket, unchanged.applied_ticket);
    try std.testing.expect(unchanged.applied_ticket < irq_ticket);
    try self.release(unchanged.token);
    const ready = try self.requestResidency();
    try std.testing.expect(!ready.pending and ready.ticket == first.applied_ticket);
    const barrier = try self.submitTracked(&.{.{ .kind = 9 }});
    try std.testing.expectError(error.NoPublication, self.acquireResidency());
    const requested = try self.requestResidency();
    const coalesced = try self.requestResidency();
    try std.testing.expect(requested.pending and coalesced.pending);
    try std.testing.expect(requested.ticket > barrier);
    try std.testing.expectEqual(requested.ticket, coalesced.ticket);
    try std.testing.expectEqual(@as(usize, 2), self.command_count);
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const current = try self.acquireResidency();
    defer self.release(current.token) catch unreachable;
    try std.testing.expect(current.applied_ticket >= requested.ticket);
    try std.testing.expect(current.sequence > first.sequence);
    try std.testing.expect(current.view.flags & 1 == 0 and current.view.count == 0);
    const already = try self.requestResidency();
    try std.testing.expect(!already.pending and already.ticket == current.applied_ticket);
}

test "normal buffers prepare the first complete CPU window once then require current samples" {
    const profiler = @import("shared_profile_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.residency.max_processors = 1;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    try std.testing.expectEqual(@as(i32, 0), profiler.rm_kernel_etw_shared_profile_enable(self, 1));
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks(), .last_event_qpc = 500 };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    self.trace = &t;
    defer self.trace = null;
    // A continuous window of ten 100 QPC slots: a later sample slides by whole slots.
    try self.submit(&.{ .{ .kind = 9, .unique = 10 }, .{ .kind = 1, .pid = 7 }, .{ .kind = 2, .pid = 7, .tid = 8 }, .{ .kind = 7, .pid = 7, .tid = 8, .processor = 0 }, .{ .kind = 7, .old_pid = 7, .old_tid = 8, .processor = 0, .qpc = 500, .time_ticks = 1000500 } });
    try std.testing.expect(trace.Trace.onBuffer(&t));
    try std.testing.expectEqual(@as(u64, 0), self.profile.cpu_detail.native_snapshot.calls);
    try std.testing.expect(trace.Trace.onBuffer(&t));
    try std.testing.expectEqual(@as(u64, 0), self.profile.cpu_detail.native_snapshot.calls);
    try self.submit(&.{ .{ .kind = 7, .pid = 7, .tid = 8, .processor = 0, .qpc = 500 }, .{ .kind = 7, .old_pid = 7, .old_tid = 8, .processor = 0, .qpc = 1000, .time_ticks = 1001000 } });
    t.last_event_qpc = 1000;
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const complete = try self.acquireResidency();
    defer self.release(complete.token) catch unreachable;
    try std.testing.expect(complete.view.flags & 1 != 0);
    try std.testing.expectEqual(@as(i64, 1000), complete.view.rows.?[0].duration);
    try std.testing.expectEqual(@as(i32, 2), complete.view.rows.?[0].switches);
    try std.testing.expectEqual(@as(u64, 1), self.profile.cpu_detail.native_snapshot.calls);
    try self.submit(&.{ .{ .kind = 7, .pid = 7, .tid = 8, .processor = 0, .qpc = 1000 }, .{ .kind = 7, .old_pid = 7, .old_tid = 8, .processor = 0, .qpc = 1100, .time_ticks = 1001100 } });
    t.last_event_qpc = 1100;
    try std.testing.expect(trace.Trace.onBuffer(&t));
    try std.testing.expectEqual(@as(u64, 1), self.profile.cpu_detail.native_snapshot.calls);
    const passive = try self.acquireResidency();
    try std.testing.expectEqual(complete.sequence, passive.sequence);
    try std.testing.expectEqual(@as(i64, 1000), passive.view.through_qpc);
    try self.release(passive.token);
    const request = try self.requestResidency();
    try std.testing.expect(request.pending);
    try std.testing.expect(trace.Trace.onBuffer(&t));
    const current = try self.acquireResidency();
    defer self.release(current.token) catch unreachable;
    try std.testing.expect(current.applied_ticket >= request.ticket and current.sequence > complete.sequence);
    try std.testing.expectEqual(@as(i64, 1100), current.view.through_qpc);
    try std.testing.expectEqual(@as(i64, 1000), current.view.rows.?[0].duration);
    try std.testing.expectEqual(@as(u64, 2), self.profile.cpu_detail.native_snapshot.calls);
    try std.testing.expectEqual(@as(i64, 1000), complete.view.rows.?[0].duration);
}

test "complete idle CPU output is a value and unreachable aggregate headers release components" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.residency.max_processors = 1;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    try self.readers.apply(&.{ .kind = 7, .processor = 0 });
    try self.readers.apply(&.{ .kind = 7, .processor = 0, .qpc = 1000, .time_ticks = 1001000 });
    try self.publish(1000);
    const first = try self.acquireResidency();
    try std.testing.expect(first.view.flags & 3 == 3 and first.view.count == 0);
    const old_index = self.banks[@intCast(first.token % 8)].cpu_index.?;
    try self.publish(2000);
    try self.publish(3000);
    var live: usize = 0;
    for (self.cpu_banks) |component| if (component.references != 0) {
        live += 1;
    };
    try std.testing.expectEqual(@as(usize, 2), live);
    try std.testing.expectEqual(@as(u32, 1), self.cpu_banks[old_index].references);
    try self.release(first.token);
    try std.testing.expectEqual(@as(u32, 0), self.cpu_banks[old_index].references);
    live = 0;
    for (self.cpu_banks) |component| if (component.references != 0) {
        live += 1;
    };
    try std.testing.expectEqual(@as(usize, 1), live);
    const latest = try self.acquireResidency();
    defer self.release(latest.token) catch unreachable;
    const again = try self.acquireResidency();
    try std.testing.expectEqual(latest.sequence, again.sequence);
    try self.release(again.token);
    var references: u32 = 0;
    for (self.cpu_banks) |component| references += component.references;
    try std.testing.expectEqual(@as(u32, 1), references);
}

test "IRQ processor capacity uses the trace scope and explicit fact independently of CPU map" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var cfg = testConfig();
    cfg.residency.max_processors = 4;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1, .processor_count = 4 };
    self.trace = &t;
    defer self.trace = null;
    try self.configureProcessors(&.{0});
    try self.drain(1000);
    try self.interrupts.owner.observe(.isr, 100, 0, 1000);
    try self.publish(10000);
    const header = try self.acquireInterruptsAt(0);
    defer self.release(header.token) catch unreachable;
    try std.testing.expectEqual(@as(u32, 4), header.view.?.interrupts.processor_count);
    try std.testing.expectEqual(@as(f64, 0.25), header.view.?.interrupts.cpu_capacity_percent);
    const ticket = try self.submitTracked(&.{.{ .kind = 21, .pid = 8 }});
    try std.testing.expectError(error.NoPublication, self.acquireInterruptsAt(ticket));
    try self.configureProcessors(&.{ 1, 2 });
    try self.drain(10000);
    try self.publish(10000);
    const configured = try self.acquireInterruptsAt(ticket);
    defer self.release(configured.token) catch unreachable;
    try std.testing.expectEqual(@as(u32, 8), configured.view.?.interrupts.processor_count);
    try std.testing.expectEqual(@as(f64, 0.125), configured.view.?.interrupts.cpu_capacity_percent);
    try std.testing.expectEqual(@as(u32, 4), header.view.?.interrupts.processor_count);
    try std.testing.expectError(error.InvalidInput, self.submit(&.{.{ .kind = 21, .pid = 0 }}));
    try std.testing.expectError(error.InvalidInput, self.submit(&.{.{ .kind = 21, .pid = 8, .qpc = 8 }}));
}

test "scoped IRQ acquire rejects a same-tick pre-reset bank and newly lost historical view" {
    const boundary = @import("shared_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    const old_path = std.unicode.utf8ToUtf16LeStringLiteral("old-driver.sys");
    try self.submit(&.{.{ .kind = 15, .qpc = 0 }});
    try self.drain(0);
    try self.interrupts.owner.upsertImage(100, 10, old_path, 0);
    try self.interrupts.owner.observe(.isr, 100, 100, 1000);
    try self.publish(10000);
    const old = try self.acquire();
    defer self.release(old.token) catch unreachable;
    try std.testing.expect(old.view.?.interrupts.flags & 1 != 0);
    const ticket = try self.submitTracked(&.{.{ .kind = 15, .qpc = 10000 }});
    var view: irq.View = .{ .event_count = 99 };
    var sequence: u64 = 99;
    var lease: u64 = 99;
    try std.testing.expectEqual(@as(i32, 3), boundary.rm_kernel_etw_shared_acquire_interrupts_at(self, ticket, &view, &sequence, &lease));
    try std.testing.expectEqual(@as(u64, 0), view.event_count);
    try std.testing.expectEqual(@as(u64, 0), sequence);
    try std.testing.expectEqual(@as(u64, 0), lease);
    try self.drain(10000);
    // A successor binding has a new module inventory, just like the managed
    // source. Its tracked reset must not preserve a same-generation predecessor's map.
    try std.testing.expectEqual(@as(?@import("interrupts.zig").Identity, null), self.interrupts.owner.modules.resolve(100));
    try std.testing.expectError(error.NoPublication, self.acquireInterruptsAt(ticket));
    try self.publish(10000);
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_acquire_interrupts_at(self, ticket, &view, &sequence, &lease));
    try std.testing.expect(view.flags & 1 == 0 and view.window_ms == 0 and view.event_count == 0);
    try self.release(lease);
    try std.testing.expectEqual(@as(u64, 1), old.view.?.interrupts.event_count);
    try std.testing.expect(old.view.?.interrupts.flags & 1 != 0);
    const old_module = old.view.?.interrupts.maximum_module;
    try std.testing.expectEqualSlices(u16, old_path, old_module.path.?[0..old_module.path_len]);
    self.capture.events_lost = 1;
    Owner.loss(self, 10001);
    view = .{ .event_count = 99 };
    sequence = 99;
    lease = 99;
    try std.testing.expectEqual(@as(i32, 3), boundary.rm_kernel_etw_shared_acquire_interrupts_at(self, 0, &view, &sequence, &lease));
    try std.testing.expectEqual(@as(u64, 0), view.event_count);
    try std.testing.expectEqual(@as(u64, 0), sequence);
    try std.testing.expectEqual(@as(u64, 0), lease);
    try std.testing.expectEqual(@as(i32, 0), self.failure.load(.acquire));
}

test "first IRQ binding keeps its ImageLoad rundown modules through residency output changes" {
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    // The first image populates the local DOS namespace: room for every drive.
    var cfg = testConfig(); cfg.maximum_namespace_entries = 64;
    const self = try Owner.createExternal(backing, cfg, absent_identity);
    defer self.destroy() catch unreachable;
    try self.setOutputs(1);
    var t: trace.Trace = .{ .owner = self.capture, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1 };
    defer t.deinitImageRound();
    self.trace = &t;
    defer self.trace = null;
    t.consumer.processing.store(true, .release);
    defer t.consumer.processing.store(false, .release);
    // The interrupt output is queued before the holder's ImageLoad keyword
    // change. Its one-time rundown, kernel modules first, then reaches the
    // consumer before any buffer boundary and before the binding command.
    try self.setOutputs(1 | 2);
    try std.testing.expect(!self.has(2));
    const path = std.unicode.utf8ToUtf16LeStringLiteral("driver.sys");
    const rundown: native.decoder.Event = .{ .qpc = 100, .processor = 0, .data = .{ .image = .{ .lifecycle = .rundown_start, .process_id = 0, .base = 0xFFFF_F800_0000_1000, .size = 0x100, .path_utf16le = std.mem.sliceAsBytes(path) } } };
    try std.testing.expect(trace.Trace.onNotice(&t, .{ .event = rundown }));
    try std.testing.expect(self.has(2));
    try std.testing.expect(self.interrupts.owner.modules.resolve(0xFFFF_F800_0000_1010) != null);
    // The binding command starts the holder's window after that rundown.
    try self.submit(&.{.{ .kind = 15, .qpc = 200 }});
    try self.drain(200);
    try std.testing.expectEqual(@as(i64, 200), self.interrupts.owner.started_tick_ms);
    // Residency leases come and go while the interrupt output stays held.
    var round: usize = 0;
    while (round < 3) : (round += 1) {
        try self.setOutputs(2);
        try self.drain(0);
        try std.testing.expect(!self.has(1));
        try self.setOutputs(1 | 2);
        try self.drain(0);
    }
    try std.testing.expect(self.interrupts.owner.modules.resolve(0xFFFF_F800_0000_1010) != null);
    try std.testing.expectEqual(@as(u64, 1), (try self.queryInterruptMetadata()).active_modules);
    // A successor binding on the same output starts a new inventory.
    try self.submit(&.{.{ .kind = 15, .qpc = 300 }});
    try self.drain(300);
    try std.testing.expectEqual(@as(u64, 0), (try self.queryInterruptMetadata()).active_modules);
    try std.testing.expectEqual(@as(i32, 0), t.failure);
    try std.testing.expectEqual(@as(i32, 0), self.failure.load(.acquire));
}

test "published progress pairs immutable counters with CPU and bank stamps during processing" {
    const boundary = @import("shared_abi.zig");
    const backing = try std.testing.allocator.alloc(u8, 4 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    const self = try Owner.createExternal(backing, testConfig(), absent_identity);
    defer self.destroy() catch unreachable;
    var t: trace.Trace = .{ .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths, .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice }), .interrupt_qpc_origin = 0, .shared_hooks = self.hooks() };
    t.consumer.metadata = .{ .qpc_frequency = 1000, .start_filetime_100ns = 1, .header_qpc = 0, .clock_type = 1, .processor_count = 4, .os_buffer_size = 65536 };
    self.trace = &t;
    defer self.trace = null;
    t.consumer.processing.store(true, .release);
    defer t.consumer.processing.store(false, .release);
    var progress: PublishedProgress = .{};
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(trace.TraceStatus));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(PublishedProgress));
    try std.testing.expectEqual(@as(i32, 3), boundary.rm_kernel_etw_shared_query_published_progress(self, &progress, @sizeOf(PublishedProgress)));
    var live_status: trace.TraceStatus = undefined;
    try std.testing.expectEqual(@as(i32, 3), boundary.rm_kernel_etw_shared_status(self, &live_status, @sizeOf(trace.TraceStatus)));
    var held: [3]?Lease = .{ null, null, null };
    defer for (held) |value| if (value) |lease| self.release(lease.token) catch unreachable;
    for (&held, 0..) |*item, i| {
        const records: u64 = @intCast((i + 1) * 10);
        t.consumer.counters.records = records;
        t.consumer.counters.decoded = records - 2;
        t.consumer.counters.unhandled = 2;
        t.switches = records / 2;
        try self.publish(@intCast((i + 1) * 1000));
        item.* = try self.acquire();
        try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_published_progress(self, &progress, @sizeOf(PublishedProgress)));
        const cpu = try self.acquireResidency();
        try std.testing.expectEqual(records, progress.status.records);
        try std.testing.expectEqual(records - 2, progress.status.decoded);
        try std.testing.expectEqual(@as(u64, 2), progress.status.unhandled);
        try std.testing.expectEqual(records / 2, progress.status.switches);
        try std.testing.expectEqual(@as(i64, 1000), progress.status.qpc_frequency);
        try std.testing.expectEqual(@as(u32, 4), progress.status.processor_count);
        try std.testing.expectEqual(@as(u32, 65536), progress.status.os_buffer_size);
        try std.testing.expectEqual(item.*.?.view.?.version, progress.bank_sequence);
        try std.testing.expectEqual(cpu.sequence, progress.cpu_sequence);
        try std.testing.expectEqual(cpu.applied_ticket, progress.cpu_applied_ticket);
        try self.release(cpu.token);
        // Mutating working counters alone must never change a bank getter.
        t.consumer.counters.records = 999;
        try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_published_progress(self, &progress, @sizeOf(PublishedProgress)));
        try std.testing.expectEqual(records, progress.status.records);
    }
    const oldest = self.banks[@intCast(held[0].?.token % 8)].published_progress.?;
    try std.testing.expectEqual(@as(u64, 10), oldest.status.records);
    try std.testing.expectEqual(held[0].?.view.?.version, oldest.bank_sequence);
    try std.testing.expectEqual(@as(u64, 1), oldest.cpu_sequence);
    const before = progress;
    // All banks are pinned. A skipped publication and concurrent processing
    // still expose the same immutable prefix, never the mutable 999 records.
    try self.publish(4000);
    try std.testing.expect(self.skipped_publications > 0);
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_query_published_progress(self, &progress, @sizeOf(PublishedProgress)));
    try std.testing.expectEqualDeep(before, progress);
    try std.testing.expectEqual(@as(i32, 3), boundary.rm_kernel_etw_shared_status(self, &live_status, @sizeOf(trace.TraceStatus)));
}
