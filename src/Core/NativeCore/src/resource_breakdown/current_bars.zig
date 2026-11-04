// One synchronous projection over the original immutable snapshot. References
// are (field, index), never a flattened bar/software/process graph. The caller
// supplies directory declarations and text primitives; this module alone owns
// selection, availability, missing-bar units and reason precedence.
pub const TextKind = enum(u32) {
    request_id,
    bar_id,
    dataset_id,
    dataset_status,
    dataset_failure,
    bar_reason,
    request_dataset,
    observed_reason,
    fallback_reason,
    declaration,
    request_scale,
};

pub const Text = extern struct {
    index: usize = 0,
    kind: TextKind,

    pub fn at(kind: TextKind, index: usize) Text {
        return .{ .kind = kind, .index = index };
    }
};

// These identify caller-supplied declarations, not another set of defaults.
pub const Declaration = enum(usize) {
    ready,
    warming,
    gpu_prefix,
    vram_suffix,
    disk_prefix,
    network_prefix,
    usage_suffix,
    cpu_usage,
    warming_reason,
    gpu_memory_reason,
    default_reason,
    bytes_unit,
    rate_unit,
    percent_unit,

    pub fn text(self: Declaration) Text {
        return Text.at(.declaration, @backingInt(self));
    }
};

pub const Scalar = enum(u32) {
    supported,
    process_fact,
    captured,
    total_present,
    capacity_present,
    percent_present,
};

// Use the host's exact ordinal/Unicode primitives during the managed-source
// transition. They contain no metric-specific policy and retain no text.
pub const Comparison = enum(u32) { equal_ignore_case, equal_ordinal, starts_ignore_case, ends_ignore_case, blank, is_null };
pub const Operation = enum(u32) { select, without_reading, unavailable_reason, _ };
pub const Action = enum(u32) { retain, clear, missing, reason };

pub const Result = extern struct {
    request_index: usize = 0,
    bar_index: usize = 0,
    reason: Text,
    unit: Text = Declaration.bytes_unit.text(),
    action: Action,
};

pub const Request = extern struct {
    request_count: usize,
    bar_count: usize,
    dataset_count: usize,
    operation: Operation,
    layout_version: u32 = 1,
};

pub const Callbacks = extern struct {
    scalar: ?*const fn (?*anyopaque, usize, Scalar, *u32) callconv(.c) i32,
    compare: ?*const fn (?*anyopaque, Comparison, Text, Text, *u32) callconv(.c) i32,
    emit: ?*const fn (?*anyopaque, *const Result) callconv(.c) i32,
};

pub const Error = error{ CallbackFailed, InvalidInput, DuplicateBar, DuplicateDataset, NullKey };

