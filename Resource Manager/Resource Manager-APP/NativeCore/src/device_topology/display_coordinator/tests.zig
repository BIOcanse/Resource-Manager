const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const all_sources_order: u64 = 0x0000_0005_0403_0201;
const capability_order: u64 = all_sources_order;

test "source masks and explicit configuration contract" {
    try std.testing.expectEqual(@as(u64, 0x1f), protocol.SourceMask.known);
    try std.testing.expectEqual(protocol.SourceMask.dxgi, protocol.sourceBit(.dxgi));
    var config = testConfig(8);
    try std.testing.expect(protocol.validConfig(&config));
    config.optional_source_mask |= protocol.SourceMask.display_config;
    try std.testing.expect(!protocol.validConfig(&config));
    config.optional_source_mask = protocol.SourceMask.dxgi;
    try std.testing.expect(protocol.validConfig(&config));
}

test "display facts merge across sources and keep optional last-good" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitDisplayConfig(session, config, 1, 1, false);
    try submitDxgi(session, config, 1, 1, 1);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(session, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.ready), snapshot.phase);
    try std.testing.expectEqual(@as(u64, 1), snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 2), snapshot.node_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.edge_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.capability_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.unresolved_count);
    try std.testing.expectEqual(protocol.SourceMask.known, snapshot.current_source_mask);

    var capabilities = [_]protocol.DisplayCapabilityOutput{
        std.mem.zeroes(protocol.DisplayCapabilityOutput),
    };
    try expectCode(
        .ok,
        session.readCapabilities(&readInput(config, snapshot), &capabilities),
    );
    const capability = capabilities[0];
    try std.testing.expectEqual(@as(i32, 10), capability.left);
    try std.testing.expectEqual(@as(i32, 20), capability.top);
    try std.testing.expectEqual(@as(i32, 1930), capability.right);
    try std.testing.expectEqual(@as(i32, 1100), capability.bottom);
    try std.testing.expectEqual(@as(u32, 10), capability.bits_per_color_channel);
    try std.testing.expectEqual(@as(u32, 40), capability.minimum_luminance_milli_nits);
    try std.testing.expectEqual(@as(u32, 1_000_000), capability.maximum_luminance_milli_nits);
    try std.testing.expectEqual(
        @as(u32, 400_000),
        capability.maximum_full_frame_luminance_milli_nits,
    );
    try std.testing.expectEqual(@as(u32, 60_000), capability.refresh_numerator);
    try std.testing.expectEqual(@as(u32, 1000), capability.refresh_denominator);
    try std.testing.expect(
        (capability.flags & protocol.CapabilityFlags.absolute_luminance_available) != 0,
    );
    try std.testing.expectEqual(
        @as(u32, @intCast(protocol.SourceMask.display_config | protocol.SourceMask.dxgi)),
        capability.capability_source_mask,
    );

    const text_outputs = try std.testing.allocator.alloc(protocol.TextOutput, snapshot.text_count);
    defer std.testing.allocator.free(text_outputs);
    @memset(text_outputs, std.mem.zeroes(protocol.TextOutput));
    var text_bytes = try std.testing.allocator.alloc(u8, config.maximum_text_byte_count);
    defer std.testing.allocator.free(text_bytes);
    @memset(text_bytes, 0);
    try expectCode(
        .ok,
        session.readTexts(&readInput(config, snapshot), text_outputs, text_bytes),
    );
    var found_monitor_path = false;
    for (text_outputs) |text| {
        if (text.normalization_kind != @intFromEnum(protocol.TextKind.monitor_device_path)) continue;
        const value = text_bytes[text.byte_offset .. text.byte_offset + text.byte_length];
        try std.testing.expectEqualStrings("DISPLAY\\DEL40A9\\5&10ABC&0&UID4357", value);
        found_monitor_path = true;
    }
    try std.testing.expect(found_monitor_path);

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 2)));
    try submitDisplayConfig(session, config, 2, 2, false);
    try submitDxgi(session, config, 2, 2, 1);
    try submitEmptyComplete(session, config, 2, 2, .edid);
    try submitEmptyComplete(session, config, 2, 2, .setup_api_monitor);
    try submitEmptyComplete(session, config, 2, 2, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 2)));
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 1), snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 0), snapshot.diff_count);

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 3)));
    try submitDisplayConfig(session, config, 3, 3, false);
    try submitStatus(session, config, 3, 2, .dxgi, .unavailable);
    try submitStatus(session, config, 3, 2, .edid, .retained);
    try submitStatus(session, config, 3, 2, .setup_api_monitor, .retained);
    try submitStatus(session, config, 3, 2, .oem_connector_profile, .retained);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 3)));
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 1), snapshot.content_generation);
    try std.testing.expectEqual(protocol.SourceMask.display_config, snapshot.current_source_mask);
    try std.testing.expectEqual(
        protocol.SourceMask.dxgi | protocol.SourceMask.edid |
            protocol.SourceMask.setup_api_monitor | protocol.SourceMask.oem_connector_profile,
        snapshot.retained_source_mask,
    );
    try std.testing.expectEqual(protocol.SourceMask.dxgi, snapshot.failed_source_mask);

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 4)));
    try submitStatus(session, config, 4, 3, .display_config, .unavailable);
    try submitStatus(session, config, 4, 2, .dxgi, .retained);
    try submitStatus(session, config, 4, 2, .edid, .retained);
    try submitStatus(session, config, 4, 2, .setup_api_monitor, .retained);
    try submitStatus(session, config, 4, 2, .oem_connector_profile, .retained);
    try expectCode(.unavailable, session.finalize(&finalizeInput(config, 4)));
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.failed_retained), snapshot.phase);
    try std.testing.expectEqual(@as(u64, 1), snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 1), snapshot.capability_count);
    try std.testing.expect(
        (snapshot.flags & protocol.SnapshotFlags.required_source_incomplete) != 0,
    );
}

test "identity conflict is explicit and never guessed from friendly name" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitDisplayConfig(session, config, 1, 1, true);
    try submitEmptyComplete(session, config, 1, 1, .dxgi);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(session, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 1), snapshot.unresolved_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.node_count);
    var unresolved = [_]protocol.UnresolvedOutput{
        std.mem.zeroes(protocol.UnresolvedOutput),
    };
    try expectCode(
        .ok,
        session.readUnresolved(&readInput(config, snapshot), &unresolved),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.UnresolvedReason.conflicting_monitor_path),
        unresolved[0].reason,
    );
}

