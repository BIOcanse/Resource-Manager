const std = @import("std");
const ResultCode = @import("../common/result_codes.zig").ResultCode;

const bcrypt_use_system_preferred_rng: u32 = 0x0000_0002;

extern "bcrypt" fn BCryptGenRandom(
    algorithm: ?*anyopaque,
    buffer: [*]u8,
    buffer_length: u32,
    flags: u32,
) callconv(.winapi) i32;

pub const abi_version: u32 = 0x0003_0000;
pub const hard_capacity_limit: u32 = 65_536;

const opaque_id_attempt_limit: u8 = 8;

pub const Capability = struct {
    pub const register: u64 = 1 << 0;
    pub const dispatch_policy: u64 = 1 << 1;
    pub const effective: u64 = register | dispatch_policy;
    pub const wire_known: u64 = effective;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    capacity: u32,
    reserved0: u32,
    host_instance_id: u64,
    lease_duration_ticks: u64,
    maximum_attestation_age_ticks: u64,
};

pub const CallerFacts = extern struct {
    host_instance_id: u64,
    transport_connection_id: u64,
    process_created_utc_ticks: i64,
    attested_at_timestamp: u64,
    authentication_id_luid: u64,
    sid_digest: [4]u64,
    image_path_digest: [4]u64,
    executable_file_id_high: u64,
    executable_file_id_low: u64,
    volume_serial_number: u64,
    process_id: u32,
    windows_session_id: u32,
    integrity_level_rid: u32,
    is_elevated: u8,
    reserved0: [7]u8,
};

pub const IssueInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    owner_application_key: u64,
    capabilities: u64,
    now_timestamp: u64,
    caller: CallerFacts,
};

pub const RenewInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    lease_id_high: u64,
    lease_id_low: u64,
    expected_generation: u64,
    now_timestamp: u64,
    caller: CallerFacts,
};

pub const ResolveInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    lease_id_high: u64,
    lease_id_low: u64,
    expected_generation: u64,
    required_capabilities: u64,
    now_timestamp: u64,
};

pub const RevokeInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    lease_id_high: u64,
    lease_id_low: u64,
    expected_generation: u64,
};

pub const LeaseReceipt = extern struct {
    abi_version: u32,
    struct_size: u32,
    lease_id_high: u64,
    lease_id_low: u64,
    instance_id_high: u64,
    instance_id_low: u64,
    owner_application_key: u64,
    capabilities: u64,
    issued_at_timestamp: u64,
    deadline_timestamp: u64,
    heartbeat_generation: u64,
    caller: CallerFacts,
};

