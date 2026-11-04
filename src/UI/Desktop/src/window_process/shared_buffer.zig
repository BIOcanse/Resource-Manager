//! Local creator lifetime only. No service adopt/grant, payload, or page Post.
//! The future W2 COM adapter calls this on the window STA; it is not thread safe.
//! This callback ABI is a leaf contract, not an exported product ABI.
const std = @import("std");

/// The adapter reads actual Size and FileMappingHandle from the created COM
/// object. The handle is borrowed: disposing the object, never CloseHandle on
/// this number, ends the creator's ownership. Native creator access is writable;
/// a future ReadOnly Post restricts script access only.
pub const Created = extern struct {
    object: ?*anyopaque = null,
    file_mapping: usize = 0,
    capacity: u64 = 0,
};

pub const Api = extern struct {
    context: ?*anyopaque,
    /// HRESULT. Even on failure, transfer any partially created object in out.
    /// This callback must not retain out, which is local to create().
    create: *const fn (?*anyopaque, u64, *Created) callconv(.c) i32,
    /// Close AND release the transferred COM reference, including when Close
    /// fails. The adapter records any Close HRESULT. Never close the borrowed
    /// file_mapping separately. Context must remain alive through this call.
    dispose: *const fn (?*anyopaque, *anyopaque) callconv(.c) void,
};

pub const Error = error{
    InvalidCapacity,
    AlreadyCreated,
    Closed,
    CreateFailed,
    InvalidObject,
    InvalidMapping,
    CapacityMismatch,
    Cancelled,
    NotCreated,
};

/// One instance owns exactly one native creator reference. Keep it at a stable
/// address and do not copy it after create starts. The owning window/block must
/// close it on every exit path. Separate buffers have separate instances; there
/// is no current-buffer registry, source state, allocator, pin or payload copy.
pub const Buffer = struct {
    const State = enum { empty, creating, open, closed };
    state: State = .empty,
    api: Api = undefined,
    value: Created = .{},

    pub fn create(self: *Buffer, api: Api, capacity: u64) Error!void {
        switch (self.state) {
            .empty => {},
            .closed => return error.Closed,
            .creating, .open => return error.AlreadyCreated,
        }
        if (capacity == 0) return error.InvalidCapacity;
        self.state = .creating;
        self.api = api;
        var candidate: Created = .{};
        const result = api.create(api.context, capacity, &candidate);
        const failure: ?Error = if (self.state == .closed)
            error.Cancelled
        else if (result < 0)
            error.CreateFailed
        else if (candidate.object == null)
            error.InvalidObject
        else if (candidate.file_mapping == 0 or candidate.file_mapping == std.math.maxInt(usize))
            error.InvalidMapping
        else if (candidate.capacity != capacity)
            error.CapacityMismatch
        else
            null;

        if (failure) |err| {
            // Keep creating/closed during disposal: a reentrant callback cannot
            // open another object in this instance before cleanup has finished.
            if (candidate.object) |object| api.dispose(api.context, object);
            if (self.state == .closed) return error.Cancelled;
            self.state = .empty;
            return err;
        }
        self.value = candidate;
        self.state = .open;
    }

    /// Metadata borrowed only for the caller's operation, valid while this
    /// instance stays open. It does NOT prove OS object rights, product-window
    /// identity, service adoption, source readiness or permission to Post.
    pub fn borrow(self: *const Buffer) Error!Created {
        return switch (self.state) {
            .open => self.value,
            .closed => error.Closed,
            .empty, .creating => error.NotCreated,
        };
    }

    /// Also cancels a reentrant create: its local candidate is disposed when
    /// the callback returns. This cannot revoke a remote duplicate or script
    /// view and must never stop a service source or another holder.
    pub fn close(self: *Buffer) void {
        const previous = self.state;
        self.state = .closed;
        if (previous != .open) return;
        const value = self.value;
        self.value = .{};
        self.api.dispose(self.api.context, value.object.?);
    }
};