test "capacity failure and stale source generation do not replace active snapshot" {
    const config = testConfig(2);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitDisplayConfig(session, config, 1, 1, false);
    try submitEmptyComplete(session, config, 1, 1, .dxgi);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(session, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 2)));
    try expectCode(
        .stale_frame,
        submitStatusResult(session, config, 2, 1, .display_config, .complete),
    );
    const two_dxgi = [_]protocol.DisplayFact{
        targetOnlyFact(.dxgi, 11, 0x100, 1),
        targetOnlyFact(.dxgi, 12, 0x200, 2),
    };
    const header = batchHeader(config, 2, 2, .dxgi, .complete, 2, 0, 0);
    try expectCode(.out_of_memory, session.submitSource(&header, &two_dxgi, &.{}, &.{}));
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 1), snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 1), snapshot.capability_count);
    try expectCode(.ok, session.abortRefresh(&abortInput(config, 2)));
    try expectCode(.stale_frame, session.beginRefresh(&refreshInput(config, 2)));
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 3)));
    try expectCode(.ok, session.abortRefresh(&abortInput(config, 3)));
}

test "persistence roundtrip revalidates raw facts and checksum" {
    const config = testConfig(8);
    const source = try state.Session.create(&config);
    defer source.destroy();
    try expectCode(.ok, source.beginRefresh(&refreshInput(config, 1)));
    try submitDisplayConfig(source, config, 1, 1, false);
    try submitDxgi(source, config, 1, 1, 1);
    try submitEmptyComplete(source, config, 1, 1, .edid);
    try submitEmptyComplete(source, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(source, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, source.finalize(&finalizeInput(config, 1)));

    var source_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, source.snapshot(&source_snapshot));
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    const facts = try std.testing.allocator.alloc(
        protocol.DisplayFact,
        config.maximum_observation_count,
    );
    defer std.testing.allocator.free(facts);
    @memset(facts, std.mem.zeroes(protocol.DisplayFact));
    const texts = try std.testing.allocator.alloc(
        protocol.TextInput,
        config.maximum_text_binding_count,
    );
    defer std.testing.allocator.free(texts);
    @memset(texts, std.mem.zeroes(protocol.TextInput));
    const bytes = try std.testing.allocator.alloc(u8, config.maximum_text_byte_count);
    defer std.testing.allocator.free(bytes);
    @memset(bytes, 0);
    const persistence = persistenceInput(config, 1);
    try expectCode(
        .ok,
        source.exportPersistence(&persistence, &header, facts, texts, bytes),
    );
    try std.testing.expect(!header.checksum.isZero());

    const restored = try state.Session.create(&config);
    defer restored.destroy();
    try expectCode(
        .ok,
        restored.importPersistence(
            &persistence,
            &header,
            facts[0..header.fact_count],
            texts[0..header.text_count],
            bytes[0..header.text_byte_count],
        ),
    );
    var restored_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, restored.snapshot(&restored_snapshot));
    try std.testing.expectEqual(
        source_snapshot.content_generation,
        restored_snapshot.content_generation,
    );
    try std.testing.expectEqual(source_snapshot.node_count, restored_snapshot.node_count);
    try std.testing.expectEqual(
        source_snapshot.capability_count,
        restored_snapshot.capability_count,
    );
    try std.testing.expectEqual(@as(u32, 0), restored_snapshot.diff_count);

    const tampered = try state.Session.create(&config);
    defer tampered.destroy();
    bytes[0] ^= 1;
    try expectCode(
        .invalid_argument,
        tampered.importPersistence(
            &persistence,
            &header,
            facts[0..header.fact_count],
            texts[0..header.text_count],
            bytes[0..header.text_byte_count],
        ),
    );
}

test "complete empty frame retracts source facts with explicit removals" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try publishSingleDisplay(session, config, false);

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 2)));
    try submitEmptyComplete(session, config, 2, 2, .display_config);
    try submitEmptyComplete(session, config, 2, 2, .dxgi);
    try submitEmptyComplete(session, config, 2, 2, .edid);
    try submitEmptyComplete(session, config, 2, 2, .setup_api_monitor);
    try submitEmptyComplete(session, config, 2, 2, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 2)));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 2), snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 0), snapshot.node_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.capability_count);
    try std.testing.expectEqual(@as(u32, 8), snapshot.diff_count);
    const diffs = try std.testing.allocator.alloc(protocol.DiffEntry, snapshot.diff_count);
    defer std.testing.allocator.free(diffs);
    @memset(diffs, std.mem.zeroes(protocol.DiffEntry));
    try expectCode(.ok, session.readDiff(&readInput(config, snapshot), diffs));
    for (diffs) |diff| {
        try std.testing.expectEqual(@as(u64, 1), diff.old_generation);
        try std.testing.expectEqual(@as(u64, 0), diff.new_generation);
        try std.testing.expectEqual(
            @intFromEnum(protocol.ChangeKind.removed),
            diff.change_kind,
        );
    }
}

test "read contracts reject short and prepopulated output buffers" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try publishSingleDisplay(session, config, false);

    var short_nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
    };
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try expectCode(
        .buffer_too_small,
        session.readNodes(&readInput(config, snapshot), &short_nodes),
    );
    var nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    nodes[0].struct_size = 1;
    try expectCode(
        .abi_mismatch,
        session.readNodes(&readInput(config, snapshot), &nodes),
    );
}

test "reserved bits duplicate frames and incomplete finalize fail closed" {
    var config = testConfig(8);
    config.reserved[0] = 1;
    try std.testing.expectError(error.InvalidConfiguration, state.Session.create(&config));
    config.reserved[0] = 0;
    const session = try state.Session.create(&config);
    defer session.destroy();

    var refresh = refreshInput(config, 1);
    refresh.flags = 1;
    try expectCode(.abi_mismatch, session.beginRefresh(&refresh));
    refresh.flags = 0;
    try expectCode(.ok, session.beginRefresh(&refresh));
    try submitEmptyComplete(session, config, 1, 1, .display_config);
    try expectCode(
        .stale_frame,
        submitStatusResult(session, config, 1, 1, .display_config, .complete),
    );
    try expectCode(.invalid_argument, session.finalize(&finalizeInput(config, 1)));
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 0), snapshot.content_generation);
}

