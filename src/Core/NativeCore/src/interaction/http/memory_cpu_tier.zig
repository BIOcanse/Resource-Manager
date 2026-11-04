//! HTTP encoding only. The settings view owns query interpretation. Z1's
//! UTF-16/JSON primitive writes directly into the caller's final output region.
//! Preserve the existing wire: "source" means tier/user override, not publication
//! or deployment provenance. Runtime fields refer to CLR GC, not native allocation.
const json = @import("crash_reports.zig");
pub const tier = @import("../settings/memory_cpu_tier_view.zig");
pub const Callbacks = tier.Callbacks;
pub const OutputCallbacks = json.OutputCallbacks;
pub const Writer = json.Writer;

fn acceptText(context: ?*anyopaque, text: ?[*]const u16, length: usize) callconv(.c) i32 {
    return json.writeString(@ptrCast(@alignCast(context.?)), text, length);
}
fn writeText(w: *Writer, value: tier.TextRef) tier.Error!void {
    try value.visit(w, acceptText);
}
fn boolean(w: *Writer, value: bool) void {
    w.raw(if (value) "true" else "false");
}
fn optional(w: *Writer, value: ?i64) void {
    if (value) |integer| w.integer(integer) else w.raw("null");
}
fn writeValue(w: *Writer, source: tier.Source, index: usize, child: usize, cell: bool) tier.Error!void {
    if (try source.scalar(index, child, if (cell) .cell_present else .value_present) != 0)
        w.integer(try source.scalar(index, child, if (cell) .cell_integer else .value_integer))
    else
        try writeText(w, source.text(index, child, if (cell) .cell_choice else .value_choice));
}
fn encode(w: *Writer, view: tier.View) tier.Error!void {
    const s = view.source;
    w.raw("{\"tier\":");
    w.integer(try s.scalar(0, 0, .tier));
    w.raw(",\"options\":[");
    const options = try s.count(0, .option_count);
    var index: usize = 0;
    while (index < options) : (index += 1) {
        if (index != 0) w.raw(",");
        w.raw("{\"name\":");
        try writeText(w, s.text(index, 0, .name));
        w.raw(",\"value\":");
        try writeValue(w, s, index, 0, false);
        w.raw(",\"unit\":");
        try writeText(w, s.text(index, 0, .unit));
        w.raw(",\"range\":{\"kind\":");
        try writeText(w, s.text(index, 0, .kind));
        if (try view.integerRange(index)) {
            w.raw(",\"min\":");
            w.integer(try s.scalar(index, 0, .minimum));
            w.raw(",\"max\":");
            w.integer(try s.scalar(index, 0, .maximum));
            w.raw(",\"unset\":");
            boolean(w, try s.scalar(index, 0, .unset) != 0);
        } else {
            w.raw(",\"choices\":[");
            const choices = try s.count(index, .choice_count);
            var child: usize = 0;
            while (child < choices) : (child += 1) {
                if (child != 0) w.raw(",");
                try writeText(w, s.text(index, child, .choice));
            }
            w.raw("]");
        }
        w.raw("},\"tiers\":[");
        const cells = try s.count(index, .cell_count);
        var child: usize = 0;
        while (child < cells) : (child += 1) {
            if (child != 0) w.raw(",");
            w.raw("{\"value\":");
            try writeValue(w, s, index, child, true);
            w.raw(",\"category\":");
            try writeText(w, s.text(index, child, .cell_category));
            w.raw("}");
        }
        w.raw("],\"category\":");
        try writeText(w, s.text(index, 0, .category));
        w.raw(",\"source\":");
        try writeText(w, s.text(index, 0, .source));
        w.raw(",\"apply\":");
        try writeText(w, s.text(index, 0, .apply));
        if (try view.restart(index)) {
            const runtime = try view.runtimeValue(index);
            w.raw(",\"runningValue\":");
            optional(w, runtime.running);
            w.raw(",\"pendingRestart\":");
            boolean(w, runtime.running != runtime.next);
        }
        w.raw("}");
    }
    w.raw("],\"runtimeConfig\":{\"writable\":");
    const has_runtime = try view.hasRuntime();
    boolean(w, has_runtime and try s.scalar(0, 0, .writable) != 0);
    w.raw(",\"reason\":");
    try writeText(w, s.text(0, 0, if (has_runtime) .reason else .not_installed_reason));
    w.raw(",\"files\":[");
    const files = if (has_runtime) try s.count(0, .file_count) else 0;
    index = 0;
    while (index < files) : (index += 1) {
        if (index != 0) w.raw(",");
        w.raw("{\"process\":");
        try writeText(w, s.text(index, 0, .file_process));
        w.raw(",\"path\":");
        try writeText(w, s.text(index, 0, .file_path));
        w.raw("}");
    }
    w.raw("],\"nativeUiRunningValue\":");
    try writeText(w, s.text(0, 0, .native_ui_running));
    w.raw(",\"lastFailure\":");
    if (has_runtime) try writeText(w, s.text(0, 0, .last_failure)) else w.raw("null");
    w.raw("},\"ignored\":[");
    const ignored = try s.count(0, .ignored_count);
    index = 0;
    while (index < ignored) : (index += 1) {
        if (index != 0) w.raw(",");
        w.raw("{\"code\":");
        try writeText(w, s.text(index, 0, .ignored_code));
        w.raw(",\"path\":");
        try writeText(w, s.text(index, 0, .ignored_path));
        w.raw("}");
    }
    w.raw("]}");
}

pub fn encodeStream(context: ?*anyopaque, callbacks: ?*const Callbacks, output: ?*const OutputCallbacks) i32 {
    const source = tier.Source{ .context = context, .callbacks = callbacks orelse return json.Code.invalid };
    var writer = Writer{ .buffer = null, .capacity = 0, .context = context, .output_callbacks = output orelse return json.Code.invalid };
    encode(&writer, .{ .source = source }) catch |err| return switch (err) {
        error.UnsupportedRuntimeOption => 5,
        error.Source => json.Code.callback,
        error.Invalid => json.Code.invalid,
    };
    if (writer.failure != 0) return writer.failure;
    if (output.?.commit(context, writer.used) != 0) return json.Code.callback;
    return 0;
}
