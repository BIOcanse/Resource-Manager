//! Adapter for the shared response framing engine.
pub const io = @import("socket_io");
const impl = @import("response_engine.zig").Implementation(io, false);
pub const Limits = impl.Limits;
pub const Header = impl.Header;
pub const Body = impl.Body;
pub const Head = impl.Head;
pub const Event = impl.Event;
pub const Response = impl.Response;
pub const FinalOutput = impl.FinalOutput;
pub const validName = impl.validName;
pub const validValue = impl.validValue;
