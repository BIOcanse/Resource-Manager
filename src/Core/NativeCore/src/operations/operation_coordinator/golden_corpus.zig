const abi_layout = @import("abi_layout.zig");

test "operation coordinator ABI sizes and offsets are frozen" {
    try abi_layout.expect();
}

test "operation coordinator enums and validity bits are frozen" {
    try abi_layout.expectValues();
}
