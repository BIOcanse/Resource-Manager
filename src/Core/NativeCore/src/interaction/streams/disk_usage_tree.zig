//! RMDU v3 successor of DiskUsageTreeBinaryWriter, over the EXISTING published
//! DiskUsageTreeRegionLayout v1. No scan/current store, allocator or lifetime
//! owner. A run-local Cursor borrows ONE immutable accepted region and caller
//! scratch. Its owner must retain that result independently of current/plan
//! replacement until completion/cancel. Never keep a source lease in a field.
//! next() is synchronous. Send only its final output, retaining that output
//! until C20 completion before calling next again. No source pointer is sent.
const std = @import("std");

pub const Error = error{ InvalidRegion, InvalidNode, InvalidLink, InvalidBounds, InvalidCursor, Budget, ScratchTooSmall, Overflow, EmptyWindow, AliasedOutput, Cancelled, Failed };
pub const Bounds = struct { max_nodes: usize, max_bytes: usize };
pub const View = struct {
    bytes: []const u8,
    count: usize,
    columns: [14]usize,

    pub fn open(bytes: []const u8) Error!View {
        if (bytes.len < 16 or get(u32, bytes, 0) != 1) return error.InvalidRegion;
        const count = get(i32, bytes, 4);
        const roots = get(i32, bytes, 8);
        const names = get(i32, bytes, 12);
        if (count < 0 or roots < 0 or roots > count or names < 0) return error.InvalidRegion;
        var self: View = .{ .bytes = bytes, .count = @intCast(count), .columns = undefined };
        var end: usize = 16;
        const widths = [_]usize{ 4, 4, 4, 8, 8, 4, 4, 4, 1, 8, 8, 2, 4, 2 };
        for (widths, 0..) |width, index| {
            const length: usize = if (index == 12) @intCast(roots) else if (index == 13) @intCast(names) else self.count;
            end = try add(end, width - 1);
            end = end / width * width;
            self.columns[index] = end;
            end = try add(end, try mul(width, length));
        }
        end = (try add(end, 7)) / 8 * 8;
        if (end > bytes.len) return error.InvalidRegion;
        return self;
    }
    fn valid(self: View, node: i32) Error!usize {
        if (node < 0 or @as(usize, @intCast(node)) >= self.count) return error.InvalidNode;
        return @intCast(node);
    }
    fn int(self: View, col: usize, node: i32) Error!i32 {
        return get(i32, self.bytes, self.columns[col] + 4 * try self.valid(node));
    }
    fn size(self: View, node: i32) Error!f64 {
        const value = get(i64, self.bytes, self.columns[3] + 8 * try self.valid(node));
        return @floatFromInt(value);
    }
    fn directory(self: View, node: i32) Error!bool {
        return self.bytes[self.columns[8] + try self.valid(node)] & 1 != 0;
    }
    fn name(self: View, node: i32) Error![]const u8 {
        const offset = try self.int(5, node);
        const length = try self.int(6, node);
        if (offset < 0 or length < 0) return error.InvalidRegion;
        const start = try add(self.columns[13], try mul(@intCast(offset), 2));
        const end = try add(start, try mul(@intCast(length), 2));
        const characters: usize = @intCast(get(i32, self.bytes, 12));
        if (end > self.columns[13] + characters * 2) return error.InvalidRegion;
        return self.bytes[start..end];
    }
    fn parent(self: View, node: i32) Error!i32 {
        const p = try self.int(0, node);
        if (p < -1 or p >= node) return error.InvalidLink;
        return p;
    }
    fn child(self: View, p: i32, c: i32) Error!void {
        _ = try self.valid(c);
        if (c <= p or try self.int(0, c) != p) return error.InvalidLink;
    }
    fn successor(self: View, start: i32, root: i32) Error!i32 {
        const c = try self.int(1, start);
        if (c >= 0) {
            try self.child(start, c);
            return c;
        }
        var node = start;
        while (node != root) {
            const sibling = try self.int(2, node);
            const p = try self.parent(node);
            if (p < root) return error.InvalidLink;
            if (sibling >= 0) {
                try self.child(p, sibling);
                if (sibling <= node) return error.InvalidLink;
                return sibling;
            }
            node = p;
        }
        return -1;
    }
    fn childTotal(self: View, node: i32) Error!f64 {
        if (!try self.directory(node)) return 0;
        var total: f64 = 0;
        var previous = node;
        var c = try self.int(1, node);
        while (c >= 0) {
            if (c <= previous) return error.InvalidLink;
            try self.child(node, c);
            total += @max(@as(f64, 0), try self.size(c));
            previous = c;
            c = try self.int(2, c);
        }
        return total;
    }
};

