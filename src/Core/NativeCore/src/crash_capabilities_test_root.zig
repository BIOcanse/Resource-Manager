pub const contract = @import("interaction/contracts/crash_reports.zig");
pub const invoke = @import("interaction/capabilities/crash_reports_abi.zig").rm_crash_capability_invoke;

// Keep the focused crash tests rooted beside the product ABI so the original
// HTTP view can import its sibling domain contracts without copying sources.
const http_view = @import("interaction/http/crash_report_view.zig");
pub const Cursor = http_view.Cursor;
pub const encodeStream = http_view.encodeStream;
