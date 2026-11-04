//! Resident copy: language selection over the generated catalog and the
//! balloon message built from a catalog template. No literal user text here.
const std = @import("std");
pub const catalog = @import("generated/tray.zig");
pub const Text = catalog.Text;

/// Exact (case-insensitive) language match, else the shared fallback language.
pub fn select(language: []const u8) *const Text {
    for (&catalog.all) |*item| if (std.ascii.eqlIgnoreCase(item.language, language)) return item;
    for (&catalog.all) |*item| if (std.mem.eql(u8, item.language, catalog.fallback_language)) return item;
    return &catalog.all[0];
}

/// NIIF_INFO, NIIF_WARNING and NIIF_ERROR, matching the C# ToolTipIcon choice.
pub const Icon = enum(u32) { info = 1, warning = 2, @"error" = 3 };
/// Shell_NotifyIcon NIF_INFO limits: szInfoTitle[64] and szInfo[256], NUL included.
pub const title_units = 63;
pub const body_units = 255;
pub const Balloon = struct {
    icon: Icon = .info,
    title: [title_units + 1]u16 = undefined,
    title_len: usize = 0,
    body: [body_units + 1]u16 = undefined,
    body_len: usize = 0,
    pub fn titleSlice(self: *const Balloon) []const u16 {
        return self.title[0..self.title_len];
    }
    pub fn bodySlice(self: *const Balloon) []const u16 {
        return self.body[0..self.body_len];
    }
};

/// Builds a balloon from a catalog title and template. `{n}` is replaced by
/// args[n]; other text is copied. Output is truncated at a code point boundary.
pub fn balloon(icon: Icon, title: []const u8, template: []const u8, args: []const []const u8) Balloon {
    var value: Balloon = .{ .icon = icon };
    var title_out: Utf16Out = .{ .buffer = value.title[0..title_units] };
    title_out.append(title);
    value.title_len = title_out.len;
    value.title[value.title_len] = 0;
    var body_out: Utf16Out = .{ .buffer = value.body[0..body_units] };
    var index: usize = 0;
    var literal_start: usize = 0;
    while (index < template.len) {
        if (template[index] == '{' and index + 2 < template.len and std.ascii.isDigit(template[index + 1]) and template[index + 2] == '}') {
            const argument = template[index + 1] - '0';
            body_out.append(template[literal_start..index]);
            if (argument < args.len) body_out.append(args[argument]);
            index += 3;
            literal_start = index;
            continue;
        }
        index += 1;
    }
    body_out.append(template[literal_start..]);
    value.body_len = body_out.len;
    value.body[value.body_len] = 0;
    return value;
}

const Utf16Out = struct {
    buffer: []u16,
    len: usize = 0,
    full: bool = false,
    fn append(self: *Utf16Out, utf8: []const u8) void {
        if (self.full) return;
        var view = std.unicode.Utf8View.init(utf8) catch {
            self.appendLossy(utf8);
            return;
        };
        var iterator = view.iterator();
        while (iterator.nextCodepoint()) |point| self.push(point);
    }
    fn appendLossy(self: *Utf16Out, utf8: []const u8) void {
        // Facts from the backend are JSON strings and valid UTF-8; this only
        // guards a malformed slice from a truncated buffer.
        for (utf8) |byte| self.push(if (byte < 0x80) byte else 0xfffd);
    }
    fn push(self: *Utf16Out, point: u21) void {
        if (self.full) return;
        const units: usize = if (point >= 0x10000) 2 else 1;
        if (self.len + units > self.buffer.len) {
            self.full = true;
            return;
        }
        if (units == 2) {
            const high = point - 0x10000;
            self.buffer[self.len] = @intCast(0xd800 + (high >> 10));
            self.buffer[self.len + 1] = @intCast(0xdc00 + (high & 0x3ff));
        } else self.buffer[self.len] = @intCast(point);
        self.len += units;
    }
};

test "every supported language supplies every resident message" {
    try std.testing.expect(catalog.all.len >= 9);
    for (catalog.all) |item| {
        inline for (@typeInfo(Text).@"struct".field_names) |field_name| {
            const text = @field(item, field_name);
            try std.testing.expect(text.len > 0);
            try std.testing.expect(std.unicode.utf8ValidateSlice(text));
        }
        // Templates use exactly the placeholders their consumers fill.
        try std.testing.expect(std.mem.indexOf(u8, item.terminate_result, "{0}") != null and std.mem.indexOf(u8, item.terminate_result, "{1}") != null);
        try std.testing.expect(std.mem.indexOf(u8, item.memory_protection_tree, "{3}") != null);
        try std.testing.expect(std.mem.indexOf(u8, item.update_available, "{0}") != null);
        try std.testing.expect(std.mem.indexOf(u8, item.access_request, "{1}") != null);
    }
}

test "language selection falls back to the shared fallback language" {
    try std.testing.expectEqualStrings("en-US", select("EN-us").language);
    try std.testing.expectEqualStrings(catalog.fallback_language, select("xx-YY").language);
    try std.testing.expectEqualStrings(catalog.fallback_language, select("").language);
}

test "balloon substitutes placeholders and truncates on code point boundaries" {
    const value = balloon(.warning, "T", "{1} of {0}, {9} {x}", &.{ "a", "b" });
    try std.testing.expectEqual(Icon.warning, value.icon);
    const expected = std.unicode.utf8ToUtf16LeStringLiteral("b of a,  {x}");
    try std.testing.expectEqualSlices(u16, expected, value.bodySlice());
    try std.testing.expectEqual(@as(u16, 0), value.body[value.body_len]);
    const long = "\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}\u{1F600}";
    const clipped = balloon(.info, long, "", &.{});
    try std.testing.expectEqual(@as(usize, 62), clipped.title_len);
    try std.testing.expect(clipped.title[61] >= 0xdc00 and clipped.title[61] <= 0xdfff);
}