test "canonical output is independent of source submission order" {
    const config = testConfig(8);
    const first = try state.Session.create(&config);
    defer first.destroy();
    const second = try state.Session.create(&config);
    defer second.destroy();

    try publishSingleDisplay(first, config, false);
    try expectCode(.ok, second.beginRefresh(&refreshInput(config, 1)));
    try submitEmptyComplete(second, config, 1, 1, .oem_connector_profile);
    try submitEmptyComplete(second, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(second, config, 1, 1, .edid);
    try submitDxgi(second, config, 1, 1, 1);
    try submitDisplayConfig(second, config, 1, 1, false);
    try expectCode(.ok, second.finalize(&finalizeInput(config, 1)));

    var first_nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    var second_nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    var first_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    var second_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, first.snapshot(&first_snapshot));
    try expectCode(.ok, second.snapshot(&second_snapshot));
    try expectCode(
        .ok,
        first.readNodes(&readInput(config, first_snapshot), &first_nodes),
    );
    try expectCode(
        .ok,
        second.readNodes(&readInput(config, second_snapshot), &second_nodes),
    );
    try std.testing.expectEqualSlices(
        u8,
        std.mem.sliceAsBytes(&first_nodes),
        std.mem.sliceAsBytes(&second_nodes),
    );
    for (first_nodes) |node| {
        try std.testing.expect(node.primary_source_id != 0);
        try std.testing.expect(node.primary_source_record_ordinal != 0);
        try std.testing.expect(!node.primary_payload_handle.isZero());
    }
    var first_capabilities = [_]protocol.DisplayCapabilityOutput{
        std.mem.zeroes(protocol.DisplayCapabilityOutput),
    };
    var second_capabilities = [_]protocol.DisplayCapabilityOutput{
        std.mem.zeroes(protocol.DisplayCapabilityOutput),
    };
    try expectCode(
        .ok,
        first.readCapabilities(
            &readInput(config, first_snapshot),
            &first_capabilities,
        ),
    );
    try expectCode(
        .ok,
        second.readCapabilities(
            &readInput(config, second_snapshot),
            &second_capabilities,
        ),
    );
    try std.testing.expectEqualSlices(
        u8,
        std.mem.sliceAsBytes(&first_capabilities),
        std.mem.sliceAsBytes(&second_capabilities),
    );
}

test "oem connector profile joins the exact adapter candidate independent of fact order" {
    const config = testConfig(8);
    const first = try state.Session.create(&config);
    defer first.destroy();
    const second = try state.Session.create(&config);
    defer second.destroy();

    try publishOemAdapterMatch(first, config, false);
    try publishOemAdapterMatch(second, config, true);

    var first_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    var second_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, first.snapshot(&first_snapshot));
    try expectCode(.ok, second.snapshot(&second_snapshot));
    try std.testing.expectEqual(@as(u32, 4), first_snapshot.node_count);
    try std.testing.expectEqual(first_snapshot.node_count, second_snapshot.node_count);

    var first_nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    var second_nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    try expectCode(
        .ok,
        first.readNodes(&readInput(config, first_snapshot), &first_nodes),
    );
    try expectCode(
        .ok,
        second.readNodes(&readInput(config, second_snapshot), &second_nodes),
    );
    try std.testing.expectEqualSlices(
        u8,
        std.mem.sliceAsBytes(&first_nodes),
        std.mem.sliceAsBytes(&second_nodes),
    );

    var matched_node_count: u32 = 0;
    for (first_nodes) |node| {
        if (node.oem_profile_payload_handle.isZero()) continue;
        matched_node_count += 1;
        try std.testing.expectEqual(@as(u64, 0x0E01), node.oem_profile_payload_handle.high);
        try std.testing.expectEqual(@as(u64, 99), node.oem_profile_payload_handle.low);
        try std.testing.expect(
            (node.source_mask & protocol.SourceMask.oem_connector_profile) != 0,
        );
    }
    try std.testing.expectEqual(@as(u32, 2), matched_node_count);
}

test "oem allow-any rule does not guess between multiple adapter candidates" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitAdapterCandidates(session, config, 1, 1, false);
    try submitOemProfile(session, config, 1, 1, null, true);
    try submitEmptyComplete(session, config, 1, 1, .dxgi);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 6), snapshot.node_count);
    var nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    try expectCode(.ok, session.readNodes(&readInput(config, snapshot), &nodes));
    var standalone_profile_nodes: u32 = 0;
    for (nodes) |node| {
        if (node.oem_profile_payload_handle.isZero()) continue;
        standalone_profile_nodes += 1;
        try std.testing.expectEqual(
            protocol.SourceMask.oem_connector_profile,
            node.source_mask,
        );
        try std.testing.expect(
            (node.source_mask & protocol.SourceMask.display_config) == 0,
        );
    }
    try std.testing.expectEqual(@as(u32, 2), standalone_profile_nodes);
}

test "unsupported source remains distinct from failed and retained" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitDisplayConfig(session, config, 1, 1, false);
    try submitStatus(session, config, 1, 1, .dxgi, .unsupported);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(session, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(protocol.SourceMask.dxgi, snapshot.unsupported_source_mask);
    try std.testing.expectEqual(@as(u64, 0), snapshot.failed_source_mask);
    try std.testing.expectEqual(@as(u64, 0), snapshot.retained_source_mask);
    try std.testing.expectEqual(
        protocol.SourceMask.known & ~protocol.SourceMask.dxgi,
        snapshot.current_source_mask,
    );
}

test "source replacement compacts text without requiring double capacity" {
    var config = testConfig(8);
    config.maximum_text_binding_count = 5;
    config.maximum_text_byte_count = 110;
    config.text_index_capacity = 8;
    try std.testing.expect(protocol.validConfig(&config));
    const session = try state.Session.create(&config);
    defer session.destroy();
    try publishSingleDisplay(session, config, false);

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 2)));
    try submitDisplayConfig(session, config, 2, 2, false);
    try submitDxgi(session, config, 2, 2, 1);
    try submitEmptyComplete(session, config, 2, 2, .edid);
    try submitEmptyComplete(session, config, 2, 2, .setup_api_monitor);
    try submitEmptyComplete(session, config, 2, 2, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 2)));
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 1), snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 4), snapshot.text_count);
}

test "absent POD fields and derived capability flags are strict" {
    var fact = targetOnlyFact(.dxgi, 1, 0x1234, 7);
    fact.width = 1;
    try std.testing.expect(!protocol.validFact(&fact, .dxgi, 0));
    fact.width = 0;
    fact.valid_mask |= protocol.FactValid.capability_flags;
    fact.capability_flags = protocol.CapabilityFlags.absolute_luminance_available;
    try std.testing.expect(!protocol.validFact(&fact, .dxgi, 0));
    fact.capability_flags = protocol.CapabilityFlags.high_dynamic_range_active;
    try std.testing.expect(protocol.validFact(&fact, .dxgi, 0));

    fact.valid_mask |= protocol.FactValid.minimum_luminance;
    fact.minimum_luminance_milli_nits = 40;
    try std.testing.expect(!protocol.validFact(&fact, .dxgi, 0));
    fact.valid_mask |= protocol.FactValid.maximum_luminance |
        protocol.FactValid.full_frame_luminance;
    fact.maximum_luminance_milli_nits = 100;
    fact.maximum_full_frame_luminance_milli_nits = 101;
    try std.testing.expect(!protocol.validFact(&fact, .dxgi, 0));
    fact.maximum_full_frame_luminance_milli_nits = 80;
    try std.testing.expect(protocol.validFact(&fact, .dxgi, 0));
}

