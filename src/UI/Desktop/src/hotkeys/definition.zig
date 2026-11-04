//! Editable hotkey definition and chord matcher, ported from the C#
//! EditableHotkeyDefinition / EditableHotkeyMatcher with fixed-size state.
const std = @import("std");

pub const encoding_length = 7;
pub const max_keys = 4;

pub const Definition = struct {
    keys: [max_keys]u8 = @splat(0),
    count: usize = 0,
    /// relations[i] links keys[i] and keys[i + 1]; 1 = must be pressed in order.
    relations: [max_keys - 1]u8 = @splat(0),

    /// Keys sit in slots 0, 2, 4, 6 and relations in 1, 3, 5. Keys outside
    /// 1..255 and duplicates are skipped, exactly like the C# definition.
    pub fn create(slots: []const i32) Definition {
        const source = slots[0..@min(slots.len, encoding_length)];
        var value: Definition = .{};
        var key_index: usize = 0;
        while (key_index < max_keys) : (key_index += 1) {
            const slot = key_index * 2;
            const virtual_key = if (slot < source.len) source[slot] else 0;
            if (virtual_key <= 0 or virtual_key > 255 or value.contains(@intCast(virtual_key))) continue;
            if (value.count > 0) {
                const relation_slot = slot - 1;
                value.relations[value.count - 1] = if (relation_slot < source.len and source[relation_slot] == 1) 1 else 0;
            }
            value.keys[value.count] = @intCast(virtual_key);
            value.count += 1;
        }
        return value;
    }
    pub fn encoding(self: *const Definition) [encoding_length]i32 {
        var out: [encoding_length]i32 = @splat(0);
        for (0..self.count) |index| {
            out[index * 2] = self.keys[index];
            if (index > 0) out[index * 2 - 1] = self.relations[index - 1];
        }
        return out;
    }
    pub fn eql(self: *const Definition, other: *const Definition) bool {
        return std.mem.eql(i32, &self.encoding(), &other.encoding());
    }
    pub fn isEmpty(self: *const Definition) bool {
        return self.count == 0;
    }
    pub fn contains(self: *const Definition, key: u8) bool {
        return std.mem.indexOfScalar(u8, self.keys[0..self.count], key) != null;
    }
    pub fn usesKeyboard(self: *const Definition) bool {
        for (self.keys[0..self.count]) |key| if (!isMouseButton(key)) return true;
        return false;
    }
    pub fn usesMouse(self: *const Definition) bool {
        for (self.keys[0..self.count]) |key| if (isMouseButton(key)) return true;
        return false;
    }
    /// At least two keys, one approved modifier (Ctrl, Alt, Win) and one non-modifier.
    pub fn isSafe(self: *const Definition) bool {
        var approved = false;
        var plain = false;
        for (self.keys[0..self.count]) |key| {
            approved = approved or isApprovedModifier(key);
            plain = plain or !isModifier(key);
        }
        return self.count >= 2 and approved and plain;
    }
};

pub fn isMouseButton(key: u8) bool {
    return switch (key) {
        1, 2, 4, 5, 6 => true,
        else => false,
    };
}
fn isApprovedModifier(key: u8) bool {
    return switch (key) {
        17, 18, 91, 92, 162, 163, 164, 165 => true,
        else => false,
    };
}
fn isModifier(key: u8) bool {
    return switch (key) {
        16, 17, 18, 91, 92, 160...165 => true,
        else => false,
    };
}

