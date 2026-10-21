//! Test-only exports for the C2 full-open fixture.
const maxima = @import("owned/owner_maxima_tests.zig");

pub const FixtureWithTables = maxima.FixtureWithTables;
pub const makeGeneratedTables = maxima.makeGeneratedTables;
pub const memory_region = maxima.memory_region;
