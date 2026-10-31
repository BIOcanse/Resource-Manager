//! The service's region owner (backend_data_area §12 step 1): position
//! declarations in, two unnamed SharedSection regions out, non-owning writer
//! bindings and reader views, borrows with a mapping epoch.
pub const plan = @import("plan.zig");
pub const section = @import("section.zig");
pub const owner = @import("owner.zig");
pub const exports = @import("exports.zig");

pub const RegionClass = plan.RegionClass;
pub const PositionDeclaration = plan.PositionDeclaration;
pub const Declaration = plan.Declaration;
pub const ApplyFailure = plan.ApplyFailure;
pub const Owner = owner.Owner;
pub const Settings = owner.Settings;
pub const Binding = owner.Binding;
pub const ReadView = owner.ReadView;
pub const Borrow = owner.Borrow;

test {
    _ = plan;
    _ = section;
    _ = owner;
    _ = exports;
    _ = @import("tests.zig");
}
