//! Overlay inputs and their projections: the per-software overlay settings in
//! the backend settings section `performanceOverlay` (received through the
//! resident's settings subscription), the injected module selection, the
//! subscription request and the lines one target shows.
const std = @import("std");
const points = @import("desktop_points_values").Bound;
const layout = @import("layout.zig");
const text = @import("text.zig");

/// Software overlays held at once: the fixed capacity of this block's
/// presentation table, not a product limit. The backend declares its own
/// limit (freedom point monitoring/performance_overlay maximum_software); a
/// section larger than this table is rejected and logged.
pub const maximum_overlays = points.overlay_maximum_software;
/// Bytes of one `performanceOverlay` section copy the host keeps and the block
/// parses: the fixed capacity of both buffers. A larger section is logged and
/// the overlay shows nothing until it fits.
/// Items per overlay this block can draw: one line each (layout.maximum_lines).
pub const maximum_items = layout.maximum_lines;
pub const Mode = enum { external, injected };
/// The tag names are the wire values.
pub const Kind = enum { frames, metric, software };
pub const SyncPrecision = enum { low, mid, high };

/// One overlay item, the targeted recording item contract
/// (docs/targeted_recording_views_2026-10-30.md §3.9).
pub const Item = struct {
    kind: Kind,
    id: []const u8,
    /// "auto" or one catalog source ("field@device identity" and so on).
    source: []const u8,
    /// Null: the catalog's natural interval, else the subscription's
    /// intervalMs. Frames items carry none.
    interval_ms: ?u32,
    /// Stored and sent only; the backend echoes it.
    sync_precision: SyncPrecision,
};
pub const Presentation = struct {
    software_id: []const u8,
    enabled: bool,
    mode: Mode,
    /// Drawn in this order.
    items: []const Item,
    settings: layout.Settings,
};

/// The overlays of one settings section. Strings are copied into the owned
/// storage, which is reused on the next load.
pub const Presentations = struct {
    items: [maximum_overlays]Presentation = undefined,
    count: usize = 0,
    storage: std.heap.FixedBufferAllocator,

    pub fn init(buffer: []u8) Presentations {
        return .{ .storage = std.heap.FixedBufferAllocator.init(buffer) };
    }
    pub fn slice(self: *const Presentations) []const Presentation {
        return self.items[0..self.count];
    }
    pub fn find(self: *const Presentations, software_id: []const u8) ?*const Presentation {
        for (self.items[0..self.count]) |*item| if (std.ascii.eqlIgnoreCase(item.software_id, software_id)) return item;
        return null;
    }
    /// `section` is the settings' `performanceOverlay` value. The backend
    /// normalizer has expanded every entry, so each field is required here;
    /// an entry missing one, or of the wrong type, rejects the whole set (the
    /// caller logs it and shows no overlay). Ranges are the backend's to check.
    pub fn load(self: *Presentations, section: ?std.json.Value) !void {
        self.count = 0;
        self.storage.reset();
        errdefer self.count = 0;
        const value = section orelse return;
        if (value != .object) return error.InvalidOverlaySettings;
        const software = value.object.get("software") orelse return error.InvalidOverlaySettings;
        if (software != .object) return error.InvalidOverlaySettings;
        var iterator = software.object.iterator();
        while (iterator.next()) |entry| {
            if (self.count == maximum_overlays) return error.TooManyOverlays;
            if (blank(entry.key_ptr.*)) return error.InvalidOverlaySettings;
            for (self.items[0..self.count]) |item| if (std.ascii.eqlIgnoreCase(item.software_id, entry.key_ptr.*)) return error.InvalidOverlaySettings;
            self.items[self.count] = try self.parseOne(entry.key_ptr.*, entry.value_ptr.*);
            self.count += 1;
        }
    }

    fn parseOne(self: *Presentations, key: []const u8, value: std.json.Value) !Presentation {
        const allocator = self.storage.allocator();
        if (value != .object) return error.InvalidOverlaySettings;
        const object = value.object;
        const items_value = object.get("items") orelse return error.InvalidOverlaySettings;
        if (items_value != .array) return error.InvalidOverlaySettings;
        const enabled = object.get("enabled") orelse return error.InvalidOverlaySettings;
        if (enabled != .bool) return error.InvalidOverlaySettings;
        const font_size = try asInteger(try field(object, "fontSizePx"));
        const margin = try asInteger(try field(object, "marginPx"));
        const width_ratio = try asDecimal(try field(object, "regionWidthRatio"));
        const height_ratio = try asDecimal(try field(object, "regionHeightRatio"));
        // Only what the drawing itself needs: a positive font, a margin that
        // is not negative, region fractions of the window.
        if (font_size <= 0 or font_size > std.math.maxInt(i32) or margin < 0 or margin > std.math.maxInt(i32) or
            !fraction(width_ratio) or !fraction(height_ratio)) return error.InvalidOverlaySettings;
        return .{
            .software_id = try allocator.dupe(u8, key),
            .enabled = enabled.bool,
            .mode = try parseMode(try string(object, "mode")),
            .items = try parseItems(allocator, items_value.array.items),
            .settings = .{
                .size_mode = try sizeMode(try string(object, "sizeMode")),
                .font_size_px = @intCast(font_size),
                .region_width_ratio = width_ratio,
                .region_height_ratio = height_ratio,
                .anchor = try anchor(try string(object, "anchor")),
                .margin_px = @intCast(margin),
            },
        };
    }
};