test "first empty snapshot and first unavailable optional source are publishable" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    const requested = protocol.SourceMask.display_config | protocol.SourceMask.dxgi;
    try expectCode(.ok, session.beginRefresh(&refreshInputMask(config, 1, requested)));
    try submitEmptyComplete(session, config, 1, 1, .display_config);
    try submitStatus(session, config, 1, 1, .dxgi, .unavailable);
    try expectCode(.ok, session.finalize(&finalizeInputMask(config, 1, requested)));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 1), snapshot.content_generation);
    try std.testing.expectEqual(protocol.SourceMask.display_config, snapshot.current_source_mask);
    try std.testing.expectEqual(protocol.SourceMask.dxgi, snapshot.failed_source_mask);
    try std.testing.expectEqual(@as(u64, 0), snapshot.retained_source_mask);
    try std.testing.expectEqual(@as(u32, 0), snapshot.node_count);
    try std.testing.expect(
        (snapshot.flags & protocol.SnapshotFlags.has_last_good) != 0,
    );
    try std.testing.expect(
        (snapshot.flags & protocol.SnapshotFlags.content_changed) != 0,
    );

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var facts: [0]protocol.DisplayFact = .{};
    var texts: [0]protocol.TextInput = .{};
    var bytes: [0]u8 = .{};
    const persistence = persistenceInput(config, 1);
    try expectCode(
        .ok,
        session.exportPersistence(&persistence, &header, &facts, &texts, &bytes),
    );
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.ready), header.phase);
    try std.testing.expectEqual(@as(u64, 1), header.source_generations[0]);
    try std.testing.expectEqual(@as(u64, 1), header.source_generations[1]);

    const restored = try state.Session.create(&config);
    defer restored.destroy();
    try expectCode(
        .ok,
        restored.importPersistence(&persistence, &header, &facts, &texts, &bytes),
    );
    var restored_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, restored.snapshot(&restored_snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.ready), restored_snapshot.phase);
    try std.testing.expectEqual(protocol.SourceMask.dxgi, restored_snapshot.failed_source_mask);
}

test "required failure retains exact last-good masks and phase across restart" {
    const config = testConfig(8);
    const source = try state.Session.create(&config);
    defer source.destroy();
    try publishSingleDisplay(source, config, false);
    try expectCode(
        .ok,
        source.beginRefresh(&refreshInputMask(config, 2, config.required_source_mask)),
    );
    try submitStatus(source, config, 2, 1, .display_config, .unavailable);
    try expectCode(
        .unavailable,
        source.finalize(&finalizeInputMask(config, 2, config.required_source_mask)),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, source.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 0), snapshot.current_source_mask);
    try std.testing.expectEqual(protocol.SourceMask.known, snapshot.retained_source_mask);
    try std.testing.expectEqual(
        protocol.SourceMask.display_config,
        snapshot.failed_source_mask,
    );
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.failed_retained), snapshot.phase);

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    const facts = try std.testing.allocator.alloc(
        protocol.DisplayFact,
        config.maximum_observation_count,
    );
    defer std.testing.allocator.free(facts);
    @memset(facts, std.mem.zeroes(protocol.DisplayFact));
    const texts = try std.testing.allocator.alloc(
        protocol.TextInput,
        config.maximum_text_binding_count,
    );
    defer std.testing.allocator.free(texts);
    @memset(texts, std.mem.zeroes(protocol.TextInput));
    const bytes = try std.testing.allocator.alloc(u8, config.maximum_text_byte_count);
    defer std.testing.allocator.free(bytes);
    @memset(bytes, 0);
    const persistence = persistenceInput(config, 2);
    try expectCode(
        .ok,
        source.exportPersistence(&persistence, &header, facts, texts, bytes),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.Phase.failed_retained),
        header.phase,
    );
    var invalid_header = header;
    invalid_header.unsupported_source_mask |= protocol.SourceMask.display_config;
    try std.testing.expect(!protocol.validPersistenceHeader(
        &invalid_header,
        &config,
        &persistence,
        facts[0..header.fact_count],
        texts[0..header.text_count],
        bytes[0..header.text_byte_count],
    ));

    const restored = try state.Session.create(&config);
    defer restored.destroy();
    try expectCode(
        .ok,
        restored.importPersistence(
            &persistence,
            &header,
            facts[0..header.fact_count],
            texts[0..header.text_count],
            bytes[0..header.text_byte_count],
        ),
    );
    var restored_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, restored.snapshot(&restored_snapshot));
    try std.testing.expectEqual(
        @intFromEnum(protocol.Phase.failed_retained),
        restored_snapshot.phase,
    );
    try std.testing.expectEqual(
        protocol.SourceMask.known,
        restored_snapshot.retained_source_mask,
    );
}

test "first required failure is explicit failed-no-data" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectCode(
        .ok,
        session.beginRefresh(&refreshInputMask(config, 1, config.required_source_mask)),
    );
    try submitStatus(session, config, 1, 1, .display_config, .unavailable);
    try expectCode(
        .unavailable,
        session.finalize(&finalizeInputMask(config, 1, config.required_source_mask)),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(
        @intFromEnum(protocol.Phase.failed_no_data),
        snapshot.phase,
    );
    try std.testing.expectEqual(@as(u64, 0), snapshot.content_generation);
    try std.testing.expectEqual(
        protocol.SourceMask.display_config,
        snapshot.failed_source_mask,
    );
    try std.testing.expect(
        (snapshot.flags & protocol.SnapshotFlags.has_last_good) == 0,
    );
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var facts: [0]protocol.DisplayFact = .{};
    var texts: [0]protocol.TextInput = .{};
    var bytes: [0]u8 = .{};
    try expectCode(
        .no_data,
        session.exportPersistence(
            &persistenceInput(config, 1),
            &header,
            &facts,
            &texts,
            &bytes,
        ),
    );
}

test "read token rejects cross-revision assembly" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try publishSingleDisplay(session, config, false);
    var before = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&before));
    const stale_read = readInput(config, before);

    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 2)));
    var nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    try expectCode(.stale_frame, session.readNodes(&stale_read, &nodes));
    var collecting = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&collecting));
    try expectCode(
        .ok,
        session.readNodes(&readInput(config, collecting), &nodes),
    );
    try expectCode(.ok, session.abortRefresh(&abortInput(config, 2)));
    @memset(nodes[0..], std.mem.zeroes(protocol.NodeOutput));
    try expectCode(
        .stale_frame,
        session.readNodes(&readInput(config, collecting), &nodes),
    );
}

