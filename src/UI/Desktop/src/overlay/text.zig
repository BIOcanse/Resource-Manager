//! Overlay line text: item names and unavailable reasons from the generated
//! nine-language tables, and the value format of the window process's
//! OverlayBitmapRenderer.FormatLine ("0.#" in the user's number format, bytes
//! shown as MiB or GiB).
const std = @import("std");
const points = @import("desktop_points_values").Bound;
const table = @import("../text/generated/overlay_metric_labels.zig");

pub const maximum_line_bytes = points.overlay_maximum_line_bytes;

pub fn languageIndex(language: []const u8) ?usize {
    for (table.languages, 0..) |name, index| if (std.mem.eql(u8, name, language)) return index;
    return null;
}

/// The label for a metric id. A numbered id such as "fan.2.speed" uses the
/// "fan.{index}.speed" entry with the number put back; an id without an entry
/// is its own label. A language without labels is an error, not a fallback.
pub fn label(metric_id: []const u8, language: []const u8, output: []u8) ![]const u8 {
    const column = languageIndex(language) orelse return error.UnknownOverlayLanguage;
    // The first ".<digits>" followed by "." or the end, as OverlayMetricLabels' regex.
    var digits_start: usize = 0;
    var digits_end: usize = 0;
    var position: usize = 0;
    while (position < metric_id.len) : (position += 1) {
        if (metric_id[position] != '.') continue;
        var end = position + 1;
        while (end < metric_id.len and std.ascii.isDigit(metric_id[end])) end += 1;
        if (end > position + 1 and (end == metric_id.len or metric_id[end] == '.')) {
            digits_start = position + 1;
            digits_end = end;
            break;
        }
    }
    var key_buffer: [256]u8 = undefined;
    const key = if (digits_end == 0) metric_id else std.fmt.bufPrint(&key_buffer, "{s}{{index}}{s}", .{ metric_id[0..digits_start], metric_id[digits_end..] }) catch return copy(metric_id, output);
    for (table.entries) |entry| {
        if (!std.ascii.eqlIgnoreCase(entry.key, key)) continue;
        const text = entry.labels[column];
        if (digits_end == 0) return copy(text, output);
        return replace(text, "{index}", metric_id[digits_start..digits_end], output);
    }
    return copy(metric_id, output);
}

const Alias = struct { id: []const u8, key: []const u8 };
/// Frames items keep the old overlay's names.
const frame_label_keys = [_]Alias{
    .{ .id = "frames.fps", .key = "target.fps" },
    .{ .id = "frames.present", .key = "target.frameTime" },
    .{ .id = "frames.low1", .key = "target.onePercentLow" },
    .{ .id = "frames.low01", .key = "target.pointOnePercentLow" },
};
/// The four software items the old overlay had keep its names too.
const software_label_keys = [_]Alias{
    .{ .id = "cpu.usage", .key = "target.cpu" },
    .{ .id = "memory.usage", .key = "target.memory" },
    .{ .id = "gpu.usage", .key = "target.gpu" },
    .{ .id = "gpu.vram", .key = "target.vram" },
};
fn aliasKey(aliases: []const Alias, id: []const u8) ?[]const u8 {
    for (aliases) |alias| if (std.ascii.eqlIgnoreCase(alias.id, id)) return alias.key;
    return null;
}

/// The name of one item, in the order of src/UI/Frontend/src/features/overlay/overlayItemLabels.ts:
/// - frames items: the old overlay's metric name, else the id;
/// - the four old software items: the old overlay's metric name;
/// - other software items: the software template around the process field
///   name, else the metric name, else the id;
/// - metric items: the metric name, else the id.
pub fn itemLabel(kind: []const u8, id: []const u8, language: []const u8, output: []u8) ![]const u8 {
    if (std.mem.eql(u8, kind, "frames")) {
        const key = aliasKey(&frame_label_keys, id) orelse return copy(id, output);
        return label(key, language, output);
    }
    if (!std.mem.eql(u8, kind, "software")) return label(id, language, output);
    if (aliasKey(&software_label_keys, id)) |key| return label(key, language, output);
    const column = languageIndex(language) orelse return error.UnknownOverlayLanguage;
    var name_buffer: [maximum_line_bytes]u8 = undefined;
    const name = for (table.process_fields) |entry| {
        if (std.mem.eql(u8, entry.key, id)) break entry.labels[column];
    } else try label(id, language, &name_buffer);
    return replace(table.software_template[column], "{name}", name, output);
}