const Slot = struct {
    active: bool = false,
    lease_id_high: u64 = 0,
    lease_id_low: u64 = 0,
    instance_id_high: u64 = 0,
    instance_id_low: u64 = 0,
    owner_application_key: u64 = 0,
    capabilities: u64 = 0,
    issued_at_timestamp: u64 = 0,
    deadline_timestamp: u64 = 0,
    heartbeat_generation: u64 = 0,
    caller: CallerFacts = std.mem.zeroes(CallerFacts),
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    host_instance_id: u64,
    lease_duration_ticks: u64,
    maximum_attestation_age_ticks: u64,
    slots: []Slot,

    pub fn create(config: *const Config) !*Session {
        if (!validConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        const slots = try allocator.alloc(Slot, @intCast(config.capacity));
        @memset(slots, Slot{});
        self.* = .{
            .allocator = allocator,
            .host_instance_id = config.host_instance_id,
            .lease_duration_ticks = config.lease_duration_ticks,
            .maximum_attestation_age_ticks = config.maximum_attestation_age_ticks,
            .slots = slots,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.slots);
        allocator.destroy(self);
    }

    pub fn issue(self: *Session, input: *const IssueInput, output: *LeaseReceipt) ResultCode {
        output.* = std.mem.zeroes(LeaseReceipt);
        if (!validIssueInput(
            input,
            self.host_instance_id,
            self.maximum_attestation_age_ticks,
        )) return .invalid_argument;
        lock(&self.mutex);
        defer self.mutex.unlock();
        _ = self.expireLocked(input.now_timestamp);
        const slot = self.firstFreeSlot() orelse return .unavailable;
        const lease_id = self.createUniqueLeaseId() orelse return .unavailable;
        const instance_id = createOpaqueId() orelse return .unavailable;
        const deadline_timestamp = std.math.add(
            u64,
            input.now_timestamp,
            self.lease_duration_ticks,
        ) catch return .unavailable;
        slot.* = .{
            .active = true,
            .lease_id_high = lease_id[0],
            .lease_id_low = lease_id[1],
            .instance_id_high = instance_id[0],
            .instance_id_low = instance_id[1],
            .owner_application_key = input.owner_application_key,
            .capabilities = input.capabilities,
            .issued_at_timestamp = input.now_timestamp,
            .deadline_timestamp = deadline_timestamp,
            .heartbeat_generation = 1,
            .caller = input.caller,
        };
        fillReceipt(slot, output);
        return .ok;
    }

    pub fn renew(self: *Session, input: *const RenewInput, output: *LeaseReceipt) ResultCode {
        output.* = std.mem.zeroes(LeaseReceipt);
        if (!validRenewInput(
            input,
            self.host_instance_id,
            self.maximum_attestation_age_ticks,
        )) return .invalid_argument;
        lock(&self.mutex);
        defer self.mutex.unlock();
        const slot = self.findActive(input.lease_id_high, input.lease_id_low) orelse return .unavailable;
        if (slot.deadline_timestamp <= input.now_timestamp) {
            slot.* = Slot{};
            return .unavailable;
        }
        if (slot.heartbeat_generation != input.expected_generation) return .stale_frame;
        if (!sameCaller(&slot.caller, &input.caller)) return .invalid_argument;
        if (input.caller.attested_at_timestamp < slot.caller.attested_at_timestamp) {
            return .invalid_argument;
        }
        if (slot.heartbeat_generation == std.math.maxInt(u64)) {
            slot.* = Slot{};
            return .unavailable;
        }
        slot.deadline_timestamp = std.math.add(
            u64,
            input.now_timestamp,
            self.lease_duration_ticks,
        ) catch return .unavailable;
        slot.heartbeat_generation += 1;
        slot.caller.attested_at_timestamp = input.caller.attested_at_timestamp;
        fillReceipt(slot, output);
        return .ok;
    }

    pub fn resolve(self: *Session, input: *const ResolveInput, output: *LeaseReceipt) ResultCode {
        output.* = std.mem.zeroes(LeaseReceipt);
        if (!validResolveInput(input)) return .invalid_argument;
        lock(&self.mutex);
        defer self.mutex.unlock();
        const slot = self.findActive(input.lease_id_high, input.lease_id_low) orelse return .unavailable;
        if (slot.deadline_timestamp <= input.now_timestamp) {
            slot.* = Slot{};
            return .unavailable;
        }
        if (slot.heartbeat_generation != input.expected_generation) return .stale_frame;
        if ((slot.capabilities & input.required_capabilities) != input.required_capabilities) {
            return .unavailable;
        }
        fillReceipt(slot, output);
        return .ok;
    }

    pub fn revoke(self: *Session, input: *const RevokeInput) ResultCode {
        if (!validRevokeInput(input)) return .invalid_argument;
        lock(&self.mutex);
        defer self.mutex.unlock();
        const slot = self.findActive(input.lease_id_high, input.lease_id_low) orelse return .unavailable;
        if (slot.heartbeat_generation != input.expected_generation) return .stale_frame;
        slot.* = Slot{};
        return .ok;
    }

    pub fn expire(self: *Session, now_timestamp: u64, expired_count: *u32) ResultCode {
        expired_count.* = 0;
        if (now_timestamp == 0) return .invalid_argument;
        lock(&self.mutex);
        defer self.mutex.unlock();
        expired_count.* = self.expireLocked(now_timestamp);
        return .ok;
    }

    fn firstFreeSlot(self: *Session) ?*Slot {
        for (self.slots) |*slot| {
            if (!slot.active) return slot;
        }
        return null;
    }

    fn findActive(self: *Session, high: u64, low: u64) ?*Slot {
        for (self.slots) |*slot| {
            if (slot.active and slot.lease_id_high == high and slot.lease_id_low == low) return slot;
        }
        return null;
    }

    fn expireLocked(self: *Session, now_timestamp: u64) u32 {
        var count: u32 = 0;
        for (self.slots) |*slot| {
            if (slot.active and slot.deadline_timestamp <= now_timestamp) {
                slot.* = Slot{};
                count += 1;
            }
        }
        return count;
    }

    fn createUniqueLeaseId(self: *Session) ?[2]u64 {
        var attempt: u8 = 0;
        while (attempt < opaque_id_attempt_limit) : (attempt += 1) {
            const candidate = createOpaqueId() orelse return null;
            if (self.findActive(candidate[0], candidate[1]) == null) return candidate;
        }
        return null;
    }
};

fn validConfig(config: *const Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.capacity > 0 and
        config.capacity <= hard_capacity_limit and
        config.host_instance_id != 0 and
        config.lease_duration_ticks > 0 and
        config.maximum_attestation_age_ticks > 0;
}

fn validIssueInput(
    input: *const IssueInput,
    host_instance_id: u64,
    maximum_attestation_age_ticks: u64,
) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(IssueInput) and
        input.owner_application_key != 0 and
        input.capabilities != 0 and
        (input.capabilities & ~Capability.effective) == 0 and
        (input.capabilities & Capability.register) != 0 and
        input.now_timestamp != 0 and
        validCaller(
            &input.caller,
            host_instance_id,
            input.now_timestamp,
            maximum_attestation_age_ticks,
        );
}

