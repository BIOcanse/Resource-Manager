//! Canonical discovery decisions. Every array and string belongs to the caller.
const std = @import("std");
const old = @import("../metric_snapshot/protocol.zig");
pub const p = @import("protocol.zig");
pub const Ref = extern struct { offset: u32 = 0, length: u32 = 0 };
pub const Text = extern struct { bytes: ?[*]const u8 = null, length: u32 = 0, reserved: u32 = 0 };
pub const Sources = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    ids: ?[*]const u64 = null,
    names: ?[*]const Ref = null,
    incarnations: ?[*]const u64 = null,
    capabilities: ?[*]const u64 = null,
    availability: ?[*]const u32 = null,
    priorities: ?[*]const u32 = null,
};
/// Unexpanded compiled configuration, not managed rule bindings.
pub const Templates = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    names: ?[*]const Ref = null,
    addresses: ?[*]const Ref = null,
    source_ids: ?[*]const u64 = null,
    fingerprints: ?[*]const u64 = null,
    expansions: ?[*]const u32 = null,
    kinds: ?[*]const u32 = null,
    scope_kinds: ?[*]const u32 = null,
    value_kinds: ?[*]const u32 = null,
    flags: ?[*]const u32 = null,
    priorities: ?[*]const u32 = null,
    vendor_masks: ?[*]const u32 = null,
    applicability: ?[*]const u32 = null,
    maximum_instances: ?[*]const u32 = null,
    minimum: ?[*]const u64 = null,
    maximum: ?[*]const u64 = null,
    masks: ?[*]const u64 = null,
};
/// Base registry declarations. Source-specific variants are native decisions.
pub const Fields = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    templates: ?[*]const Ref = null,
    names: ?[*]const Ref = null,
    codes: ?[*]const u32 = null,
    resources: ?[*]const u32 = null,
    vendors: ?[*]const u32 = null,
    legacy: ?[*]const u32 = null,
};
/// Hardware directory facts. Flags: dedicated1, NVMe2, ATA4, OEM8,
/// NVML fan16, IGCL fan32, relative battery64. Scope expansion retains 1..17.
/// applicability pairs (scope index, field code) carry raw device support facts;
/// an empty list means no field restriction except battery absolute/relative.
pub const Scopes = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    keys: ?[*]const Ref = null,
    names: ?[*]const Ref = null,
    expansions: ?[*]const u32 = null,
    display_indices: ?[*]const u32 = null,
    generations: ?[*]const u64 = null,
    luids: ?[*]const u64 = null,
    vendor_masks: ?[*]const u32 = null,
    flags: ?[*]const u32 = null,
    binding_keys: ?[*]const Ref = null,
    thermal_caps: ?[*]const u32 = null,
    rapl_domains: ?[*]const u32 = null,
    power_domains: ?[*]const u32 = null,
    base_mhz: ?[*]const u32 = null,
    support_count: u32 = 0,
    support_reserved: u32 = 0,
    support_scopes: ?[*]const u32 = null,
    support_fields: ?[*]const u32 = null,
};
pub const Handles = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    kinds: ?[*]const u32 = null,
    ids: ?[*]const u64 = null,
    keys: ?[*]const Ref = null,
};
pub const Facts = extern struct {
    text: Text = .{},
    sources: Sources = .{},
    templates: Templates = .{},
    fields: Fields = .{},
    scopes: Scopes = .{},
    handles: Handles = .{},
    cpu_vendor: u32 = 0,
    reserved: u32 = 0,
};
pub const Limits = extern struct {
    sources: u32,
    rules: u32,
    metrics: u32,
    scopes: u32,
    handles: u32,
    text_bytes: u32,
    work_bytes: u64,
};
/// One row per native rule, ordered by (metric ID, priority, rule ID).
pub const Bindings = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    addresses: ?[*]const Ref = null,
    legacy: ?[*]const Ref = null,
    field_names: ?[*]const Ref = null,
    device_keys: ?[*]const Ref = null,
    scope_ids: ?[*]const u64 = null,
    field_codes: ?[*]const u32 = null,
    template_indices: ?[*]const u32 = null,
    scope_indices: ?[*]const u32 = null,
    source_indices: ?[*]const u32 = null,
    metric_indices: ?[*]const u32 = null,
    display_indices: ?[*]const u32 = null,
};
pub const Legacy = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    keys: ?[*]const Ref = null,
    metric_indices: ?[*]const u32 = null,
    rule_indices: ?[*]const u32 = null,
};
pub const Compiled = extern struct {
    catalog: p.Catalog = .{ .sources = .{ .count = 0, .ids = null, .incarnations = null, .capabilities = null, .availability = null }, .rules = .{ .count = 0, .ids = null, .source_ids = null, .metric_ids = null, .identities = null, .kinds = null, .flags = null, .priorities = null, .value_kinds = null, .minimum_bits = null, .maximum_bits = null, .capability_masks = null }, .metrics = .{ .count = 0, .ids = null, .identities = null } },
    text: Text = .{},
    bindings: Bindings = .{},
    handles: Handles = .{},
    sources: Sources = .{},
    scopes: Scopes = .{},
    fact_text: Text = .{},
    legacy: Legacy = .{},
};
pub const Error = error{ Invalid, Capacity, Payload, Work, Exhausted, Identity };
pub fn fail(f: *p.Failure, code: p.Code, family: p.Family, required_bytes: u64, limit: u64) Error {
    f.* = .{ .code = code, .family = family, .required = required_bytes, .limit = limit };
    return error.Invalid;
}
pub fn get(text: Text, ref: Ref) Error![]const u8 {
    if (text.reserved != 0 or (text.length != 0 and text.bytes == null) or @as(u64, ref.offset) + ref.length > text.length) return error.Invalid;
    if (ref.length == 0) return "";
    const bytes = text.bytes orelse return error.Invalid;
    const value = bytes[ref.offset..][0..ref.length];
    if (!std.unicode.utf8ValidateSlice(value) or std.mem.indexOfScalar(u8, value, 0) != null) return error.Invalid;
    return value;
}
pub fn exact(value: []const u8) bool {
    if (value.len == 0 or value.len > 4096) return false;
    const view = std.unicode.Utf8View.init(value) catch return false;
    var iterator = view.iterator();
    const first = iterator.nextCodepoint() orelse return false;
    var last = first;
    while (iterator.nextCodepoint()) |codepoint| last = codepoint;
    return !whitespace(first) and !whitespace(last);
}
fn whitespace(codepoint: u21) bool {
    return switch (codepoint) {
        9...13, 32, 0x85, 0xa0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 => true,
        else => false,
    };
}
fn hasNonWhitespace(value: []const u8) bool {
    const view = std.unicode.Utf8View.init(value) catch return false;
    var iterator = view.iterator();
    while (iterator.nextCodepoint()) |codepoint| if (!whitespace(codepoint)) return true;
    return false;
}
pub fn eql(a: []const u8, b: []const u8) bool {
    return std.mem.eql(u8, a, b);
}
pub fn ci(a: []const u8, b: []const u8) bool {
    return std.ascii.eqlIgnoreCase(a, b);
}
pub fn pointers(comptime T: type, value: T) bool {
    if (value.reserved != 0) return false;
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| {
        if (comptime eql(field_name, "support_scopes") or eql(field_name, "support_fields") or eql(field_name, "thermal_caps") or eql(field_name, "rapl_domains") or eql(field_name, "power_domains") or eql(field_name, "base_mhz")) continue;
        if (@typeInfo(field_type) == .optional and value.count != 0 and @field(value, field_name) == null) return false;
        if (@typeInfo(field_type) == .optional) {
            if (@field(value, field_name)) |ptr| {
                const Element = @typeInfo(@TypeOf(ptr)).pointer.child;
                if (@intFromPtr(ptr) % @alignOf(Element) != 0) return false;
            }
        }
    }
    return true;
}
pub fn sourceName(facts: Facts, index: usize) []const u8 {
    return get(facts.text, facts.sources.names.?[index]) catch unreachable;
}
fn sourceIndex(facts: Facts, id: u64) ?u32 {
    var i: u32 = 0;
    while (i < facts.sources.count) : (i += 1) if (facts.sources.ids.?[i] == id) return i;
    return null;
}
fn fieldIndex(facts: Facts, template: u32) ?u32 {
    const address = get(facts.text, facts.templates.addresses.?[template]) catch unreachable;
    var i: u32 = 0;
    while (i < facts.fields.count) : (i += 1) if (eql(address, get(facts.text, facts.fields.templates.?[i]) catch unreachable)) return i;
    return null;
}
fn scopePrefix(expansion: u32, source: []const u8) []const u8 {
    return switch (expansion) {
        4 => "gpu",
        5 => "storage",
        6 => "fan",
        8 => "battery",
        9 => "numa",
        10, 11, 12, 15 => "intel-cpu",
        13 => if (eql(source, "amd.smn")) "amd-ccd" else "intel-cpu",
        14 => "network-interface",
        16 => "npu",
        17 => "npu-engine",
        else => "",
    };
}
fn placeholder(expansion: u32) []const u8 {
    return switch (expansion) {
        4 => "{gpu}",
        5 => "{disk}",
        6 => "{fan}",
        8 => "{battery}",
        9 => "{numa}",
        10 => "{core}",
        11 => "{package}",
        12 => "{thread}",
        13 => "{die}",
        14 => "{interface}",
        15 => "{platform}",
        16 => "{npu}",
        17 => "{engine}",
        else => "",
    };
}
fn staticScope(expansion: u32) ?u64 {
    return switch (expansion) {
        1 => 0x1000000000000001,
        2 => 0x1000000000000002,
        3 => 0x1000000000000003,
        7 => 0x1000000000000004,
        else => null,
    };
}
fn staticKey(expansion: u32) []const u8 {
    return switch (expansion) {
        1 => "static|host",
        2 => "static|cpu",
        3 => "static|memory",
        7 => "static|virtual-memory",
        else => "",
    };
}
fn applies(f: Facts, ti: u32, si: u32) bool {
    const t = f.templates;
    if (t.expansions.?[ti] != f.scopes.expansions.?[si]) return false;
    const src = sourceName(f, sourceIndex(f, t.source_ids.?[ti]).?);
    const flags = f.scopes.flags.?[si];
    if (t.expansions.?[ti] == 13 and eql(src, "amd.smn") != (flags & 128 != 0)) return false;
    switch (t.expansions.?[ti]) {
        4 => if (t.vendor_masks.?[ti] & f.scopes.vendor_masks.?[si] == 0 or (t.applicability.?[ti] & 1 != 0 and flags & 1 == 0)) return false,
        5 => {
            if (eql(src, "windows.nvme-health") and flags & 2 == 0) return false;
            if (eql(src, "windows.ata-smart") and flags & 4 == 0) return false;
        },
        6 => {
            if (eql(src, "intel.igcl")) {
                if (flags & 32 == 0) return false;
            } else if (eql(src, "nvidia.nvml")) {
                if (flags & 16 == 0) return false;
            } else if (eql(src, "notebook-oem.fan")) {
                if (flags & 8 == 0 or flags & 48 != 0) return false;
            } else if (eql(src, "hardware-monitor.core")) {
                if (flags & 56 != 0) return false;
            } else return false;
        },
        else => {},
    }
    if (fieldIndex(f, ti)) |fi| {
        const code = f.fields.codes.?[fi];
        const expansion = f.scopes.expansions.?[si];
        if ((expansion >= 10 and expansion <= 13 or expansion == 15) and flags & 128 == 0 and !intelApplies(f.scopes, si, code)) return false;
        // Battery absolute/relative variants are mutually exclusive physical units.
        if (code >= 129 and code <= 136 and ((code - 129) % 2 == 1) != (flags & 64 != 0)) return false;
        var restricted = false;
        var support = false;
        var n: u32 = 0;
        while (n < f.scopes.support_count) : (n += 1) if (f.scopes.support_scopes.?[n] == si) {
            restricted = true;
            if (f.scopes.support_fields.?[n] == code) support = true;
        };
        if (restricted and !support) return false;
    }
    return true;
}
/// Exact IntelMsrCatalogIdentity.AppliesTo decisions from raw device evidence.
pub fn intelApplies(scopes: Scopes, si: u32, code: u32) bool {
    const ex = scopes.expansions.?[si];
    const flags = scopes.flags.?[si];
    const core = ex == 10;
    const package = ex == 11;
    const die = ex == 13;
    if (code >= 191 and code <= 204) {
        const is_package = code >= 200;
        const cap: u32 = switch (code) {
            191, 200 => 1,
            192, 193, 201, 202 => 2,
            194, 195, 203, 204 => 4,
            196, 197 => 8,
            else => 16,
        };
        return (core or package) and is_package == package and scopes.thermal_caps.?[si] & cap != 0;
    }
    if ((code >= 171 and code <= 178) or (code >= 187 and code <= 190)) {
        const is_die = code >= 187;
        const dram = code >= 175 and code <= 178;
        const domain: u32 = if (dram) 8 else 1;
        return (package or die) and is_die == die and scopes.power_domains.?[si] & domain != 0 and (!dram or code == 175 or flags & 2048 != 0);
    }
    if ((code >= 152 and code <= 167) or (code >= 179 and code <= 186)) {
        const is_die = code >= 179;
        const domain: u32 = if (code <= 155 or (code >= 179 and code <= 182)) 1 else if (code <= 159) 2 else if (code <= 163) 4 else 8;
        return (package or die) and is_die == die and scopes.rapl_domains.?[si] & domain != 0;
    }
    return switch (code) {
        298, 299 => ex == 15,
        168, 169, 170 => ex == 12 and flags & 1024 != 0,
        310 => ex == 12 and flags & 1024 != 0 and scopes.base_mhz.?[si] > 0,
        148 => core and flags & 256 != 0,
        149 => package and flags & 256 != 0,
        146, 150 => core and flags & 768 == 768,
        147, 151 => package and flags & 768 == 768,
        else => false,
    };
}
fn cpuApplies(f: Facts, ti: u32) bool {
    const ex = f.templates.expansions.?[ti];
    if ((ex >= 10 and ex <= 13) or f.cpu_vendor == 0) return true;
    const fi = fieldIndex(f, ti) orelse return true;
    if (f.fields.resources.?[fi] != 1) return true;
    const vendor = variant(f, ti, fi).vendor;
    return (vendor == 0 or vendor == f.cpu_vendor) and (!eql(sourceName(f, sourceIndex(f, f.templates.source_ids.?[ti]).?), "amd.smu") or f.cpu_vendor == 2);
}
pub fn validate(f: Facts, limits: Limits, failure: *p.Failure) Error!u32 {
    failure.* = .{};
    if (f.reserved != 0 or f.cpu_vendor > 4 or !pointers(Sources, f.sources) or !pointers(Templates, f.templates) or !pointers(Fields, f.fields) or !pointers(Handles, f.handles) or !pointers(Scopes, f.scopes) or f.scopes.support_reserved != 0) return error.Invalid;
    _ = try get(f.text, .{});
    if (f.scopes.support_count != 0 and (f.scopes.support_scopes == null or f.scopes.support_fields == null)) return error.Invalid;
    if (f.sources.count > limits.sources) return fail(failure, .capacity_exceeded, .sources, f.sources.count, limits.sources);
    if (f.scopes.count > limits.scopes) return fail(failure, .capacity_exceeded, .binding, f.scopes.count, limits.scopes);
    var i: u32 = 0;
    while (i < f.sources.count) : (i += 1) {
        if (f.sources.ids.?[i] == 0 or f.sources.incarnations.?[i] == 0 or f.sources.capabilities.?[i] == 0 or f.sources.priorities.?[i] == 0 or f.sources.availability.?[i] < 1 or f.sources.availability.?[i] > 3 or !exact(try get(f.text, f.sources.names.?[i]))) return error.Invalid;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (f.sources.ids.?[i] == f.sources.ids.?[j] or eql(try get(f.text, f.sources.names.?[i]), try get(f.text, f.sources.names.?[j]))) return error.Invalid;
    }
    i = 0;
    while (i < f.fields.count) : (i += 1) {
        if (!exact(try get(f.text, f.fields.templates.?[i])) or !exact(try get(f.text, f.fields.names.?[i])) or f.fields.legacy.?[i] > 1 or f.fields.resources.?[i] > 10 or f.fields.vendors.?[i] > 4) return error.Invalid;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (eql(try get(f.text, f.fields.templates.?[i]), try get(f.text, f.fields.templates.?[j]))) return error.Invalid;
    }
    i = 0;
    while (i < f.scopes.count) : (i += 1) {
        const s = f.scopes;
        const ex = s.expansions.?[i];
        if (staticScope(ex) != null or ex < 4 or ex > 17 or !exact(try get(f.text, s.keys.?[i])) or s.generations.?[i] == 0 or s.display_indices.?[i] > std.math.maxInt(i32) or s.flags.?[i] & ~@as(u32, 4095) != 0) return error.Invalid;
        if ((ex >= 10 and ex <= 13 or ex == 15) and s.flags.?[i] & 128 == 0) {
            if (s.thermal_caps == null or s.rapl_domains == null or s.power_domains == null or s.base_mhz == null) return error.Invalid;
            if (s.thermal_caps.?[i] & ~@as(u32, 31) != 0 or s.rapl_domains.?[i] & ~@as(u32, 15) != 0 or s.power_domains.?[i] & ~@as(u32, 15) != 0) return error.Invalid;
        }
        _ = try get(f.text, s.names.?[i]);
        _ = try get(f.text, s.binding_keys.?[i]);
        if (ex == 6 and @popCount(s.flags.?[i] & 56) > 1) return error.Invalid;
        if (ex == 5 and s.flags.?[i] & 56 != 0) return error.Invalid;
        if (ex == 4 and (s.luids.?[i] == 0 or !hasNonWhitespace(try get(f.text, s.names.?[i])) or s.vendor_masks.?[i] == 0 or s.vendor_masks.?[i] > 8 or @popCount(s.vendor_masks.?[i]) != 1)) return error.Invalid;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (s.expansions.?[j] == ex and (ex != 13 or s.flags.?[j] & 128 == s.flags.?[i] & 128)) {
            if (eql(try get(f.text, s.keys.?[i]), try get(f.text, s.keys.?[j])) or s.display_indices.?[i] == s.display_indices.?[j] or s.generations.?[i] != s.generations.?[j] or (ex == 4 and s.luids.?[i] == s.luids.?[j])) return error.Invalid;
        };
    }
    i = 0;
    while (i < f.scopes.support_count) : (i += 1) if (f.scopes.support_scopes.?[i] >= f.scopes.count) return error.Invalid;
    i = 0;
    while (i < f.handles.count) : (i += 1) {
        const h = f.handles;
        if (h.ids.?[i] == std.math.maxInt(u64)) return fail(failure, .capacity_exceeded, .generation, std.math.maxInt(u64), std.math.maxInt(u64) - 1);
        if (h.kinds.?[i] < 1 or h.kinds.?[i] > 3 or h.ids.?[i] == 0 or h.ids.?[i] == std.math.maxInt(u64) or (h.kinds.?[i] == 1 and h.ids.?[i] < 0x2000000000000001) or !exact(try get(f.text, h.keys.?[i]))) return error.Invalid;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (h.kinds.?[i] == h.kinds.?[j] and (h.ids.?[i] == h.ids.?[j] or eql(try get(f.text, h.keys.?[i]), try get(f.text, h.keys.?[j])))) return error.Invalid;
    }
    var count: u64 = 0;
    i = 0;
    while (i < f.templates.count) : (i += 1) {
        const t = f.templates;
        if (!exact(try get(f.text, t.names.?[i])) or !exact(try get(f.text, t.addresses.?[i])) or sourceIndex(f, t.source_ids.?[i]) == null or t.fingerprints.?[i] == 0 or t.expansions.?[i] < 1 or t.expansions.?[i] > 17 or t.kinds.?[i] < 1 or t.kinds.?[i] > 19 or t.value_kinds.?[i] < 1 or t.value_kinds.?[i] > 6 or t.flags.?[i] & ~@as(u32, 15) != 0) return error.Invalid;
        var definition = std.mem.zeroes(old.MetricDefinitionInput);
        definition.struct_size = @sizeOf(old.MetricDefinitionInput);
        definition.rule_handle = 1;
        definition.metric_handle = 1;
        definition.source_handle = t.source_ids.?[i];
        definition.scope_handle = 1;
        definition.metric_kind = t.kinds.?[i];
        definition.scope_kind = t.scope_kinds.?[i];
        definition.value_kind = t.value_kinds.?[i];
        definition.flags = t.flags.?[i];
        definition.source_priority = t.priorities.?[i];
        definition.semantic_fingerprint = t.fingerprints.?[i];
        definition.minimum_value_bits = t.minimum.?[i];
        definition.maximum_value_bits = t.maximum.?[i];
        if (definition.value_kind == 5 and definition.minimum_value_bits == 0) {
            definition.minimum_value_bits = 1;
            definition.maximum_value_bits = @max(definition.maximum_value_bits, 1);
        }
        if (!old.validMetricDefinition(&definition)) return error.Invalid;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (eql(try get(f.text, t.names.?[i]), try get(f.text, t.names.?[j]))) return error.Invalid;
        if (!cpuApplies(f, i)) continue;
        var instances: u32 = 0;
        if (staticScope(t.expansions.?[i]) != null) {
            instances = 1;
        } else {
            j = 0;
            while (j < f.scopes.count) : (j += 1) if (applies(f, i, j)) {
                instances += 1;
            };
        }
        if (staticScope(t.expansions.?[i]) == null and instances > t.maximum_instances.?[i]) return fail(failure, .capacity_exceeded, .rules, instances, t.maximum_instances.?[i]);
        count += instances;
        if (count > limits.rules) return fail(failure, .capacity_exceeded, .rules, count, limits.rules);
    }
    return @intCast(count);
}
pub fn required(f: Facts, limits: Limits, failure: *p.Failure) Error!u64 {
    const n = try validate(f, limits, failure);
    // Explicit bounded layout: flat rule columns108B, binding columns80B,
    // metric columns16B, scope-order8B, handle columns24B; each alloc <=7 pad.
    const h = @as(u64, f.handles.count) + f.scopes.count + @as(u64, n) * 2;
    const bytes = @as(u64, n) * 256 + @as(u64, f.sources.count) * 64 + @as(u64, f.scopes.count) * 16 + h * 32 + limits.text_bytes + 1024;
    if (f.handles.count > limits.handles) return fail(failure, .capacity_exceeded, .binding, f.handles.count, limits.handles);
    if (bytes > limits.work_bytes) return fail(failure, .capacity_exceeded, .work, bytes, limits.work_bytes);
    return bytes;
}
const Arena = struct {
    bytes: []u8,
    used: u32 = 0,
    failure: *p.Failure,
    fn put(a: *Arena, input: []const u8) Error!Ref {
        if (input.len > a.bytes.len - a.used) {
            a.failure.* = .{ .code = .payload_capacity, .family = .payload, .required = @as(u64, a.used) + input.len, .limit = a.bytes.len };
            return error.Payload;
        }
        const r = Ref{ .offset = a.used, .length = @intCast(input.len) };
        @memcpy(a.bytes[a.used..][0..input.len], input);
        a.used += @intCast(input.len);
        return r;
    }
    fn text(a: Arena) Text {
        return .{ .bytes = a.bytes.ptr, .length = a.used };
    }
    fn join(a: *Arena, parts: []const []const u8) Error!Ref {
        const begin = a.used;
        for (parts) |part| _ = try a.put(part);
        return .{ .offset = begin, .length = a.used - begin };
    }
    fn value(a: Arena, r: Ref) []const u8 {
        return a.bytes[r.offset..][0..r.length];
    }
};
const Map = struct {
    kinds: []u32,
    ids: []u64,
    keys: []Ref,
    count: u32 = 0,
    failure: *p.Failure,
    next: [3]u64 = .{ 0x2000000000000001, 1, 1 },
    fn add(m: *Map, arena: *Arena, kind: u32, key: Ref) Error!u64 {
        if (!exact(arena.value(key))) return error.Invalid;
        var i: u32 = 0;
        while (i < m.count) : (i += 1) if (m.kinds[i] == kind and eql(arena.value(key), arena.value(m.keys[i]))) return m.ids[i];
        if (m.count == m.ids.len) return fail(m.failure, .capacity_exceeded, .binding, @as(u64, m.count) + 1, m.ids.len);
        const id = m.next[kind - 1];
        if (id == std.math.maxInt(u64)) return fail(m.failure, .capacity_exceeded, .generation, id, id - 1);
        m.next[kind - 1] += 1;
        m.kinds[m.count] = kind;
        m.ids[m.count] = id;
        m.keys[m.count] = key;
        m.count += 1;
        return id;
    }
};
const Variant = struct { name: []const u8, vendor: u32, legacy: bool };
fn variant(f: Facts, ti: u32, fi: u32) Variant {
    const source = sourceName(f, sourceIndex(f, f.templates.source_ids.?[ti]).?);
    const base = Variant{ .name = get(f.text, f.fields.names.?[fi]) catch unreachable, .vendor = f.fields.vendors.?[fi], .legacy = f.fields.legacy.?[fi] != 0 };
    const code = f.fields.codes.?[fi];
    if (eql(source, "amd.smu")) return switch (code) {
        7 => .{ .name = "cpu_amd_smu_temperature_reported_celsius", .vendor = 2, .legacy = true },
        60 => .{ .name = "gpu_amd_apu_temperature_reported_celsius", .vendor = 2, .legacy = false },
        else => base,
    };
    if (eql(source, "amd.adlx")) return switch (code) {
        50 => .{ .name = "gpu_amd_activity_percent", .vendor = 2, .legacy = true },
        51 => .{ .name = "gpu_amd_adlx_graphics_clock_mhz", .vendor = 2, .legacy = true },
        65 => .{ .name = "gpu_amd_adlx_core_voltage_reported_volts", .vendor = 2, .legacy = true },
        60 => .{ .name = "gpu_amd_edge_temperature_celsius", .vendor = 2, .legacy = true },
        69 => .{ .name = "gpu_amd_memory_temperature_celsius", .vendor = 2, .legacy = true },
        53 => .{ .name = "gpu_amd_vram_used_reported_bytes", .vendor = 2, .legacy = true },
        55 => .{ .name = "gpu_amd_vram_used_reported_percent", .vendor = 2, .legacy = true },
        else => base,
    };
    if (eql(source, "nvidia.nvml")) return switch (code) {
        50 => .{ .name = "gpu_nvidia_processor_busy_percent", .vendor = 3, .legacy = true },
        60 => .{ .name = "gpu_nvidia_core_temperature_celsius", .vendor = 3, .legacy = true },
        69 => .{ .name = "gpu_nvidia_memory_temperature_celsius", .vendor = 3, .legacy = true },
        53 => .{ .name = "gpu_nvidia_fb_used_v1_including_reserved_bytes", .vendor = 3, .legacy = true },
        54 => .{ .name = "gpu_nvidia_fb_total_bytes", .vendor = 3, .legacy = true },
        55 => .{ .name = "gpu_nvidia_fb_used_v1_including_reserved_percent", .vendor = 3, .legacy = true },
        57 => .{ .name = "gpu_nvidia_power_reported_watts", .vendor = 3, .legacy = true },
        else => base,
    };
    return base;
}
pub fn expandedFingerprint(template: u64, scope: u64, binding: []const u8) u64 {
    var result = template ^ (scope +% 0x9E3779B97F4A7C15 +% (template << 6) +% (template >> 2));
    for (binding) |byte| result = (result ^ byte) *% 1099511628211;
    return if (result == 0) 1 else result;
}
fn metricIdentity(scope: u64, f: Facts, ti: u32, address: []const u8) u64 {
    const t = f.templates;
    const values = [_]u64{ scope, t.kinds.?[ti], t.scope_kinds.?[ti], t.value_kinds.?[ti], t.flags.?[ti], t.minimum.?[ti], t.maximum.?[ti] };
    // Different fields can share every numeric declaration operand on one scope
    // (for example GPU usage and engine busy). Their canonical addresses still
    // identify distinct metrics; alternate sources for one address share it.
    const seed = std.hash.Wyhash.hash(0x726d6d6574726963, address);
    const hash = std.hash.Wyhash.hash(seed, std.mem.asBytes(&values));
    return if (hash == 0) 1 else hash;
}
fn allocateColumns(comptime T: type, a: std.mem.Allocator, count: u32) !T {
    var result: T = undefined;
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| {
        if (comptime eql(field_name, "count")) {
            @field(result, field_name) = count;
        } else if (comptime eql(field_name, "reserved")) {
            @field(result, field_name) = 0;
        } else {
            const Pointer = @typeInfo(field_type).optional.child;
            const Element = @typeInfo(Pointer).pointer.child;
            @field(result, field_name) = (try a.alloc(Element, count)).ptr;
        }
    }
    return result;
}
pub fn compile(f: Facts, limits: Limits, work: []align(8) u8, failure: *p.Failure) Error!Compiled {
    const bytes = try required(f, limits, failure);
    if (work.len < bytes) return fail(failure, .workspace_unavailable, .work, bytes, work.len);
    // Facts and output must be disjoint; this prevents overwriting facts while expanding.
    if (overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(f.text.bytes), f.text.length)) return error.Invalid;
    if (!disjointColumns(Sources, f.sources, f.sources.count, work) or !disjointColumns(Templates, f.templates, f.templates.count, work) or !disjointColumns(Fields, f.fields, f.fields.count, work) or !disjointColumns(Scopes, f.scopes, f.scopes.count, work) or !disjointColumns(Handles, f.handles, f.handles.count, work)) return error.Invalid;
    const n = try validate(f, limits, failure);
    var fba = std.heap.FixedBufferAllocator.init(work);
    const a = fba.allocator();
    var out: Compiled = .{};
    out.sources = allocateColumns(Sources, a, f.sources.count) catch return error.Work;
    if (f.sources.count != 0) inline for (@typeInfo(Sources).@"struct".field_names, @typeInfo(Sources).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        @memcpy(@constCast(@field(out.sources, field_name).?)[0..f.sources.count], @field(f.sources, field_name).?[0..f.sources.count]);
    };
    var source_at: usize = 1;
    while (source_at < f.sources.count) : (source_at += 1) {
        var source_before = source_at;
        while (source_before > 0 and out.sources.ids.?[source_before] < out.sources.ids.?[source_before - 1]) : (source_before -= 1) swapColumns(Sources, &out.sources, source_before, source_before - 1);
    }
    out.catalog.sources = .{ .count = out.sources.count, .ids = out.sources.ids, .incarnations = out.sources.incarnations, .capabilities = out.sources.capabilities, .availability = out.sources.availability };
    out.scopes = f.scopes;
    out.fact_text = f.text;
    out.catalog.rules = allocateColumns(p.RuleColumns, a, n) catch return error.Work;
    out.catalog.metrics = allocateColumns(p.MetricColumns, a, n) catch return error.Work;
    out.bindings = allocateColumns(Bindings, a, n) catch return error.Work;
    out.legacy = allocateColumns(Legacy, a, n) catch return error.Work;
    out.legacy.count = 0;
    const cap: u32 = @intCast(@min(@as(u64, f.handles.count) + f.scopes.count + @as(u64, n) * 2, limits.handles));
    var map = Map{ .kinds = a.alloc(u32, cap) catch return error.Work, .ids = a.alloc(u64, cap) catch return error.Work, .keys = a.alloc(Ref, cap) catch return error.Work, .failure = failure };
    const scope_ids = a.alloc(u64, f.scopes.count) catch return error.Work;
    const scope_order = a.alloc(u32, f.scopes.count) catch return error.Work;
    var arena = Arena{ .bytes = a.alloc(u8, limits.text_bytes) catch return error.Work, .failure = failure };
    var i: u32 = 0;
    while (i < f.handles.count) : (i += 1) {
        const kind = f.handles.kinds.?[i];
        const id = f.handles.ids.?[i];
        map.kinds[i] = kind;
        map.ids[i] = id;
        map.keys[i] = try arena.put(try get(f.text, f.handles.keys.?[i]));
        map.next[kind - 1] = @max(map.next[kind - 1], id + 1);
        map.count += 1;
    }
    // Allocate scope handles in the old directory-kind / exact-key order.
    i = 0;
    while (i < f.scopes.count) : (i += 1) scope_order[i] = i;
    var k: usize = 1;
    while (k < scope_order.len) : (k += 1) {
        var j = k;
        while (j > 0 and scopeLess(f, scope_order[j], scope_order[j - 1])) : (j -= 1) std.mem.swap(u32, &scope_order[j], &scope_order[j - 1]);
    }
    for (scope_order) |si| {
        const ex = f.scopes.expansions.?[si];
        const src: []const u8 = if (ex == 13 and f.scopes.flags.?[si] & 128 != 0) "amd.smn" else "";
        const key = try arena.join(&.{ scopePrefix(ex, src), "|", try get(f.text, f.scopes.keys.?[si]) });
        scope_ids[si] = try map.add(&arena, 1, key);
    }
    var row: u32 = 0;
    i = 0;
    while (i < f.templates.count) : (i += 1) {
        if (!cpuApplies(f, i)) continue;
        if (staticScope(f.templates.expansions.?[i])) |scope| {
            try emit(&out, &map, &arena, f, i, p.none, scope, &row);
        } else {
            // Expansion order is display index, independent of discovery arrival order.
            k = 1;
            while (k < scope_order.len) : (k += 1) {
                var j = k;
                while (j > 0 and f.scopes.display_indices.?[scope_order[j]] < f.scopes.display_indices.?[scope_order[j - 1]]) : (j -= 1) std.mem.swap(u32, &scope_order[j], &scope_order[j - 1]);
            }
            for (scope_order) |si| if (applies(f, i, si)) try emit(&out, &map, &arena, f, i, si, scope_ids[si], &row);
        }
    }
    // Sort all parallel rule columns together.
    k = 1;
    while (k < n) : (k += 1) {
        var j = k;
        while (j > 0 and ruleLess(out.catalog.rules, j, j - 1)) : (j -= 1) {
            swapColumns(p.RuleColumns, &out.catalog.rules, j, j - 1);
            swapColumns(Bindings, &out.bindings, j, j - 1);
        }
    }
    var metrics: u32 = 0;
    i = 0;
    while (i < n) : (i += 1) {
        const r = out.catalog.rules;
        var src: u32 = 0;
        while (src < out.sources.count) : (src += 1) if (out.sources.ids.?[src] == r.source_ids.?[i]) {
            @constCast(out.bindings.source_indices.?)[i] = src;
            break;
        };
        if (i == 0 or r.metric_ids.?[i] != r.metric_ids.?[i - 1]) {
            if (metrics == limits.metrics) return fail(failure, .capacity_exceeded, .metrics, metrics + 1, limits.metrics);
            @constCast(out.catalog.metrics.ids.?)[metrics] = r.metric_ids.?[i];
            @constCast(out.catalog.metrics.identities.?)[metrics] = metricIdentity(out.bindings.scope_ids.?[i], f, out.bindings.template_indices.?[i], arena.value(out.bindings.addresses.?[i]));
            metrics += 1;
        } else if (!sameMetric(out, f, i, i - 1)) return error.Identity;
        @constCast(out.bindings.metric_indices.?)[i] = metrics - 1;
        var j: u32 = 0;
        while (j < i) : (j += 1) {
            if (r.ids.?[i] == r.ids.?[j] or r.identities.?[i] == r.identities.?[j]) return error.Identity;
            if (ci(arena.value(out.bindings.addresses.?[i]), arena.value(out.bindings.addresses.?[j])) and (r.metric_ids.?[i] != r.metric_ids.?[j] or !sameMetric(out, f, i, j))) return error.Identity;
        }
    }
    out.catalog.metrics.count = metrics;
    i = 0;
    while (i < n) : (i += 1) {
        const key = out.bindings.legacy.?[i];
        if (key.length == 0) continue;
        var at: ?u32 = null;
        var j: u32 = 0;
        while (j < out.legacy.count) : (j += 1) if (ci(arena.value(key), arena.value(out.legacy.keys.?[j]))) {
            at = j;
            break;
        };
        if (at) |entry| {
            const prior = out.legacy.rule_indices.?[entry];
            const rules = out.catalog.rules;
            if (rules.priorities.?[i] < rules.priorities.?[prior] or (rules.priorities.?[i] == rules.priorities.?[prior] and rules.ids.?[i] < rules.ids.?[prior])) {
                @constCast(out.legacy.rule_indices.?)[entry] = i;
                @constCast(out.legacy.metric_indices.?)[entry] = out.bindings.metric_indices.?[i];
            }
        } else {
            const entry = out.legacy.count;
            @constCast(out.legacy.keys.?)[entry] = key;
            @constCast(out.legacy.rule_indices.?)[entry] = i;
            @constCast(out.legacy.metric_indices.?)[entry] = out.bindings.metric_indices.?[i];
            out.legacy.count += 1;
        }
    }
    k = 1;
    while (k < map.count) : (k += 1) {
        var j = k;
        while (j > 0 and (map.kinds[j] < map.kinds[j - 1] or (map.kinds[j] == map.kinds[j - 1] and map.ids[j] < map.ids[j - 1]))) : (j -= 1) {
            std.mem.swap(u32, &map.kinds[j], &map.kinds[j - 1]);
            std.mem.swap(u64, &map.ids[j], &map.ids[j - 1]);
            std.mem.swap(Ref, &map.keys[j], &map.keys[j - 1]);
        }
    }
    out.text = arena.text();
    out.handles = .{ .count = map.count, .kinds = map.kinds.ptr, .ids = map.ids.ptr, .keys = map.keys.ptr };
    return out;
}
pub fn overlap(a: usize, al: usize, b: usize, bl: usize) bool {
    return al != 0 and bl != 0 and @as(u128, a) < @as(u128, b) + bl and @as(u128, b) < @as(u128, a) + al;
}
pub fn disjointColumns(comptime T: type, value: T, count: u32, work: []align(8) u8) bool {
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        if (@field(value, field_name)) |ptr| {
            const n = if (comptime eql(field_name, "support_scopes") or eql(field_name, "support_fields")) value.support_count else count;
            const element = @typeInfo(@TypeOf(ptr)).pointer.child;
            if (overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(ptr), @as(usize, n) * @sizeOf(element))) return false;
        }
    };
    return true;
}
fn scopeLess(f: Facts, a: u32, b: u32) bool {
    const ae = f.scopes.expansions.?[a];
    const be = f.scopes.expansions.?[b];
    const ar = scopeRank(ae, f.scopes.flags.?[a]);
    const br = scopeRank(be, f.scopes.flags.?[b]);
    if (ar != br) return ar < br;
    if (ar == 5 and ae != be) return ae < be;
    if (ar == 3 or ar == 4 or ar == 5 or ar == 6 or ar == 9) {
        if (f.scopes.display_indices.?[a] != f.scopes.display_indices.?[b]) return f.scopes.display_indices.?[a] < f.scopes.display_indices.?[b];
    }
    return std.mem.order(u8, get(f.text, f.scopes.keys.?[a]) catch unreachable, get(f.text, f.scopes.keys.?[b]) catch unreachable) == .lt;
}
fn scopeRank(expansion: u32, flags: u32) u32 {
    return switch (expansion) {
        4 => 0,
        5 => 1,
        6 => 2,
        8 => 3,
        9 => 4,
        10, 11, 12, 15 => 5,
        13 => if (flags & 128 != 0) 9 else 5,
        14 => 6,
        16 => 7,
        17 => 8,
        else => 10,
    };
}
fn ruleLess(r: p.RuleColumns, a: usize, b: usize) bool {
    if (r.metric_ids.?[a] != r.metric_ids.?[b]) return r.metric_ids.?[a] < r.metric_ids.?[b];
    if (r.priorities.?[a] != r.priorities.?[b]) return r.priorities.?[a] < r.priorities.?[b];
    return r.ids.?[a] < r.ids.?[b];
}
fn swapColumns(comptime T: type, value: *T, a: usize, b: usize) void {
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        const ptr = @constCast(@field(value, field_name).?);
        std.mem.swap(@TypeOf(ptr[a]), &ptr[a], &ptr[b]);
    };
}
fn sameMetric(out: Compiled, f: Facts, a: usize, b: usize) bool {
    const ta = out.bindings.template_indices.?[a];
    const tb = out.bindings.template_indices.?[b];
    const t = f.templates;
    return out.bindings.scope_ids.?[a] == out.bindings.scope_ids.?[b] and t.kinds.?[ta] == t.kinds.?[tb] and t.scope_kinds.?[ta] == t.scope_kinds.?[tb] and t.flags.?[ta] == t.flags.?[tb] and t.value_kinds.?[ta] == t.value_kinds.?[tb] and t.minimum.?[ta] == t.minimum.?[tb] and t.maximum.?[ta] == t.maximum.?[tb];
}
fn emit(out: *Compiled, map: *Map, arena: *Arena, f: Facts, ti: u32, si: u32, scope: u64, row: *u32) Error!void {
    const ex = f.templates.expansions.?[ti];
    const key = if (si == p.none) staticKey(ex) else try get(f.text, f.scopes.keys.?[si]);
    var index = if (si == p.none) p.none else f.scopes.display_indices.?[si];
    if (si != p.none and (ex == 16 or ex == 17)) {
        index = 0;
        var other: u32 = 0;
        while (other < f.scopes.count) : (other += 1) if (f.scopes.expansions.?[other] == ex and std.mem.order(u8, try get(f.text, f.scopes.keys.?[other]), key) == .lt) {
            index += 1;
        };
    }
    const address_template = try get(f.text, f.templates.addresses.?[ti]);
    var number: [10]u8 = undefined; // maximum u32 decimal, no row-sized stack storage
    const decimal = if (index == p.none) "" else std.fmt.bufPrint(&number, "{d}", .{index}) catch unreachable;
    const begin = arena.used;
    const token = placeholder(ex);
    var offset: usize = 0;
    if (token.len != 0) {
        while (std.mem.indexOfPos(u8, address_template, offset, token)) |at| {
            _ = try arena.put(address_template[offset..at]);
            _ = try arena.put(decimal);
            offset = at + token.len;
        }
    }
    _ = try arena.put(address_template[offset..]);
    const legacy = Ref{ .offset = begin, .length = arena.used - begin };
    const fi = fieldIndex(f, ti);
    const field = if (fi) |idx| variant(f, ti, idx) else Variant{ .name = address_template, .vendor = 0, .legacy = true };
    const name = try arena.put(field.name);
    var hex: [16]u8 = undefined; // exact old X16 scope formatting
    const scope_text = std.fmt.bufPrint(&hex, "{X:0>16}", .{scope}) catch unreachable;
    const address = if (fi != null) try arena.join(&.{ scope_text, "/", field.name }) else legacy;
    const metric_key = try arena.join(&.{ "metric|", field.name, "|", key });
    const rule_key = try arena.join(&.{ "rule|", try get(f.text, f.templates.names.?[ti]), "|", key });
    const metric = try map.add(arena, 2, metric_key);
    const rule = try map.add(arena, 3, rule_key);
    const r = &out.catalog.rules;
    const b = &out.bindings;
    const n = row.*;
    @constCast(r.ids.?)[n] = rule;
    @constCast(r.source_ids.?)[n] = f.templates.source_ids.?[ti];
    @constCast(r.metric_ids.?)[n] = metric;
    const binding = if (si != p.none and ex == 17) try get(f.text, f.scopes.binding_keys.?[si]) else "";
    @constCast(r.identities.?)[n] = expandedFingerprint(f.templates.fingerprints.?[ti], scope, binding);
    @constCast(r.kinds.?)[n] = f.templates.kinds.?[ti];
    @constCast(r.flags.?)[n] = f.templates.flags.?[ti];
    @constCast(r.priorities.?)[n] = f.templates.priorities.?[ti];
    @constCast(r.value_kinds.?)[n] = f.templates.value_kinds.?[ti];
    @constCast(r.minimum_bits.?)[n] = f.templates.minimum.?[ti];
    @constCast(r.maximum_bits.?)[n] = f.templates.maximum.?[ti];
    @constCast(r.capability_masks.?)[n] = f.templates.masks.?[ti];
    @constCast(b.addresses.?)[n] = address;
    @constCast(b.legacy.?)[n] = if (field.legacy) legacy else .{};
    @constCast(b.field_names.?)[n] = name;
    @constCast(b.device_keys.?)[n] = try arena.put(key);
    @constCast(b.scope_ids.?)[n] = scope;
    @constCast(b.field_codes.?)[n] = if (fi) |idx| f.fields.codes.?[idx] else 0;
    @constCast(b.template_indices.?)[n] = ti;
    @constCast(b.scope_indices.?)[n] = si;
    @constCast(b.source_indices.?)[n] = sourceIndex(f, f.templates.source_ids.?[ti]).?;
    @constCast(b.metric_indices.?)[n] = 0;
    @constCast(b.display_indices.?)[n] = index;
    row.* += 1;
}
