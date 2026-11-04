const std = @import("std");
const m = @import("model.zig");
const rules = @import("rules.zig");
const Node = struct { name: []const u8, attributes: []const u8, text: []const u8 = "", parent: ?usize };
/// Small XML reader for rendered Windows Event XML. Parses element boundaries,
/// quoted attributes, nesting and XML entities; rejects DTD/external entities.
const Document = struct {
    allocator: std.mem.Allocator,
    nodes: std.ArrayList(Node) = .empty,
    fn init(allocator: std.mem.Allocator, xml: []const u8) !Document {
        var d: Document = .{ .allocator = allocator };
        errdefer d.nodes.deinit(allocator);
        var stack: std.ArrayList(usize) = .empty;
        defer stack.deinit(allocator);
        var pos: usize = 0;
        while (pos < xml.len) {
            if (xml[pos] != '<') {
                const end = std.mem.findScalarPos(u8, xml, pos, '<') orelse xml.len;
                if (stack.items.len != 0) {
                    const n = &d.nodes.items[stack.items[stack.items.len - 1]];
                    if (std.mem.trim(u8, xml[pos..end], " \t\r\n").len != 0) {
                        if (n.text.len != 0) return error.MixedXmlContent;
                        n.text = xml[pos..end];
                    }
                } else if (std.mem.trim(u8, xml[pos..end], " \t\r\n").len != 0) return error.InvalidXml;
                pos = end;
                continue;
            }
            if (std.mem.startsWith(u8, xml[pos..], "<?")) {
                pos = (std.mem.findPos(u8, xml, pos, "?>") orelse return error.InvalidXml) + 2;
                continue;
            }
            if (std.mem.startsWith(u8, xml[pos..], "<!--")) {
                pos = (std.mem.findPos(u8, xml, pos, "-->") orelse return error.InvalidXml) + 3;
                continue;
            }
            if (pos + 1 >= xml.len or xml[pos + 1] == '!') return error.UnsupportedXml;
            var end = pos + 1;
            var quote: ?u8 = null;
            while (end < xml.len) : (end += 1) {
                const c = xml[end];
                if (quote) |q| {
                    if (c == q) quote = null;
                } else if (c == '\'' or c == '"') {
                    quote = c;
                } else if (c == '>') break;
            }
            if (end == xml.len) return error.InvalidXml;
            var tag = std.mem.trim(u8, xml[pos + 1 .. end], " \t\r\n");
            if (tag.len == 0) return error.InvalidXml;
            if (tag[0] == '/') {
                if (stack.items.len == 0) return error.InvalidXml;
                const index = stack.pop().?;
                if (!std.mem.eql(u8, std.mem.trim(u8, tag[1..], " \t\r\n"), d.nodes.items[index].name)) return error.InvalidXml;
            } else {
                const closed = tag[tag.len - 1] == '/';
                if (closed) tag = std.mem.trimEnd(u8, tag[0 .. tag.len - 1], " \t\r\n");
                const split = std.mem.findAny(u8, tag, " \t\r\n") orelse tag.len;
                if (split == 0) return error.InvalidXml;
                const parent: ?usize = if (stack.items.len == 0) null else stack.items[stack.items.len - 1];
                if (parent == null and d.nodes.items.len != 0) return error.InvalidXml;
                try d.nodes.append(allocator, .{ .name = tag[0..split], .attributes = tag[split..], .parent = parent });
                if (!closed) try stack.append(allocator, d.nodes.items.len - 1);
            }
            pos = end + 1;
        }
        if (stack.items.len != 0 or d.nodes.items.len == 0 or !std.mem.eql(u8, d.nodes.items[0].name, "Event")) return error.InvalidXml;
        return d;
    }
    fn deinit(self: *Document) void {
        self.nodes.deinit(self.allocator);
    }
    fn child(self: Document, parent: usize, name: []const u8) ?usize {
        for (self.nodes.items, 0..) |n, i| if (n.parent == parent and std.mem.eql(u8, n.name, name)) return i;
        return null;
    }
    fn value(self: Document, parent: usize, name: []const u8) !?[]const u8 {
        const index = self.child(parent, name) orelse return null;
        return try decode(self.allocator, self.nodes.items[index].text);
    }
    fn attribute(self: Document, index: usize, name: []const u8) !?[]const u8 {
        var rest = self.nodes.items[index].attributes;
        while (true) {
            rest = std.mem.trimStart(u8, rest, " \t\r\n");
            if (rest.len == 0) return null;
            const split = std.mem.findAny(u8, rest, "= \t\r\n") orelse return error.InvalidXml;
            const key = rest[0..split];
            rest = std.mem.trimStart(u8, rest[split..], " \t\r\n");
            if (rest.len < 3 or rest[0] != '=') return error.InvalidXml;
            rest = std.mem.trimStart(u8, rest[1..], " \t\r\n");
            if (rest.len == 0 or (rest[0] != '\'' and rest[0] != '"')) return error.InvalidXml;
            const end = std.mem.findScalarPos(u8, rest, 1, rest[0]) orelse return error.InvalidXml;
            if (std.mem.eql(u8, key, name)) return try decode(self.allocator, rest[1..end]);
            rest = rest[end + 1 ..];
        }
    }
    fn data(self: Document, name: ?[]const u8, position: usize) !?[]const u8 {
        const event_data = self.child(0, "EventData") orelse return null;
        var count: usize = 0;
        for (self.nodes.items, 0..) |n, i| {
            if (n.parent != event_data or !std.mem.eql(u8, n.name, "Data")) continue;
            if (name) |key| {
                if (try self.attribute(i, "Name")) |actual| if (std.mem.eql(u8, actual, key)) return try decode(self.allocator, n.text);
            } else if (count == position) return try decode(self.allocator, n.text);
            count += 1;
        }
        return null;
    }
};
fn decode(a: std.mem.Allocator, raw: []const u8) ![]const u8 {
    if (std.mem.findScalar(u8, raw, '&') == null) return raw;
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(a);
    var i: usize = 0;
    while (i < raw.len) {
        if (raw[i] != '&') {
            try out.append(a, raw[i]);
            i += 1;
            continue;
        }
        const end = std.mem.findScalarPos(u8, raw, i, ';') orelse return error.InvalidEntity;
        const entity = raw[i + 1 .. end];
        if (std.mem.eql(u8, entity, "amp")) try out.append(a, '&') else if (std.mem.eql(u8, entity, "lt")) try out.append(a, '<') else if (std.mem.eql(u8, entity, "gt")) try out.append(a, '>') else if (std.mem.eql(u8, entity, "quot")) try out.append(a, '"') else if (std.mem.eql(u8, entity, "apos")) try out.append(a, '\'') else if (entity.len > 1 and entity[0] == '#') {
            const hex = entity.len > 2 and entity[1] == 'x';
            const cp = try std.fmt.parseInt(u21, entity[if (hex) @as(usize, 2) else 1..], if (hex) 16 else 10);
            var bytes: [4]u8 = undefined;
            const len = try std.unicode.utf8Encode(cp, &bytes);
            try out.appendSlice(a, bytes[0..len]);
        } else return error.InvalidEntity;
        i = end + 1;
    }
    return out.toOwnedSlice(a);
}
fn number(comptime T: type, text: []const u8) !T {
    const raw = std.mem.trim(u8, text, " \t\r\n");
    return std.fmt.parseInt(T, raw, if (std.mem.startsWith(u8, raw, "0x")) 0 else 10);
}
fn dateMs(year: i64, month: i64, day: i64, hour: i64, minute: i64, second: i64, millis: i64) !i64 {
    if (year < 1601 or year > 9999 or month < 1 or month > 12 or day < 1 or day > 31 or hour < 0 or hour > 23 or minute < 0 or minute > 59 or second < 0 or second > 59 or millis < 0 or millis > 999) return error.InvalidTime;
    const y = year - @as(i64, if (month <= 2) 1 else 0);
    const era = @divFloor(y, 400);
    const yoe = y - era * 400;
    const mp = month + @as(i64, if (month > 2) -3 else 9);
    const doy = @divFloor(153 * mp + 2, 5) + day - 1;
    const days = era * 146097 + yoe * 365 + @divFloor(yoe, 4) - @divFloor(yoe, 100) + doy - 719468;
    return ((days * 24 + hour) * 60 + minute) * 60000 + second * 1000 + millis;
}
pub fn timestampMs(s: []const u8) !i64 {
    if (s.len < 20 or s[4] != '-' or s[7] != '-' or s[10] != 'T' or s[13] != ':' or s[16] != ':' or s[s.len - 1] != 'Z') return error.InvalidTime;
    var ms: i64 = 0;
    if (s.len > 20) {
        if (s[19] != '.' or s.len < 22) return error.InvalidTime;
        const fraction = s[20 .. s.len - 1];
        for (fraction) |c| if (!std.ascii.isDigit(c)) return error.InvalidTime;
        var scale: i64 = 100;
        for (fraction[0..@min(fraction.len, 3)]) |c| {
            ms += (c - '0') * scale;
            scale = @divTrunc(scale, 10);
        }
    }
    return dateMs(try number(i64, s[0..4]), try number(i64, s[5..7]), try number(i64, s[8..10]), try number(i64, s[11..13]), try number(i64, s[14..16]), try number(i64, s[17..19]), ms);
}
fn shutdownTime(d: Document) !?i64 {
    const ed = d.child(0, "EventData") orelse return null;
    const raw = (try d.value(ed, "Binary")) orelse return null;
    // EventLog 6008 payload: local SYSTEMTIME followed by UTC SYSTEMTIME.
    // Parse the UTC structure, avoiding localized Data date/time strings entirely.
    if (raw.len < 64) return null;
    var words: [8]i64 = undefined;
    for (&words, 0..) |*word, i| {
        const p = 32 + i * 4;
        const low = std.fmt.parseInt(u8, raw[p .. p + 2], 16) catch return null;
        const high = std.fmt.parseInt(u8, raw[p + 2 .. p + 4], 16) catch return null;
        word.* = @as(i64, high) * 256 + low;
    }
    return dateMs(words[0], words[1], words[3], words[4], words[5], words[6], words[7]) catch null;
}
/// Caller supplies an arena/scratch allocator whose lifetime covers commit.
pub fn parse(a: std.mem.Allocator, table: []const rules.Rule, raw: []const u8, delivery: m.Delivery) !?m.Event {
    var d = try Document.init(a, raw);
    defer d.deinit();
    const system = d.child(0, "System") orelse return error.MissingSystem;
    const provider_node = d.child(system, "Provider") orelse return error.MissingProvider;
    const provider = (try d.attribute(provider_node, "Name")) orelse return error.MissingProvider;
    const channel_text = (try d.value(system, "Channel")) orelse return error.MissingChannel;
    const channel: m.Channel = if (std.mem.eql(u8, channel_text, "System")) .system else if (std.mem.eql(u8, channel_text, "Application")) .application else return null;
    const id = try number(u32, (try d.value(system, "EventID")) orelse return error.MissingEventId);
    const rule = rules.match(table, channel, provider, id) orelse return null;
    const time = d.child(system, "TimeCreated") orelse return error.MissingTimestamp;
    const utc = (try d.attribute(time, "SystemTime")) orelse return error.MissingTimestamp;
    var target: []const u8 = "machine";
    switch (rule.target_kind) {
        .machine, .hardware => {},
        .desktop_process => {
            const app = (try d.data("AppName", 0)) orelse (try d.data(null, 0)) orelse return null;
            const base = app[(if (std.mem.findLastAny(u8, app, "\\/")) |i| i + 1 else 0)..];
            if (std.ascii.eqlIgnoreCase(base, "dwm.exe")) target = "dwm.exe" else if (std.ascii.eqlIgnoreCase(base, "explorer.exe")) target = "explorer.exe" else return null;
        },
        .physical_disk, .storage_port, .gpu_device => target = (try d.data("DeviceName", 0)) orelse (try d.data(null, 0)) orelse "unknown",
        // Microsoft-Windows-Ntfs 140 starts with VolumeIdLength, not a target.
        .volume => target = (try d.data("VolumeName", 0)) orelse (try d.data("VolumeId", 0)) orelse (try d.data("DeviceName", 0)) orelse (try d.data(null, 0)) orelse "unknown",
    }
    if (target.len == 0) target = "unknown";
    var event: m.Event = .{ .category = rule.category, .target_kind = rule.target_kind, .target = target, .channel = channel, .provider = provider, .event_id = id, .record_id = try number(u64, (try d.value(system, "EventRecordID")) orelse return error.MissingRecordId), .timestamp_ms = try timestampMs(utc), .timestamp_utc = utc, .raw_xml = raw, .delivery = delivery };
    if (event.category == .unexpected_shutdown) {
        if (id == 6008) event.actual_shutdown_ms = try shutdownTime(d);
        if (id == 41) if (try d.data("BugcheckCode", 0)) |code| {
            const value = number(u32, code) catch 0;
            if (value != 0) event.bugcheck_code = value;
        };
        if (id == 1001) {
            if (try d.data("param1", 0)) |code| {
                const end = std.mem.findAny(u8, code, " (\t") orelse code.len;
                event.bugcheck_code = number(u32, code[0..end]) catch null;
            }
            event.dump_path = try d.data("param2", 0);
        }
    }
    return event;
}
