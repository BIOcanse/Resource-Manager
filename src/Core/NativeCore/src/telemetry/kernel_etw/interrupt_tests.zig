const std = @import("std");
const testing = std.testing;
const interrupts = @import("interrupts.zig");
const u = std.unicode.utf8ToUtf16LeStringLiteral;
const config: interrupts.Config = .{ .max_modules = 32, .max_path_units = 32768, .max_identities = 32, .max_slices = 128, .max_slice_drivers = 256, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16 };

test "native IRQ normalized admission preserves five receives and four managed rejection reasons" {
    const counters = @import("admission_counters.zig");
    var store = try counters.Store.init(testing.allocator, 1);
    defer store.deinit();
    var descriptor: counters.Config = .{ .token = 1, .generation = 17, .kind_mask = counters.bit(.dpc),
        .output_mask = 2, .flags = counters.active | counters.current_binding | counters.window_active,
        .window_start = 100, .incomplete_through = -1 };
    try store.configure(descriptor);
    var owner = try interrupts.Owner.init(testing.allocator, config, 17, 100, 0);
    defer owner.deinit();
    const Callback = struct {
        fn deliver(s: *counters.Store, o: *interrupts.Owner, facts: counters.Facts) !bool {
            s.recordPhysical(.dpc, true);
            const accepted = try s.observeAdmitted(1, .dpc, facts);
            if (accepted) try o.observe(.dpc, facts.duration_ms, 0, facts.observed_tick);
            return accepted;
        }
    };
    try testing.expect(try Callback.deliver(&store, &owner, .{ .generation = 17, .observed_tick = 101, .duration_ms = 2 }));
    descriptor.window_start = 105;
    try store.configure(descriptor);
    try testing.expect(!try Callback.deliver(&store, &owner, .{ .generation = 17, .observed_tick = 104, .duration_ms = 2 }));
    try testing.expect(!try Callback.deliver(&store, &owner, .{ .generation = 999, .observed_tick = 105, .duration_ms = 2 }));
    try testing.expect(!try Callback.deliver(&store, &owner, .{ .generation = 17, .observed_tick = 105, .duration_ms = std.math.nan(f64) }));
    // The legacy dispatcher could capture a callback before Cancel. Native
    // production intake has no such managed payload callback; observeAdmitted
    // proves the same policy before the ordered descriptor retirement.
    descriptor.flags &= ~counters.active;
    try store.configure(descriptor);
    try testing.expect(!try Callback.deliver(&store, &owner, .{ .generation = 17, .observed_tick = 105, .duration_ms = 2 }));
    try testing.expectEqual(counters.Pair{ .received = 5, .dropped = 4 }, store.query(1, .dpc, .none).?);
    for ([_]counters.Reason{ .before_window, .generation_mismatch, .invalid_duration, .inactive_binding }) |reason|
        try testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, .dpc, reason).?);
    try testing.expectEqual(counters.Pair{ .received = 5 }, store.queryPhysical(.dpc));
    try testing.expectEqual(counters.Pair{}, store.query(1, .dpc, .reserved_2).?);
    var published = try owner.publish(testing.allocator, 10100, 1);
    defer published.deinit();
    try testing.expectEqual(@as(u64, 1), published.event_count);
    try testing.expectEqual(@as(f64, 2), published.total_duration_ms);
    store.retire(1);
    try testing.expect(store.query(1, .dpc, .none) == null);
}

test "address index allocates by occupied ranges instead of its compiled cap" {
    const Budget = @import("budget.zig").Budget;
    var budget: Budget = .{ .backing = testing.allocator, .cap = 1024 * 1024 };
    var wide = config;
    wide.max_modules = 1 << 20;
    wide.max_path_units = 128;
    var map = try interrupts.ModuleMap.init(budget.allocator(), wide);
    defer map.deinit();
    try testing.expectEqual(@as(usize, 0), budget.current);
    try testing.expectEqual(@as(usize, 0), map.ranges.len);
    try testing.expectEqual(@as(usize, 0), map.tree.len);
    for (0..17) |index| try map.upsert(100 + index * 100, 200, u("driver.sys"));
    try testing.expectEqual(@as(usize, 17), map.len);
    try testing.expectEqual(@as(usize, 32), map.ranges.len);
    try testing.expectEqualSlices(u16, u("driver.sys"), map.resolve(1799).?.name.?);
    try testing.expect(budget.current < 16 * 1024);
    for (0..17) |index| map.remove(100 + index * 100);
    try testing.expectEqual(@as(usize, 0), map.len);
    try testing.expectEqual(@as(usize, 0), budget.current);
    try testing.expect(map.resolve(100) == null);
}