/// The stored items as the backend validated them: kind frames, metric or
/// software; id and source not blank; sync precision low, mid or high; an
/// interval null or a positive integer; no (kind, id) twice, the id ignoring
/// case; at most as many as this block draws.
fn parseItems(allocator: std.mem.Allocator, values: []const std.json.Value) ![]const Item {
    if (values.len > maximum_items) return error.InvalidOverlaySettings;
    const items = try allocator.alloc(Item, values.len);
    for (values, 0..) |value, index| {
        if (value != .object) return error.InvalidOverlaySettings;
        const object = value.object;
        const kind = std.meta.stringToEnum(Kind, try string(object, "kind")) orelse return error.InvalidOverlaySettings;
        const id = try string(object, "id");
        const source = try string(object, "source");
        if (blank(id) or blank(source)) return error.InvalidOverlaySettings;
        const precision = std.meta.stringToEnum(SyncPrecision, try string(object, "syncPrecision")) orelse return error.InvalidOverlaySettings;
        const interval: ?i64 = if (object.get("intervalMs")) |item| switch (item) {
            .null => null,
            .integer => |number| number,
            else => return error.InvalidOverlaySettings,
        } else null;
        if (interval) |number| if (number <= 0 or number > std.math.maxInt(u32)) return error.InvalidOverlaySettings;
        for (items[0..index]) |previous| if (previous.kind == kind and std.ascii.eqlIgnoreCase(previous.id, id)) return error.InvalidOverlaySettings;
        items[index] = .{
            .kind = kind,
            .id = try allocator.dupe(u8, id),
            .source = try allocator.dupe(u8, source),
            .interval_ms = if (interval) |number| @intCast(number) else null,
            .sync_precision = precision,
        };
    }
    return items;
}

fn parseMode(value: []const u8) !Mode {
    return std.meta.stringToEnum(Mode, value) orelse error.InvalidOverlaySettings;
}
fn sizeMode(value: []const u8) !layout.SizeMode {
    if (std.mem.eql(u8, value, "absolutePixels")) return .absolute_pixels;
    if (std.mem.eql(u8, value, "windowRatio")) return .window_ratio;
    return error.InvalidOverlaySettings;
}
fn anchor(value: []const u8) !layout.Anchor {
    if (std.mem.eql(u8, value, "topLeft")) return .top_left;
    if (std.mem.eql(u8, value, "topRight")) return .top_right;
    if (std.mem.eql(u8, value, "bottomLeft")) return .bottom_left;
    if (std.mem.eql(u8, value, "bottomRight")) return .bottom_right;
    return error.InvalidOverlaySettings;
}

fn field(object: std.json.ObjectMap, name: []const u8) !std.json.Value {
    return object.get(name) orelse error.InvalidOverlaySettings;
}
fn string(object: std.json.ObjectMap, name: []const u8) ![]const u8 {
    const value = try field(object, name);
    if (value != .string) return error.InvalidOverlaySettings;
    return value.string;
}
fn asInteger(value: std.json.Value) !i64 {
    if (value != .integer) return error.InvalidOverlaySettings;
    return value.integer;
}
fn asDecimal(value: std.json.Value) !f64 {
    return switch (value) {
        .integer => |number| @floatFromInt(number),
        .float => |number| number,
        else => error.InvalidOverlaySettings,
    };
}
fn fraction(value: f64) bool {
    return std.math.isFinite(value) and value > 0 and value <= 1;
}
fn blank(value: []const u8) bool {
    return std.mem.trim(u8, value, " \t\r\n\x0b\x0c").len == 0;
}

pub const overlay_modules = [_][]const u8{ "performance-overlay-preload", "performance-overlay-vulkan" };
pub const maximum_module_software = points.overlay_maximum_module_software;

