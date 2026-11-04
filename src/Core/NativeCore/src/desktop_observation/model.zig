//! Desktop business fields only. These are synchronous in-process views, not a
//! SharedSection header, grant, selection token, or a new physical wire format.
//! L owns selection/T/W; R must supply a stable, trusted-layout copy of W.
pub const Publisher = extern struct {
    session_id: i32,
    process_id: i32,
    creation_file_time_utc: i64,
};

pub const Window = extern struct {
    process_id: i32,
    has_visible_window: u32,
    has_background_window: u32,
    creation_file_time_utc: i64,
};

pub const Frame = extern struct {
    publisher: Publisher,
    observed_at_utc_ticks: i64,
    complete: u32,
    foreground_known: u32,
    foreground_present: u32,
    windows_present: u32,
    foreground: Window,
    window_count: u32,
};

/// The existing MaximumWindows is supplied by the host, not duplicated here.
pub const Limits = extern struct { maximum_windows: u32 };

/// A scoped stable native copy, borrowed for one synchronous call. The count
/// describes accessible rows, not allocation capacity. Never supply live W.
pub const Windows = extern struct {
    rows: ?[*]const Window,
    count: u32,

    pub fn valid(self: Windows, expected_count: u32) bool {
        return self.count == expected_count and (self.rows != null or self.count == 0);
    }

    pub fn at(self: Windows, index: u32) ?Window {
        if (index >= self.count) return null;
        const rows = self.rows orelse return null;
        return rows[index];
    }
};

pub fn validWindow(row: Window) bool {
    return row.process_id > 0 and row.creation_file_time_utc >= 0 and
        row.has_visible_window <= 1 and row.has_background_window <= 1;
}

pub fn samePublisher(a: Publisher, b: Publisher) bool {
    return a.session_id == b.session_id and a.process_id == b.process_id and
        a.creation_file_time_utc == b.creation_file_time_utc;
}