test "IRQ metadata starts small and reset returns all duration storage" {
    const Budget = @import("budget.zig").Budget;
    var budget: Budget = .{ .backing = testing.allocator, .cap = 1024 * 1024 };
    var owner = try interrupts.Owner.init(budget.allocator(), .{
        .max_modules = 65536,
        .max_path_units = 128,
        .max_identities = 65536,
        .max_slices = 4096,
        .max_slice_drivers = 1 << 20,
        .bucket_ms = 10000,
        .bucket_count = 6,
        .reported_drivers = 16,
    }, 1, 0, 0);
    defer owner.deinit();
    try testing.expect(budget.current < 4096);
    try testing.expectEqual(@as(usize, 0), owner.slices.len);
    try testing.expectEqual(@as(usize, 0), owner.cells.len);
    try owner.upsertImage(100, 100, u("C:\\driver.sys"), 0);
    const metadata_bytes = budget.current;
    const metadata_revision = owner.modules.revision;
    try owner.observe(.dpc, 1, 101, 1);
    try testing.expect(budget.current > metadata_bytes);
    var held = try owner.publish(testing.allocator, 10000, 1);
    defer held.deinit();
    owner.reset(20000, 0);
    try testing.expectEqual(@as(usize, 0), owner.slice_len);
    try testing.expectEqual(@as(usize, 0), owner.cell_len);
    try testing.expectEqual(@as(usize, 0), owner.slices.len);
    try testing.expectEqual(@as(usize, 0), owner.cells.len);
    try testing.expect(budget.current < metadata_bytes);
    try testing.expectEqual(@as(usize, 0), owner.modules.len);
    try testing.expect(owner.modules.revision > metadata_revision);
    try testing.expect(owner.modules.resolve(101) == null);
    try testing.expectEqualSlices(u16, u("driver.sys"), held.drivers[0].module.name.?);
}

test "module revision tracks successful metadata mutations and survives a no-op" {
    var map = try interrupts.ModuleMap.init(testing.allocator, config);
    defer map.deinit();
    try testing.expectEqual(@as(u64, 0), map.revision);
    try map.upsert(0, 10, u("invalid.sys"));
    map.remove(100);
    map.clear();
    try testing.expectEqual(@as(u64, 0), map.revision);
    try map.upsert(100, 10, u("driver.sys"));
    try testing.expectEqual(@as(u64, 1), map.revision);
    try map.upsert(100, 10, u("driver.sys"));
    try testing.expectEqual(@as(u64, 1), map.revision);
    try map.upsert(100, 20, u("driver.sys"));
    try testing.expectEqual(@as(u64, 2), map.revision);
    try map.upsert(100, 20, u("replacement.sys"));
    try testing.expectEqual(@as(u64, 3), map.revision);
    map.remove(100);
    try testing.expectEqual(@as(u64, 4), map.revision);
    try map.upsert(100, 10, null);
    map.clear();
    try testing.expectEqual(@as(u64, 6), map.revision);
}

test "address index chooses highest containing base through overlaps and holes" {
    var map = try interrupts.ModuleMap.init(testing.allocator, config);
    defer map.deinit();
    try map.upsert(100, 1000, u("wide.sys"));
    try map.upsert(200, 10, u("nested.sys"));
    try map.upsert(300, 10, u("later.sys"));
    try testing.expectEqualSlices(u16, u("wide.sys"), map.resolve(500).?.name.?);
    try testing.expectEqualSlices(u16, u("nested.sys"), map.resolve(209).?.name.?);
    try testing.expectEqualSlices(u16, u("wide.sys"), map.resolve(210).?.name.?);
    try testing.expectEqualSlices(u16, u("later.sys"), map.resolve(300).?.name.?);
    try testing.expect(map.resolve(0) == null);
    try testing.expect(map.resolve(99) == null);
    try testing.expect(map.resolve(1100) == null);
    map.remove(200);
    try testing.expectEqualSlices(u16, u("wide.sys"), map.resolve(205).?.name.?);
    try map.upsert(100, 5, u("replacement.sys"));
    try testing.expect(map.resolve(205) == null);
    map.clear();
    try testing.expect(map.resolve(300) == null);
}

