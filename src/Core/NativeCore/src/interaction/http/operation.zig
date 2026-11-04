//! One request lifecycle engine shared by neutral boundary and production dispatch.
const std = @import("std");
pub fn Engine(comptime API: type) type {
    return struct {
        const Operation = @This();
        pub const Phase = enum { fresh, pending, output, done, released };
        const Call = API.Call;
        const Family = API.Family;
        const WakeArg = API.WakeArg;
        const Request = API.Request;
        const Progress = API.Progress;
        const Event = API.Event;
        call: Call,
        family: ?Family = null,
        phase: Phase = .fresh,
        more: bool = false,
        cancel_delivered: bool = false,
        response_status: ?u16 = null,

        pub fn init(state: []u8, wake: WakeArg) Operation {
            return .{ .call = .{ .state = state, .wake = wake } };
        }
        pub fn done(self: *const Operation) bool {
            return self.phase == .done or self.phase == .released;
        }
        pub const start = API.startOperation;
        pub const cancel = API.cancelOperation;
        pub fn isPending(self: *const Operation) bool {
            return self.phase == .pending;
        }
        pub fn startFamily(self: *Operation, family: Family, admitted: Request, output: []u8) !Progress {
            if (self.phase != .fresh) return error.AlreadyStarted;
            try API.validateCapacity(family.capacity, self.call.state, output);
            if (family.can_suspend and !API.hasWake(self.call.wake)) return error.CompletionSinkRequired;
            self.family = family;
            self.call.state = self.call.state[0..family.capacity.state_bytes];
            @memset(self.call.state, 0);
            const progress = family.begin(family.context, admitted, &self.call, output) catch |err| {
                return API.familyFailure(self, err, output);
            };
            // Invalid progress retains the owner for explicit cancel/join. It is
            // never evidence that an accepted command did not execute.
            self.phase = .pending;
            return self.accept(progress, output);
        }
        pub fn step(self: *Operation, event: Event, output: []u8) !Progress {
            if (event == .cancel) return self.cancel(output);
            if (self.done()) return .done;
            if (self.phase == .output) {
                if (event != .writable) return error.OutputStillBorrowed;
                // writable means terminal success/failure/cancel of the whole send,
                // not an OS partial send. Exact output bytes are now reusable.
                self.phase = .pending;
                if (!self.more) {
                    self.phase = .done;
                    return .done;
                }
                if (self.call.isCancelled()) return self.deliverCancel(output);
            } else if (self.phase != .pending or event != .ready) return error.InvalidOperationEvent;
            return self.invoke(event, output);
        }
        pub fn cancelDeferred(self: *Operation, output: []u8) !Progress {
            if (self.done()) return .done;
            self.call.cancelled.store(true, .release);
            if (self.phase == .fresh) {
                self.phase = .done;
                return .done;
            }
            // Never let cancellation overwrite bytes still borrowed by Winsock.
            if (self.phase == .output) return .pending;
            return self.deliverCancel(output);
        }
        /// Transport adapter calls this only after its outstanding send drained.
        pub fn cancelAfterDrain(self: *Operation, output: []u8) !Progress {
            if (self.done()) return .done;
            self.call.cancelled.store(true, .release);
            if (self.phase == .fresh or (self.phase == .output and !self.more)) {
                self.phase = .done;
                return .done;
            }
            if (self.phase == .output) self.phase = .pending;
            return self.deliverCancel(output);
        }
        fn deliverCancel(self: *Operation, output: []u8) !Progress {
            if (self.cancel_delivered) return .pending;
            self.cancel_delivered = true;
            return self.invoke(.cancel, output);
        }
        fn invoke(self: *Operation, event: Event, output: []u8) !Progress {
            const family = self.family orelse return error.InvalidOperationEvent;
            try API.validateCapacity(family.capacity, self.call.state, output);
            const progress = family.step(family.context, &self.call, event, output) catch |err| {
                return API.familyFailure(self, err, output);
            };
            return self.accept(progress, output);
        }
        pub fn accept(self: *Operation, progress: Progress, output: []u8) !Progress {
            switch (progress) {
                .pending => {
                    if (!self.family.?.can_suspend or !API.hasWake(self.call.wake)) return error.InvalidFamilyProgress;
                    self.phase = .pending;
                },
                .output => |result| {
                    try API.validateProgressResult(result, output);
                    if (self.call.isCancelled()) {
                        if (result.more) return error.OutputAfterCancellation;
                        self.phase = .done;
                        return .done;
                    }
                    if (result.more and (!self.family.?.can_suspend or !API.hasWake(self.call.wake))) return error.InvalidFamilyProgress;
                    if (self.response_status) |status| {
                        if (result.status != status or result.headers.len != 0 or result.total_length != null or
                            !API.continuationValid(result))
                            return error.ResponseHeadAlreadySent;
                    }
                    self.response_status = result.status;
                    self.more = result.more;
                    self.phase = .output;
                },
                .done => {
                    self.phase = .done;
                    if (self.response_status == null and !self.call.isCancelled()) return error.FamilyEndedWithoutResponse;
                },
            }
            return progress;
        }
        /// Caller invokes only after final IO AND source terminal completion. release
        /// must join callbacks before returning; wake retirement follows, never leads.
        pub fn deinit(self: *Operation) !void {
            if (self.phase == .released) return;
            if (self.phase != .done) return error.OperationNotDrained;
            if (self.family) |family| family.release(family.context, &self.call);
            self.family = null;
            self.phase = .released;
        }
    };
}
