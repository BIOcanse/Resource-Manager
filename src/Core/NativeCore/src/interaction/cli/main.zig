//! Independent CLI process. Production assembly supplies rm_cli_runtime using
//! the sole P0 Connector; tests replace that I/O boundary at build time only.
const std = @import("std");
const builtin = @import("builtin");
const client_module = @import("rm_invocation_client");
const contract = @import("rm_invocation_contract");
pub const arguments = @import("rm_cli_arguments");
pub const input = @import("rm_cli_input");
pub const output = @import("rm_cli_output");

// Grammar only: local help never pretends that a service directory is present.
pub const syntax =
    \\ResourceManager.Cli [--format text|json] help [operation]
    \\ResourceManager.Cli [--format text|json] capabilities
    \\ResourceManager.Cli call <operation> [--format text|json] [--input <json-file> | --<parameter> <value> ...]
    \\ResourceManager.Cli query <operation> [--format text|json] [--input <json-file> | --<parameter> <value> ...]
    \\Operation help and parameter types require the service directory.
    \\
;

pub const Cancellation = struct {
    requested: std.atomic.Value(bool) = .init(false),

    pub fn request(self: *Cancellation) void {
        self.requested.store(true, .release);
    }
    pub fn token(self: *Cancellation) contract.Cancel {
        return .{ .context = self, .is_cancelled = cancelled };
    }
    fn cancelled(context: ?*anyopaque) bool {
        const self: *Cancellation = @ptrCast(@alignCast(context.?));
        return self.requested.load(.acquire);
    }
};

// There is one foreground call in this process. The OS handler touches only an
// atomic flag; the shared transport must honor Control.cancel during blocked IO.
var console_cancel: Cancellation = .{};
extern "kernel32" fn SetConsoleCtrlHandler(?*const fn (u32) callconv(.winapi) i32, i32) callconv(.winapi) i32;
extern "kernel32" fn GetConsoleCP() callconv(.winapi) u32;
fn consoleHandler(event: u32) callconv(.winapi) i32 {
    // Windows CTRL_C_EVENT / CTRL_BREAK_EVENT are OS protocol constants.
    if (event != 0 and event != 1) return 0;
    console_cancel.request();
    return 1;
}

pub fn main(init: std.process.Init) void {
    const status = process(init);
    // process has returned, so every defer (including connector cleanup) ran.
    std.process.exit(@backingInt(status));
}

fn process(init: std.process.Init) output.Exit {
    const allocator = init.gpa;
    var argv_arena = std.heap.ArenaAllocator.init(allocator);
    defer argv_arena.deinit();
    const argv_z = init.minimal.args.toSlice(argv_arena.allocator()) catch |err| return diagnostic(init.io, err, .syntax);
    const argv = argv_arena.allocator().alloc([]const u8, argv_z.len) catch |err| return diagnostic(init.io, err, .syntax);
    for (argv_z, argv) |arg, *target| target.* = arg;
    var options = arguments.parse(allocator, if (argv.len == 0) &.{} else argv[1..]) catch |err| return diagnostic(init.io, err, .syntax);
    defer options.deinit(allocator);
    if (options.command == .help and options.operation == null) {
        return emitSyntax(allocator, init.io, options.format);
    }
    // CLI2 owns watch. Until its real shared subscription seam is joined this
    // explicit syntax error cannot advertise or start a pretend subscription.
    if (options.command == .watch) return diagnostic(init.io, error.WatchNotIntegrated, .syntax);

    console_cancel.requested.store(false, .release);
    var handler_registered = false;
    if (builtin.os.tag == .windows) {
        // Redirected/no-console child processes still support synchronous CLI
        // calls. A console has a nonzero input code page; only that process can
        // receive the console's keyboard control events. No console is created.
        // https://learn.microsoft.com/en-us/windows/console/getconsolecp
        if (GetConsoleCP() != 0) {
            if (SetConsoleCtrlHandler(consoleHandler, 1) == 0) return diagnostic(init.io, error.ConsoleCancellationUnavailable, .transport);
            handler_registered = true;
        }
    }
    defer if (builtin.os.tag == .windows and handler_registered) {
        _ = SetConsoleCtrlHandler(consoleHandler, 0);
    };

    // Required assembly seam, with no release fallback. Runtime.init owns the
    // OS Connector and authoritative limits; Runtime.deinit closes its state.
    // It must not open a service instance or link service capabilities/owners.
    var runtime = @import("rm_cli_runtime").init(allocator, init.io) catch |err| {
        const failure: contract.Failure = .{
            .kind = if (console_cancel.token().is_cancelled(console_cancel.token().context)) .cancelled else .transport,
            .effect = .not_started,
        };
        _ = diagnostic(init.io, err, output.failureExit(failure));
        return emitFailure(allocator, init.io, options.format, failure);
    };
    defer runtime.deinit();
    return run(runtime.client.allocator, init.io, runtime.client, options, console_cancel.token());
}

