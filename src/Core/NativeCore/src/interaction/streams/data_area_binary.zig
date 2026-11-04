//! Call-local output for the C04 binary carrier. This is NOT a wire schema,
//! a grant, or a stable source reader. R/S and the same-version TS codec must
//! supply those contracts before the subscription endpoint can be connected.
//! The codec writes once into its owner's final output lease. Source borrows
//! end synchronously; only the completed output may survive an async send.
const std = @import("std");

pub const Error = error{
    InvalidLimit,
    FrameTooLarge,
    OutputTooSmall,
    LengthOverflow,
    AliasedInput,
    EmptyFrame,
    Finished,
    UncommittedRange,
};

/// Construct inside one encoding call, never store on a source/subscription.
/// All behavior limits come from the caller's configured output policy.
pub const Writer = struct {
    buffer: []u8,
    maximum_frame_bytes: usize,
    used: usize = 0,
    failure: ?Error = null,
    finished: bool = false,
    pending_end: ?usize = null,

    pub fn init(buffer: []u8, maximum_frame_bytes: usize) Error!Writer {
        if (maximum_frame_bytes == 0) return error.InvalidLimit;
        return .{ .buffer = buffer, .maximum_frame_bytes = maximum_frame_bytes };
    }

    /// R may stable-copy directly into this final range, avoiding a second
    /// payload staging buffer. Commit only after every byte has been filled
    /// successfully. No pointer here outlives the output lease.
    pub fn reserve(self: *Writer, length: usize) Error![]u8 {
        if (self.failure) |err| return err;
        if (self.finished) return error.Finished;
        if (self.pending_end != null) return self.fail(error.UncommittedRange);
        const end = std.math.add(usize, self.used, length) catch return self.fail(error.LengthOverflow);
        if (end > self.maximum_frame_bytes) return self.fail(error.FrameTooLarge);
        if (end > self.buffer.len) return self.fail(error.OutputTooSmall);
        const result = self.buffer[self.used..end];
        self.pending_end = end;
        return result;
    }

    /// Successful stable reading/encoding commits its reserved final range.
    /// A failed reader must propagate its error and leave the range pending.
    pub fn commit(self: *Writer) Error!void {
        if (self.failure) |err| return err;
        if (self.finished) return error.Finished;
        self.used = self.pending_end orelse return self.fail(error.UncommittedRange);
        self.pending_end = null;
    }

    /// Small already-encoded metadata or a borrowed stable payload. Sources
    /// and output must be disjoint; append is not an in-place transform.
    pub fn append(self: *Writer, bytes: []const u8) Error!void {
        if (self.failure) |err| return err;
        if (self.finished) return error.Finished;
        if (overlaps(self.buffer, bytes)) return self.fail(error.AliasedInput);
        const destination = try self.reserve(bytes.len);
        @memcpy(destination, bytes);
        try self.commit();
    }

    /// Codec primitive only: no protocol fields, magic, version or defaults
    /// are chosen here. Those remain blocked on the common frozen schema.
    pub fn integer(self: *Writer, comptime T: type, value: T, endian: std.builtin.Endian) Error!void {
        const destination = try self.reserve(@sizeOf(T));
        std.mem.writeInt(T, destination[0..@sizeOf(T)], value, endian);
        try self.commit();
    }

    /// A failed codec cannot accidentally publish its prefix, even if it
    /// swallowed a reserve/append error. A completed writer cannot be reused.
    pub fn finish(self: *Writer) Error![]const u8 {
        if (self.failure) |err| return err;
        if (self.finished) return error.Finished;
        if (self.pending_end != null) return self.fail(error.UncommittedRange);
        if (self.used == 0) return self.fail(error.EmptyFrame);
        self.finished = true;
        return self.buffer[0..self.used];
    }

    fn fail(self: *Writer, err: Error) Error {
        self.failure = err;
        return err;
    }
};

/// Exactly one synchronous codec invocation; no count pass, retry, allocation,
/// fallback or cached current. Errors retain the codec's typed meaning. A
/// caller must not hand output to the sending bridge unless this succeeds.
pub fn encode(buffer: []u8, maximum_frame_bytes: usize, context: anytype, codec: anytype) ![]const u8 {
    var writer = try Writer.init(buffer, maximum_frame_bytes);
    try codec(context, &writer);
    return writer.finish();
}

fn overlaps(output: []u8, input: []const u8) bool {
    if (output.len == 0 or input.len == 0) return false;
    const output_start = @intFromPtr(output.ptr);
    const input_start = @intFromPtr(input.ptr);
    // Subtraction avoids adding possibly malicious lengths to pointer values.
    return if (output_start <= input_start)
        input_start - output_start < output.len
    else
        output_start - input_start < input.len;
}
