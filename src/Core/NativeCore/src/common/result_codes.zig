pub const ResultCode = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    unavailable = 3,
    no_data = 4,
    buffer_too_small = 5,
    stale_frame = 6,
    out_of_memory = 7,
    pdh_error = 8,
};