/// Options and directory/payload buffers remain local until the synchronous
/// client call and output finish. The client owns each actual connection scope.
pub fn run(allocator: std.mem.Allocator, io: std.Io, client: client_module.Client, options: arguments.Options, cancel: contract.Cancel) output.Exit {
    if (options.command == .help and options.operation == null) {
        return emitSyntax(allocator, io, options.format);
    }
    if (options.command == .watch) return diagnostic(io, error.WatchNotIntegrated, .syntax);
    var listed = client.list(cancel);
    defer listed.deinit();
    const response = switch (listed) {
        .failure => |failure| return emitFailure(allocator, io, options.format, failure),
        .response => |owned| owned.value,
    };
    if (response.status != .ok) return emitResponse(allocator, io, options.format, response);
    if (options.command == .capabilities) {
        _ = directoryOperations(response.payload) catch |err| return diagnostic(io, err, .transport);
        return emitResponse(allocator, io, options.format, response);
    }
    const descriptor = findDescriptor(response.payload, options.operation.?) catch |err| {
        return diagnostic(io, err, if (err == error.UnknownOperation) .syntax else .transport);
    };
    if (options.command == .help) {
        const bytes = output.renderHelp(allocator, options.format, descriptor) catch |err| return diagnostic(io, err, .transport);
        defer allocator.free(bytes);
        std.Io.File.stdout().writeStreamingAll(io, bytes) catch |err| return diagnostic(io, err, .transport);
        return .success;
    }
    const kind = descriptor.object.get("kind").?.string;
    // Read/scan is declared by the service; no prefix/operation whitelist and
    // no invented current-read replacing crash Read's original rescan.
    if (options.command == .query and !std.mem.eql(u8, kind, "current_read") and !std.mem.eql(u8, kind, "scan"))
        return diagnostic(io, error.QueryRequiresReadCapability, .syntax);
    if (std.mem.eql(u8, kind, "subscription")) return diagnostic(io, error.SubscriptionRequiresWatch, .syntax);
    const schema = descriptor.object.get("inputSchema").?;
    var payload = if (options.input_file) |path| fromFile(allocator, io, path, schema, client.limits.maximum_frame_bytes, cancel) catch |err| {
        return diagnostic(io, err, if (err == error.Cancelled) .cancelled else .syntax);
    } else input.fromParameters(allocator, schema, options.parameters) catch |err| return diagnostic(io, err, .syntax);
    defer payload.deinit();
    var invoked = client.invoke(.{ .operation = options.operation.?, .payload = payload.value }, cancel);
    defer invoked.deinit();
    return switch (invoked) {
        .failure => |failure| emitFailure(allocator, io, options.format, failure),
        .response => |owned| emitResponse(allocator, io, options.format, owned.value),
    };
}

/// Borrow a descriptor directly from the actual service result. Never keep a
/// parallel registry, authorization table, output schema or defaults here.
pub fn findDescriptor(directory: std.json.Value, operation: []const u8) !std.json.Value {
    const operations = try directoryOperations(directory);
    var found: ?std.json.Value = null;
    for (operations) |descriptor| {
        const id = descriptor.object.get("operation").?.string;
        if (std.mem.eql(u8, id, operation)) {
            if (found != null) return error.InvalidDirectory;
            found = descriptor;
        }
    }
    return found orelse error.UnknownOperation;
}