fn copy(text: []const u8, output: []u8) ![]const u8 {
    if (text.len > output.len) return error.OverlayLineTooLong;
    @memcpy(output[0..text.len], text);
    return output[0..text.len];
}

/// Every `placeholder` in `text` replaced by `value`.
fn replace(text: []const u8, placeholder: []const u8, value: []const u8, output: []u8) ![]const u8 {
    var length: usize = 0;
    var position: usize = 0;
    while (position < text.len) {
        if (std.mem.startsWith(u8, text[position..], placeholder)) {
            length = try append(output, length, value);
            position += placeholder.len;
        } else {
            length = try append(output, length, text[position .. position + 1]);
            position += 1;
        }
    }
    return output[0..length];
}

/// The decimal separator and negative sign of the user's number format, the
/// CultureInfo.CurrentCulture the window process formatted with.
pub const NumberFormat = struct {
    decimal: [16]u8 = .{'.'} ++ @as([15]u8, @splat(0)),
    decimal_len: usize = 1,
    negative: [16]u8 = .{'-'} ++ @as([15]u8, @splat(0)),
    negative_len: usize = 1,

    pub fn decimalSlice(self: *const NumberFormat) []const u8 {
        return self.decimal[0..self.decimal_len];
    }
    pub fn negativeSlice(self: *const NumberFormat) []const u8 {
        return self.negative[0..self.negative_len];
    }
};

/// Declared significant/fractional precision, rounded half away from zero;
/// trailing fractional zeroes are omitted (the default format is "0.#").
pub fn number(value: f64, format: *const NumberFormat, output: []u8) ![]const u8 {
    var scientific: [64]u8 = undefined;
    const scientific_format = comptime std.fmt.comptimePrint("{{e:.{d}}}", .{points.overlay_number_significant_digits - 1});
    const text = try std.fmt.bufPrint(&scientific, scientific_format, .{@abs(value)});
    const e = std.mem.indexOfScalar(u8, text, 'e') orelse return error.InvalidNumber;
    var digits: [points.overlay_number_significant_digits + 1]u8 = undefined;
    var count: usize = 0;
    for (text[0..e]) |character| {
        if (character == '.') continue;
        digits[count] = character - '0';
        count += 1;
    }
    var exponent = try std.fmt.parseInt(i32, text[e + 1 ..], 10);
    // Digits before the decimal point, then the one fractional digit kept.
    const keep = exponent + 1 + points.overlay_number_fraction_digits;
    if (value == 0) {
        count = 0;
    } else if (keep <= 0) {
        // Only a first digit at the rounding position can carry into a "1".
        if (keep == 0 and digits[0] >= 5) {
            digits[0] = 1;
            count = 1;
            exponent += 1;
        } else count = 0;
    } else if (keep < count) {
        const position: usize = @intCast(keep);
        const round_up = digits[position] >= 5;
        count = position;
        if (round_up) {
            var index = count;
            while (index > 0) {
                index -= 1;
                if (digits[index] < 9) {
                    digits[index] += 1;
                    break;
                }
                digits[index] = 0;
                if (index == 0) {
                    std.mem.copyBackwards(u8, digits[1 .. count + 1], digits[0..count]);
                    digits[0] = 1;
                    count += 1;
                    exponent += 1;
                }
            }
        }
    }
    while (count > 0 and digits[count - 1] == 0) count -= 1;
    var length: usize = 0;
    if (std.math.signbit(value) and count > 0) length = try append(output, length, format.negativeSlice());
    if (count == 0 or exponent < 0) {
        length = try append(output, length, "0");
    } else {
        var index: usize = 0;
        const integer_digits: usize = @intCast(exponent + 1);
        while (index < integer_digits) : (index += 1) {
            const digit: u8 = if (index < count) digits[index] else 0;
            length = try append(output, length, &.{'0' + digit});
        }
    }
    var fraction_index = exponent + 1;
    if (count > 0 and fraction_index < count and points.overlay_number_fraction_digits > 0) {
        length = try append(output, length, format.decimalSlice());
        var fractional: usize = 0;
        while (fraction_index < count and fractional < points.overlay_number_fraction_digits) : ({
            fraction_index += 1;
            fractional += 1;
        }) {
            const digit: u8 = if (fraction_index < 0) 0 else digits[@intCast(fraction_index)];
            length = try append(output, length, &.{'0' + digit});
        }
    }
    return output[0..length];
}