test "text-only and unresolved-only changes advance generation with exact diffs" {
    const config = testConfig(8);
    const text_session = try state.Session.create(&config);
    defer text_session.destroy();
    try publishSingleDisplay(text_session, config, false);
    try expectCode(.ok, text_session.beginRefresh(&refreshInput(config, 2)));
    try submitDisplayConfigVariant(
        text_session,
        config,
        2,
        2,
        false,
        1,
        2,
        "DisplayConfig-v2",
    );
    try submitDxgi(text_session, config, 2, 2, 1);
    try submitEmptyComplete(text_session, config, 2, 2, .edid);
    try submitEmptyComplete(text_session, config, 2, 2, .setup_api_monitor);
    try submitEmptyComplete(text_session, config, 2, 2, .oem_connector_profile);
    try expectCode(.ok, text_session.finalize(&finalizeInput(config, 2)));
    var text_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, text_session.snapshot(&text_snapshot));
    try std.testing.expectEqual(@as(u64, 2), text_snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 2), text_snapshot.diff_count);
    const text_diffs = try std.testing.allocator.alloc(
        protocol.DiffEntry,
        text_snapshot.diff_count,
    );
    defer std.testing.allocator.free(text_diffs);
    @memset(text_diffs, std.mem.zeroes(protocol.DiffEntry));
    try expectCode(
        .ok,
        text_session.readDiff(&readInput(config, text_snapshot), text_diffs),
    );
    for (text_diffs) |diff| {
        try std.testing.expectEqual(@intFromEnum(protocol.EntityKind.text), diff.entity_kind);
        try std.testing.expect(
            diff.change_kind == @intFromEnum(protocol.ChangeKind.added) or
                diff.change_kind == @intFromEnum(protocol.ChangeKind.removed),
        );
    }

    const unresolved_session = try state.Session.create(&config);
    defer unresolved_session.destroy();
    try publishSingleDisplay(unresolved_session, config, true);
    try expectCode(.ok, unresolved_session.beginRefresh(&refreshInput(config, 2)));
    try submitDisplayConfigVariant(
        unresolved_session,
        config,
        2,
        2,
        true,
        3,
        4,
        "DisplayConfig",
    );
    try submitEmptyComplete(unresolved_session, config, 2, 2, .dxgi);
    try submitEmptyComplete(unresolved_session, config, 2, 2, .edid);
    try submitEmptyComplete(unresolved_session, config, 2, 2, .setup_api_monitor);
    try submitEmptyComplete(unresolved_session, config, 2, 2, .oem_connector_profile);
    try expectCode(.ok, unresolved_session.finalize(&finalizeInput(config, 2)));
    var unresolved_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, unresolved_session.snapshot(&unresolved_snapshot));
    try std.testing.expectEqual(@as(u64, 2), unresolved_snapshot.content_generation);
    try std.testing.expectEqual(@as(u32, 2), unresolved_snapshot.diff_count);
    var unresolved_diffs = [_]protocol.DiffEntry{
        std.mem.zeroes(protocol.DiffEntry),
        std.mem.zeroes(protocol.DiffEntry),
    };
    try expectCode(
        .ok,
        unresolved_session.readDiff(
            &readInput(config, unresolved_snapshot),
            &unresolved_diffs,
        ),
    );
    for (unresolved_diffs) |diff| {
        try std.testing.expectEqual(
            @intFromEnum(protocol.EntityKind.unresolved),
            diff.entity_kind,
        );
    }
}

test "same-source record order is not canonical authority" {
    const config = testConfig(8);
    const first = try state.Session.create(&config);
    defer first.destroy();
    const second = try state.Session.create(&config);
    defer second.destroy();
    try publishSameSourceTie(first, config, false);
    try publishSameSourceTie(second, config, true);

    var first_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    var second_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, first.snapshot(&first_snapshot));
    try expectCode(.ok, second.snapshot(&second_snapshot));
    try std.testing.expectEqual(first_snapshot.node_count, second_snapshot.node_count);
    try std.testing.expectEqual(first_snapshot.text_count, second_snapshot.text_count);
    var first_nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    var second_nodes = [_]protocol.NodeOutput{
        std.mem.zeroes(protocol.NodeOutput),
        std.mem.zeroes(protocol.NodeOutput),
    };
    try expectCode(
        .ok,
        first.readNodes(&readInput(config, first_snapshot), &first_nodes),
    );
    try expectCode(
        .ok,
        second.readNodes(&readInput(config, second_snapshot), &second_nodes),
    );
    try std.testing.expectEqualSlices(
        u8,
        std.mem.sliceAsBytes(&first_nodes),
        std.mem.sliceAsBytes(&second_nodes),
    );
}

test "EDID identity participates in union and explicit conflict detection" {
    const config = testConfig(8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitEmptyComplete(session, config, 1, 1, .display_config);
    const bytes = "SERIAL-A" ++ "SERIAL-B";
    const texts = [_]protocol.TextInput{
        textInput(.identity_token, 0, "SERIAL-A".len),
        textInput(.identity_token, "SERIAL-A".len, "SERIAL-B".len),
    };
    var first = targetOnlyFact(.edid, 1, 0x1234, 7);
    first.valid_mask |= protocol.FactValid.edid_serial;
    first.identity_mask |= protocol.IdentityMask.edid_serial;
    first.edid_serial_text_index = 0;
    var second = targetOnlyFact(.edid, 2, 0x1234, 7);
    second.valid_mask |= protocol.FactValid.edid_serial;
    second.identity_mask |= protocol.IdentityMask.edid_serial;
    second.edid_serial_text_index = 1;
    const facts = [_]protocol.DisplayFact{ first, second };
    const header = batchHeader(
        config,
        1,
        1,
        .edid,
        .complete,
        facts.len,
        texts.len,
        bytes.len,
    );
    try expectCode(.ok, session.submitSource(&header, &facts, &texts, bytes));
    try submitEmptyComplete(session, config, 1, 1, .dxgi);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(session, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try expectCode(.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 1), snapshot.unresolved_count);
    var unresolved = [_]protocol.UnresolvedOutput{
        std.mem.zeroes(protocol.UnresolvedOutput),
    };
    try expectCode(
        .ok,
        session.readUnresolved(&readInput(config, snapshot), &unresolved),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.UnresolvedReason.conflicting_edid_serial),
        unresolved[0].reason,
    );
    try std.testing.expectEqual(
        @as(u32, @intCast(protocol.IdentityMask.edid_serial)),
        unresolved[0].identity_kind,
    );
}

