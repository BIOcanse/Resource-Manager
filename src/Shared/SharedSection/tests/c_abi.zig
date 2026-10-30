const std = @import("std");
const support = @import("support.zig");
const ss = support.ss;
const testing = std.testing;

fn code(failure: ss.Failure) u32 {
    return @intFromEnum(failure);
}

test "every C ABI function round-trips" {
    var region: [support.region_size]u8 align(8) = undefined;
    const length: u64 = region.len;
    const identities = try support.identities();
    const spec = ss.LayoutSpec{
        .sources = &identities,
        .positions = &support.positions,
        .data_kind = support.data_kind,
        .source_capacity = 2,
        .position_capacity = 5,
        .data_capacity = support.data_capacity,
        .source_count = 2,
        .position_count = 5,
    };

    // Layout.
    try testing.expectEqual(@as(u64, support.region_size), ss.rm_shared_section_required_size(2, 5, support.data_capacity));
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_initialize(&region, length, &spec));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_initialize(&region, length, null));
    var bad_spec = spec;
    bad_spec.positions = null;
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_initialize(&region, length, &bad_spec));

    // Layout queries.
    var header: ss.Header = undefined;
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_describe(&region, length, &header));
    try testing.expectEqual(ss.magic, header.magic);
    try testing.expectEqual(support.data_kind, header.data_kind);
    try testing.expectEqual(@as(u32, 2), header.source_count);
    try testing.expectEqual(@as(u32, 5), header.position_count);
    try testing.expectEqual(support.data_capacity, header.data_capacity);
    try testing.expectEqual(code(.region_misaligned), ss.rm_shared_section_describe(region[1..], length - 1, &header));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_describe(null, length, &header));
    try testing.expectEqual(code(.region_too_small), ss.rm_shared_section_describe(null, 0, &header));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_describe(&region, length, null));

    var identity: ss.SourceIdentity = undefined;
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_source(&region, length, support.metrics_source, &identity));
    try testing.expectEqualStrings("cpu-sampler", identity.owner());
    try testing.expectEqual(@as(u32, 4242), identity.process_id);
    try testing.expectEqual(@as(u64, 133_000_000_000_000_000), identity.process_start_time);
    try testing.expectEqual(code(.source_index), ss.rm_shared_section_source(&region, length, 2, &identity));

    var position: ss.PositionSpec = undefined;
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_position(&region, length, support.cpu, &position));
    try testing.expectEqual(ss.PositionKind.column, position.kind);
    try testing.expectEqual(support.ids, position.identity);
    try testing.expectEqual(@as(u32, 4), position.element_width);
    try testing.expectEqual(code(.position_index), ss.rm_shared_section_position(&region, length, 5, &position));

    // Writes: rows first, then a column against them.
    var writer: ss.Writer = undefined;
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_begin(&region, length, support.rows_source, &writer));
    try testing.expectEqual(code(.write_in_progress), ss.rm_shared_section_write_begin(&region, length, support.rows_source, &writer));
    const row_ids = [_]u64{ 7, 8 };
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_elements(&writer, support.ids, 0, std.mem.sliceAsBytes(row_ids[0..]).ptr, 16));
    try testing.expectEqual(code(.element_width), ss.rm_shared_section_write_elements(&writer, support.ids, 0, std.mem.sliceAsBytes(row_ids[0..]).ptr, 3));
    try testing.expectEqual(code(.not_owner), ss.rm_shared_section_write_elements(&writer, support.cpu, 0, std.mem.sliceAsBytes(row_ids[0..]).ptr, 8));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_write_elements(&writer, support.ids, 0, null, 8));
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_length(&writer, support.ids, 2, 0));
    try testing.expectEqual(code(.length_over_capacity), ss.rm_shared_section_write_length(&writer, support.ids, 9, 0));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_write_end(&writer, 9, 0, 100));
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_end(&writer, @intFromEnum(ss.SourceState.has_value), 0, 100));
    try testing.expectEqual(code(.not_writing), ss.rm_shared_section_write_end(&writer, @intFromEnum(ss.SourceState.has_value), 0, 100));
    const rows_sequence = writer.sequence;

    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_begin(&region, length, support.metrics_source, &writer));
    const cpu_values = [_]u32{ 30, 40 };
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_elements(&writer, support.cpu, 0, std.mem.sliceAsBytes(cpu_values[0..]).ptr, 8));
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_length(&writer, support.cpu, 2, rows_sequence));
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_end(&writer, @intFromEnum(ss.SourceState.has_value), 0, 200));

    // Sizing and setting up selections in caller memory.
    var size: ss.SelectionSize = .{};
    const rows_only = [_]u32{support.rows_source};
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_selection_size(&region, length, &rows_only, 1, &size));
    try testing.expectEqual(ss.SelectionSize{ .data_bytes = 256, .position_count = 3 }, size);
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_selection_size(&region, length, &support.both_sources, 2, &size));
    try testing.expectEqual(ss.SelectionSize{ .data_bytes = support.data_capacity, .position_count = 5 }, size);
    const metrics_only = [_]u32{support.metrics_source};
    try testing.expectEqual(code(.identity_not_selected), ss.rm_shared_section_selection_size(&region, length, &metrics_only, 1, &size));
    try testing.expectEqual(code(.selection_order), ss.rm_shared_section_selection_size(&region, length, &[_]u32{ 1, 0 }, 2, &size));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_selection_size(&region, length, null, 2, &size));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_selection_size(&region, length, &rows_only, 1, null));

    var data: [support.data_capacity]u8 = undefined;
    var sources: [2]ss.SourceCopy = undefined;
    var entries: [5]ss.SelectedPosition = undefined;
    var copy = ss.Selection{ .held = &sources, .held_length = 2, .positions = &entries, .position_length = 5, .data = &data, .data_length = data.len - 1 };
    try testing.expectEqual(code(.copy_too_small), ss.rm_shared_section_select(&region, length, &support.both_sources, 2, &copy));
    copy.data_length = data.len;
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_select(&region, length, &support.both_sources, 2, &copy));
    try testing.expectEqual(@as(u32, 5), copy.position_length);
    try testing.expectEqual(support.metrics_source, sources[1].source);
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_select(&region, length, &support.both_sources, 2, null));

    // Reads.
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.has_value), ss.rm_shared_section_read(&region, length, &copy, support.rows_source, ss.default_retry_limit));
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.has_value), ss.rm_shared_section_read(&region, length, &copy, support.metrics_source, ss.default_retry_limit));
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.unchanged), ss.rm_shared_section_read(&region, length, &copy, support.metrics_source, ss.default_retry_limit));
    try testing.expectEqual(@as(i64, 200), sources[support.metrics_source].write_time_unix_ms);
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.failed), ss.rm_shared_section_read(&region, length, null, 0, 1));
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.failed), ss.rm_shared_section_read(region[4..], length - 4, &copy, support.rows_source, 1));
    try testing.expectEqual(code(.region_misaligned), sources[support.rows_source].reason);
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.has_value), ss.rm_shared_section_read(&region, length, &copy, support.rows_source, 1));

    var value: ss.ValueView = .{};
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.has_value), ss.rm_shared_section_position_value(&copy, support.cpu, &value));
    try testing.expectEqual(@as(u64, 256), value.offset);
    try testing.expectEqual(@as(u64, 8), value.byte_length);
    try testing.expectEqual(@as(u32, 2), value.length);
    try testing.expectEqualSlices(u8, std.mem.sliceAsBytes(cpu_values[0..]), data[@intCast(value.offset)..][0..@intCast(value.byte_length)]);
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.has_value), ss.rm_shared_section_position_value(&copy, support.ids, &value));
    try testing.expectEqualSlices(u8, std.mem.sliceAsBytes(row_ids[0..]), data[@intCast(value.offset)..][0..@intCast(value.byte_length)]);
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.not_arrived), ss.rm_shared_section_position_value(&copy, support.names, &value));
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.failed), ss.rm_shared_section_position_value(&copy, 9, &value));
    try testing.expectEqual(code(.not_selected), value.reason);
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.failed), ss.rm_shared_section_read(&region, length, &copy, 2, 1));
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.failed), ss.rm_shared_section_position_value(null, 0, &value));
    try testing.expectEqual(code(.invalid_argument), value.reason);
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.failed), ss.rm_shared_section_position_value(&copy, 0, null));

    // Unavailable with a reason, through the C ABI.
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_begin(&region, length, support.metrics_source, &writer));
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_write_end(&writer, @intFromEnum(ss.SourceState.unavailable), 17, 300));
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.unavailable), ss.rm_shared_section_read(&region, length, &copy, support.metrics_source, 1));
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.unavailable), ss.rm_shared_section_position_value(&copy, support.totals, &value));
    try testing.expectEqual(@as(u32, 17), value.reason);

    // End.
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_end(&region, length, 400));
    try testing.expectEqual(@intFromEnum(ss.ReadStatus.ended), ss.rm_shared_section_read(&region, length, &copy, support.rows_source, 1));
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.ended), ss.rm_shared_section_position_value(&copy, support.ids, &value));
    try testing.expectEqual(code(.ended), ss.rm_shared_section_write_begin(&region, length, support.rows_source, &writer));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_end(null, 8, 0));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_write_begin(&region, length, 0, null));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_write_length(null, 0, 0, 0));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_write_end(null, 1, 0, 0));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_write_elements(null, 0, 0, null, 0));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_source(&region, length, 0, null));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_position(&region, length, 0, null));
}

test "C ABI structs have the documented sizes" {
    try testing.expectEqual(@as(usize, 40), @sizeOf(ss.LayoutSpec));
    try testing.expectEqual(@as(usize, 72), @sizeOf(ss.Selection));
    try testing.expectEqual(@as(usize, 56), @sizeOf(ss.SelectedPosition));
    try testing.expectEqual(@as(usize, 16), @sizeOf(ss.SelectionSize));
    try testing.expectEqual(@as(usize, 32), @sizeOf(ss.SourceCopy));
    try testing.expectEqual(@as(usize, 32), @sizeOf(ss.Writer));
    try testing.expectEqual(@as(usize, 32), @sizeOf(ss.ValueView));
}