fn append(output: []u8, length: usize, piece: []const u8) !usize {
    if (length + piece.len > output.len) return error.OverlayLineTooLong;
    @memcpy(output[length..][0..piece.len], piece);
    return length + piece.len;
}

/// The text for a backend reason code, or null for a code without an entry.
pub fn reason(code: []const u8, language: []const u8) !?[]const u8 {
    const column = languageIndex(language) orelse return error.UnknownOverlayLanguage;
    for (table.reasons) |entry| if (std.mem.eql(u8, entry.key, code)) return entry.labels[column];
    return null;
}

/// One item's line, "{name}  {value} {unit}" with trailing white space
/// removed. Without a finite value it is "{name}  {reason text}"; a reason
/// code without text, or no reason, is "{name}  —".
pub fn line(kind: []const u8, id: []const u8, value: ?f64, unit: ?[]const u8, missing_reason: ?[]const u8, language: []const u8, format: *const NumberFormat, output: []u8) ![]const u8 {
    const name = try itemLabel(kind, id, language, output);
    var length = try append(output, name.len, "  ");
    const shown = value orelse std.math.nan(f64);
    if (!std.math.isFinite(shown)) {
        const code = missing_reason orelse "";
        const explained = if (code.len == 0) "\u{2014}" else (try reason(code, language)) orelse "\u{2014}";
        return output[0..try append(output, length, explained)];
    }
    var scaled = shown;
    var shown_unit = unit orelse "";
    if (std.ascii.eqlIgnoreCase(shown_unit, "bytes") or std.ascii.eqlIgnoreCase(shown_unit, "B")) {
        if (@abs(scaled) >= 1_073_741_824) {
            scaled /= 1_073_741_824;
            shown_unit = "GiB";
        } else if (@abs(scaled) >= 1_048_576) {
            scaled /= 1_048_576;
            shown_unit = "MiB";
        }
    }
    var digits: [64]u8 = undefined;
    length = try append(output, length, try number(scaled, format, &digits));
    length = try append(output, length, " ");
    length = try append(output, length, shown_unit);
    return output[0..trimEnd(output[0..length])];
}

/// The length without trailing characters that .NET's char.IsWhiteSpace accepts.
fn trimEnd(text: []const u8) usize {
    var end = text.len;
    while (end > 0) {
        const start = startOfLast(text[0..end]);
        const code = std.unicode.utf8Decode(text[start..end]) catch return end;
        const white = switch (code) {
            0x09...0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 => true,
            else => false,
        };
        if (!white) return end;
        end = start;
    }
    return 0;
}

fn startOfLast(text: []const u8) usize {
    var start = text.len - 1;
    var steps: usize = 0;
    while (start > 0 and steps < 3 and text[start] & 0xc0 == 0x80) : (steps += 1) start -= 1;
    return start;
}