fn testConfig(maximum_observation_count: u32) protocol.Config {
    const maximum_node_count = maximum_observation_count * 2;
    const maximum_edge_count = maximum_observation_count;
    const maximum_capability_count = maximum_observation_count;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .maximum_source_count = protocol.source_count,
        .maximum_observation_count = maximum_observation_count,
        .maximum_node_count = maximum_node_count,
        .maximum_edge_count = maximum_edge_count,
        .maximum_capability_count = maximum_capability_count,
        .maximum_diff_entry_count = 2 * (maximum_node_count + maximum_edge_count +
            maximum_capability_count + 32 + maximum_observation_count),
        .maximum_text_binding_count = 32,
        .maximum_text_byte_count = 4096,
        .maximum_unresolved_count = maximum_observation_count,
        .maximum_source_batch_count = protocol.source_count,
        .identity_index_capacity = nextPowerOfTwo(maximum_observation_count * 5),
        .text_index_capacity = 32,
        .resident_byte_budget = 16 * 1024 * 1024,
        .required_source_mask = protocol.SourceMask.display_config,
        .optional_source_mask = protocol.SourceMask.known & ~protocol.SourceMask.display_config,
        .identity_source_priority_order = all_sources_order,
        .friendly_name_source_priority_order = all_sources_order,
        .capability_source_priority_order = capability_order,
        .projection_source_priority_order = all_sources_order,
        .identity_contract_version = protocol.identity_contract_version,
        .capability_contract_version = protocol.capability_contract_version,
        .maximum_future_skew_ms = 1000,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
}

fn refreshInput(config: protocol.Config, epoch: u64) protocol.RefreshInput {
    return refreshInputMask(
        config,
        epoch,
        config.required_source_mask | config.optional_source_mask,
    );
}

fn refreshInputMask(
    config: protocol.Config,
    epoch: u64,
    requested_source_mask: u64,
) protocol.RefreshInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.RefreshInput),
        .configuration_generation = config.generation,
        .refresh_epoch = epoch,
        .captured_utc_ms = @intCast(1000 * epoch),
        .monotonic_ms = 1000 * epoch,
        .required_source_mask = config.required_source_mask,
        .requested_source_mask = requested_source_mask,
        .valid_mask = protocol.RefreshValid.required,
        .flags = 0,
        .reserved = .{0},
    };
}

fn finalizeInput(config: protocol.Config, epoch: u64) protocol.FinalizeInput {
    return finalizeInputMask(
        config,
        epoch,
        config.required_source_mask | config.optional_source_mask,
    );
}

fn finalizeInputMask(
    config: protocol.Config,
    epoch: u64,
    expected_source_mask: u64,
) protocol.FinalizeInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.FinalizeInput),
        .configuration_generation = config.generation,
        .refresh_epoch = epoch,
        .expected_source_mask = expected_source_mask,
        .valid_mask = 1,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn abortInput(config: protocol.Config, epoch: u64) protocol.AbortInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.AbortInput),
        .configuration_generation = config.generation,
        .refresh_epoch = epoch,
        .valid_mask = 1,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
}

fn readInput(
    config: protocol.Config,
    snapshot: protocol.SnapshotOutput,
) protocol.ReadInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ReadInput),
        .configuration_generation = config.generation,
        .state_revision = snapshot.state_revision,
        .content_generation = snapshot.content_generation,
        .valid_mask = protocol.ReadValid.required,
        .flags = 0,
        .reserved = .{0},
    };
}

fn persistenceInput(
    config: protocol.Config,
    epoch: u64,
) protocol.PersistenceInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceInput),
        .configuration_generation = config.generation,
        .operation_epoch = epoch,
        .valid_mask = protocol.PersistenceValid.required,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
}

fn submitDisplayConfig(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    conflict: bool,
) !void {
    try submitDisplayConfigVariant(
        session,
        config,
        refresh_epoch,
        source_generation,
        conflict,
        1,
        2,
        "DisplayConfig",
    );
}

fn submitDisplayConfigVariant(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    conflict: bool,
    first_ordinal: u64,
    second_ordinal: u64,
    comptime evidence: []const u8,
) !void {
    const monitor_one = "\\\\?\\DISPLAY#DEL40A9#5&10abc&0&UID4357";
    const source_device = "\\\\.\\DISPLAY1";
    const friendly = " Test HDR ";
    if (conflict) {
        const monitor_two = "\\\\?\\DISPLAY#ACR0001#5&20def&0&UID99";
        const bytes = monitor_one ++ monitor_two ++ source_device ++ friendly ++ evidence;
        const monitor_two_offset: u32 = monitor_one.len;
        const source_offset: u32 = monitor_one.len + monitor_two.len;
        const friendly_offset: u32 = source_offset + source_device.len;
        const evidence_offset: u32 = friendly_offset + friendly.len;
        const texts = [_]protocol.TextInput{
            textInput(.monitor_device_path, 0, monitor_one.len),
            textInput(.monitor_device_path, monitor_two_offset, monitor_two.len),
            textInput(.source_device_name, source_offset, source_device.len),
            textInput(.friendly_name, friendly_offset, friendly.len),
            textInput(.evidence, evidence_offset, evidence.len),
        };
        const facts = [_]protocol.DisplayFact{
            displayConfigFact(first_ordinal, 0, 2, 3, 4, 0x1234, 7),
            displayConfigFact(second_ordinal, 1, 2, 3, 4, 0x9999, 8),
        };
        const header = batchHeader(
            config,
            refresh_epoch,
            source_generation,
            .display_config,
            .complete,
            facts.len,
            texts.len,
            bytes.len,
        );
        try expectCode(.ok, session.submitSource(&header, &facts, &texts, bytes));
        return;
    }

    const bytes = monitor_one ++ source_device ++ friendly ++ evidence;
    const source_offset: u32 = monitor_one.len;
    const friendly_offset: u32 = source_offset + source_device.len;
    const evidence_offset: u32 = friendly_offset + friendly.len;
    const texts = [_]protocol.TextInput{
        textInput(.monitor_device_path, 0, monitor_one.len),
        textInput(.source_device_name, source_offset, source_device.len),
        textInput(.friendly_name, friendly_offset, friendly.len),
        textInput(.evidence, evidence_offset, evidence.len),
    };
    const fact = displayConfigFact(first_ordinal, 0, 1, 2, 3, 0x1234, 7);
    const header = batchHeader(
        config,
        refresh_epoch,
        source_generation,
        .display_config,
        .complete,
        1,
        texts.len,
        bytes.len,
    );
    try expectCode(.ok, session.submitSource(&header, &.{fact}, &texts, bytes));
}

fn publishSingleDisplay(
    session: *state.Session,
    config: protocol.Config,
    conflict: bool,
) !void {
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitDisplayConfig(session, config, 1, 1, conflict);
    try submitDxgi(session, config, 1, 1, 1);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(session, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));
}

