const std = @import("std");

pub const DeterministicSum = struct {
    sum: f64 = 0,
    correction: f64 = 0,

    pub fn add(self: *DeterministicSum, value: f64) bool {
        if (!std.math.isFinite(value) or value < 0) return false;
        const next = self.sum + value;
        if (!std.math.isFinite(next)) return false;
        if (@abs(self.sum) >= @abs(value)) {
            self.correction += (self.sum - next) + value;
        } else {
            self.correction += (value - next) + self.sum;
        }
        if (!std.math.isFinite(self.correction)) return false;
        self.sum = next;
        return true;
    }

    pub fn total(self: DeterministicSum) ?f64 {
        const value = self.sum + self.correction;
        if (!std.math.isFinite(value) or value < 0) return null;
        return if (value == 0) 0 else value;
    }
};