const testing = std.testing;
test "labels use the generated table, numbered entries and the id itself" {
    var buffer: [maximum_line_bytes]u8 = undefined;
    try testing.expectEqualStrings("Fan 2", try label("fan.2.speed", "en-US", &buffer));
    try testing.expectEqualStrings("Вентилятор 12", try label("fan.12.speed", "ru-RU", &buffer));
    try testing.expectEqualStrings("CPU actual power", try label("CPU.ACTUALPOWER", "en-US", &buffer));
    try testing.expectEqualStrings("vendor.custom", try label("vendor.custom", "en-US", &buffer));
    try testing.expectEqualStrings("fan.x.speed", try label("fan.x.speed", "en-US", &buffer));
    try testing.expectError(error.UnknownOverlayLanguage, label("fan.2.speed", "xx-XX", &buffer));
    for (table.entries) |entry| for (entry.labels) |text| try testing.expect(text.len > 0);
}

test "numbers follow the 0.# format" {
    var buffer: [64]u8 = undefined;
    const invariant: NumberFormat = .{};
    const cases = [_]struct { f64, []const u8 }{
        .{ 0, "0" },          .{ 60, "60" },                      .{ 59.94, "59.9" }, .{ 59.95, "60" },
        .{ 0.04, "0" },       .{ 0.05, "0.1" },                   .{ 1.25, "1.3" },   .{ 999.96, "1000" },
        .{ 16.6667, "16.7" }, .{ 1e20, "100000000000000000000" }, .{ -2.5, "-2.5" },  .{ 0.25, "0.3" },
    };
    for (cases) |case| try testing.expectEqualStrings(case[1], try number(case[0], &invariant, &buffer));
    var comma: NumberFormat = .{};
    comma.decimal[0] = ',';
    try testing.expectEqualStrings("1,5", try number(1.5, &comma, &buffer));
}

test "item names follow the frontend lookup order" {
    var buffer: [maximum_line_bytes]u8 = undefined;
    // Frames items and the four old software items keep the old overlay's names.
    try testing.expectEqualStrings("Target FPS", try itemLabel("frames", "frames.fps", "en-US", &buffer));
    try testing.expectEqualStrings("Target frame time", try itemLabel("frames", "FRAMES.PRESENT", "en-US", &buffer));
    try testing.expectEqualStrings("frames.unknown", try itemLabel("frames", "frames.unknown", "en-US", &buffer));
    try testing.expectEqualStrings("Target process CPU", try itemLabel("software", "cpu.usage", "en-US", &buffer));
    try testing.expectEqualStrings("Target process memory", try itemLabel("software", "memory.usage", "en-US", &buffer));
    try testing.expectEqualStrings("Target process GPU", try itemLabel("software", "gpu.usage", "en-US", &buffer));
    try testing.expectEqualStrings("Target process VRAM", try itemLabel("software", "gpu.vram", "en-US", &buffer));
    // Other software items: the template around the process field name, else the metric name, else the id.
    try testing.expectEqualStrings("Target process · Network sent", try itemLabel("software", "process_all_network_send_bytes_attributed", "en-US", &buffer));
    try testing.expectEqualStrings("Target process · CPU actual power", try itemLabel("software", "cpu.actualPower", "en-US", &buffer));
    try testing.expectEqualStrings("Target process · vendor.custom", try itemLabel("software", "vendor.custom", "en-US", &buffer));
    // Metric items: the metric name, numbered entries included, else the id.
    try testing.expectEqualStrings("Fan 2", try itemLabel("metric", "fan.2.speed", "en-US", &buffer));
    try testing.expectEqualStrings("vendor.custom", try itemLabel("metric", "vendor.custom", "en-US", &buffer));
    try testing.expectError(error.UnknownOverlayLanguage, itemLabel("software", "vendor.custom", "xx-XX", &buffer));
    for (table.software_template) |template| try testing.expect(std.mem.indexOf(u8, template, "{name}") != null);
    for (table.process_fields) |entry| for (entry.labels) |text| try testing.expect(text.len > 0);
    for (table.reasons) |entry| for (entry.labels) |text| try testing.expect(text.len > 0);
}