fn validRenewInput(
    input: *const RenewInput,
    host_instance_id: u64,
    maximum_attestation_age_ticks: u64,
) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(RenewInput) and
        (input.lease_id_high | input.lease_id_low) != 0 and
        input.expected_generation != 0 and
        input.now_timestamp != 0 and
        validCaller(
            &input.caller,
            host_instance_id,
            input.now_timestamp,
            maximum_attestation_age_ticks,
        );
}

fn validResolveInput(input: *const ResolveInput) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ResolveInput) and
        (input.lease_id_high | input.lease_id_low) != 0 and
        input.expected_generation != 0 and
        input.required_capabilities != 0 and
        (input.required_capabilities & ~Capability.effective) == 0 and
        input.now_timestamp != 0;
}

fn validRevokeInput(input: *const RevokeInput) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(RevokeInput) and
        (input.lease_id_high | input.lease_id_low) != 0 and
        input.expected_generation != 0;
}

fn validCaller(
    caller: *const CallerFacts,
    host_instance_id: u64,
    now_timestamp: u64,
    maximum_attestation_age_ticks: u64,
) bool {
    return caller.host_instance_id == host_instance_id and
        caller.transport_connection_id != 0 and
        caller.process_created_utc_ticks > 0 and
        caller.attested_at_timestamp != 0 and
        caller.attested_at_timestamp <= now_timestamp and
        now_timestamp - caller.attested_at_timestamp <= maximum_attestation_age_ticks and
        caller.authentication_id_luid != 0 and
        caller.process_id != 0 and
        caller.integrity_level_rid != 0 and
        !allZero(&caller.sid_digest) and
        !allZero(&caller.image_path_digest) and
        (caller.executable_file_id_high | caller.executable_file_id_low) != 0;
}

fn sameCaller(expected: *const CallerFacts, actual: *const CallerFacts) bool {
    return expected.host_instance_id == actual.host_instance_id and
        expected.transport_connection_id == actual.transport_connection_id and
        expected.process_created_utc_ticks == actual.process_created_utc_ticks and
        expected.process_id == actual.process_id and
        expected.windows_session_id == actual.windows_session_id and
        expected.authentication_id_luid == actual.authentication_id_luid and
        expected.volume_serial_number == actual.volume_serial_number and
        expected.integrity_level_rid == actual.integrity_level_rid and
        expected.is_elevated == actual.is_elevated and
        std.mem.eql(u64, &expected.sid_digest, &actual.sid_digest) and
        std.mem.eql(u64, &expected.image_path_digest, &actual.image_path_digest) and
        expected.executable_file_id_high == actual.executable_file_id_high and
        expected.executable_file_id_low == actual.executable_file_id_low;
}