const Source = struct {
    context: ?*anyopaque,
    callbacks: *const Callbacks,
    request: Request,

    fn scalar(self: Source, index: usize, field: Scalar) Error!bool {
        var value: u32 = 0;
        if (self.callbacks.scalar.?(self.context, index, field, &value) != 0) return error.CallbackFailed;
        return value != 0;
    }

    fn compare(self: Source, operation: Comparison, left: Text, right: Text) Error!bool {
        var value: u32 = 0;
        if (self.callbacks.compare.?(self.context, operation, left, right, &value) != 0) return error.CallbackFailed;
        return value != 0;
    }

    fn emit(self: Source, result: Result) Error!void {
        if (self.callbacks.emit.?(self.context, &result) != 0) return error.CallbackFailed;
    }

    fn find(self: Source, needle: Text, kind: TextKind, count: usize) Error!?usize {
        var index: usize = 0;
        while (index < count) : (index += 1) {
            if (try self.compare(.equal_ignore_case, needle, Text.at(kind, index))) return index;
        }
        return null;
    }

    fn unique(self: Source, kind: TextKind, count: usize) Error!void {
        var index: usize = 0;
        while (index < count) : (index += 1) {
            if (try self.compare(.is_null, Text.at(kind, index), Text.at(kind, index))) return error.NullKey;
            if (try self.find(Text.at(kind, index), kind, index) != null)
                return if (kind == .bar_id) error.DuplicateBar else error.DuplicateDataset;
        }
    }

    fn dataset(self: Source, index: usize) Error!?usize {
        return self.find(Text.at(.request_dataset, index), .dataset_id, self.request.dataset_count);
    }

    fn gpuMemory(self: Source, id: Text) Error!bool {
        return try self.compare(.starts_ignore_case, id, Declaration.gpu_prefix.text()) and
            try self.compare(.ends_ignore_case, id, Declaration.vram_suffix.text());
    }

    fn unavailableReason(self: Source, index: usize, observed: Text, fallback: Text) Error!Text {
        if (!try self.compare(.blank, observed, observed)) return observed;
        if (try self.dataset(index)) |dataset_index| {
            const failure = Text.at(.dataset_failure, dataset_index);
            if (!try self.compare(.blank, failure, failure)) return failure;
            const status = Text.at(.dataset_status, dataset_index);
            // Status comparison was case-sensitive in the original C#.
            if (try self.compare(.equal_ordinal, status, Declaration.warming.text()))
                return Declaration.warming_reason.text();
            if (try self.compare(.equal_ordinal, status, Declaration.ready.text()) and
                try self.gpuMemory(Text.at(.request_id, index)))
            {
                // UnavailableReason is also callable without Select's duplicate
                // validation: Any, not only the first bar, is intentional here.
                var bar_index: usize = 0;
                while (bar_index < self.request.bar_count) : (bar_index += 1) {
                    if (try self.compare(.equal_ignore_case, Text.at(.request_id, index), Text.at(.bar_id, bar_index)) and
                        !try self.scalar(bar_index, .total_present)) return Declaration.gpu_memory_reason.text();
                }
            }
        }
        return fallback;
    }

    fn missingUnit(self: Source, index: usize) Error!Text {
        const id = Text.at(.request_id, index);
        if (try self.compare(.starts_ignore_case, id, Declaration.disk_prefix.text()) or
            try self.compare(.starts_ignore_case, id, Declaration.network_prefix.text())) return Declaration.rate_unit.text();
        if (try self.compare(.ends_ignore_case, id, Declaration.usage_suffix.text()) and
            (try self.compare(.starts_ignore_case, id, Declaration.gpu_prefix.text()) or
                try self.compare(.equal_ignore_case, id, Declaration.cpu_usage.text()))) return Declaration.percent_unit.text();
        return Declaration.bytes_unit.text();
    }

    fn select(self: Source) Error!void {
        // ToDictionary rejected source duplicates even for an empty selection.
        // Bound scans by original list lengths; no native scratch allocation.
        try self.unique(.bar_id, self.request.bar_count);
        try self.unique(.dataset_id, self.request.dataset_count);
        var index: usize = 0;
        while (index < self.request.request_count) : (index += 1) {
            const id = Text.at(.request_id, index);
            if (try self.find(id, .request_id, index) != null) continue;
            if (try self.scalar(index, .process_fact)) continue;
            const bar = try self.find(id, .bar_id, self.request.bar_count);
            if (!try self.scalar(index, .supported) and bar == null) continue;
            // GetValueOrDefault evaluated the directory key even when the
            // dataset dictionary was empty. Keep its original input refusal.
            _ = try self.compare(.is_null, Text.at(.request_dataset, index), Text.at(.request_dataset, index));
            const dataset_index = try self.dataset(index);
            var readable = false;
            if (dataset_index) |dataset_id| {
                readable = try self.compare(.equal_ordinal, Text.at(.dataset_status, dataset_id), Declaration.ready.text()) and
                    try self.scalar(dataset_id, .captured);
            }
            if (bar) |bar_index| {
                readable = readable and try self.scalar(bar_index, .total_present) and
                    try self.scalar(bar_index, .capacity_present) and try self.scalar(bar_index, .percent_present);
                try self.emit(.{
                    .request_index = index,
                    .bar_index = bar_index,
                    .action = if (readable) .retain else .clear,
                    .reason = if (readable) Text.at(.bar_reason, bar_index) else try self.unavailableReason(index, Text.at(.bar_reason, bar_index), Declaration.default_reason.text()),
                });
            } else {
                try self.emit(.{
                    .request_index = index,
                    .action = .missing,
                    .unit = try self.missingUnit(index),
                    .reason = try self.unavailableReason(index, Text.at(.observed_reason, 0), Declaration.default_reason.text()),
                });
            }
        }
    }
};

pub fn project(context: ?*anyopaque, callbacks: *const Callbacks, request: Request) Error!void {
    if (request.layout_version != 1 or callbacks.scalar == null or callbacks.compare == null or callbacks.emit == null) return error.InvalidInput;
    const source = Source{ .context = context, .callbacks = callbacks, .request = request };
    switch (request.operation) {
        .select => try source.select(),
        .without_reading => {
            if (request.bar_count != 1) return error.InvalidInput;
            try source.emit(.{ .action = .clear, .reason = Text.at(.observed_reason, 0) });
        },
        .unavailable_reason => {
            if (request.request_count != 1) return error.InvalidInput;
            try source.emit(.{ .action = .reason, .reason = try source.unavailableReason(0, Text.at(.observed_reason, 0), Text.at(.fallback_reason, 0)) });
        },
        _ => return error.InvalidInput,
    }
}