test "lines show units, byte scaling, the reason or a dash without a value" {
    var buffer: [maximum_line_bytes]u8 = undefined;
    const invariant: NumberFormat = .{};
    try testing.expectEqualStrings("Target FPS  144", try line("frames", "frames.fps", 144, "", null, "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("Target FPS  143.9 FPS", try line("frames", "frames.fps", 143.94, "FPS", null, "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("Target frame time  16.7 ms", try line("frames", "frames.present", 16.6667, "ms", null, "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("Target process CPU  12.5", try line("software", "cpu.usage", 12.5, null, null, "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("Target FPS  \u{2014}", try line("frames", "frames.fps", null, "FPS", null, "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("Target FPS  \u{2014}", try line("frames", "frames.fps", std.math.nan(f64), "FPS", "", "en-US", &invariant, &buffer));
    const vram = try line("software", "gpu.vram", 3 * 1_073_741_824.0 / 2.0, "bytes", null, "en-US", &invariant, &buffer);
    try testing.expectEqualStrings("Target process VRAM  1.5 GiB", vram);
    const memory = try line("software", "memory.usage", 5 * 1_048_576.0, "B", null, "en-US", &invariant, &buffer);
    try testing.expectEqualStrings("Target process memory  5 MiB", memory);
    // A missing value shows the backend's reason text; a code without text is shown as is.
    try testing.expectEqualStrings("Target FPS  No frame data", try line("frames", "frames.fps", null, "FPS", "no-frame-data", "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("目标帧率  无帧数据", try line("frames", "frames.fps", null, "FPS", "no-frame-data", "zh-CN", &invariant, &buffer));
    try testing.expectEqualStrings("Target process CPU  Target not running", try line("software", "cpu.usage", null, "%", "target-not-running", "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("Target FPS  \u{2014}", try line("frames", "frames.fps", null, "FPS", "a-future-reason", "en-US", &invariant, &buffer));
    try testing.expectEqualStrings("Target FPS  144", try line("frames", "frames.fps", 144, "", "no-frame-data", "en-US", &invariant, &buffer));
}

test "batch13 target GPU labels and reader reasons are localized in every language" {
    for (table.languages) |language| {
        var buffer: [maximum_line_bytes]u8 = undefined;
        for ([_][]const u8{ "gpu.target.usage", "gpu.target.temperature", "gpu.target.vram" }) |id| try testing.expect(!std.mem.eql(u8, id, try itemLabel("metric", id, language, &buffer)));
        // Concrete reader/gap reasons added by the merged backend. They are
        // protocol identities, not new native defaults or translated copy.
        for ([_][]const u8{
            "metric-unknown",        "metric-warming",             "metric-unsupported",                       "metric-unavailable",
            "breakdown-warming",     "breakdown-failed",           "breakdown-bar-missing",                    "breakdown-unsupported",
            "breakdown-unavailable", "process-metric-unavailable", "not-applicable",                           "no-source",
            "source-not-refreshed",  "target-gpu-unresolved",      "gpu-dedicated-memory-metrics-unsupported",
        }) |code| {
            const shown = (try reason(code, language)) orelse return error.MissingBackendReason;
            try testing.expect(shown.len > 0 and !std.mem.eql(u8, shown, code));
        }
    }
}

test "batch13 unlocalized frame pipeline reasons use the documented unavailable marker" {
    var buffer: [maximum_line_bytes]u8 = undefined;
    const format: NumberFormat = .{};
    for ([_][]const u8{ "gpu-frame-tier-not-requested", "gpu-frame-tracker-capacity", "present-stop-not-observed", "frames-window-skipped" }) |code| {
        const shown = try line("frames", "frames.present", null, "ms", code, "en-US", &format, &buffer);
        try testing.expect(std.mem.endsWith(u8, shown, "\u{2014}"));
    }
}
