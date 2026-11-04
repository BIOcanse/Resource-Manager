//! S-tier selection owner. The original declaration/table/JSON fields are borrowed
//! synchronously; no setting graph, publication state or allocation lives here.
//! The CLR output sink is temporary until S-store and the parameter consumers retire.
const view = @import("memory_cpu_tier_view.zig");

pub const Value = extern struct { integer: i64 = 0, kind: u32 = 0, choice: u32 = 0 };
pub const Range = extern struct { minimum: i64, maximum: i64, kind: u32, unset: u32, choices: usize };
pub const Input = extern struct {
    tier: i32,
    minimum_tier: i32,
    default_tier: i32,
    maximum_tier: i32,
    has_settings: u32,
    options: usize,
    overrides: usize,
};
pub const Effective = extern struct {
    gen0: i64 = 0,
    conserve: i64 = 0,
    stop_ms: i64 = 0,
    identity_ms: i64 = 0,
    browser_seconds: i64 = 0,
    platform_seconds: i64 = 0,
    storage_seconds: i64 = 0,
    renderer_entries: i64 = 0,
    sessions_max: i64 = 0,
    sessions_prebuilt: i64 = 0,
    gen0_present: u32 = 0,
    release_after_use: u32 = 0,
    shrink_after: u32 = 0,
    close_idle: u32 = 0,
};
pub const Text = enum(u32) { option_name, override_name, choice, cell_choice, override_choice, standalone_choice, cell_category, selected_override_choice };
pub const Read = enum(u32) { cell, override, standalone, selected_override };
pub const Event = enum(u32) { tier, cell, option, ignored_tier, unknown_option, invalid_override };
pub const Callbacks = extern struct {
    range: *const fn (?*anyopaque, usize, *Range, *usize) callconv(.c) i32,
    value: *const fn (?*anyopaque, usize, usize, Read, *Value) callconv(.c) i32,
    text: *const fn (?*anyopaque, usize, usize, Text, ?*anyopaque, view.Visitor) callconv(.c) i32,
    matches: *const fn (?*anyopaque, usize, usize, Read, ?[*]const u16, usize, *u32) callconv(.c) i32,
    has_override: *const fn (?*anyopaque, usize, *u32) callconv(.c) i32,
    emit: *const fn (?*anyopaque, usize, usize, Event, *const Value, u32) callconv(.c) i32,
};
pub const Error = error{ Source, InvalidTable, MissingOption, InvalidInput };
pub const Source = struct {
    context: ?*anyopaque,
    callbacks: *const Callbacks,

    fn text(self: Source, option: usize, child: usize, field: Text, sink: ?*anyopaque, visitor: view.Visitor) Error!void {
        if (self.callbacks.text(self.context, option, child, field, sink, visitor) != 0) return error.Source;
    }
    fn equals(self: Source, option: usize, child: usize, field: Text, expected: []const u8) Error!bool {
        var compare = Comparison{ .ascii = expected };
        try self.text(option, child, field, &compare, Comparison.accept);
        return compare.equal;
    }
    fn same(self: Source, option: usize, child: usize, field: Text, choice: usize) Error!bool {
        const read_field: Read = switch (field) {
            .cell_choice => .cell,
            .override_choice => .override,
            .standalone_choice => .standalone,
            .selected_override_choice => .selected_override,
            else => return error.InvalidInput,
        };
        var compare = BorrowedComparison{ .source = self, .option = option, .child = child, .field = read_field };
        try self.text(option, choice, .choice, &compare, BorrowedComparison.accept);
        if (compare.failed) return error.Source;
        return compare.equal;
    }
    fn range(self: Source, option: usize, count: *usize) Error!Range {
        var result: Range = undefined;
        if (self.callbacks.range(self.context, option, &result, count) != 0) return error.Source;
        return result;
    }
    fn read(self: Source, option: usize, child: usize, field: Read) Error!Value {
        var result: Value = undefined;
        if (self.callbacks.value(self.context, option, child, field, &result) != 0) return error.Source;
        return result;
    }
    fn emit(self: Source, option: usize, child: usize, event: Event, value: Value, flag: u32) Error!void {
        if (self.callbacks.emit(self.context, option, child, event, &value, flag) != 0) return error.Source;
    }
};
const Comparison = struct {
    ascii: []const u8,
    equal: bool = false,
    fn accept(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *Comparison = @ptrCast(@alignCast(context.?));
        if (pointer == null or length != self.ascii.len) return 0;
        for (pointer.?[0..length], self.ascii) |a, b| if (a != b) return 0;
        self.equal = true;
        return 0;
    }
};
// Borrow the declaration choice and compare it against the original JSON region.
// JSON decoding belongs to the source bridge; it does not materialize strings.
const BorrowedComparison = struct {
    source: Source,
    option: usize,
    child: usize,
    field: Read,
    equal: bool = false,
    failed: bool = false,
    fn accept(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *BorrowedComparison = @ptrCast(@alignCast(context.?));
        var equal: u32 = 0;
        if (self.source.callbacks.matches(self.source.context, self.option, self.child, self.field, pointer, length, &equal) != 0) self.failed = true;
        self.equal = equal != 0;
        return 0;
    }
};