/// The software ids whose startup module selection carries both overlay
/// modules, from the startup-modules settings subscription.
pub const Modules = struct {
    known: bool = false,
    ids: [maximum_module_software][]const u8 = undefined,
    count: usize = 0,
    storage: std.heap.FixedBufferAllocator,

    pub fn init(buffer: []u8) Modules {
        return .{ .storage = std.heap.FixedBufferAllocator.init(buffer) };
    }
    pub fn clear(self: *Modules) void {
        self.known = false;
        self.count = 0;
        self.storage.reset();
    }
    pub fn complete(self: *const Modules, software_id: []const u8) bool {
        for (self.ids[0..self.count]) |id| if (std.ascii.eqlIgnoreCase(id, software_id)) return true;
        return false;
    }
    /// Returns whether the complete set changed.
    pub fn load(self: *Modules, document: Document) !bool {
        var previous: [maximum_module_software]u64 = undefined;
        const previous_count = self.count;
        const was_known = self.known;
        for (self.ids[0..self.count], 0..) |id, index| previous[index] = hashFolded(id);
        self.clear();
        // FirstOrDefault per software id: a later duplicate entry is ignored.
        for (document.software, 0..) |item, index| {
            var duplicate = false;
            for (document.software[0..index]) |earlier| duplicate = duplicate or std.ascii.eqlIgnoreCase(earlier.softwareId, item.softwareId);
            if (duplicate) continue;
            var all = true;
            for (overlay_modules) |module| {
                var found = false;
                for (item.moduleIds) |id| found = found or std.mem.eql(u8, id, module);
                all = all and found;
            }
            if (!all) continue;
            if (self.count == maximum_module_software) return error.TooManyModuleSelections;
            self.ids[self.count] = try self.storage.allocator().dupe(u8, item.softwareId);
            self.count += 1;
        }
        self.known = true;
        if (!was_known or previous_count != self.count) return true;
        for (self.ids[0..self.count], 0..) |id, index| if (previous[index] != hashFolded(id)) return true;
        return false;
    }
    pub const Document = struct {
        version: i64,
        software: []const struct { softwareId: []const u8, moduleIds: []const []const u8 },
    };
};

fn hashFolded(value: []const u8) u64 {
    var hasher = std.hash.Wyhash.init(0);
    for (value) |byte| hasher.update(&.{std.ascii.toLower(byte)});
    return hasher.final();
}

/// An overlay is wanted while its backend switch is on; an injected one also
/// needs its software's startup selection to carry both overlay modules.
pub fn wanted(presentation: *const Presentation, modules: *const Modules) bool {
    return presentation.enabled and switch (presentation.mode) {
        .external => true,
        .injected => modules.complete(presentation.software_id),
    };
}
/// The module selection is read while any switched-on overlay is injected.
pub fn anyInjected(presentations: *const Presentations) bool {
    for (presentations.slice()) |item| if (item.enabled and item.mode == .injected) return true;
    return false;
}

pub const modules_id = "modules";
pub const modules_path = "/api/startup-modules/settings/subscribe";

/// The /api/subscriptions/stream body. Overlay subscriptions follow
/// OverlaySubscriptions.Create: one per wanted overlay that has items,
/// ordered by software id ignoring case, ids "overlay-<n>", the path naming
/// the software and the sample interval, and the stored items sent as they
/// are in stored order. `order` receives the presentation index of each
/// overlay-<n>. The startup module subscription is included while any overlay
/// is injected.
pub const Request = struct {
    body: []const u8,
    overlay_count: usize,
    /// Hash of the overlay paths and items; a change re-subscribes.
    signature: u64,
    with_modules: bool,
};
pub fn request(presentations: *const Presentations, modules: *const Modules, interval_ms: u32, order: *[maximum_overlays]usize, output: []u8) !Request {
    var candidates: [maximum_overlays]usize = undefined;
    var candidate_count: usize = 0;
    for (presentations.slice(), 0..) |*item, index| {
        if (!wanted(item, modules) or item.items.len == 0) continue;
        candidates[candidate_count] = index;
        candidate_count += 1;
    }
    const presentation_items = presentations.slice();
    std.mem.sort(usize, candidates[0..candidate_count], presentation_items, struct {
        fn less(context: []const Presentation, a: usize, b: usize) bool {
            return std.ascii.lessThanIgnoreCase(context[a].software_id, context[b].software_id);
        }
    }.less);
    var writer: Writer = .{ .output = output };
    const with_modules = anyInjected(presentations);
    try writer.write("{\"version\":1,\"subscriptions\":[");
    if (with_modules) try writer.print("{{\"id\":\"{s}\",\"path\":\"{s}\"}}", .{ modules_id, modules_path });
    var hasher = std.hash.Wyhash.init(0);
    for (candidates[0..candidate_count], 0..) |index, overlay| {
        const presentation = &presentation_items[index];
        if (with_modules or overlay > 0) try writer.write(",");
        try writer.print("{{\"id\":\"overlay-{d}\",\"path\":\"", .{overlay});
        const path_start = writer.length;
        try writer.print("/api/performance-overlay/subscribe?intervalMs={d}&softwareId=", .{interval_ms});
        try writer.escape(presentation.software_id);
        try writer.write("\",\"items\":[");
        for (presentation.items, 0..) |item, position| {
            if (position > 0) try writer.write(",");
            try writer.print("{{\"kind\":\"{s}\",\"id\":", .{@tagName(item.kind)});
            try writer.jsonString(item.id);
            try writer.write(",\"source\":");
            try writer.jsonString(item.source);
            if (item.interval_ms) |interval| try writer.print(",\"intervalMs\":{d}", .{interval}) else try writer.write(",\"intervalMs\":null");
            try writer.print(",\"syncPrecision\":\"{s}\"}}", .{@tagName(item.sync_precision)});
        }
        try writer.write("]");
        hasher.update(writer.output[path_start..writer.length]);
        hasher.update("\n");
        try writer.write("}");
        order[overlay] = index;
    }
    try writer.write("]}");
    return .{ .body = writer.output[0..writer.length], .overlay_count = candidate_count, .signature = hasher.final(), .with_modules = with_modules };
}