fn directoryOperations(directory: std.json.Value) ![]const std.json.Value {
    if (directory != .object) return error.InvalidDirectory;
    const version = directory.object.get("contract") orelse return error.InvalidDirectory;
    if (version != .string or !std.mem.eql(u8, version.string, contract.version)) return error.InvalidDirectoryVersion;
    const operations = directory.object.get("operations") orelse return error.InvalidDirectory;
    if (operations != .array) return error.InvalidDirectory;
    for (operations.array.items) |descriptor| {
        if (descriptor != .object) return error.InvalidDirectory;
        const id = descriptor.object.get("operation") orelse return error.InvalidDirectory;
        const kind = descriptor.object.get("kind") orelse return error.InvalidDirectory;
        if (id != .string or !contract.operationValid(id.string) or kind != .string) return error.InvalidDirectory;
        _ = std.meta.stringToEnum(contract.Kind, kind.string) orelse return error.InvalidDirectory;
        _ = descriptor.object.get("inputSchema") orelse return error.InvalidDirectory;
        _ = descriptor.object.get("outputSchema") orelse return error.InvalidDirectory;
    }
    return operations.array.items;
}

fn fromFile(allocator: std.mem.Allocator, io: std.Io, path: []const u8, schema: std.json.Value, maximum_bytes: u32, cancel: contract.Cancel) !input.Payload {
    try cancel.check();
    // A payload cannot exceed the authoritative client frame limit; final
    // envelope overhead is checked by the same shared client's encoder.
    const bytes = try std.Io.Dir.cwd().readFileAlloc(io, path, allocator, .limited(maximum_bytes));
    defer allocator.free(bytes);
    try cancel.check();
    return input.fromJson(allocator, schema, bytes);
}

fn emitResponse(allocator: std.mem.Allocator, io: std.Io, format: arguments.Format, response: contract.Response) output.Exit {
    const bytes = output.renderResponse(allocator, format, response) catch |err| return diagnostic(io, err, .transport);
    defer allocator.free(bytes);
    std.Io.File.stdout().writeStreamingAll(io, bytes) catch |err| return diagnostic(io, err, .transport);
    return output.responseExit(response);
}

fn emitFailure(allocator: std.mem.Allocator, io: std.Io, format: arguments.Format, failure: contract.Failure) output.Exit {
    const bytes = output.renderFailure(allocator, format, failure) catch |err| return diagnostic(io, err, .transport);
    defer allocator.free(bytes);
    std.Io.File.stdout().writeStreamingAll(io, bytes) catch |err| return diagnostic(io, err, .transport);
    _ = diagnostic(io, error.InvocationFailed, output.failureExit(failure));
    return output.failureExit(failure);
}

fn emitSyntax(allocator: std.mem.Allocator, io: std.Io, format: arguments.Format) output.Exit {
    if (format == .text) {
        std.Io.File.stdout().writeStreamingAll(io, syntax) catch |err| return diagnostic(io, err, .transport);
    } else {
        var writer: std.Io.Writer.Allocating = .init(allocator);
        defer writer.deinit();
        std.json.Stringify.value(.{ .syntax = syntax }, .{}, &writer.writer) catch |err| return diagnostic(io, err, .transport);
        writer.writer.writeByte('\n') catch |err| return diagnostic(io, err, .transport);
        std.Io.File.stdout().writeStreamingAll(io, writer.written()) catch |err| return diagnostic(io, err, .transport);
    }
    return .success;
}

fn diagnostic(io: std.Io, err: anyerror, status: output.Exit) output.Exit {
    std.Io.File.stderr().writeStreamingAll(io, @errorName(err)) catch {};
    std.Io.File.stderr().writeStreamingAll(io, "\n") catch {};
    return status;
}