fn allZero(values: *const [4]u64) bool {
    return (values[0] | values[1] | values[2] | values[3]) == 0;
}

fn fillReceipt(slot: *const Slot, output: *LeaseReceipt) void {
    output.* = .{
        .abi_version = abi_version,
        .struct_size = @sizeOf(LeaseReceipt),
        .lease_id_high = slot.lease_id_high,
        .lease_id_low = slot.lease_id_low,
        .instance_id_high = slot.instance_id_high,
        .instance_id_low = slot.instance_id_low,
        .owner_application_key = slot.owner_application_key,
        .capabilities = slot.capabilities,
        .issued_at_timestamp = slot.issued_at_timestamp,
        .deadline_timestamp = slot.deadline_timestamp,
        .heartbeat_generation = slot.heartbeat_generation,
        .caller = slot.caller,
    };
}

fn createOpaqueId() ?[2]u64 {
    var value: [2]u64 = undefined;
    var attempt: u8 = 0;
    while (attempt < opaque_id_attempt_limit) : (attempt += 1) {
        const bytes = std.mem.asBytes(&value);
        const status = BCryptGenRandom(
            null,
            bytes.ptr,
            @intCast(bytes.len),
            bcrypt_use_system_preferred_rng,
        );
        if (status < 0) return null;
        if ((value[0] | value[1]) != 0) return value;
    }
    return null;
}

fn lock(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

fn testCaller(host_instance_id: u64, connection_id: u64, attested_at: u64) CallerFacts {
    return .{
        .host_instance_id = host_instance_id,
        .transport_connection_id = connection_id,
        .process_created_utc_ticks = 90,
        .attested_at_timestamp = attested_at,
        .authentication_id_luid = 14,
        .sid_digest = .{ 1, 2, 3, 4 },
        .image_path_digest = .{ 5, 6, 7, 8 },
        .executable_file_id_high = 9,
        .executable_file_id_low = 10,
        .volume_serial_number = 13,
        .process_id = 11,
        .windows_session_id = 12,
        .integrity_level_rid = 0x2000,
        .is_elevated = 0,
        .reserved0 = [_]u8{0} ** 7,
    };
}

test "lease authority uses exact generation identity capability and monotonic expiry" {
    const config = Config{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Config),
        .capacity = 2,
        .reserved0 = 0,
        .host_instance_id = 77,
        .lease_duration_ticks = 100,
        .maximum_attestation_age_ticks = 10,
    };
    const session = try Session.create(&config);
    defer session.destroy();
    var issue = IssueInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(IssueInput),
        .owner_application_key = 88,
        .capabilities = Capability.register | Capability.dispatch_policy,
        .now_timestamp = 100,
        .caller = testCaller(77, 99, 100),
    };
    var receipt: LeaseReceipt = undefined;
    try std.testing.expectEqual(ResultCode.ok, session.issue(&issue, &receipt));
    try std.testing.expectEqual(@as(u64, 1), receipt.heartbeat_generation);

    var resolve = ResolveInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(ResolveInput),
        .lease_id_high = receipt.lease_id_high,
        .lease_id_low = receipt.lease_id_low,
        .expected_generation = receipt.heartbeat_generation,
        .required_capabilities = Capability.dispatch_policy,
        .now_timestamp = 150,
    };
    var resolved: LeaseReceipt = undefined;
    try std.testing.expectEqual(ResultCode.ok, session.resolve(&resolve, &resolved));
    resolve.required_capabilities = 1 << 63;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.resolve(&resolve, &resolved));

    var disabled_issue = issue;
    disabled_issue.capabilities |= 1 << 63;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.issue(&disabled_issue, &resolved));

    var renew = RenewInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(RenewInput),
        .lease_id_high = receipt.lease_id_high,
        .lease_id_low = receipt.lease_id_low,
        .expected_generation = receipt.heartbeat_generation,
        .now_timestamp = 160,
        .caller = testCaller(77, 99, 160),
    };
    try std.testing.expectEqual(ResultCode.ok, session.renew(&renew, &receipt));
    try std.testing.expectEqual(@as(u64, 2), receipt.heartbeat_generation);
    try std.testing.expectEqual(ResultCode.stale_frame, session.renew(&renew, &resolved));
    renew.expected_generation = receipt.heartbeat_generation;
    renew.caller.transport_connection_id = 100;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.renew(&renew, &resolved));

    var expired: u32 = 0;
    try std.testing.expectEqual(ResultCode.ok, session.expire(260, &expired));
    try std.testing.expectEqual(@as(u32, 1), expired);
}