const Writer = struct {
    output: []u8,
    length: usize = 0,
    fn write(self: *Writer, bytes: []const u8) !void {
        if (bytes.len > self.output.len - self.length) return error.SubscriptionRequestTooLarge;
        @memcpy(self.output[self.length..][0..bytes.len], bytes);
        self.length += bytes.len;
    }
    fn print(self: *Writer, comptime format: []const u8, args: anytype) !void {
        const written = std.fmt.bufPrint(self.output[self.length..], format, args) catch return error.SubscriptionRequestTooLarge;
        self.length += written.len;
    }
    /// Uri.EscapeDataString: everything but A-Z a-z 0-9 - . _ ~ as %XX of its UTF-8.
    fn escape(self: *Writer, value: []const u8) !void {
        for (value) |byte| {
            if (std.ascii.isAlphanumeric(byte) or byte == '-' or byte == '.' or byte == '_' or byte == '~') {
                try self.write(&.{byte});
            } else try self.print("%{X:0>2}", .{byte});
        }
    }
    /// A JSON string: the quote, the backslash and control characters escaped,
    /// other UTF-8 as it is.
    fn jsonString(self: *Writer, value: []const u8) !void {
        try self.write("\"");
        for (value) |byte| switch (byte) {
            '"' => try self.write("\\\""),
            '\\' => try self.write("\\\\"),
            0...0x1f => try self.print("\\u{x:0>4}", .{byte}),
            else => try self.write(&.{byte}),
        };
        try self.write("\"");
    }
};

/// One /api/performance-overlay/subscribe value (PerformanceOverlaySnapshot,
/// version 3). `items` are the values of the subscription's items in request
/// order; every target of one subscription is the same software.
pub const Snapshot = struct {
    /// Absent in a rejection frame's value; 0 is then not the supported version.
    version: i64 = 0,
    targets: []const Target = &.{},
    items: []const ItemValue = &.{},
};
pub const Target = struct {
    softwareId: []const u8,
    processId: i64,
    processStartKey: u64,
};
/// The current value of one requested item (PerformanceOverlayItemValue).
/// State is current or unavailable; reason says why a value is unavailable.
pub const ItemValue = struct {
    kind: []const u8,
    id: []const u8,
    unit: ?[]const u8 = null,
    selectedSource: []const u8 = "",
    actualSource: ?[]const u8 = null,
    intervalMs: i64 = 0,
    intervalOrigin: []const u8 = "",
    syncPrecision: []const u8 = "",
    value: ?f64 = null,
    state: []const u8 = "",
    reason: ?[]const u8 = null,
    observedAt: ?[]const u8 = null,
};

/// One line per selected item in presentation order. An absent item still
/// shows its label and the unavailable marker. Explicit sources never show
/// an automatic-source reason or silently fall back to another source.
pub const Lines = struct {
    items: [maximum_items][]const u8 = undefined,
    count: usize = 0,
    storage: [maximum_items * text.maximum_line_bytes]u8 = undefined,
    used: usize = 0,

    pub fn slice(self: *const Lines) []const []const u8 {
        return self.items[0..self.count];
    }
    pub fn build(self: *Lines, values: []const ItemValue, presentation: *const Presentation, language: []const u8, format: *const text.NumberFormat) !void {
        self.count = 0;
        self.used = 0;
        for (presentation.items) |item| {
            const kind = @tagName(item.kind);
            const value: ?*const ItemValue = for (values) |*candidate| {
                if (std.mem.eql(u8, candidate.kind, kind) and std.ascii.eqlIgnoreCase(candidate.id, item.id)) break candidate;
            } else null;
            const output = self.storage[self.used..][0..text.maximum_line_bytes];
            const shown = try text.line(kind, item.id, if (value) |v| v.value else null, if (value) |v| v.unit else null, if (value != null and std.mem.eql(u8, item.source, "auto")) value.?.reason else null, language, format, output);
            self.items[self.count] = shown;
            self.count += 1;
            self.used += shown.len;
        }
    }
};

