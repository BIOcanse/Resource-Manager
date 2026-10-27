pub const abi_version: u32 = 0x0001_0000;

// All capacities are supplied by the host's compiled configuration. This ABI
// has no defaults or runtime configuration fallback.
pub const Config = extern struct {
    size: u32 = @sizeOf(Config),
    version: u32 = abi_version,
    qpc_frequency: i64,
    window_ms: u32,
    bucket_ms: u32,
    max_pairs: u32,
    max_cells: u32,
    max_processes: u32,
    max_threads: u32,
    max_processors: u32,
    max_name_bytes: u32,
    byte_cap: u64,
};
pub const Input = extern struct {
    kind: u32,
    flags: u32 = 0,
    qpc: i64 = 0,
    time_ticks: i64 = 0,
    pid: i32 = 0,
    tid: i32 = 0,
    old_pid: i32 = 0,
    old_tid: i32 = 0,
    processor: i32 = 0,
    reserved: u32 = 0,
    unique: u64 = 0,
    start: i64 = 0,
    name: ?[*]const u8 = null,
    name_len: u32 = 0,
    reserved2: u32 = 0,
};
pub const Row = extern struct {
    process_instance: i64,
    pid: i32,
    flags: u32,
    start: i64,
    thread_instance: i64,
    tid: i32,
    processor: i32,
    duration: i64,
    switches: i32,
    name_len: u32,
    name: ?[*]const u8,
};
pub const View = extern struct {
    frequency: i64 = 0,
    start_qpc: i64 = 0,
    through_qpc: i64 = 0,
    time_ticks: i64 = 0,
    flags: u32 = 0,
    count: u32 = 0,
    rows: ?[*]const Row = null,
    current_bytes: u64 = 0,
    peak_bytes: u64 = 0,
};
pub const Usage = extern struct {
    current_bytes: u64,
    peak_bytes: u64,
    byte_cap: u64,
    failed_allocations: u64,
    last_error: i32,
    trace_error: u32,
    events_lost: u64,
};