test "address index saturates overflowing exclusive ends and ignores invalid loads" {
    var map = try interrupts.ModuleMap.init(testing.allocator, config);
    defer map.deinit();
    try map.upsert(0, 10, u("ignored.sys"));
    try map.upsert(1, 0, u("ignored.sys"));
    try testing.expectEqual(@as(usize, 0), map.len);
    const max = std.math.maxInt(u64);
    try map.upsert(max - 3, 10, u("  C:\\drivers\\末尾.sys  "));
    try testing.expectEqualSlices(u16, u("末尾.sys"), map.resolve(max).?.name.?);
    try testing.expectEqualSlices(u16, u("C:\\drivers\\末尾.sys"), map.resolve(max).?.path.?);
    try testing.expect(map.resolve(max - 4) == null);
    try map.upsert(max - 1, 1, null);
    try testing.expect(map.resolve(max - 1).?.path == null);
    try testing.expectEqualSlices(u16, u("末尾.sys"), map.resolve(max).?.name.?);
}

test "address index matches subtraction oracle across adversarial ranges" {
    var map = try interrupts.ModuleMap.init(testing.allocator, config);
    defer map.deinit();
    var bases: [32]u64 = undefined;
    var sizes: [32]u64 = undefined;
    for (0..32) |i| {
        bases[i] = 1 + i * 17;
        sizes[i] = if (i % 7 == 0) 1000 else 1 + i % 11;
        try map.upsert(bases[i], sizes[i], null);
        map.ranges[i].tag = @intCast(i + 1);
    }
    for (1..1600) |address| {
        var expected: ?u32 = null;
        for (bases, sizes, 0..) |base, size, i| if (address >= base and address - base < size) {
            expected = @intCast(i + 1);
        };
        const actual = map.resolve(@intCast(address));
        try testing.expectEqual(expected, if (actual) |found| found.tag else null);
    }
}

test "address index replacements and shifted suffixes match a linear oracle" {
    var map = try interrupts.ModuleMap.init(testing.allocator, config);
    defer map.deinit();
    var active: [32]bool = @splat(false);
    var sizes: [32]u64 = @splat(0);
    var random: u64 = 73;
    for (0..1000) |step| {
        random = random *% 6364136223846793005 +% 1;
        const slot: usize = @intCast((random >> 16) % 32);
        const base: u64 = 1 + slot * 17;
        if (step % 4 == 0) {
            map.remove(base);
            active[slot] = false;
        } else {
            sizes[slot] = 1 + (random >> 24) % 1000;
            active[slot] = true;
            try map.upsert(base, sizes[slot], null);
            for (map.ranges[0..map.len]) |*range| if (range.base == base) {
                range.tag = @intCast(slot + 1);
                break;
            };
        }
        for (0..16) |_| {
            random = random *% 6364136223846793005 +% 1;
            const address = 1 + (random >> 16) % 1600;
            var expected: ?u32 = null;
            for (active, sizes, 0..) |present, size, index| {
                const candidate: u64 = 1 + index * 17;
                if (present and address >= candidate and address - candidate < size) expected = @intCast(index + 1);
            }
            const actual = map.resolve(address);
            try testing.expectEqual(expected, if (actual) |found| found.tag else null);
        }
    }
}

test "unchanged image names require no allocation on range replacement" {
    var map = try interrupts.ModuleMap.init(testing.allocator, config);
    defer map.deinit();
    try map.upsert(100, 200, u("C:\\driver.sys"));
    var failing = testing.FailingAllocator.init(testing.allocator, .{ .fail_index = 0 });
    map.allocator = failing.allocator();
    defer map.allocator = testing.allocator;
    try map.upsert(100, 400, u("C:\\driver.sys"));
    try testing.expectEqualSlices(u16, u("C:\\driver.sys"), map.resolve(499).?.path.?);
    try testing.expect(map.resolve(500) == null);
}

