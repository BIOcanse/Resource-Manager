// Identity decisions for adapter-level PDH counters. No settings, allocations,
// query, clock or retained map. Input is the original immutable topology view.
pub const Adapter = extern struct { luid: u64, index: i32, software: u32 };
fn eligible(adapter: Adapter) bool { return adapter.software == 0 and adapter.index >= 0 and adapter.luid != 0; }
fn uniqueLuid(adapters: []const Adapter, adapter: Adapter) bool {
    if (!eligible(adapter)) return false;
    var count: usize = 0;
    for (adapters) |other| if (eligible(other) and other.luid == adapter.luid) { count += 1; };
    return count == 1;
}
fn duplicateIndex(adapters: []const Adapter, index: i32) bool {
    var count: usize = 0;
    for (adapters) |adapter| if (adapter.index == index and uniqueLuid(adapters, adapter)) { count += 1; };
    return count > 1;
}
pub fn selected(adapters: []const Adapter, adapter: Adapter) bool {
    return uniqueLuid(adapters, adapter) and !duplicateIndex(adapters, adapter.index);
}
pub fn ambiguousIndex(adapters: []const Adapter, index: i32) bool {
    for (adapters) |adapter| {
        if (eligible(adapter) and adapter.index == index and !uniqueLuid(adapters, adapter)) return true;
    }
    return duplicateIndex(adapters, index);
}
pub fn ambiguousCount(adapters: []const Adapter) u32 {
    var count: u32 = 0;
    for (adapters, 0..) |adapter, i| {
        if (!eligible(adapter) or !ambiguousIndex(adapters, adapter.index)) continue;
        var seen = false;
        for (adapters[0..i]) |prior| if (eligible(prior) and prior.index == adapter.index) { seen = true; break; };
        if (!seen) count += 1;
    }
    return count;
}
pub fn selectedCount(adapters: []const Adapter) u32 {
    var count: u32 = 0;
    for (adapters) |adapter| if (selected(adapters, adapter)) { count += 1; };
    return count;
}
pub fn indexOf(adapters: []const Adapter, luid: u64) ?u32 {
    for (adapters) |adapter| if (adapter.luid == luid and selected(adapters, adapter) and !ambiguousIndex(adapters, adapter.index)) {
        return @intCast(adapter.index);
    };
    return null;
}
