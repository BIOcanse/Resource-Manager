pub const polynomial: u64 = 0x42f0_e1eb_a9ea_3693;

pub fn crc64EcmaWithZeroRange(bytes: []const u8, zero_offset: usize, zero_length: usize) u64 {
    var crc: u64 = 0;
    for (bytes, 0..) |byte, index| {
        const actual: u8 = if (index >= zero_offset and index < zero_offset + zero_length) 0 else byte;
        crc = updateByte(crc, actual);
    }
    return crc;
}

fn updateByte(initial: u64, byte: u8) u64 {
    var crc = initial ^ (@as(u64, byte) << 56);
    var bit: u4 = 0;
    while (bit < 8) : (bit += 1) {
        crc = if ((crc & 0x8000_0000_0000_0000) != 0)
            (crc << 1) ^ polynomial
        else
            crc << 1;
    }
    return crc;
}