test "lease authority rejects stale revoke and reuses fixed capacity" {
    const config = Config{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Config),
        .capacity = 1,
        .reserved0 = 0,
        .host_instance_id = 177,
        .lease_duration_ticks = 100,
        .maximum_attestation_age_ticks = 10,
    };
    const session = try Session.create(&config);
    defer session.destroy();
    const issue = IssueInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(IssueInput),
        .owner_application_key = 188,
        .capabilities = Capability.register,
        .now_timestamp = 100,
        .caller = testCaller(177, 199, 100),
    };
    var receipt: LeaseReceipt = undefined;
    try std.testing.expectEqual(ResultCode.ok, session.issue(&issue, &receipt));
    var second: LeaseReceipt = undefined;
    try std.testing.expectEqual(ResultCode.unavailable, session.issue(&issue, &second));
    var revoke = RevokeInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(RevokeInput),
        .lease_id_high = receipt.lease_id_high,
        .lease_id_low = receipt.lease_id_low,
        .expected_generation = receipt.heartbeat_generation + 1,
    };
    try std.testing.expectEqual(ResultCode.stale_frame, session.revoke(&revoke));
    revoke.expected_generation = receipt.heartbeat_generation;
    try std.testing.expectEqual(ResultCode.ok, session.revoke(&revoke));
    try std.testing.expectEqual(ResultCode.ok, session.issue(&issue, &second));
    try std.testing.expect(receipt.lease_id_high != second.lease_id_high or receipt.lease_id_low != second.lease_id_low);
}

test "lease generation exhaustion revokes the old identity instead of wrapping" {
    const config = Config{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Config),
        .capacity = 1,
        .reserved0 = 0,
        .host_instance_id = 277,
        .lease_duration_ticks = 100,
        .maximum_attestation_age_ticks = 10,
    };
    const session = try Session.create(&config);
    defer session.destroy();
    const issue = IssueInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(IssueInput),
        .owner_application_key = 288,
        .capabilities = Capability.register,
        .now_timestamp = 100,
        .caller = testCaller(277, 299, 100),
    };
    var original: LeaseReceipt = undefined;
    try std.testing.expectEqual(ResultCode.ok, session.issue(&issue, &original));
    const slot = session.findActive(original.lease_id_high, original.lease_id_low).?;
    slot.heartbeat_generation = std.math.maxInt(u64);

    const renew = RenewInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(RenewInput),
        .lease_id_high = original.lease_id_high,
        .lease_id_low = original.lease_id_low,
        .expected_generation = std.math.maxInt(u64),
        .now_timestamp = 101,
        .caller = testCaller(277, 299, 101),
    };
    var renewed: LeaseReceipt = undefined;
    try std.testing.expectEqual(ResultCode.unavailable, session.renew(&renew, &renewed));

    const resolve = ResolveInput{
        .abi_version = abi_version,
        .struct_size = @sizeOf(ResolveInput),
        .lease_id_high = original.lease_id_high,
        .lease_id_low = original.lease_id_low,
        .expected_generation = 1,
        .required_capabilities = Capability.register,
        .now_timestamp = 102,
    };
    try std.testing.expectEqual(ResultCode.unavailable, session.resolve(&resolve, &renewed));

    var replacement: LeaseReceipt = undefined;
    try std.testing.expectEqual(ResultCode.ok, session.issue(&issue, &replacement));
    try std.testing.expect(
        original.lease_id_high != replacement.lease_id_high or
            original.lease_id_low != replacement.lease_id_low,
    );
}