pub fn parse(source: Source, option: usize, child: usize, field: Read) Error!?Value {
    var unused: usize = 0;
    const range = try source.range(option, &unused);
    var value = try source.read(option, child, field);
    if (value.kind == 0) return if (range.kind == 0 and range.unset != 0) value else null;
    if (range.kind == 0) {
        return if (value.kind == 1 and value.integer >= range.minimum and value.integer <= range.maximum) value else null;
    }
    if (value.kind != 2) return null;
    const text_field: Text = switch (field) {
        .cell => .cell_choice,
        .override => .override_choice,
        .standalone => .standalone_choice,
        .selected_override => .selected_override_choice,
    };
    for (0..range.choices) |choice| {
        if (try source.same(option, child, text_field, choice)) {
            value.choice = @intCast(choice);
            return value;
        }
    }
    return null;
}

// TryParse historically exposes the syntactically parsed candidate even when
// the range rejects it. Keep that public out-value contract at the native boundary.
pub const ParseResult = struct { value: Value, accepted: bool };
pub fn parseStandalone(source: Source) Error!ParseResult {
    if (try parse(source, 0, 0, .standalone)) |value| return .{ .value = value, .accepted = true };
    var unused: usize = 0;
    const range = try source.range(0, &unused);
    const candidate = try source.read(0, 0, .standalone);
    return .{ .value = if ((range.kind == 0 and candidate.kind == 1) or (range.kind != 0 and candidate.kind == 2)) candidate else .{}, .accepted = false };
}

pub fn initialTier(input: Input) Error!i32 {
    if (input.minimum_tier < 1 or input.maximum_tier < input.minimum_tier or
        input.default_tier < input.minimum_tier or input.default_tier > input.maximum_tier) return error.InvalidInput;
    return input.default_tier;
}

fn findOverrideOption(source: Source, input: Input, override: usize) Error!?usize {
    for (0..input.options) |option| {
        if (try overrideNames(source, option, override)) return option;
    }
    return null;
}
fn overrideNames(source: Source, option: usize, override: usize) Error!bool {
    var compare = NameComparison{ .source = source, .option = option };
    try source.text(override, 0, .override_name, &compare, NameComparison.accept);
    if (compare.failed) return error.Source;
    return compare.equal;
}
const NameComparison = struct {
    source: Source,
    option: usize,
    pointer: ?[*]const u16 = null,
    length: usize = 0,
    equal: bool = false,
    failed: bool = false,
    fn accept(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *NameComparison = @ptrCast(@alignCast(context.?));
        self.pointer = pointer;
        self.length = length;
        self.source.text(self.option, 0, .option_name, self, compare) catch {
            self.failed = true;
        };
        self.pointer = null;
        return 0;
    }
    fn compare(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *NameComparison = @ptrCast(@alignCast(context.?));
        if (pointer == null or self.pointer == null or length != self.length) return 0;
        for (pointer.?[0..length], self.pointer.?[0..length]) |a, b| if (a != b) return 0;
        self.equal = true;
        return 0;
    }
};

pub fn validate(source: Source, input: Input) Error!void {
    _ = try initialTier(input);
    if (input.tier < input.minimum_tier or input.tier > input.maximum_tier) {
        try source.emit(0, 0, .ignored_tier, .{}, 0);
        return;
    }
    for (0..input.overrides) |override| {
        const option = (try findOverrideOption(source, input, override)) orelse {
            try source.emit(override, 0, .unknown_option, .{}, 0);
            return;
        };
        if (try parse(source, option, override, .override) == null) {
            try source.emit(override, 0, .invalid_override, .{}, 0);
            return;
        }
    }
}