pub const Cursor = struct {
    view: View,
    nodes: []const i32,
    path: []const i32, // query root -> ancestors, reversed only while emitting
    header: [64]u8,
    length: usize,
    next_cursor: i32,
    phase: u8 = 0,
    index: usize = 0,
    character: usize = 0,
    path_separator: bool = false,
    pending: [8]u8 = undefined,
    pending_start: usize = 0,
    pending_end: usize = 0,
    terminal: ?Error = null,

    /// Scratch belongs in the run's declared Ledger area; capacity failure is
    /// explicit. Only page IDs and root ancestry are retained, never wire bytes.
    pub fn begin(view: View, root: i32, continuation: ?i32, bounds: Bounds, node_scratch: []i32, path_scratch: []i32) Error!Cursor {
        const node_bytes = std.mem.sliceAsBytes(node_scratch);
        const path_bytes_scratch = std.mem.sliceAsBytes(path_scratch);
        if (overlap(node_bytes, view.bytes) or overlap(path_bytes_scratch, view.bytes) or overlap(node_bytes, path_bytes_scratch)) return error.AliasedOutput;
        // These are the existing public endpoint's supported wire-page bounds.
        if (bounds.max_nodes < 2 or bounds.max_nodes > 400_000 or bounds.max_bytes < 1024 or bounds.max_bytes > 33_554_432) return error.InvalidBounds;
        _ = try view.valid(root);
        var first = continuation orelse root;
        _ = try view.valid(first);
        if (continuation != null and first == root) return error.InvalidCursor;
        var path_count: usize = 0;
        var path_bytes: usize = 0;
        var ancestor = root;
        while (ancestor >= 0) {
            if (path_count == path_scratch.len) return error.ScratchTooSmall;
            path_scratch[path_count] = ancestor;
            const name = try view.name(ancestor);
            path_bytes = try add(path_bytes, utf8Length(name));
            if (path_count != 0 and name.len != 0 and get(u16, name, name.len - 2) != '\\') path_bytes = try add(path_bytes, 1);
            if (path_bytes > bounds.max_bytes - 64) return error.Budget;
            path_count += 1;
            ancestor = try view.parent(ancestor);
        }
        var count: usize = 0;
        var names: usize = 0;
        if (continuation != null) {
            ancestor = first;
            const cursor_bytes = utf8Length(try view.name(first));
            while (ancestor != root) {
                ancestor = try view.parent(ancestor);
                if (ancestor < root) return error.InvalidCursor;
                if (count >= bounds.max_nodes - 1 or try required(count + 2, try add(names, try add(utf8Length(try view.name(ancestor)), cursor_bytes)), path_bytes) > bounds.max_bytes) return error.Budget;
                if (count == node_scratch.len) return error.ScratchTooSmall;
                node_scratch[count] = ancestor;
                names = try add(names, utf8Length(try view.name(ancestor)));
                count += 1;
            }
            std.mem.reverse(i32, node_scratch[0..count]);
        }
        var accepted: usize = 0;
        while (first >= 0) {
            const n = utf8Length(try view.name(first));
            if (count >= bounds.max_nodes or try required(count + 1, try add(names, n), path_bytes) > bounds.max_bytes) {
                if (accepted == 0) return error.Budget;
                break;
            }
            if (count == node_scratch.len) return error.ScratchTooSmall;
            node_scratch[count] = first;
            names += n;
            count += 1;
            accepted += 1;
            first = try view.successor(first, root);
        }
        var self: Cursor = .{ .view = view, .nodes = node_scratch[0..count], .path = path_scratch[0..path_count], .header = @splat(0), .length = 64 + 41 * count + padding(count) + names + path_bytes, .next_cursor = first };
        put(u32, &self.header, 0, 0x55444d52);
        put(u32, &self.header, 4, 3);
        put(u32, &self.header, 8, @intCast(count));
        put(i32, &self.header, 12, root);
        put(i32, &self.header, 16, first);
        put(u64, &self.header, 24, @bitCast(try view.size(root)));
        put(u32, &self.header, 60, @intCast(path_bytes));
        return self;
    }

    pub fn empty() Cursor {
        var self: Cursor = .{ .view = .{ .bytes = &.{}, .count = 0, .columns = @splat(0) }, .nodes = &.{}, .path = &.{}, .header = @splat(0), .length = 64, .next_cursor = -1 };
        put(u32, &self.header, 0, 0x55444d52);
        put(u32, &self.header, 4, 3);
        put(i32, &self.header, 12, -1);
        put(i32, &self.header, 16, -1);
        return self;
    }
    pub fn cancel(self: *Cursor) void {
        self.terminal = error.Cancelled;
    }
    pub fn done(self: *const Cursor) bool {
        return self.phase == 13 and self.pending_start == self.pending_end and self.terminal == null;
    }

    /// Returns only complete final bytes; a one-byte window is valid, including
    /// inside f64/i32/UTF8. Failure is terminal; callers close a begun response.
    pub fn next(self: *Cursor, output: []u8) Error![]const u8 {
        if (self.terminal) |err| return err;
        if (output.len == 0) return error.EmptyWindow;
        if (overlap(output, self.view.bytes) or overlap(output, std.mem.sliceAsBytes(self.nodes)) or overlap(output, std.mem.sliceAsBytes(self.path)) or overlap(output, std.mem.asBytes(self))) return error.AliasedOutput;
        return self.fill(output) catch |err| {
            self.terminal = error.Failed;
            return err;
        };
    }
    fn fill(self: *Cursor, output: []u8) Error![]const u8 {
        var used: usize = 0;
        while (used < output.len) {
            if (self.pending_start < self.pending_end) {
                const n = @min(output.len - used, self.pending_end - self.pending_start);
                @memcpy(output[used..][0..n], self.pending[self.pending_start..][0..n]);
                self.pending_start += n;
                used += n;
                continue;
            }
            if (self.phase == 13) break;
            try self.advance();
        }
        return output[0..used];
    }
    fn scalar(self: *Cursor, comptime T: type, value: T) void {
        put(T, &self.pending, 0, value);
        self.pending_start = 0;
        self.pending_end = @sizeOf(T);
    }
    fn advance(self: *Cursor) Error!void {
        if (self.phase == 0) {
            if (self.index < 64) {
                self.scalar(u8, self.header[self.index]);
                self.index += 1;
                return;
            }
        } else if (self.phase <= 8) {
            if (self.index < self.nodes.len) {
                const node = self.nodes[self.index];
                switch (self.phase) {
                    1 => self.scalar(u64, @bitCast(try self.view.size(node))),
                    2 => self.scalar(u64, @bitCast(try self.view.childTotal(node))),
                    3 => self.scalar(i32, node),
                    4 => self.scalar(i32, try self.view.int(0, node)),
                    5 => self.scalar(i32, try self.view.int(7, node)),
                    6 => self.scalar(i32, try self.view.int(1, node)),
                    7 => self.scalar(i32, try self.view.int(2, node)),
                    8 => self.scalar(u8, @intFromBool(try self.view.directory(node))),
                    else => unreachable,
                }
                self.index += 1;
                return;
            }
        } else if (self.phase == 9) {
            if (self.index < padding(self.nodes.len)) {
                self.scalar(u8, 0);
                self.index += 1;
                return;
            }
        } else if (self.phase == 10) {
            if (self.index < self.nodes.len) {
                self.scalar(u32, @intCast(utf8Length(try self.view.name(self.nodes[self.index]))));
                self.index += 1;
                return;
            }
        } else if (self.phase == 11 or self.phase == 12) {
            const path = self.phase == 12;
            const ids = if (path) self.path else self.nodes;
            if (self.index < ids.len) {
                const node = ids[if (path) ids.len - 1 - self.index else self.index];
                const name = try self.view.name(node);
                if (self.character < name.len) {
                    const cp = codepoint(name, &self.character);
                    self.pending_end = std.unicode.utf8Encode(cp, self.pending[0..4]) catch unreachable;
                    self.pending_start = 0;
                    return;
                }
                if (path and self.index + 1 < ids.len and name.len != 0 and get(u16, name, name.len - 2) != '\\' and !self.path_separator) {
                    self.path_separator = true;
                    self.scalar(u8, '\\');
                    return;
                }
                self.index += 1;
                self.character = 0;
                self.path_separator = false;
                return;
            }
        }
        self.phase += 1;
        self.index = 0;
    }
};

