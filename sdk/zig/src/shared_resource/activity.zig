const activity_decay = @import("activity_decay");

pub const use_increment: u8 = 32;
pub const decay_numerator: u32 = 7;
pub const decay_denominator: u32 = 8;

pub fn afterSuccessfulUse(current: u8) u8 {
    return current +| use_increment;
}

pub fn afterGenerations(current: u8, generations: u64) u8 {
    return @intCast(activity_decay.decay(
        current,
        generations,
        decay_numerator,
        decay_denominator,
    ));
}

test "public activity is bounded and generation based" {
    try @import("std").testing.expectEqual(@as(u8, 32), afterSuccessfulUse(0));
    try @import("std").testing.expectEqual(@as(u8, 255), afterSuccessfulUse(250));
    try @import("std").testing.expectEqual(@as(u8, 28), afterGenerations(32, 1));
}
