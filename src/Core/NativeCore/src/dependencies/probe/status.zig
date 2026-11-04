//! Dependency state decisions. Inputs, scratch, record bytes and output all borrow one registered region.
const std = @import("std");
const win = @import("windows.zig");
const A = std.mem.Allocator;
pub const Slice = extern struct { offset: u32 = 0, count: u32 = 0 };
pub const Software = extern struct { name: Slice, publisher: Slice, directory: Slice };
pub const Request = extern struct {
    id: Slice,
    vendor: Slice,
    installer_name: Slice,
    install_name: Slice,
    dependency_root: Slice,
    installer_root: Slice,
    release_tag: Slice,
    release_asset: Slice,
    payload_tag: Slice,
    payload_asset: Slice,
    patterns: Slice,
    probes: Slice,
    products: Slice,
    payload_files: Slice,
    software: Slice,
    candidates: Slice,
    left: Slice,
    right: Slice,
    download_url: Slice,
    package_root: Slice,
    has_payload: u32,
    has_release: u32,
    explicit_candidates: u32,
    maximum_entries: u32,
    read_attempt_limit: u32,
    read_delay_ms: u32,
    unicode_profile: u32,
};
pub const Result = extern struct {
    status: u32 = 0,
    state: u32 = 0,
    flags: u32 = 0,
    message: u32 = 0,
    dependency_root: Slice = .{},
    installer_directory: Slice = .{},
    install_directory: Slice = .{},
    installer_path: Slice = .{},
    external_directory: Slice = .{},
    detection: Slice = .{},
    value: Slice = .{},
    warnings: u32 = 0,
    os_error: u32 = 0,
    source_kind: u32 = 0,
    reserved: u32 = 0,
    required_bytes: u64 = 0,
    read_attempts: u32 = 0,
    read_status: i32 = 0,
};
pub const Context = struct {
    region: []u8,
    input_length: usize,
    allocator: A,
    pub fn list(self: Context, comptime T: type, s: Slice) ![]const T {
        const bytes = std.math.mul(usize, s.count, @sizeOf(T)) catch return error.InvalidInput;
        if (s.offset > self.input_length or bytes > self.input_length - s.offset or s.offset % @alignOf(T) != 0) return error.InvalidInput;
        return @as([*]const T, @ptrCast(@alignCast(self.region.ptr + s.offset)))[0..s.count];
    }
    pub fn str(self: Context, s: Slice) ![]const u16 {
        return self.list(u16, s);
    }
    pub fn output(self: Context, value: []const u16) !Slice {
        const address = @intFromPtr(value.ptr);
        const base = @intFromPtr(self.region.ptr);
        if (address >= base and address <= base + self.region.len and value.len * 2 <= base + self.region.len - address)
            return .{ .offset = @intCast(address - base), .count = @intCast(value.len) };
        const copy = try self.allocator.dupe(u16, value);
        return .{ .offset = @intCast(@intFromPtr(copy.ptr) - base), .count = @intCast(copy.len) };
    }
};
// .NET 10.0.14 char.IsLetterOrDigit + char.ToLowerInvariant, all 65536 UTF-16 code units.
// Surrogates become spaces, preserving the previous char-based normalization, not Unicode scalar normalization.
const normalization = @embedFile("dotnet-char-normalization.bin");
pub fn normalizeMatch(a: A, input: []const u16) ![]const u16 {
    const output = try a.alloc(u16, input.len);
    var used: usize = 0;
    var space = false;
    var i: usize = 0;
    while (i < input.len) : (i += 1) {
        const table = switch (@import("ordinal.zig").profile) {
            0 => normalization,
            1 => @embedFile("dotnet-char-normalization.icu.bin"),
            2 => @embedFile("dotnet-char-normalization.invariant.bin"),
            else => unreachable,
        };
        const index = @as(usize, input[i]) * 2;
        const ch: u16 = @as(u16, table[index]) | (@as(u16, table[index + 1]) << 8);
        if (ch == ' ') {
            space = used != 0;
            continue;
        }
        if (space) {
            output[used] = ' ';
            used += 1;
            space = false;
        }
        output[used] = ch;
        used += 1;
    }
    return output[0..used];
}
fn installed(c: Context, r: Request, directory: []const u16) !bool {
    return installedInner(c, r, directory) catch |err| switch (err) {
        error.OutOfMemory => return err,
        else => false,
    };
}
fn installedInner(c: Context, r: Request, directory: []const u16) !bool {
    if (win.trim(directory).len == 0 or !try win.directory(c.allocator, directory)) return false;
    const probes = try c.list(Slice, r.probes);
    if (probes.len == 0) {
        var enumeration = try win.Enumeration.open(c.allocator, try win.combine(c.allocator, directory, &.{'*'})) orelse return false;
        defer enumeration.close();
        return try enumeration.next() != null;
    }
    var i: usize = 0;
    while (i < probes.len) : (i += 1) if (!try win.regular(c.allocator, try win.combine(c.allocator, directory, try c.str(probes[i])))) return false;
    return true;
}
fn resolveInstaller(c: Context, r: Request, directory: []const u16) !?[]const u16 {
    if (!try win.directory(c.allocator, directory)) return null;
    const patterns = try c.list(Slice, r.patterns);
    var i: usize = 0;
    while (i < patterns.len) : (i += 1) {
        const pattern = try c.str(patterns[i]);
        var split: usize = pattern.len;
        while (split > 0 and pattern[split - 1] != '\\' and pattern[split - 1] != '/') : (split -= 1) {}
        const search_directory = if (split == 0) directory else try win.combine(c.allocator, directory, pattern[0..split]);
        const file_pattern = pattern[split..];
        // Enumerate all long names, then use the .NET-compatible Win32 expression matcher.
        // FindFirstFile's own pattern matching can also match an 8.3 alias, which .NET does not.
        var enumeration = try win.Enumeration.open(c.allocator, try win.combine(c.allocator, search_directory, &.{'*'})) orelse continue;
        defer enumeration.close();
        var best: ?[]const u16 = null;
        var newest: u64 = 0;
        const expression = try @import("wildcard.zig").translate(c.allocator, file_pattern);
        while (try enumeration.next()) |data| {
            if (data.attributes & 0x10 != 0 or !try @import("wildcard.zig").matches(c.allocator, expression, win.nameOf(data))) continue;
            const stamp = win.timestamp(data);
            if (best == null or stamp > newest) {
                best = try c.allocator.dupe(u16, win.nameOf(data));
                newest = stamp;
            }
        }
        if (best) |name| return try win.combine(c.allocator, search_directory, name);
    }
    const exact = try win.combine(c.allocator, directory, try c.str(r.installer_name));
    return if (try win.file(c.allocator, exact)) exact else null;
}
fn matches(c: Context, r: Request, software: Software) !bool {
    const entry = try normalizeMatch(c.allocator, try c.str(software.name));
    const vendor = try normalizeMatch(c.allocator, try c.str(r.vendor));
    const publisher = try normalizeMatch(c.allocator, try c.str(software.publisher));
    const products = try c.list(Slice, r.products);
    var i: usize = 0;
    while (entry.len > 0 and i < products.len) : (i += 1) if (win.contains(entry, try normalizeMatch(c.allocator, try c.str(products[i])))) return true;
    return vendor.len > 0 and publisher.len > 0 and win.contains(publisher, vendor) and win.contains(entry, try normalizeMatch(c.allocator, try c.str(r.install_name)));
}
const External = struct { directory: []const u16, source: []const u16 };
fn external(c: Context, r: Request, managed: []const u16) !?External {
    const software = try c.list(Software, r.software);
    var i: usize = 0;
    while (i < software.len) : (i += 1) {
        const entry = software[i];
        if (!try matches(c, r, entry)) continue;
        const path = try win.normalize(c.allocator, try c.str(entry.directory)) orelse continue;
        if (try win.same(c.allocator, path, managed) or !try installed(c, r, path)) continue;
        const prefix = try win.text(c.allocator, "WindowsInstalledSoftware:");
        const name = try c.str(entry.name);
        const source = try c.allocator.alloc(u16, prefix.len + name.len);
        @memcpy(source[0..prefix.len], prefix);
        @memcpy(source[prefix.len..], name);
        return .{ .directory = path, .source = source };
    }
    if (r.explicit_candidates != 0) {
        const candidates = try c.list(Slice, r.candidates);
        i = 0;
        while (i < candidates.len) : (i += 1) {
            const path = try c.str(candidates[i]);
            if (try win.same(c.allocator, path, managed)) continue;
            if (try installed(c, r, path)) return .{ .directory = path, .source = try win.text(c.allocator, "CommonInstallRoot") };
        }
    } else {
        const roots = try win.commonRoots(c.allocator);
        var seen: std.ArrayList([]const u16) = .empty;
        i = 0;
        while (i < roots.len) : (i += 1) {
            const path = win.full(c.allocator, try win.combine(c.allocator, roots[i], try c.str(r.install_name))) catch |err| switch (err) {
                error.OutOfMemory => return err,
                else => continue,
            };
            if (try win.same(c.allocator, path, managed)) continue;
            var duplicate = false;
            var j: usize = 0;
            while (j < seen.items.len) : (j += 1) if (win.eq(path, seen.items[j])) {
                duplicate = true;
                break;
            };
            if (duplicate) continue;
            try seen.append(c.allocator, path);
            if (try installed(c, r, path)) return .{ .directory = path, .source = try win.text(c.allocator, "CommonInstallRoot") };
        }
    }
    return null;
}
fn interrupted(c: Context, r: Request, live: []const u16) !bool {
    const full = try win.full(c.allocator, live);
    const parent = win.parent(full) orelse return false;
    try win.rejectLinks(c.allocator, parent);
    const base = full[parent.len + @as(usize, if (parent[parent.len - 1] == '\\') 0 else 1) ..];
    const suffix = try win.text(c.allocator, ".previous-");
    const prefix = try c.allocator.alloc(u16, base.len + suffix.len);
    @memcpy(prefix[0..base.len], base);
    @memcpy(prefix[base.len..], suffix);
    var enumeration = try win.Enumeration.open(c.allocator, try win.combine(c.allocator, parent, &.{'*'})) orelse return false;
    defer enumeration.close();
    var found = false;
    var count: u32 = 0;
    while (try enumeration.next()) |data| {
        count += 1;
        if (count > r.maximum_entries) return error.EntryBound;
        const name = win.nameOf(data);
        if (name.len < prefix.len or !win.eq(name[0..prefix.len], prefix)) continue;
        const guid = win.trim(name[prefix.len..]);
        if (guid.len != 32) continue;
        var valid = true;
        var i: usize = 0;
        while (i < guid.len) : (i += 1) if (guid[i] > 127 or !std.ascii.isHex(@intCast(guid[i]))) {
            valid = false;
            break;
        };
        if (!valid) continue;
        try win.rejectLinks(c.allocator, try win.combine(c.allocator, parent, name));
        if (data.attributes & 0x10 != 0) found = true;
    }
    return found;
}
// JsonSerializerOptions.MaxDepth defaults to an effective 64 (System.Text.Json).
// This preserves the existing receipt JSON protocol; it is not a new resource policy.
// Count the root container as depth one, and ignore structural bytes inside escaped strings.
fn withinReceiptJsonDepth(input: []const u8) bool {
    var depth: usize = 0;
    var in_string = false;
    var escaped = false;
    var index: usize = 0;
    while (index < input.len) : (index += 1) {
        const byte = input[index];
        if (in_string) {
            if (escaped) {
                escaped = false;
            } else if (byte == '\\') {
                escaped = true;
            } else if (byte == '"') {
                in_string = false;
            }
            continue;
        }
        switch (byte) {
            '"' => in_string = true,
            '{', '[' => {
                if (depth == 64) return false;
                depth += 1;
            },
            '}', ']' => if (depth != 0) {
                depth -= 1;
            },
            else => {},
        }
    }
    // The standard parser still owns all syntax/UTF-8 validation, including unmatched containers/strings.
    return true;
}