fn publishSameSourceTie(
    session: *state.Session,
    config: protocol.Config,
    reverse: bool,
) !void {
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    const monitor = "\\\\?\\DISPLAY#DEL40A9#5&10abc&0&UID4357";
    const source_device = "\\\\.\\DISPLAY1";
    const friendly_one = "First";
    const friendly_two = "Second";
    const evidence = "DisplayConfig";
    const bytes = monitor ++ source_device ++ friendly_one ++ friendly_two ++ evidence;
    const source_offset: u32 = monitor.len;
    const first_offset: u32 = source_offset + source_device.len;
    const second_offset: u32 = first_offset + friendly_one.len;
    const evidence_offset: u32 = second_offset + friendly_two.len;
    const texts = [_]protocol.TextInput{
        textInput(.monitor_device_path, 0, monitor.len),
        textInput(.source_device_name, source_offset, source_device.len),
        textInput(.friendly_name, first_offset, friendly_one.len),
        textInput(.friendly_name, second_offset, friendly_two.len),
        textInput(.evidence, evidence_offset, evidence.len),
    };
    const first_fact = displayConfigFact(1, 0, 1, 2, 4, 0x1234, 7);
    const second_fact = displayConfigFact(2, 0, 1, 3, 4, 0x1234, 7);
    const facts = if (reverse)
        [_]protocol.DisplayFact{ second_fact, first_fact }
    else
        [_]protocol.DisplayFact{ first_fact, second_fact };
    const header = batchHeader(
        config,
        1,
        1,
        .display_config,
        .complete,
        facts.len,
        texts.len,
        bytes.len,
    );
    try expectCode(.ok, session.submitSource(&header, &facts, &texts, bytes));
    try submitEmptyComplete(session, config, 1, 1, .dxgi);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try submitEmptyComplete(session, config, 1, 1, .oem_connector_profile);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));
}

fn publishOemAdapterMatch(
    session: *state.Session,
    config: protocol.Config,
    reverse_candidates: bool,
) !void {
    try expectCode(.ok, session.beginRefresh(&refreshInput(config, 1)));
    try submitAdapterCandidates(session, config, 1, 1, reverse_candidates);
    try submitOemProfile(session, config, 1, 1, "VEN_10DE", false);
    try submitEmptyComplete(session, config, 1, 1, .dxgi);
    try submitEmptyComplete(session, config, 1, 1, .edid);
    try submitEmptyComplete(session, config, 1, 1, .setup_api_monitor);
    try expectCode(.ok, session.finalize(&finalizeInput(config, 1)));
}

fn submitAdapterCandidates(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    reverse: bool,
) !void {
    const nvidia_path = "\\\\?\\PCI#VEN_10DE&DEV_2D04";
    const amd_path = "\\\\?\\PCI#VEN_1002&DEV_150E";
    const bytes = nvidia_path ++ amd_path;
    const texts = [_]protocol.TextInput{
        textInput(.adapter_device_path, 0, nvidia_path.len),
        textInput(.adapter_device_path, nvidia_path.len, amd_path.len),
    };
    const nvidia = displayConfigAdapterFact(1, 0, 0x10DE, 7);
    const amd = displayConfigAdapterFact(2, 1, 0x1002, 8);
    const facts = if (reverse)
        [_]protocol.DisplayFact{ amd, nvidia }
    else
        [_]protocol.DisplayFact{ nvidia, amd };
    const header = batchHeader(
        config,
        refresh_epoch,
        source_generation,
        .display_config,
        .complete,
        facts.len,
        texts.len,
        bytes.len,
    );
    try expectCode(.ok, session.submitSource(&header, &facts, &texts, bytes));
}

fn submitOemProfile(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    preferred_adapter_token: ?[]const u8,
    allow_any_active_adapter: bool,
) !void {
    var valid_mask = protocol.FactValid.output_technology |
        protocol.FactValid.oem_match_rule | protocol.FactValid.source_object_key |
        protocol.FactValid.payload_handle;
    if (preferred_adapter_token != null) {
        valid_mask |= protocol.FactValid.preferred_adapter_token;
    }
    const fact = protocol.DisplayFact{
        .struct_size = @sizeOf(protocol.DisplayFact),
        .source_id = @intFromEnum(protocol.SourceId.oem_connector_profile),
        .valid_mask = valid_mask,
        .identity_mask = protocol.IdentityMask.source_object_key,
        .source_record_ordinal = 99,
        .adapter_luid = 0,
        .target_id = 0,
        .connector_instance = 0,
        .output_technology = 5,
        .status_flags = 0,
        .monitor_path_text_index = 0,
        .source_device_text_index = 0,
        .friendly_name_text_index = 0,
        .edid_serial_text_index = 0,
        .evidence_text_index = 0,
        .match_text_index = 0,
        .match_flags = if (allow_any_active_adapter)
            protocol.OemMatchFlags.allow_any_active_adapter
        else
            0,
        .reserved_u32 = 0,
        .position_x = 0,
        .position_y = 0,
        .width = 0,
        .height = 0,
        .refresh_numerator = 0,
        .refresh_denominator = 0,
        .bits_per_color_channel = 0,
        .minimum_luminance_milli_nits = 0,
        .maximum_luminance_milli_nits = 0,
        .maximum_full_frame_luminance_milli_nits = 0,
        .capability_flags = 0,
        .observed_at_utc_ms = 0,
        .source_object_key = 99,
        .payload_handle = .{ .high = 0x0E01, .low = 99 },
    };
    if (preferred_adapter_token) |token| {
        const token_length: u32 = @intCast(token.len);
        const texts = [_]protocol.TextInput{
            textInput(.adapter_match_token, 0, token_length),
        };
        const header = batchHeader(
            config,
            refresh_epoch,
            source_generation,
            .oem_connector_profile,
            .complete,
            1,
            texts.len,
            token_length,
        );
        try expectCode(.ok, session.submitSource(&header, &.{fact}, &texts, token));
        return;
    }
    const header = batchHeader(
        config,
        refresh_epoch,
        source_generation,
        .oem_connector_profile,
        .complete,
        1,
        0,
        0,
    );
    try expectCode(.ok, session.submitSource(&header, &.{fact}, &.{}, &.{}));
}

fn displayConfigAdapterFact(
    ordinal: u64,
    adapter_path_text_index: u32,
    adapter_luid: u64,
    target_id: u32,
) protocol.DisplayFact {
    var fact = targetOnlyFact(.display_config, ordinal, adapter_luid, target_id);
    fact.valid_mask |= protocol.FactValid.output_technology |
        protocol.FactValid.status_flags | protocol.FactValid.adapter_device_path;
    fact.output_technology = 5;
    fact.status_flags = 1;
    fact.match_text_index = adapter_path_text_index;
    return fact;
}