test "window publishes complete coverage buckets without resetting totals" {
    var owner = try interrupts.Owner.init(testing.allocator, config, 42, 1000, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 100, u("C:\\drivers\\driver.sys"), 1000);
    try owner.observe(.isr, 0.5, 101, 1000);
    try owner.observe(.dpc, 2, 101, 10999);
    try owner.observe(.threaded_dpc, 2, 101, 11000);
    var warming = try owner.publish(testing.allocator, 10999, 2);
    defer warming.deinit();
    try testing.expect(!warming.available);
    try testing.expectEqual(@as(u64, 2), warming.event_count);
    var snapshot = try owner.publish(testing.allocator, 11000, 2);
    defer snapshot.deinit();
    try testing.expect(snapshot.available);
    try testing.expectEqual(@as(u64, 42), snapshot.generation);
    try testing.expectEqual(@as(u32, 1), snapshot.bucket_count);
    try testing.expectEqual(@as(i64, 1000), snapshot.buckets[0].started_tick_ms);
    try testing.expectEqual(@as(i64, 11000), snapshot.buckets[0].ended_tick_ms);
    try testing.expectEqual(@as(f64, 2.5), snapshot.buckets[0].total_duration_ms);
    try testing.expectEqual(@as(f64, 4.5), snapshot.total_duration_ms);
    try testing.expectEqual(@as(u64, 3), snapshot.event_count);
    try testing.expectEqual(@as(u64, 2), snapshot.long_event_count);
    try testing.expectEqual(interrupts.Kind.dpc, snapshot.maximum_kind.?);
    try testing.expectEqual(@as(f64, 0.0225), snapshot.cpu_capacity_percent);
    try testing.expectEqualSlices(u16, u("driver.sys"), snapshot.drivers[0].module.name.?);
    var again = try owner.publish(testing.allocator, 11000, 2);
    defer again.deinit();
    try testing.expectEqual(snapshot.event_count, again.event_count);
}

test "sliding window excludes a partly covered second and anchors buckets to coverage" {
    var owner = try interrupts.Owner.init(testing.allocator, config, 1, 10000, 0);
    defer owner.deinit();
    try owner.observe(.isr, 8, 0, 10001);
    try owner.observe(.dpc, 4, 0, 11001);
    try owner.observe(.timer_dpc, 2, 0, 70000);
    var snapshot = try owner.publish(testing.allocator, 70500, 1);
    defer snapshot.deinit();
    try testing.expectEqual(@as(i64, 60000), snapshot.window_ms);
    try testing.expectEqual(@as(f64, 6), snapshot.total_duration_ms);
    try testing.expectEqual(@as(u32, 6), snapshot.bucket_count);
    try testing.expectEqual(@as(i64, 10500), snapshot.buckets[0].started_tick_ms);
    try testing.expectEqual(@as(i64, 70500), snapshot.buckets[5].ended_tick_ms);
    try testing.expectEqual(@as(u64, 1), snapshot.buckets[0].event_count);
    try testing.expectEqual(@as(u64, 1), snapshot.buckets[5].event_count);
}

test "loss resets both module ownership and statistics and rejects old callbacks" {
    var owner = try interrupts.Owner.init(testing.allocator, config, 7, 100, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 100, u("old.sys"), 100);
    try owner.observe(.isr, 3, 101, 200);
    var before = try owner.publish(testing.allocator, 10100, 1);
    defer before.deinit();
    try testing.expect(owner.refreshLoss(20000, 2));
    try testing.expect(!owner.refreshLoss(30000, 2));
    try owner.upsertImage(100, 100, u("stale.sys"), 19999);
    try owner.observe(.isr, 100, 101, 19999);
    try owner.observe(.timer_dpc, 1, 101, 20000);
    var after = try owner.publish(testing.allocator, 30000, 0);
    defer after.deinit();
    try testing.expectEqual(@as(u64, 2), after.events_lost);
    try testing.expectEqual(@as(u64, 1), after.event_count);
    try testing.expectEqual(@as(u32, 1), after.processor_count);
    try testing.expect(after.maximum_module.path == null);
    // Published strings own their storage across reset.
    try testing.expectEqualSlices(u16, u("old.sys"), before.maximum_module.name.?);
}