fn pinned(c: Context, r: Request, record_path: []const u16, result: *Result) !bool {
    const warnings = &result.warnings;
    const bytes = win.read(c.allocator, record_path, r.read_attempt_limit, r.read_delay_ms, &result.read_attempts, &result.read_status) catch |err| switch (err) {
        error.OutOfMemory => return err,
        else => {
            warnings.* |= 1;
            return false;
        },
    } orelse return false;
    const input = if (std.mem.startsWith(u8, bytes, "\xef\xbb\xbf")) bytes[3..] else bytes;
    if (!withinReceiptJsonDepth(input)) {
        warnings.* |= 1;
        return false;
    }
    const Record = struct { tag: ?[]const u8 = null, assetName: ?[]const u8 = null, assetSha256: ?[]const u8 = null };
    const record = std.json.parseFromSliceLeaky(Record, c.allocator, input, .{ .ignore_unknown_fields = true, .duplicate_field_behavior = .use_last }) catch |err| switch (err) {
        error.OutOfMemory => return err,
        else => {
            warnings.* |= 1;
            return false;
        },
    };
    const tag = record.tag orelse {
        warnings.* |= 1;
        return false;
    };
    const asset = record.assetName orelse {
        warnings.* |= 1;
        return false;
    };
    const sha = record.assetSha256 orelse {
        warnings.* |= 1;
        return false;
    };
    if (win.trim(try win.text(c.allocator, tag)).len == 0 or win.trim(try win.text(c.allocator, asset)).len == 0 or sha.len != 64) {
        warnings.* |= 1;
        return false;
    }
    var i: usize = 0;
    while (i < sha.len) : (i += 1) if (!std.ascii.isHex(sha[i])) {
        warnings.* |= 1;
        return false;
    };
    if (r.has_payload == 0 and r.has_release == 0) return false;
    const tag_pin = try c.str(if (r.has_payload != 0) r.payload_tag else r.release_tag);
    const asset_pin = try c.str(if (r.has_payload != 0) r.payload_asset else r.release_asset);
    return std.mem.eql(u16, try win.text(c.allocator, tag), tag_pin) and std.mem.eql(u16, try win.text(c.allocator, asset), asset_pin);
}
pub fn build(c: Context, r: Request) !Result {
    var result: Result = .{};
    const package_root = try c.str(r.package_root);
    const dependency_root = if (r.package_root.count != 0) try win.combine(c.allocator, package_root, try win.text(c.allocator, "Dependencies")) else try c.str(r.dependency_root);
    const installer_root = if (r.package_root.count != 0) try win.combine(c.allocator, try win.combine(c.allocator, package_root, try win.text(c.allocator, "Misc")), try win.text(c.allocator, "DependencyInstallers")) else try c.str(r.installer_root);
    const directory = try win.combine(c.allocator, dependency_root, try c.str(r.id));
    const installer_directory = try win.combine(c.allocator, installer_root, try c.str(r.id));
    const installer_path = try resolveInstaller(c, r, installer_directory);
    const runtime = try installed(c, r, directory);
    const record_path = try win.combine(c.allocator, directory, try win.text(c.allocator, ".component-release.json"));
    const record_matches = try pinned(c, r, record_path, &result);
    const probes = try c.list(Slice, r.probes);
    const payload_files = try c.list(Slice, r.payload_files);
    var owns_probe = r.has_payload != 0 and probes.len > 0;
    var i: usize = 0;
    while (owns_probe and i < probes.len) : (i += 1) {
        var found = false;
        var j: usize = 0;
        while (j < payload_files.len) : (j += 1) if (win.eq(try c.str(probes[i]), try c.str(payload_files[j]))) {
            found = true;
            break;
        };
        if (!found) owns_probe = false;
    }
    const receipt = owns_probe and try win.regular(c.allocator, record_path);
    const managed = runtime or receipt;
    const live = try win.directory(c.allocator, directory);
    const replacement = if (!live and r.has_payload != 0) interrupted(c, r, directory) catch |err| switch (err) {
        error.OutOfMemory => return err,
        else => blk: {
            result.warnings |= 2;
            break :blk false;
        },
    } else false;
    const outside = if (runtime) null else try external(c, r, directory);
    const is_installed = managed or outside != null;
    var payload_present = true;
    i = 0;
    while (r.has_payload != 0 and i < payload_files.len) : (i += 1) if (!try win.regular(c.allocator, try win.combine(c.allocator, directory, try c.str(payload_files[i])))) {
        payload_present = false;
        break;
    };
    const update = replacement or ((managed or (outside != null and !payload_present and r.has_payload != 0)) and (r.has_release != 0 or r.has_payload != 0) and !record_matches);
    result.source_kind = if (win.trim(try c.str(r.download_url)).len != 0) 1 else if (r.has_release != 0) 2 else 0;
    const downloadable = result.source_kind != 0;
    result.state = if (update and !is_installed) 1 else if (is_installed) (if (!payload_present) @as(u32, 2) else if (update) @as(u32, 1) else @as(u32, 3)) else if (installer_path != null) 4 else if (downloadable) 5 else 6;
    // Existing public backend message codes, not new UI text or state policy.
    result.message = if (update) 26 else switch (result.state) {
        2 => 25,
        1 => 26,
        3 => 5,
        4 => 6,
        5 => if (result.source_kind == 2) @as(u32, 7) else @as(u32, 8),
        else => 9,
    };
    if (outside != null and result.state != 2) result.message = 10;
    result.flags = @as(u32, if (installer_path != null) 1 else 0) | @as(u32, if (is_installed) 2 else 0) | @as(u32, if ((!is_installed or update) and downloadable) 4 else 0) | @as(u32, if ((!is_installed or update) and installer_path != null) 8 else 0) | @as(u32, if (payload_present) 16 else 0) | @as(u32, if (update) 32 else 0);
    result.dependency_root = try c.output(dependency_root);
    result.install_directory = try c.output(directory);
    result.installer_directory = try c.output(installer_directory);
    if (installer_path) |path| result.installer_path = try c.output(path);
    if (outside) |found| result.external_directory = try c.output(found.directory);
    const source = if (replacement) try win.text(c.allocator, "ManagedDependenciesInterruptedReplacement") else if (managed) try win.text(c.allocator, "ManagedDependencies") else if (outside) |found| found.source else try win.text(c.allocator, "None");
    result.detection = try c.output(source);
    return result;
}
pub fn execute(c: Context, r: Request, operation: u32) !Result {
    switch (operation) {
        0 => return build(c, r),
        1 => return .{ .flags = if (try installed(c, r, try c.str(r.left))) 1 else 0 },
        2 => {
            var result: Result = .{};
            if (try resolveInstaller(c, r, try c.str(r.left))) |path| result.value = try c.output(path);
            return result;
        },
        3 => {
            var result: Result = .{};
            if (try win.normalize(c.allocator, try c.str(r.left))) |path| result.value = try c.output(path);
            return result;
        },
        4 => return .{ .value = try c.output(try normalizeMatch(c.allocator, try c.str(r.left))) },
        5 => return .{ .flags = if (try win.same(c.allocator, try c.str(r.left), try c.str(r.right))) 1 else 0 },
        else => return error.InvalidInput,
    }
}