fn get(comptime T: type, bytes: []const u8, offset: usize) T {
    return std.mem.readInt(T, bytes[offset..][0..@sizeOf(T)], .little);
}
fn put(comptime T: type, bytes: []u8, offset: usize, value: T) void {
    std.mem.writeInt(T, bytes[offset..][0..@sizeOf(T)], value, .little);
}
fn add(a: usize, b: usize) Error!usize {
    return std.math.add(usize, a, b) catch error.Overflow;
}
fn mul(a: usize, b: usize) Error!usize {
    return std.math.mul(usize, a, b) catch error.Overflow;
}
fn padding(count: usize) usize {
    return (4 - 37 * count % 4) % 4;
}
fn required(count: usize, names: usize, path: usize) Error!usize {
    return add(try add(67, try mul(41, count)), try add(names, path));
}
fn codepoint(bytes: []const u8, offset: *usize) u21 {
    const first = get(u16, bytes, offset.*);
    offset.* += 2;
    if (first >= 0xd800 and first <= 0xdbff) {
        if (offset.* < bytes.len) {
            const second = get(u16, bytes, offset.*);
            if (second >= 0xdc00 and second <= 0xdfff) {
                offset.* += 2;
                return 0x10000 + (@as(u21, first - 0xd800) << 10) + (second - 0xdc00);
            }
        }
        return 0xfffd;
    }
    return if (first >= 0xdc00 and first <= 0xdfff) 0xfffd else first;
}
fn utf8Length(bytes: []const u8) usize {
    var offset: usize = 0;
    var length: usize = 0;
    while (offset < bytes.len) {
        length += std.unicode.utf8CodepointSequenceLength(codepoint(bytes, &offset)) catch unreachable;
    }
    return length;
}
fn overlap(a: []const u8, b: []const u8) bool {
    if (a.len == 0 or b.len == 0) return false;
    const x = @intFromPtr(a.ptr);
    const y = @intFromPtr(b.ptr);
    return if (x <= y) y - x < a.len else x - y < b.len;
}