fn displayConfigFact(
    ordinal: u64,
    monitor_index: u32,
    source_index: u32,
    friendly_index: u32,
    evidence_index: u32,
    adapter_luid: u64,
    target_id: u32,
) protocol.DisplayFact {
    return .{
        .struct_size = @sizeOf(protocol.DisplayFact),
        .source_id = @intFromEnum(protocol.SourceId.display_config),
        .valid_mask = protocol.FactValid.adapter_luid | protocol.FactValid.target_id |
            protocol.FactValid.connector_instance | protocol.FactValid.output_technology |
            protocol.FactValid.status_flags |
            protocol.FactValid.monitor_path | protocol.FactValid.source_device_name |
            protocol.FactValid.friendly_name | protocol.FactValid.evidence |
            protocol.FactValid.bounds | protocol.FactValid.refresh_rate |
            protocol.FactValid.capability_flags |
            protocol.FactValid.observed_at_utc_ms |
            protocol.FactValid.payload_handle,
        .identity_mask = protocol.IdentityMask.target |
            protocol.IdentityMask.monitor_path | protocol.IdentityMask.source_device_name,
        .source_record_ordinal = ordinal,
        .adapter_luid = adapter_luid,
        .target_id = target_id,
        .connector_instance = 1,
        .output_technology = 5,
        .status_flags = 1,
        .monitor_path_text_index = monitor_index,
        .source_device_text_index = source_index,
        .friendly_name_text_index = friendly_index,
        .edid_serial_text_index = 0,
        .evidence_text_index = evidence_index,
        .match_text_index = 0,
        .match_flags = 0,
        .reserved_u32 = 0,
        .position_x = 10,
        .position_y = 20,
        .width = 1920,
        .height = 1080,
        .refresh_numerator = 60_000,
        .refresh_denominator = 1000,
        .bits_per_color_channel = 0,
        .minimum_luminance_milli_nits = 0,
        .maximum_luminance_milli_nits = 0,
        .maximum_full_frame_luminance_milli_nits = 0,
        .capability_flags = protocol.CapabilityFlags.advanced_color_supported |
            protocol.CapabilityFlags.advanced_color_enabled |
            protocol.CapabilityFlags.high_dynamic_range_supported |
            protocol.CapabilityFlags.high_dynamic_range_user_enabled |
            protocol.CapabilityFlags.high_dynamic_range_active,
        .observed_at_utc_ms = 1000,
        .source_object_key = 0,
        .payload_handle = .{
            .high = @intFromEnum(protocol.SourceId.display_config),
            .low = ordinal,
        },
    };
}

fn submitDxgi(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    ordinal: u64,
) !void {
    var fact = targetOnlyFact(.dxgi, ordinal, 0x1234, 7);
    fact.valid_mask |= protocol.FactValid.bits_per_color_channel |
        protocol.FactValid.minimum_luminance | protocol.FactValid.maximum_luminance |
        protocol.FactValid.full_frame_luminance;
    fact.bits_per_color_channel = 10;
    fact.minimum_luminance_milli_nits = 40;
    fact.maximum_luminance_milli_nits = 1_000_000;
    fact.maximum_full_frame_luminance_milli_nits = 400_000;
    const header = batchHeader(
        config,
        refresh_epoch,
        source_generation,
        .dxgi,
        .complete,
        1,
        0,
        0,
    );
    try expectCode(.ok, session.submitSource(&header, &.{fact}, &.{}, &.{}));
}

fn targetOnlyFact(
    source: protocol.SourceId,
    ordinal: u64,
    adapter_luid: u64,
    target_id: u32,
) protocol.DisplayFact {
    return .{
        .struct_size = @sizeOf(protocol.DisplayFact),
        .source_id = @intFromEnum(source),
        .valid_mask = protocol.FactValid.adapter_luid | protocol.FactValid.target_id |
            protocol.FactValid.payload_handle,
        .identity_mask = protocol.IdentityMask.target,
        .source_record_ordinal = ordinal,
        .adapter_luid = adapter_luid,
        .target_id = target_id,
        .connector_instance = 0,
        .output_technology = 0,
        .status_flags = 0,
        .monitor_path_text_index = 0,
        .source_device_text_index = 0,
        .friendly_name_text_index = 0,
        .edid_serial_text_index = 0,
        .evidence_text_index = 0,
        .match_text_index = 0,
        .match_flags = 0,
        .reserved_u32 = 0,
        .position_x = 0,
        .position_y = 0,
        .width = 0,
        .height = 0,
        .refresh_numerator = 0,
        .refresh_denominator = 0,
        .bits_per_color_channel = 0,
        .minimum_luminance_milli_nits = 0,
        .maximum_luminance_milli_nits = 0,
        .maximum_full_frame_luminance_milli_nits = 0,
        .capability_flags = 0,
        .observed_at_utc_ms = 0,
        .source_object_key = 0,
        .payload_handle = .{ .high = @intFromEnum(source), .low = ordinal },
    };
}

fn submitEmptyComplete(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    source: protocol.SourceId,
) !void {
    try expectCode(
        .ok,
        submitStatusResult(
            session,
            config,
            refresh_epoch,
            source_generation,
            source,
            .complete,
        ),
    );
}

fn submitStatus(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    source: protocol.SourceId,
    status: protocol.SourceStatus,
) !void {
    try expectCode(
        .ok,
        submitStatusResult(
            session,
            config,
            refresh_epoch,
            source_generation,
            source,
            status,
        ),
    );
}

fn submitStatusResult(
    session: *state.Session,
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    source: protocol.SourceId,
    status: protocol.SourceStatus,
) ResultCode {
    const header = batchHeader(
        config,
        refresh_epoch,
        source_generation,
        source,
        status,
        0,
        0,
        0,
    );
    return session.submitSource(&header, &.{}, &.{}, &.{});
}

fn batchHeader(
    config: protocol.Config,
    refresh_epoch: u64,
    source_generation: u64,
    source: protocol.SourceId,
    status: protocol.SourceStatus,
    fact_count: u32,
    text_count: u32,
    text_byte_count: u32,
) protocol.SourceBatchHeader {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SourceBatchHeader),
        .configuration_generation = config.generation,
        .refresh_epoch = refresh_epoch,
        .source_generation = source_generation,
        .captured_utc_ms = @intCast(1000 * refresh_epoch),
        .source_id = @intFromEnum(source),
        .status = @intFromEnum(status),
        .fact_count = fact_count,
        .text_binding_count = text_count,
        .text_byte_count = text_byte_count,
        .reserved_u32 = 0,
        .valid_mask = protocol.BatchValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn textInput(
    kind: protocol.TextKind,
    offset: u32,
    length: u32,
) protocol.TextInput {
    return .{
        .struct_size = @sizeOf(protocol.TextInput),
        .normalization_kind = @intFromEnum(kind),
        .byte_offset = offset,
        .byte_length = length,
        .valid_mask = protocol.TextValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn expectCode(expected: ResultCode, actual: ResultCode) !void {
    try std.testing.expectEqual(expected, actual);
}

fn nextPowerOfTwo(value: u32) u32 {
    var result: u32 = 1;
    while (result < value) result *= 2;
    return result;
}