pub const Matcher = struct {
    definition: Definition,
    order: [max_keys]u64 = @splat(0),
    next_order: u64 = 0,
    latched: bool = false,

    pub fn init(definition: Definition) Matcher {
        return .{ .definition = definition };
    }
    /// True exactly once per full chord press; the hook swallows that event.
    pub fn keyDown(self: *Matcher, virtual_key: u32) bool {
        const index = self.resolve(virtual_key) orelse return false;
        if (self.order[index] == 0) {
            self.next_order += 1;
            self.order[index] = self.next_order;
        }
        if (self.latched or self.downCount() != self.definition.count or !self.orderedRelationsMatch()) return false;
        self.latched = true;
        return true;
    }
    pub fn keyUp(self: *Matcher, virtual_key: u32) void {
        const index = self.resolve(virtual_key) orelse return;
        self.order[index] = 0;
        self.latched = false;
        if (self.downCount() == 0) self.next_order = 0;
    }
    fn downCount(self: *const Matcher) usize {
        var count: usize = 0;
        for (self.order[0..self.definition.count]) |value| count += @intFromBool(value != 0);
        return count;
    }
    fn orderedRelationsMatch(self: *const Matcher) bool {
        var index: usize = 1;
        while (index < self.definition.count) : (index += 1) {
            if (self.definition.relations[index - 1] == 1 and self.order[index - 1] >= self.order[index]) return false;
        }
        return true;
    }
    /// Configured key index; a left/right modifier resolves to its generic key
    /// (Shift 16, Ctrl 17, Alt 18) when only the generic key is configured.
    fn resolve(self: *const Matcher, virtual_key: u32) ?usize {
        if (virtual_key == 0 or virtual_key > 255) return null;
        const keys = self.definition.keys[0..self.definition.count];
        if (std.mem.indexOfScalar(u8, keys, @intCast(virtual_key))) |index| return index;
        const generic: u8 = switch (virtual_key) {
            160, 161 => 16,
            162, 163 => 17,
            164, 165 => 18,
            else => return null,
        };
        return std.mem.indexOfScalar(u8, keys, generic);
    }
};

test "definition normalizes, skips invalid and duplicate keys and keeps relations" {
    const value = Definition.create(&.{ 17, 1, 17, 0, 300, 1, 0x4b });
    try std.testing.expectEqual(@as(usize, 2), value.count);
    try std.testing.expectEqualSlices(u8, &.{ 17, 0x4b }, value.keys[0..2]);
    // The relation is the slot just before the accepted key.
    try std.testing.expectEqualSlices(i32, &.{ 17, 1, 0x4b, 0, 0, 0, 0 }, &value.encoding());
    try std.testing.expect(value.isSafe());
    try std.testing.expect(Definition.create(&.{}).isEmpty());
    try std.testing.expect(!Definition.create(&.{ 16, 0, 0x4b }).isSafe());
    try std.testing.expect(!Definition.create(&.{ 17, 0, 18 }).isSafe());
    try std.testing.expect(Definition.create(&.{ 162, 0, 2 }).usesMouse());
    try std.testing.expect(Definition.create(&.{ 162, 0, 2 }).usesKeyboard());
    try std.testing.expect(Definition.create(&.{ 17, 0, 0x4b }).eql(&Definition.create(&.{ 17, 0, 0x4b, 0, 0, 0, 0, 9 })));
}

test "matcher fires once per chord, honours order and generic modifiers" {
    var matcher = Matcher.init(Definition.create(&.{ 17, 1, 0x4b }));
    try std.testing.expect(!matcher.keyDown(162));
    try std.testing.expect(matcher.keyDown(0x4b));
    try std.testing.expect(!matcher.keyDown(0x4b));
    matcher.keyUp(0x4b);
    try std.testing.expect(matcher.keyDown(0x4b));
    matcher.keyUp(0x4b);
    matcher.keyUp(162);
    // Reversed order does not satisfy an ordered relation.
    try std.testing.expect(!matcher.keyDown(0x4b));
    try std.testing.expect(!matcher.keyDown(17));
    matcher.keyUp(17);
    matcher.keyUp(0x4b);
    try std.testing.expect(!matcher.keyDown(0x41));
    var unordered = Matcher.init(Definition.create(&.{ 17, 0, 0x4b }));
    try std.testing.expect(!unordered.keyDown(0x4b));
    try std.testing.expect(unordered.keyDown(163));
}