test "driver aggregation uses Unicode ordinal ignore case and first selected spelling" {
    var owner = try interrupts.Owner.init(testing.allocator, config, 1, 0, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 10, u("C:\\Ä.sys"), 0);
    try owner.observe(.isr, 1, 100, 0);
    try owner.upsertImage(100, 10, u("c:\\ä.SYS"), 1000);
    try owner.observe(.dpc, 2, 100, 1000);
    var snapshot = try owner.publish(testing.allocator, 10000, 1);
    defer snapshot.deinit();
    try testing.expectEqual(@as(usize, 1), snapshot.drivers.len);
    try testing.expectEqual(@as(f64, 3), snapshot.drivers[0].total_duration_ms);
    try testing.expectEqualSlices(u16, u("C:\\Ä.sys"), snapshot.drivers[0].module.path.?);
    try testing.expectEqualSlices(u16, u("ä.SYS"), snapshot.maximum_module.name.?);
    // At 60.5 seconds the first spelling has left the selected window.
    var later = try owner.publish(testing.allocator, 60500, 1);
    defer later.deinit();
    try testing.expectEqualSlices(u16, u("c:\\ä.SYS"), later.drivers[0].module.path.?);
}

test "invalid durations and before-start images are ignored" {
    var owner = try interrupts.Owner.init(testing.allocator, config, 1, 100, 0);
    defer owner.deinit();
    try owner.observe(.isr, std.math.nan(f64), 0, 100);
    try owner.observe(.isr, std.math.inf(f64), 0, 100);
    try owner.observe(.isr, 0, 0, 100);
    try owner.observe(.isr, -1, 0, 100);
    try owner.observe(.isr, 1, 0, 99);
    var snapshot = try owner.publish(testing.allocator, 10100, 1);
    defer snapshot.deinit();
    try testing.expectEqual(@as(u64, 0), snapshot.event_count);
    try testing.expect(snapshot.available);
    try testing.expect(snapshot.maximum_kind == null);
}

test "top sixteen drivers sort by duration then ordinal name" {
    var owner = try interrupts.Owner.init(testing.allocator, config, 1, 0, 0);
    defer owner.deinit();
    var i: usize = 20;
    while (i > 0) {
        i -= 1;
        const path = [_]u16{ 'a', @as(u16, @intCast(i / 10)) + '0', @as(u16, @intCast(i % 10)) + '0', '.', 's', 'y', 's' };
        const base: u64 = @intCast(100 + i * 10);
        try owner.upsertImage(base, 5, &path, 0);
        try owner.observe(.isr, if (i == 19) 10 else 1, base, 0);
    }
    var snapshot = try owner.publish(testing.allocator, 10000, 1);
    defer snapshot.deinit();
    try testing.expectEqual(@as(usize, 16), snapshot.drivers.len);
    try testing.expectEqualSlices(u16, u("a19.sys"), snapshot.drivers[0].module.name.?);
    try testing.expectEqualSlices(u16, u("a00.sys"), snapshot.drivers[1].module.name.?);
    try testing.expectEqualSlices(u16, u("a14.sys"), snapshot.drivers[15].module.name.?);
    try testing.expectEqual(@as(u64, 20), snapshot.event_count);
}

test "maximum identity survives unload and differently cased driver reuse" {
    var owner = try interrupts.Owner.init(testing.allocator, config, 1, 0, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 10, u("C:\\a.sys"), 0);
    try owner.observe(.isr, 1, 100, 0);
    try owner.upsertImage(100, 10, u("c:\\A.SYS"), 1);
    try owner.observe(.dpc, 2, 100, 1);
    owner.removeImage(100, 2);
    var snapshot = try owner.publish(testing.allocator, 10000, 1);
    defer snapshot.deinit();
    try testing.expectEqualSlices(u16, u("A.SYS"), snapshot.maximum_module.name.?);
    try testing.expectEqualSlices(u16, u("a.sys"), snapshot.drivers[0].module.name.?);
}

