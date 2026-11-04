//! Native HTTP work-slot admission only. The common host still owns requests,
//! cancellation and IO. No timer, wait queue, policy or family defaults here.
const std = @import("std");
const work_area = @import("ledger_work_area");

/// Composition declares one stable UID/capacity per concurrent writer. Areas
/// belong to this host; borrowed domain areas must not be passed to this owner.
/// Keep Slots at a stable address, and keep every Use local to its run scope.
pub fn Slots(comptime Ledger: type, comptime count: usize) type {
    if (count == 0) @compileError("request work slots require declared concurrency");
    return struct {
        const Self = @This();
        const Cell = struct { busy: bool = false, ending: bool = false, ticket: u64 = 0 };
        pub const Acquire = union(enum) { acquired: Use, busy, closing, exhausted, ledger_failure: i32 };
        pub const Close = union(enum) { closed, busy, ledger_failure: i32 };
        pub const AccessError = error{EndedUse};

        ledger: *Ledger,
        areas: [count]Ledger.Area,
        cells: [count]Cell = @splat(.{}),
        gate: std.atomic.Value(bool) = .init(false),
        stopping: bool = false,
        draining: bool = false,
        closed: bool = false,

        pub fn init(ledger: *Ledger, areas: [count]Ledger.Area) error{InvalidDeclaration}!Self {
            for (areas, 0..) |area, index| {
                if (area.bytes == 0) return error.InvalidDeclaration;
                for (areas[0..index]) |earlier| {
                    if (std.mem.eql(u8, &area.uid, &earlier.uid)) return error.InvalidDeclaration;
                }
            }
            return .{ .ledger = ledger, .areas = areas };
        }

        fn lock(self: *Self) void {
            while (self.gate.cmpxchgWeak(false, true, .acquire, .monotonic) != null)
                std.atomic.spinLoopHint();
        }
        fn unlock(self: *Self) void {
            self.gate.store(false, .release);
        }

        /// Immediate bounded rejection: at most count cells, no queued writable
        /// area. A host may wait using its bounded control state and retry on its
        /// own completion event; this binding never polls or allocates a queue.
        pub fn tryAcquire(self: *Self) Acquire {
            self.lock();
            if (self.stopping) {
                self.unlock();
                return .closing;
            }
            var selected: ?usize = null;
            for (&self.cells, 0..) |*cell, index| {
                if (!cell.busy and cell.ticket != std.math.maxInt(u64)) {
                    cell.busy = true;
                    cell.ticket += 1;
                    selected = index;
                    break;
                }
            }
            const index = selected orelse {
                var exhausted = true;
                for (self.cells) |cell| {
                    if (cell.ticket != std.math.maxInt(u64)) exhausted = false;
                }
                self.unlock();
                return if (exhausted) .exhausted else .busy;
            };
            const ticket = self.cells[index].ticket;
            self.unlock();

            switch (self.ledger.begin(self.areas[index])) {
                .failed => |status| {
                    self.lock();
                    self.cells[index].busy = false;
                    self.unlock();
                    return .{ .ledger_failure = status };
                },
                .acquired => |borrow| {
                    // Match fresh per-request scratch's zero-filled contents,
                    // including reuse of resident pages from an earlier request.
                    @memset(borrow.data.?[0..self.areas[index].bytes], 0);
                    return .{ .acquired = .{ .owner = self, .index = index, .ticket = ticket, .borrow = borrow } };
                },
            }
        }

        /// Close admission first, then retry close after active run locals have
        /// ended. Does not revoke IO bytes or close any shared/domain workspace.
        pub fn close(self: *Self) Close {
            self.lock();
            self.stopping = true;
            if (self.closed) {
                self.unlock();
                return .closed;
            }
            if (self.draining) {
                self.unlock();
                return .busy;
            }
            for (self.cells) |cell| {
                if (cell.busy) {
                    self.unlock();
                    return .busy;
                }
            }
            self.draining = true;
            self.unlock();
            var failure: ?i32 = null;
            for (self.areas) |area| {
                const status = self.ledger.close(area);
                if (status != @backingInt(work_area.Status.ok) and
                    status != @backingInt(work_area.Status.nonexistent) and
                    status != @backingInt(work_area.Status.not_in_memory))
                {
                    failure = status;
                    break;
                }
            }
            self.lock();
            self.draining = false;
            self.closed = failure == null;
            self.unlock();
            return if (failure) |status| .{ .ledger_failure = status } else .closed;
        }

        /// Owning lifecycle adapter. Never store in a host/family field. The run
        /// local spans accepted-work and send waits only while those bytes are
        /// actually needed; defer finish after all IO completes or drains.
        pub const Use = struct {
            owner: *Self,
            index: usize,
            ticket: u64,
            borrow: work_area.Borrow,
            ended: bool = false,

            pub fn region(self: *const Use) AccessError![]u8 {
                self.owner.lock();
                defer self.owner.unlock();
                const cell = self.owner.cells[self.index];
                if (self.ended or !cell.busy or cell.ending or cell.ticket != self.ticket)
                    return error.EndedUse;
                return self.borrow.data.?[0..self.owner.areas[self.index].bytes];
            }

            /// All terminal SDK outcomes settle admission exactly once. A failed
            /// end retains the use for correction/abandon in this same scope.
            /// Copies are non-owning: an old copy cannot release a reused slot.
            pub fn finish(self: *Use, how: work_area.End) AccessError!i32 {
                self.owner.lock();
                const cell = &self.owner.cells[self.index];
                if (self.ended or !cell.busy or cell.ending or cell.ticket != self.ticket) {
                    self.owner.unlock();
                    return error.EndedUse;
                }
                cell.ending = true;
                self.owner.unlock();
                const status = self.owner.ledger.finish(&self.borrow, how);
                self.owner.lock();
                cell.ending = false;
                if (work_area.ended(status)) {
                    cell.busy = false;
                    self.ended = true;
                }
                self.owner.unlock();
                return status;
            }
        };
    };
}