pub fn validateTable(source: Source, input: Input) Error!void {
    _ = try initialTier(input);
    for (0..input.options) |option| {
        var count: usize = 0;
        _ = try source.range(option, &count);
        if (count != @as(usize, @intCast(input.maximum_tier))) return error.InvalidTable;
        for (0..count) |cell| {
            if (!(try source.equals(option, cell, .cell_category, "proportional")) and
                !(try source.equals(option, cell, .cell_category, "aggressive"))) return error.InvalidTable;
            if (try parse(source, option, cell, .cell) == null) return error.InvalidTable;
        }
    }
}

pub fn resolve(source: Source, input: Input) Error!Effective {
    const first = try initialTier(input);
    var tier = if (input.has_settings != 0) input.tier else first;
    if (tier < input.minimum_tier or tier > input.maximum_tier) {
        try source.emit(0, 0, .ignored_tier, .{}, 0);
        tier = first;
    }
    try source.emit(0, 0, .tier, .{ .integer = tier, .kind = 1 }, 0);
    for (0..input.overrides) |override| {
        const option = (try findOverrideOption(source, input, override)) orelse {
            try source.emit(override, 0, .unknown_option, .{}, 0);
            continue;
        };
        if (try parse(source, option, override, .override) == null)
            try source.emit(override, 0, .invalid_override, .{}, 0);
    }
    var effective = Effective{};
    for (0..input.options) |option| {
        var count: usize = 0;
        _ = try source.range(option, &count);
        var selected: ?Value = null;
        for (0..count) |cell| {
            const value = (try parse(source, option, cell, .cell)) orelse return error.InvalidTable;
            try source.emit(option, cell, .cell, value, 0);
            if (cell + 1 == @as(usize, @intCast(tier))) selected = value;
        }
        var value = selected orelse return error.InvalidTable;
        var user: u32 = 0;
        // Preserve the source dictionary's lookup semantics separately from
        // ordinal unknown-name validation (including custom dictionary comparers).
        var present: u32 = 0;
        if (source.callbacks.has_override(source.context, option, &present) != 0) return error.Source;
        if (present != 0) {
            if (try parse(source, option, 0, .selected_override)) |parsed| {
                value = parsed;
                user = 1;
            }
        }
        try source.emit(option, @intCast(tier - 1), .option, value, user);
        try expand(source, option, value, &effective);
    }
    return effective;
}

fn expand(source: Source, option: usize, value: Value, result: *Effective) Error!void {
    if (try source.equals(option, 0, .option_name, "gcGen0MaxBudget")) {
        result.gen0 = value.integer;
        result.gen0_present = @intFromBool(value.kind == 1);
    }
    if (try source.equals(option, 0, .option_name, "gcConserveMemory")) result.conserve = value.integer;
    if (try source.equals(option, 0, .option_name, "singleRequestStopBufferMs")) result.stop_ms = value.integer;
    if (try source.equals(option, 0, .option_name, "serviceIdentityCacheMs")) result.identity_ms = value.integer;
    if (try source.equals(option, 0, .option_name, "browserRuntimeSnapshotSeconds")) result.browser_seconds = value.integer;
    if (try source.equals(option, 0, .option_name, "platformSensorSnapshotSeconds")) result.platform_seconds = value.integer;
    if (try source.equals(option, 0, .option_name, "storageSnapshotSeconds")) result.storage_seconds = value.integer;
    if (try source.equals(option, 0, .option_name, "rendererAbsenceEntries")) result.renderer_entries = value.integer;
    if (try source.equals(option, 0, .option_name, "fileQuerySessionsMax")) result.sessions_max = value.integer;
    if (try source.equals(option, 0, .option_name, "fileQuerySessionsPrebuilt")) result.sessions_prebuilt = value.integer;
    if (try source.equals(option, 0, .option_name, "fileQueryIdleSessions")) result.close_idle = @intFromBool(try source.equals(option, value.choice, .choice, "close"));
    if (try source.equals(option, 0, .option_name, "bufferReturn")) {
        if (try source.equals(option, value.choice, .choice, "releaseAfterUse")) {
            result.release_after_use = 1;
        } else if (try source.equals(option, value.choice, .choice, "shrinkAfter2LowReads")) {
            result.shrink_after = 2;
        } else if (try source.equals(option, value.choice, .choice, "shrinkAfter8LowReads")) {
            result.shrink_after = 8;
        } else if (try source.equals(option, value.choice, .choice, "shrinkAfter32LowReads")) {
            result.shrink_after = 32;
        } else if (!(try source.equals(option, value.choice, .choice, "keepPeak"))) return error.InvalidInput;
    }
}