test "owner cap failures invalidate until reset while map failure is atomic" {
    const small: interrupts.Config = .{ .max_modules = 1, .max_identities = 2, .max_slices = 1, .max_slice_drivers = 1, .max_path_units = 8, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16 };
    var map = try interrupts.ModuleMap.init(testing.allocator, small);
    defer map.deinit();
    try map.upsert(100, 10, u("a.sys"));
    try testing.expectError(error.CapacityExceeded, map.upsert(200, 10, null));
    try testing.expectEqualSlices(u16, u("a.sys"), map.resolve(100).?.name.?);
    var owner = try interrupts.Owner.init(testing.allocator, small, 1, 0, 0);
    defer owner.deinit();
    try owner.observe(.isr, 1, 0, 0);
    try testing.expectError(error.CapacityExceeded, owner.observe(.isr, 1, 0, 1000));
    try testing.expectError(error.Invalidated, owner.publish(testing.allocator, 10000, 1));
    owner.reset(20000, 0);
    try owner.observe(.dpc, 1, 0, 20000);
    var snapshot = try owner.publish(testing.allocator, 30000, 1);
    defer snapshot.deinit();
    try testing.expectEqual(@as(u64, 1), snapshot.event_count);
}

test "pruning reclaims retired identity capacity" {
    var small = config;
    small.max_identities = 2;
    var owner = try interrupts.Owner.init(testing.allocator, small, 1, 0, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 10, u("a.sys"), 0);
    try owner.observe(.isr, 1, 100, 0);
    owner.removeImage(100, 1);
    var snapshot = try owner.publish(testing.allocator, 62000, 1);
    defer snapshot.deinit();
    try owner.upsertImage(200, 10, u("b.sys"), 62000);
    try owner.observe(.dpc, 1, 200, 62000);
    var next = try owner.publish(testing.allocator, 72000, 1);
    defer next.deinit();
    try testing.expectEqualSlices(u16, u("b.sys"), next.drivers[0].module.name.?);
}

fn allocationScenario(allocator: std.mem.Allocator) !void {
    var owner = try interrupts.Owner.init(allocator, config, 1, 0, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 10, u("driver.sys"), 0);
    try owner.observe(.isr, 1, 100, 0);
    try owner.upsertImage(100, 20, u("replacement.sys"), 1);
    try owner.observe(.dpc, 2, 100, 1);
    owner.removeImage(100, 2);
    var snapshot = try owner.publish(allocator, 10000, 1);
    defer snapshot.deinit();
}

test "every allocation failure unwinds with no leaks" {
    try testing.checkAllAllocationFailures(testing.allocator, allocationScenario, .{});
}


test "bucket width, bucket count and reported drivers come from the configuration" {
    var custom = config;
    custom.bucket_ms = 5000;
    custom.bucket_count = 3;
    custom.reported_drivers = 1;
    try testing.expectError(error.InvalidConfig, interrupts.Owner.init(testing.allocator, blk: {
        var invalid = custom;
        invalid.bucket_ms = 1500;
        break :blk invalid;
    }, 1, 0, 0));
    var owner = try interrupts.Owner.init(testing.allocator, custom, 1, 10000, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 100, u("a.sys"), 10000);
    try owner.upsertImage(1000, 100, u("b.sys"), 10000);
    try owner.observe(.isr, 8, 101, 11001);
    try owner.observe(.dpc, 4, 1001, 24000);
    var early = try owner.publish(testing.allocator, 14000, 1);
    defer early.deinit();
    // Not yet one complete bucket: data are not available.
    try testing.expect(!early.available);
    try testing.expectEqual(@as(u32, 0), early.bucket_count);
    var snapshot = try owner.publish(testing.allocator, 25500, 1);
    defer snapshot.deinit();
    try testing.expect(snapshot.available);
    try testing.expectEqual(@as(i64, 15000), snapshot.window_ms);
    try testing.expectEqual(@as(u32, 3), snapshot.bucket_count);
    try testing.expectEqual(@as(i64, 10500), snapshot.buckets[0].started_tick_ms);
    try testing.expectEqual(@as(i64, 25500), snapshot.buckets[2].ended_tick_ms);
    try testing.expectEqual(@as(u64, 1), snapshot.buckets[0].event_count);
    try testing.expectEqual(@as(u64, 1), snapshot.buckets[2].event_count);
    try testing.expectEqual(@as(usize, 1), snapshot.drivers.len);
    try testing.expectEqual(@as(f64, 8), snapshot.drivers[0].total_duration_ms);
}

test "interrupt window overflow is rejected before construction" {
    var overflowing = config;
    overflowing.bucket_ms = 4_000_000_000;
    overflowing.bucket_count = 4_000_000_000;
    try testing.expectError(error.InvalidConfig, interrupts.Owner.init(testing.allocator, overflowing, 1, 0, 0));
}
