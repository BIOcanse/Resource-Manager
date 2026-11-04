//! Subscription envelope on the ONE Invoke.v1 connection and framer.
//! `data` is the original source/grant/result, never a replacement current.
const std = @import("std");

pub const version = "ResourceManager.TrustedAccess.Invoke.v1/subscriptions/1";
pub const Mode = enum { current, messages };
pub const Kind = enum { registered, current, record, end };

/// Initial registration contains the original H grant/layout (current mode),
/// or the original message-source registration. A current frame is ONLY a wake
/// and its original source/region/version identity. H/R must read current; it
/// is not the snapshot that existed when the notification was issued.
/// Records retain original IDs/order/typed empty/failure; they are never merged.
pub const Frame = struct {
    kind: Kind,
    mode: ?Mode,
    data: std.json.Value,

    pub fn parse(value: std.json.Value) !Frame {
        if (value != .object) return error.InvalidSubscriptionFrame;
        const contract = value.object.get("contract") orelse return error.InvalidSubscriptionFrame;
        if (contract != .string or !std.mem.eql(u8, contract.string, version)) return error.InvalidSubscriptionFrame;
        const kind_value = value.object.get("kind") orelse return error.InvalidSubscriptionFrame;
        if (kind_value != .string) return error.InvalidSubscriptionFrame;
        const kind = std.meta.stringToEnum(Kind, kind_value.string) orelse return error.InvalidSubscriptionFrame;
        // Fixed envelope members are protocol structure, not a product limit.
        if (value.object.count() != (if (kind == .registered) @as(usize, 4) else 3)) return error.InvalidSubscriptionFrame;
        const data = value.object.get("data") orelse return error.InvalidSubscriptionFrame;
        var mode: ?Mode = null;
        if (kind == .registered) {
            const field = value.object.get("mode") orelse return error.InvalidSubscriptionFrame;
            if (field != .string) return error.InvalidSubscriptionFrame;
            mode = std.meta.stringToEnum(Mode, field.string) orelse return error.InvalidSubscriptionFrame;
        } else if (value.object.contains("mode")) return error.InvalidSubscriptionFrame;
        return .{ .kind = kind, .mode = mode, .data = data };
    }
};

/// Assembly supplies these from the single service declarations. No defaults
/// here. Registration is finite; an admitted subscription has no total lifetime
/// deadline, while EACH send remains bounded by P0's cancellable transport.
pub const Limits = struct {
    registration_timeout_ms: u32,
    send_timeout_ms: u32,
    pub fn valid(self: Limits) bool {
        return self.registration_timeout_ms != 0 and self.send_timeout_ms != 0;
    }
};
