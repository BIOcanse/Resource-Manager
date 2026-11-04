const std = @import("std");

// Windows MIB_TCP{,6}ROW_OWNER_PID layout, not configurable policy values.
pub const ipv4: u16 = 2;
pub const ipv6: u16 = 23;
const established: u32 = 5;

pub const Endpoint = extern struct {
    address: [16]u8,
    scope: u32,
    port: u16,
    family: u16,
};

pub const Connection = extern struct { client: Endpoint, server: Endpoint };
pub const FindError = error{ InvalidEndpoint, InvalidTable, AmbiguousOwner };

fn word(bytes: []const u8, offset: usize) u32 {
    return std.mem.readInt(u32, bytes[offset..][0..4], .little);
}

fn port(bytes: []const u8, offset: usize) u16 {
    // Windows stores the network-order port in the low two bytes of a DWORD.
    return std.mem.readInt(u16, bytes[offset..][0..2], .big);
}

pub fn validate(connection: Connection) FindError!void {
    if (connection.client.family != connection.server.family or
        (connection.client.family != ipv4 and connection.client.family != ipv6) or
        connection.client.port == 0 or connection.server.port == 0)
        return error.InvalidEndpoint;
}

// The entire observation belongs to this call. Nothing is cached across calls:
// the second connection proof after opening the process must see a fresh table.
// Bounds derive from the supplied (already capacity-limited) observation.
pub fn find(bytes: []const u8, connection: Connection) FindError!?u32 {
    try validate(connection);
    if (bytes.len < @sizeOf(u32)) return error.InvalidTable;
    const six = connection.client.family == ipv6;
    const width: usize = if (six) 56 else 24;
    const count: usize = word(bytes, 0);
    if (count > (bytes.len - @sizeOf(u32)) / width) return error.InvalidTable;
    var owner: ?u32 = null;
    var index: usize = 0;
    while (index < count) : (index += 1) {
        const row = bytes[@sizeOf(u32) + index * width ..][0..width];
        if (word(row, if (six) 48 else 0) != established or
            port(row, if (six) 20 else 8) != connection.client.port or
            port(row, if (six) 44 else 16) != connection.server.port) continue;
        if (six) {
            if (!std.mem.eql(u8, row[0..16], &connection.client.address) or
                !std.mem.eql(u8, row[24..40], &connection.server.address) or
                word(row, 16) != connection.client.scope or
                word(row, 40) != connection.server.scope) continue;
        } else {
            if (!std.mem.eql(u8, row[4..8], connection.client.address[0..4]) or
                !std.mem.eql(u8, row[12..16], connection.server.address[0..4])) continue;
        }
        const pid = word(row, if (six) 52 else 20);
        // Two matching rows, including two with the same PID, never prove identity.
        if (pid == 0 or pid > std.math.maxInt(i32) or owner != null) return error.AmbiguousOwner;
        owner = pid;
    }
    return owner;
}
