// Windows blocking synchronization, with no heap allocation.
extern "kernel32" fn AcquireSRWLockExclusive(*usize) callconv(.winapi) void;
extern "kernel32" fn ReleaseSRWLockExclusive(*usize) callconv(.winapi) void;
extern "kernel32" fn SleepConditionVariableSRW(*usize, *usize, u32, u32) callconv(.winapi) c_int;
extern "kernel32" fn WakeAllConditionVariable(*usize) callconv(.winapi) void;
pub const Mutex = struct {
    value: usize = 0,
    pub fn lock(self: *Mutex) void {
        AcquireSRWLockExclusive(&self.value);
    }
    pub fn unlock(self: *Mutex) void {
        ReleaseSRWLockExclusive(&self.value);
    }
};
pub const Condition = struct {
    value: usize = 0,
    pub fn wait(self: *Condition, mutex: *Mutex) void {
        const ok = SleepConditionVariableSRW(&self.value, &mutex.value, 0xffffffff, 0);
        @import("std").debug.assert(ok != 0);
    }
    pub fn broadcast(self: *Condition) void {
        WakeAllConditionVariable(&self.value);
    }
};
