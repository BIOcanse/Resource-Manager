// Portions adapted from .NET runtime v10.0.0, Copyright (c) .NET Foundation and Contributors.
// Source: https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Enumeration/FileSystemName.cs
// MIT License: Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal in the
// Software without restriction, including without limitation the rights to use, copy,
// modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,
// and to permit persons to whom the Software is furnished to do so, subject to the
// following conditions: The above copyright notice and this permission notice shall
// be included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
// PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//! Win32 wildcard compatibility, based on .NET runtime v10.0.0 FileSystemName.cs (MIT).
//! https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Enumeration/FileSystemName.cs
//! Translates the same DOS operators, then uses a bounded iterative two-row automaton.
const std = @import("std");
const A = std.mem.Allocator;

fn folded(ch: u16) u16 {
    const upper = switch (@import("ordinal.zig").profile) {
        0 => @embedFile("dotnet-char-upper.bin"),
        1 => @embedFile("dotnet-char-upper.icu.bin"),
        2 => @embedFile("dotnet-char-upper.invariant.bin"),
        else => unreachable,
    };
    const index = @as(usize, ch) * 2;
    return @as(u16, upper[index]) | (@as(u16, upper[index + 1]) << 8);
}
pub fn translate(a: A, input: []const u16) ![]const u16 {
    if (input.len == 0 or std.mem.eql(u16, input, &.{'*'}) or std.mem.eql(u16, input, &.{ '*', '.', '*' })) return a.dupe(u16, &.{'*'});
    const output = try a.alloc(u16, input.len);
    var used: usize = 0;
    var i: usize = 0;
    while (i < input.len) : (i += 1) {
        const ch = input[i];
        if (ch == '.' and i > 0 and i == input.len - 1 and input[i - 1] == '*') {
            output[used - 1] = '<';
            continue;
        }
        output[used] = if (ch == '?') '>' else if (ch == '.' and i + 1 < input.len and (input[i + 1] == '?' or input[i + 1] == '*')) '"' else ch;
        used += 1;
    }
    return output[0..used];
}
pub fn matches(a: A, expression: []const u16, name: []const u16) !bool {
    if (expression.len == 0 or name.len == 0) return false;
    if (expression[0] == '*' and std.mem.indexOfAny(u16, expression[1..], &.{ '"', '<', '>', '*', '?' }) == null) {
        const suffix = expression[1..];
        return name.len >= suffix.len and @import("ordinal.zig").equal(name[name.len - suffix.len ..], suffix, @import("ordinal.zig").profile);
    }
    const storage = try a.alloc(bool, (name.len + 1) * 2);
    defer a.free(storage);
    var previous = storage[0 .. name.len + 1];
    var next = storage[name.len + 1 ..];
    @memset(previous, false);
    previous[0] = true;
    var last_dot: ?usize = null;
    var j: usize = 0;
    while (j < name.len) : (j += 1) if (name[j] == '.') {
        last_dot = j;
    };
    var i: usize = 0;
    while (i < expression.len) : (i += 1) {
        @memset(next, false);
        var ch = expression[i];
        var escaped = false;
        if (ch == '\\') {
            i += 1;
            if (i == expression.len) return previous[name.len];
            ch = expression[i];
            escaped = true;
        }
        j = 0;
        while (j <= name.len) : (j += 1) {
            if (!escaped and (ch == '*' or ch == '<')) {
                if (previous[j]) next[j] = true;
                if (j > 0 and next[j - 1] and (ch == '*' or last_dot == null or j - 1 != last_dot.?)) next[j] = true;
            } else if (!escaped and ch == '>') {
                if (j == name.len or name[j] == '.') {
                    if (previous[j]) next[j] = true;
                }
                if (j > 0 and name[j - 1] != '.' and previous[j - 1]) next[j] = true;
            } else if (!escaped and ch == '"') {
                if (j == name.len and previous[j]) next[j] = true;
                if (j > 0 and name[j - 1] == '.' and previous[j - 1]) next[j] = true;
            } else if (j > 0 and previous[j - 1] and ((!escaped and ch == '?') or folded(ch) == folded(name[j - 1]))) next[j] = true;
        }
        const swap = previous;
        previous = next;
        next = swap;
    }
    return previous[name.len];
}