const testing = std.testing;
fn parsePresentations(storage: []u8, json: []const u8, presentations: *Presentations) !void {
    var arena = std.heap.FixedBufferAllocator.init(storage);
    const document = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), json, .{});
    try presentations.load(document.object.get("performanceOverlay"));
}

/// The presentation fields every normalized backend entry carries.
const look =
    \\"sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12
;

const example =
    \\{"performanceOverlay":{"software":{
    \\ "game.b":{"enabled":true,"mode":"external","items":[
    \\   {"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"mid"},
    \\   {"kind":"metric","id":"gpu.temperature","source":"Temperature@gpu 0","intervalMs":250,"syncPrecision":"high"},
    \\   {"kind":"software","id":"cpu.usage","source":"auto","syncPrecision":"low"}],
    \\   "sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topRight","marginPx":12},
    \\ "Game.A":{"enabled":true,"mode":"injected","items":[{"kind":"frames","id":"frames.low1","source":"auto","intervalMs":null,"syncPrecision":"mid"}],
    \\   "sizeMode":"windowRatio","fontSizePx":20,"regionWidthRatio":1,"regionHeightRatio":0.5,"anchor":"bottomLeft","marginPx":0},
    \\ "empty":{"enabled":true,"mode":"external","items":[],
++ look ++
    \\},
    \\ "off":{"enabled":false,"mode":"external","items":[{"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"mid"}],
++ look ++
    \\}
    \\}}}
;

test "backend overlay settings parse and project the subscription request" {
    const storage = try testing.allocator.alloc(u8, 1 << 20);
    defer testing.allocator.free(storage);
    var presentations = Presentations.init(storage[0 .. 1 << 19]);
    try parsePresentations(storage[1 << 19 ..], example, &presentations);
    try testing.expectEqual(@as(usize, 4), presentations.count);
    const b = presentations.find("GAME.B").?;
    try testing.expectEqual(layout.Anchor.top_right, b.settings.anchor);
    try testing.expectEqual(@as(usize, 3), b.items.len);
    try testing.expectEqual(Item{ .kind = .metric, .id = b.items[1].id, .source = b.items[1].source, .interval_ms = 250, .sync_precision = .high }, b.items[1]);
    try testing.expectEqualStrings("Temperature@gpu 0", b.items[1].source);
    try testing.expectEqual(@as(?u32, null), b.items[2].interval_ms);
    // A software without an entry has no overlay.
    try testing.expectEqual(@as(?*const Presentation, null), presentations.find("unknown"));
    var modules_storage: [4096]u8 = undefined;
    var modules = Modules.init(&modules_storage);
    var order: [maximum_overlays]usize = undefined;
    var body: [8192]u8 = undefined;
    // The injected overlay waits for the module selection; an enabled overlay
    // without items and a switched-off one are not subscribed.
    var result = try request(&presentations, &modules, 500, &order, &body);
    try testing.expectEqualStrings(
        "{\"version\":1,\"subscriptions\":[{\"id\":\"modules\",\"path\":\"/api/startup-modules/settings/subscribe\"}," ++
            "{\"id\":\"overlay-0\",\"path\":\"/api/performance-overlay/subscribe?intervalMs=500&softwareId=game.b\",\"items\":[" ++
            "{\"kind\":\"frames\",\"id\":\"frames.fps\",\"source\":\"auto\",\"intervalMs\":null,\"syncPrecision\":\"mid\"}," ++
            "{\"kind\":\"metric\",\"id\":\"gpu.temperature\",\"source\":\"Temperature@gpu 0\",\"intervalMs\":250,\"syncPrecision\":\"high\"}," ++
            "{\"kind\":\"software\",\"id\":\"cpu.usage\",\"source\":\"auto\",\"intervalMs\":null,\"syncPrecision\":\"low\"}]}]}",
        result.body,
    );
    try testing.expectEqual(@as(usize, 1), result.overlay_count);
    const first_signature = result.signature;
    const Software = @typeInfo(@TypeOf(@as(Modules.Document, undefined).software)).pointer.child;
    const selection = [_]Software{
        .{ .softwareId = "game.a", .moduleIds = &.{ "performance-overlay-vulkan", "other", "performance-overlay-preload" } },
        .{ .softwareId = "off", .moduleIds = &.{ "performance-overlay-preload", "performance-overlay-vulkan" } },
    };
    try testing.expect(try modules.load(.{ .version = 1, .software = &selection }));
    try testing.expect(!try modules.load(.{ .version = 1, .software = &selection }));
    result = try request(&presentations, &modules, 500, &order, &body);
    // Game.A is enabled and its modules are complete; "off" has complete modules but its switch is off.
    try testing.expectEqual(@as(usize, 2), result.overlay_count);
    try testing.expect(result.signature != first_signature);
    // Game.A sorts before game.b ignoring case.
    try testing.expectEqualStrings("Game.A", presentations.items[order[0]].software_id);
    try testing.expect(std.mem.indexOf(u8, result.body, "{\"id\":\"overlay-0\",\"path\":\"/api/performance-overlay/subscribe?intervalMs=500&softwareId=Game.A\",\"items\":[" ++
        "{\"kind\":\"frames\",\"id\":\"frames.low1\",\"source\":\"auto\",\"intervalMs\":null,\"syncPrecision\":\"mid\"}]}") != null);
    // Escaping: the path with Uri.EscapeDataString, the items as JSON strings.
    var escaped_storage: [1 << 16]u8 = undefined;
    var escaped = Presentations.init(escaped_storage[0 .. 1 << 15]);
    try parsePresentations(escaped_storage[1 << 15 ..],
        \\{"performanceOverlay":{"software":{"a b/c":{"enabled":true,"mode":"external","items":[{"kind":"metric","id":"x\"y","source":"f\\g@d","intervalMs":100,"syncPrecision":"mid"}],
    ++ look ++
        \\}}}}
    , &escaped);
    result = try request(&escaped, &modules, 60000, &order, &body);
    try testing.expectEqualStrings(
        "{\"version\":1,\"subscriptions\":[{\"id\":\"overlay-0\",\"path\":\"/api/performance-overlay/subscribe?intervalMs=60000&softwareId=a%20b%2Fc\",\"items\":[" ++
            "{\"kind\":\"metric\",\"id\":\"x\\\"y\",\"source\":\"f\\\\g@d\",\"intervalMs\":100,\"syncPrecision\":\"mid\"}]}]}",
        result.body,
    );
}

test "a missing section is no overlay and an incomplete entry rejects the set" {
    const storage = try testing.allocator.alloc(u8, 1 << 20);
    defer testing.allocator.free(storage);
    var presentations = Presentations.init(storage[0 .. 1 << 19]);
    try parsePresentations(storage[1 << 19 ..], "{}", &presentations);
    try testing.expectEqual(@as(usize, 0), presentations.count);
    const item_cases = [_][]const u8{
        \\{"kind":"other","id":"x","source":"auto","syncPrecision":"mid"}
        ,
        \\{"kind":"metric","id":" ","source":"auto","syncPrecision":"mid"}
        ,
        \\{"kind":"metric","source":"auto","syncPrecision":"mid"}
        ,
        \\{"kind":"metric","id":"x","source":"","syncPrecision":"mid"}
        ,
        \\{"kind":"metric","id":"x","source":"auto","syncPrecision":"max"}
        ,
        \\{"kind":"metric","id":"x","source":"auto"}
        ,
        \\{"kind":"metric","id":"x","source":"auto","intervalMs":0,"syncPrecision":"mid"}
        ,
        \\{"kind":"metric","id":"x","source":"auto","intervalMs":500.5,"syncPrecision":"mid"}
        ,
        \\{"kind":"metric","id":"x","source":"auto","syncPrecision":"mid"},{"kind":"metric","id":"X","source":"a@b","syncPrecision":"low"}
        ,
        "1",
    };
    for (item_cases) |case| {
        var json: [2048]u8 = undefined;
        const document = try std.fmt.bufPrint(&json, "{{\"performanceOverlay\":{{\"software\":{{\"a\":{{\"enabled\":true,\"mode\":\"external\",\"items\":[{s}],{s}}}}}}}}}", .{ case, look });
        try testing.expect(std.meta.isError(parsePresentations(storage[1 << 19 ..], document, &presentations)));
        try testing.expectEqual(@as(usize, 0), presentations.count);
    }
    const cases = [_][]const u8{
        \\{"performanceOverlay":{"software":{"a":{"enabled":true,"mode":"other","items":[],"sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12}}}}
        ,
        \\{"performanceOverlay":{"software":{"a":{"enabled":true,"mode":"external","items":[],"sizeMode":"absolutePixels","fontSizePx":0,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12}}}}
        ,
        \\{"performanceOverlay":{"software":{"a":{"enabled":true,"mode":"external","items":[],"sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":1.5,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12}}}}
        ,
        // The backend expands every field; a missing one is a broken section, not a default.
        \\{"performanceOverlay":{"software":{"a":{"enabled":true,"mode":"external","items":[]}}}}
        ,
        \\{"performanceOverlay":{"software":{"a":{"enabled":null,"mode":"external","items":[],"sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12}}}}
        ,
        \\{"performanceOverlay":{"software":{"a":{"enabled":true,"mode":"external","items":[],"sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"middle","marginPx":12}}}}
        ,
        \\{"performanceOverlay":{"software":[]}}
        ,
        \\{"performanceOverlay":{"software":{"a":{"enabled":true,"mode":"external","items":[],"sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12},"A":{"enabled":true,"mode":"external","items":[],"sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12}}}}
    };
    for (cases) |case| {
        try testing.expect(std.meta.isError(parsePresentations(storage[1 << 19 ..], case, &presentations)));
        try testing.expectEqual(@as(usize, 0), presentations.count);
    }
    // As many items as the block draws are accepted, one more is not.
    for ([_]usize{ maximum_items, maximum_items + 1 }) |count| {
        var many: [16384]u8 = undefined;
        var stream = std.Io.Writer.fixed(&many);
        try stream.writeAll("{\"performanceOverlay\":{\"software\":{\"a\":{\"enabled\":true,\"mode\":\"external\",\"items\":[");
        for (0..count) |index| try stream.print("{s}{{\"kind\":\"metric\",\"id\":\"m{d}\",\"source\":\"auto\",\"syncPrecision\":\"mid\"}}", .{ if (index == 0) "" else ",", index });
        try stream.print("],{s}}}}}}}}}", .{look});
        const outcome = parsePresentations(storage[1 << 19 ..], stream.buffered(), &presentations);
        if (count == maximum_items) {
            try outcome;
            try testing.expectEqual(@as(usize, maximum_items), presentations.find("a").?.items.len);
        } else try testing.expect(std.meta.isError(outcome));
    }
}

test "an injected overlay is drawn only while its switch is on and its modules are complete" {
    var storage: [1 << 16]u8 = undefined;
    var presentations = Presentations.init(storage[0 .. 1 << 15]);
    try parsePresentations(storage[1 << 15 ..],
        \\{"performanceOverlay":{"software":{"g":{"enabled":false,"mode":"injected","items":[{"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"mid"}],
    ++ look ++
        \\}}}}
    , &presentations);
    var modules_storage: [4096]u8 = undefined;
    var modules = Modules.init(&modules_storage);
    const Software = @typeInfo(@TypeOf(@as(Modules.Document, undefined).software)).pointer.child;
    const selection = [_]Software{.{ .softwareId = "g", .moduleIds = &overlay_modules }};
    _ = try modules.load(.{ .version = 1, .software = &selection });
    try testing.expect(!wanted(presentations.find("g").?, &modules));
}

test "lines follow the stored items and show a value, the reason or a dash" {
    const storage = try testing.allocator.alloc(u8, 1 << 20);
    defer testing.allocator.free(storage);
    var presentations = Presentations.init(storage[0 .. 1 << 19]);
    try parsePresentations(storage[1 << 19 ..],
        \\{"performanceOverlay":{"software":{"game":{"enabled":true,"mode":"external","items":[
        \\ {"kind":"software","id":"gpu.vram","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\ {"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\ {"kind":"metric","id":"cpu.temperature","source":"cpu_amd_tctl_temperature_celsius@cpu|0","intervalMs":null,"syncPrecision":"mid"},
        \\ {"kind":"frames","id":"frames.low1","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\ {"kind":"metric","id":"absent.metric","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\ {"kind":"frames","id":"frames.present","source":"auto","intervalMs":null,"syncPrecision":"mid"}],
    ++ look ++
        \\}}}}
    , &presentations);
    // The backend's values, in another order and with a value of another kind.
    var arena_storage: [16384]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&arena_storage);
    const snapshot = try std.json.parseFromSliceLeaky(Snapshot, arena.allocator(),
        \\{"version":3,"targets":[{"softwareId":"game","processId":4,"processStartKey":9,"settings":{}}],"items":[
        \\ {"kind":"frames","id":"frames.present","unit":"ms","selectedSource":"auto","actualSource":null,"intervalMs":0,"intervalOrigin":"frame-events","syncPrecision":"mid","value":null,"state":"unavailable","reason":"a-future-reason","observedAt":null},
        \\ {"kind":"frames","id":"FRAMES.LOW1","unit":"fps","selectedSource":"auto","actualSource":null,"intervalMs":0,"intervalOrigin":"frame-events","syncPrecision":"mid","value":null,"state":"unavailable","reason":null,"observedAt":null},
        \\ {"kind":"metric","id":"cpu.temperature","unit":"°C","selectedSource":"cpu_amd_tctl_temperature_celsius@cpu|0","actualSource":"cpu_amd_tctl_temperature_celsius@cpu|0","intervalMs":500,"intervalOrigin":"profile-default","syncPrecision":"mid","value":61,"state":"current","reason":null,"observedAt":"2026-10-30T08:00:00+00:00"},
        \\ {"kind":"frames","id":"frames.fps","unit":"fps","selectedSource":"auto","actualSource":"frame-events","intervalMs":0,"intervalOrigin":"frame-events","syncPrecision":"mid","value":null,"state":"unavailable","reason":"no-frame-data","observedAt":null},
        \\ {"kind":"metric","id":"gpu.vram","unit":"B","selectedSource":"auto","actualSource":null,"intervalMs":500,"intervalOrigin":"profile-default","syncPrecision":"mid","value":1,"state":"current","reason":null,"observedAt":null},
        \\ {"kind":"software","id":"gpu.vram","unit":"B","selectedSource":"auto","actualSource":"process","intervalMs":500,"intervalOrigin":"profile-default","syncPrecision":"mid","value":1610612736,"state":"current","reason":null,"observedAt":null}
        \\]}
    , .{ .ignore_unknown_fields = true });
    try testing.expectEqual(@as(i64, 3), snapshot.version);
    const lines = try testing.allocator.create(Lines);
    defer testing.allocator.destroy(lines);
    lines.* = .{};
    const invariant: text.NumberFormat = .{};
    try lines.build(snapshot.items, presentations.find("game").?, "en-US", &invariant);
    const expected = [_][]const u8{
        "Target process VRAM  1.5 GiB",
        "Target FPS  No frame data",
        "CPU temperature  61 °C",
        "Target 1% low  \u{2014}",
        "absent.metric  \u{2014}",
        "Target frame time  \u{2014}",
    };
    try testing.expectEqual(expected.len, lines.count);
    for (expected, lines.slice()) |want, line| try testing.expectEqualStrings(want, line);
}

test "an unavailable explicit source has its own line without an automatic reason" {
    const items = [_]Item{.{ .kind = .metric, .id = "cpu.temperature", .source = "chosen@cpu", .interval_ms = null, .sync_precision = .mid }};
    const presentation: Presentation = .{
        .software_id = "game",
        .enabled = true,
        .mode = .external,
        .items = &items,
        .settings = .{ .size_mode = .absolute_pixels, .font_size_px = 14, .region_width_ratio = 0.25, .region_height_ratio = 0.2, .anchor = .top_left, .margin_px = 12 },
    };
    const values = [_]ItemValue{.{ .kind = "metric", .id = "cpu.temperature", .value = null, .reason = "no-frame-data" }};
    const lines = try testing.allocator.create(Lines);
    defer testing.allocator.destroy(lines);
    lines.* = .{};
    const format: text.NumberFormat = .{};
    try lines.build(&values, &presentation, "en-US", &format);
    try testing.expectEqual(@as(usize, 1), lines.count);
    try testing.expectEqualStrings("CPU temperature  \u{2014}", lines.slice()[0]);
}

test "batch13 item values keep target GPU and frame pipeline reasons in configured order" {
    const selected = [_]Item{
        .{ .kind = .metric, .id = "gpu.target.temperature", .source = "auto", .interval_ms = null, .sync_precision = .mid },
        .{ .kind = .metric, .id = "gpu.target.vram", .source = "auto", .interval_ms = null, .sync_precision = .mid },
        .{ .kind = .frames, .id = "frames.present", .source = "auto", .interval_ms = null, .sync_precision = .mid },
    };
    const presentation: Presentation = .{ .software_id = "running", .enabled = true, .mode = .external, .items = &selected, .settings = .{ .size_mode = .absolute_pixels, .font_size_px = 14, .region_width_ratio = 0.25, .region_height_ratio = 0.2, .anchor = .top_left, .margin_px = 12 } };
    // Values arrive out of order. No native GPU resolution or sampling is added.
    const received = [_]ItemValue{
        .{ .kind = "frames", .id = "frames.present", .reason = "present-stop-not-observed", .state = "unavailable" },
        .{ .kind = "metric", .id = "gpu.target.vram", .reason = "gpu-dedicated-memory-metrics-unsupported", .state = "unavailable" },
        .{ .kind = "metric", .id = "gpu.target.temperature", .value = 61, .unit = "°C", .actualSource = "sensor@gpu|1", .state = "current" },
    };
    const lines = try testing.allocator.create(Lines);
    defer testing.allocator.destroy(lines);
    const format: text.NumberFormat = .{};
    try lines.build(&received, &presentation, "en-US", &format);
    try testing.expectEqual(@as(usize, 3), lines.count);
    try testing.expectEqualStrings("Target's GPU temperature  61 °C", lines.slice()[0]);
    try testing.expectEqualStrings("Target's GPU resident VRAM  No dedicated GPU memory metrics", lines.slice()[1]);
    try testing.expectEqualStrings("Target frame time  \u{2014}", lines.slice()[2]);
}
