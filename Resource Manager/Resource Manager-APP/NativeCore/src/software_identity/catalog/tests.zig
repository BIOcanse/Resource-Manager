const std = @import("std");
const abi = @import("abi.zig");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

test "installed process and portable exact evidence keep strict catalog semantics" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var catalog = CatalogFixture.init();
    try std.testing.expectEqual(ResultCode.ok, applyCatalog(session, 1, 1, &catalog));

    var installed = QueryFixture.init(.installed_software);
    installed.appendNumeric(.steam_app_id, 489_830);
    installed.appendKey(.installed_name, "the elder scrolls v: skyrim special edition");
    const installed_result = runQuery(session, 1, &installed);
    try std.testing.expectEqual(ResultCode.ok, installed_result.code);
    try expectExactMatched(installed_result.output, 10, .confirmed);
    try std.testing.expectEqual(@as(u32, 2), installed_result.output.matched_fact_count);

    var process = QueryFixture.init(.process);
    process.appendKey(.executable_name, "vlc");
    process.appendKey(.product_name, "vlc media player");
    const process_result = runQuery(session, 2, &process);
    try std.testing.expectEqual(ResultCode.ok, process_result.code);
    try expectExactMatched(process_result.output, 20, .confirmed);
    try std.testing.expectEqual(
        protocol.Evidence.executable_name | protocol.Evidence.product_name,
        process_result.output.evidence_mask,
    );

    var prohibited = QueryFixture.init(.process);
    prohibited.appendKey(.executable_name, "launcher");
    const prohibited_result = runQuery(session, 3, &prohibited);
    try std.testing.expectEqual(ResultCode.ok, prohibited_result.code);
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.no_match), prohibited_result.output.status);

    var candidate = QueryFixture.init(.portable_process);
    candidate.appendKey(.executable_name, "skyrimse");
    try expectExactMatched(runQuery(session, 4, &candidate).output, 10, .candidate);

    var confirmed = QueryFixture.init(.portable_process);
    confirmed.appendKey(.executable_name, "skyrimse");
    confirmed.appendKey(.product_name, "the elder scrolls v: skyrim special edition");
    try expectExactMatched(runQuery(session, 5, &confirmed).output, 10, .confirmed);

    var unmatched = QueryFixture.init(.portable_process);
    unmatched.appendKey(.executable_name, "skyrimse");
    unmatched.appendKey(.product_name, "unknown product");
    try std.testing.expectEqual(
        @intFromEnum(protocol.MatchStatus.no_match),
        runQuery(session, 6, &unmatched).output.status,
    );

    var conflict = QueryFixture.init(.portable_process);
    conflict.appendKey(.executable_name, "skyrimse");
    conflict.appendKey(.product_name, "vlc media player");
    const conflict_result = runQuery(session, 7, &conflict);
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.conflict), conflict_result.output.status);
    try std.testing.expectEqual(@as(u32, 2), conflict_result.output.conflict_count);
}

test "catalog replacement validates roots rules and identities before active swap" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var catalog = CatalogFixture.init();
    try std.testing.expectEqual(ResultCode.ok, applyCatalog(session, 1, 1, &catalog));
    const before = snapshot(session);

    var invalid_alias = CatalogFixture.init();
    invalid_alias.aliases[0].entry_handle = 999;
    try std.testing.expectEqual(ResultCode.invalid_argument, applyCatalog(session, 2, 2, &invalid_alias));

    var duplicate_identity = CatalogFixture.init();
    duplicate_identity.entries[1].attribution_id_handle = duplicate_identity.entries[0].attribution_id_handle;
    try std.testing.expectEqual(ResultCode.invalid_argument, applyCatalog(session, 2, 2, &duplicate_identity));

    var duplicate_root = CatalogFixture.init();
    duplicate_root.roots[1].root_handle = duplicate_root.roots[0].root_handle;
    try std.testing.expectEqual(ResultCode.invalid_argument, applyCatalog(session, 2, 2, &duplicate_root));

    var unknown_root_entry = CatalogFixture.init();
    unknown_root_entry.roots[1].entry_handle = 999;
    try std.testing.expectEqual(ResultCode.invalid_argument, applyCatalog(session, 2, 2, &unknown_root_entry));

    var invalid_rule = CatalogFixture.init();
    invalid_rule.aliases[10].entry_handle = 30;
    try std.testing.expectEqual(ResultCode.invalid_argument, applyCatalog(session, 2, 2, &invalid_rule));

    var missing_rule_count = CatalogFixture.init();
    var missing_rule_input = replacementInput(2, 2, &missing_rule_count, config.generation);
    missing_rule_input.launcher_token_count = 0;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session.replace(
            &missing_rule_input,
            &missing_rule_count.entries,
            &missing_rule_count.aliases,
            &missing_rule_count.roots,
            missing_rule_count.keys[0..missing_rule_count.key_count],
        ),
    );

    const after = snapshot(session);
    try std.testing.expectEqual(before.catalog_generation, after.catalog_generation);
    try std.testing.expectEqual(before.state_revision, after.state_revision);
    try std.testing.expectEqual(before.catalog_fingerprint_low, after.catalog_fingerprint_low);
    try std.testing.expectEqual(before.catalog_fingerprint_high, after.catalog_fingerprint_high);

    var query = QueryFixture.init(.installed_software);
    query.appendNumeric(.steam_app_id, 489_830);
    try expectExactMatched(runQuery(session, 1, &query).output, 10, .confirmed);
}

test "known identity scoring uses explicit strong gate weights and tie conflicts" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var catalog = CatalogFixture.init();
    try std.testing.expectEqual(ResultCode.ok, applyCatalog(session, 1, 1, &catalog));

    var contains = KnownQueryFixture.init(null);
    contains.appendSignal(.process_name, "vlc");
    const contains_result = runKnownQuery(session, 1, &contains);
    try std.testing.expectEqual(ResultCode.ok, contains_result.code);
    try expectKnownMatched(contains_result.output, 20, .identity);
    try std.testing.expectEqual(@as(i32, 95), contains_result.output.score);
    try std.testing.expectEqual(protocol.SignalEvidence.process_name, contains_result.output.evidence_mask);

    var exact = KnownQueryFixture.init(null);
    exact.appendSignal(.product_name, "vlcmediaplayer");
    const exact_result = runKnownQuery(session, 2, &exact);
    try expectKnownMatched(exact_result.output, 20, .identity);
    try std.testing.expectEqual(@as(i32, 160), exact_result.output.score);

    var auxiliary_only = KnownQueryFixture.init(null);
    auxiliary_only.appendSignal(.window_title, "vlc");
    const auxiliary_result = runKnownQuery(session, 3, &auxiliary_only);
    try std.testing.expectEqual(ResultCode.ok, auxiliary_result.code);
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.no_match), auxiliary_result.output.status);

    var unknown = KnownQueryFixture.init(null);
    unknown.appendSignal(.process_name, "unknown");
    const no_implicit_tie = runKnownQuery(session, 4, &unknown);
    try std.testing.expectEqual(ResultCode.ok, no_implicit_tie.code);
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.no_match), no_implicit_tie.output.status);

    var tie_config = testConfig();
    tie_config.required_prohibited_alias_count = 0;
    tie_config.required_launcher_token_count = 0;
    tie_config.required_managed_child_rule_count = 0;
    const tie_session = try session_module.Session.create(&tie_config);
    defer tie_session.destroy();
    var tie_catalog = IdentityTieCatalog.init();
    try std.testing.expectEqual(ResultCode.ok, applyIdentityTieCatalog(tie_session, &tie_catalog));
    var same_identity_score = KnownQueryFixture.init(null);
    same_identity_score.appendSignal(.process_name, "sameapp");
    const identity_conflict = runKnownQuery(tie_session, 1, &same_identity_score);
    try std.testing.expectEqual(ResultCode.ok, identity_conflict.code);
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.conflict), identity_conflict.output.status);
    try std.testing.expectEqual(@intFromEnum(protocol.KnownMatchMode.identity), identity_conflict.output.mode);
    try std.testing.expectEqual(@as(u32, 2), identity_conflict.output.conflict_count);
    try std.testing.expectEqual(@as(u64, 0), identity_conflict.output.entry_handle);

    var lower_contains_score = config;
    lower_contains_score.generation = 2;
    lower_contains_score.contains_text_score = 40;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&lower_contains_score));
    var same_contains = KnownQueryFixture.init(null);
    same_contains.appendSignal(.process_name, "vlc");
    const reconfigured_result = runKnownQuery(session, 5, &same_contains);
    try std.testing.expectEqual(ResultCode.ok, reconfigured_result.code);
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.no_match), reconfigured_result.output.status);
}

test "known root chooses longest root and equal authority is conflict" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var catalog = CatalogFixture.init();
    try std.testing.expectEqual(ResultCode.ok, applyCatalog(session, 1, 1, &catalog));

    var root_query = KnownQueryFixture.init("c:/apps/vlc/bin/vlc.exe");
    const root_result = runKnownQuery(session, 1, &root_query);
    try std.testing.expectEqual(ResultCode.ok, root_result.code);
    try expectKnownMatched(root_result.output, 20, .root);
    try std.testing.expectEqual(@as(u64, 1_002), root_result.output.matched_root_handle);
    try std.testing.expect((root_result.output.flags & protocol.KnownOutputFlags.root_hit) != 0);

    var tie_config = testConfig();
    tie_config.required_prohibited_alias_count = 0;
    tie_config.required_launcher_token_count = 0;
    tie_config.required_managed_child_rule_count = 0;
    const tie_session = try session_module.Session.create(&tie_config);
    defer tie_session.destroy();
    var tie_catalog = RootTieCatalog.init();
    try std.testing.expectEqual(ResultCode.ok, applyRootTieCatalog(tie_session, &tie_catalog));
    var tie_query = KnownQueryFixture.init("c:/shared/tool.exe");
    const tie_result = runKnownQuery(tie_session, 1, &tie_query);
    try std.testing.expectEqual(ResultCode.ok, tie_result.code);
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.conflict), tie_result.output.status);
    try std.testing.expectEqual(@intFromEnum(protocol.KnownMatchMode.root), tie_result.output.mode);
    try std.testing.expectEqual(@as(u32, 2), tie_result.output.conflict_count);
    try std.testing.expectEqual(@as(u64, 0), tie_result.output.entry_handle);
}

test "launcher and managed child rules are catalog data rather than source constants" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var catalog = CatalogFixture.init();
    try std.testing.expectEqual(ResultCode.ok, applyCatalog(session, 1, 1, &catalog));

    var launcher = KnownQueryFixture.init("c:/steam/steam.exe");
    launcher.appendSignal(.process_name, "steamlauncher");
    const launcher_result = runKnownQuery(session, 1, &launcher);
    try expectKnownMatched(launcher_result.output, 30, .identity);
    try std.testing.expect((launcher_result.output.flags & protocol.KnownOutputFlags.query_launcher) != 0);
    try std.testing.expect((launcher_result.output.flags & protocol.KnownOutputFlags.entry_launcher) != 0);

    const game_root = "c:/steam/games/eldenring";
    var nested_game = KnownQueryFixture.init(game_root ++ "/game.exe");
    nested_game.appendSignal(.process_name, "eldenring");
    const nested_result = runKnownQuery(session, 2, &nested_game);
    try std.testing.expectEqual(ResultCode.ok, nested_result.code);
    try expectKnownMatched(nested_result.output, 30, .launcher_managed_child);
    try std.testing.expectEqual(@as(u64, 1_003), nested_result.output.matched_root_handle);
    try std.testing.expectEqual(@as(u32, game_root.len), nested_result.output.derived_root_byte_length);
    try std.testing.expect(nested_result.output.derived_identity_fingerprint_low != 0 or
        nested_result.output.derived_identity_fingerprint_high != 0);

    var next_nested_game = KnownQueryFixture.init("c:/steam/games/eldenring/other.exe");
    next_nested_game.appendSignal(.process_name, "eldenring");
    const stable_identity = runKnownQuery(session, 3, &next_nested_game);
    try std.testing.expectEqual(
        nested_result.output.derived_identity_fingerprint_low,
        stable_identity.output.derived_identity_fingerprint_low,
    );
    try std.testing.expectEqual(
        nested_result.output.derived_identity_fingerprint_high,
        stable_identity.output.derived_identity_fingerprint_high,
    );
}

test "explicit empty rules disable launcher special behavior without hidden fallback" {
    var config = testConfig();
    config.required_launcher_token_count = 0;
    config.required_managed_child_rule_count = 0;
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var catalog = CatalogFixture.init();
    catalog.alias_count = 10;
    catalog.key_count -= "launcher".len + "games".len;
    catalog.launcher_token_count = 0;
    catalog.managed_child_rule_count = 0;
    try std.testing.expectEqual(ResultCode.ok, applyCatalog(session, 1, 1, &catalog));

    var nested = KnownQueryFixture.init("c:/steam/games/eldenring/game.exe");
    nested.appendSignal(.process_name, "eldenring");
    const result = runKnownQuery(session, 1, &nested);
    try std.testing.expectEqual(ResultCode.ok, result.code);
    try expectKnownMatched(result.output, 30, .root);
    try std.testing.expectEqual(@as(u32, 0), result.output.derived_root_byte_length);
    try std.testing.expectEqual(@as(u64, 0), result.output.flags & protocol.KnownOutputFlags.entry_launcher);
}

test "fixed capacity and resident budget remain explicit" {
    const capacity = 64;
    var config = testConfig();
    config.maximum_entry_count = capacity;
    config.maximum_alias_count = capacity;
    config.maximum_root_count = 0;
    config.maximum_catalog_key_byte_count = capacity * 12;
    config.maximum_query_fact_count = 1;
    config.maximum_query_signal_count = 1;
    config.maximum_query_key_byte_count = 6;
    config.entry_index_capacity = 64;
    config.alias_index_capacity = 64;
    config.identity_index_capacity = 64;
    config.root_index_capacity = 1;
    config.required_prohibited_alias_count = 0;
    config.required_launcher_token_count = 0;
    config.required_managed_child_rule_count = 0;
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    var entries = std.mem.zeroes([capacity]protocol.EntryInput);
    var aliases = std.mem.zeroes([capacity]protocol.AliasInput);
    var keys = std.mem.zeroes([capacity * 12]u8);
    var index: usize = 0;
    while (index < capacity) : (index += 1) {
        const handle: u64 = @intCast(index + 1);
        const primary_offset = index * 6;
        const primary = try std.fmt.bufPrint(keys[primary_offset..][0..6], "app{d:0>3}", .{index});
        entries[index] = entry(handle, 1_000 + handle, 2_000 + handle, 1, 1, @intCast(primary_offset), 6);
        const alias_offset = capacity * 6 + index * 6;
        @memcpy(keys[alias_offset .. alias_offset + 6], primary);
        aliases[index] = keyAlias(.executable_name, handle, @intCast(alias_offset), 6);
    }
    var replace_input = rawReplacementInput(
        &config,
        1,
        1,
        capacity,
        capacity,
        0,
        keys.len,
        0,
        0,
        0,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session.replace(&replace_input, &entries, &aliases, &.{}, &keys),
    );

    var query = QueryFixture.init(.process);
    query.appendKey(.executable_name, "app063");
    try expectExactMatched(runQuery(session, 1, &query).output, 64, .confirmed);
    const summary = snapshot(session);
    try std.testing.expectEqual(@as(u32, capacity), summary.entry_count);
    try std.testing.expectEqual(@as(u32, capacity), summary.alias_count);
    try std.testing.expect(summary.resident_byte_count > 0);

    var insufficient = config;
    insufficient.resident_byte_budget = summary.resident_byte_count - 1;
    try std.testing.expectError(error.InvalidConfiguration, session_module.Session.create(&insufficient));
}

test "empty catalog and strict query shapes are explicit" {
    var config = testConfig();
    config.required_prohibited_alias_count = 0;
    config.required_launcher_token_count = 0;
    config.required_managed_child_rule_count = 0;
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var replace_input = rawReplacementInput(&config, 1, 1, 0, 0, 0, 0, 0, 0, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.replace(session, &replace_input, null, 0, null, 0, null, 0, null, 0),
    );
    try std.testing.expectEqual(@as(u32, 0), snapshot(session).entry_count);

    var exact = QueryFixture.init(.process);
    try std.testing.expectEqual(
        @intFromEnum(protocol.MatchStatus.no_match),
        runQuery(session, 1, &exact).output.status,
    );
    var known = KnownQueryFixture.init(null);
    try std.testing.expectEqual(
        @intFromEnum(protocol.MatchStatus.no_match),
        runKnownQuery(session, 2, &known).output.status,
    );
}

test "canonical ordering epochs dirty outputs and shape drift fail closed" {
    var config = testConfig();
    var invalid_config = config;
    invalid_config.root_index_capacity = 3;
    try std.testing.expectError(error.InvalidConfiguration, session_module.Session.create(&invalid_config));

    const relative_path = KnownQueryFixture.init("apps/vlc/vlc.exe");
    try std.testing.expect(!protocol.validCanonicalPath(
        relative_path.keys[0..relative_path.key_count],
        0,
        relative_path.path_length,
    ));
    invalid_config = config;
    invalid_config.resident_byte_budget = 1;
    try std.testing.expectError(error.InvalidConfiguration, session_module.Session.create(&invalid_config));

    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var catalog = CatalogFixture.init();
    catalog.keys[0] = 'V';
    try std.testing.expectEqual(ResultCode.invalid_argument, applyCatalog(session, 1, 1, &catalog));
    catalog = CatalogFixture.init();
    try std.testing.expectEqual(ResultCode.ok, applyCatalog(session, 1, 1, &catalog));
    try std.testing.expectEqual(ResultCode.stale_frame, applyCatalog(session, 1, 2, &catalog));

    var known = KnownQueryFixture.init("c:/apps/vlc/vlc.exe");
    known.appendSignal(.product_name, "vlcmediaplayer");
    var dirty = emptyKnownOutput();
    dirty.flags = 1;
    try std.testing.expectEqual(ResultCode.abi_mismatch, matchKnownWithOutput(session, 1, &known, &dirty));
    known.signals[0].kind = @intFromEnum(protocol.SignalKind.process_name);
    const valid = runKnownQuery(session, 1, &known);
    try std.testing.expectEqual(ResultCode.ok, valid.code);
    try std.testing.expectEqual(ResultCode.stale_frame, runKnownQuery(session, 1, &known).code);

    var unsorted = KnownQueryFixture.init(null);
    unsorted.appendSignal(.product_name, "vlcmediaplayer");
    unsorted.appendSignal(.process_name, "vlc");
    try std.testing.expectEqual(ResultCode.invalid_argument, runKnownQuery(session, 2, &unsorted).code);

    var next_config = config;
    next_config.generation = 2;
    next_config.maximum_root_count += 1;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.reconfigure(&next_config));
    next_config.maximum_root_count -= 1;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&next_config));
}

test "module-local C ABI covers exact and known paths without root export" {
    var config = testConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_catalog_create(&config, &handle),
    );
    defer abi.rm_software_identity_catalog_destroy(handle);

    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_catalog_query_capacity(handle, &capacity, @sizeOf(protocol.Capacity)),
    );
    try std.testing.expectEqual(config.maximum_root_count, capacity.root_capacity);
    try std.testing.expectEqual(config.root_index_capacity, capacity.root_index_capacity);
    try std.testing.expect(capacity.resident_byte_count > 0);
    try std.testing.expectEqual(protocol.abi_version, abi.rm_software_identity_catalog_abi_version());

    var catalog = CatalogFixture.init();
    var replace_input = replacementInput(1, 1, &catalog, config.generation);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_catalog_replace(
            handle,
            &replace_input,
            catalog.entries[0..catalog.entry_count].ptr,
            @intCast(catalog.entry_count),
            catalog.aliases[0..catalog.alias_count].ptr,
            @intCast(catalog.alias_count),
            catalog.roots[0..catalog.root_count].ptr,
            @intCast(catalog.root_count),
            catalog.keys[0..catalog.key_count].ptr,
            @intCast(catalog.key_count),
        ),
    );

    var exact = QueryFixture.init(.process);
    exact.appendKey(.executable_name, "vlc");
    var exact_input = queryInput(1, &exact, config.generation, 1);
    var exact_output = emptyMatchOutput();
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_catalog_match(
            handle,
            &exact_input,
            exact.facts[0..exact.fact_count].ptr,
            @intCast(exact.fact_count),
            exact.keys[0..exact.key_count].ptr,
            @intCast(exact.key_count),
            &exact_output,
        ),
    );
    try expectExactMatched(exact_output, 20, .confirmed);

    var known = KnownQueryFixture.init("c:/steam/games/eldenring/game.exe");
    known.appendSignal(.process_name, "eldenring");
    var known_input = knownQueryInput(2, &known, config.generation, 1);
    var known_output = emptyKnownOutput();
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_catalog_match_known(
            handle,
            &known_input,
            known.signals[0..known.signal_count].ptr,
            @intCast(known.signal_count),
            known.keys[0..known.key_count].ptr,
            @intCast(known.key_count),
            &known_output,
        ),
    );
    try expectKnownMatched(known_output, 30, .launcher_managed_child);
}

const MatchCall = struct {
    code: ResultCode,
    output: protocol.MatchOutput,
};

const KnownMatchCall = struct {
    code: ResultCode,
    output: protocol.KnownMatchOutput,
};

fn applyCatalog(
    session: *session_module.Session,
    catalog_generation: u64,
    operation_epoch: u64,
    fixture: *CatalogFixture,
) ResultCode {
    var input = replacementInput(catalog_generation, operation_epoch, fixture, 1);
    return session_module.replace(
        session,
        &input,
        fixture.entries[0..fixture.entry_count].ptr,
        @intCast(fixture.entry_count),
        fixture.aliases[0..fixture.alias_count].ptr,
        @intCast(fixture.alias_count),
        fixture.roots[0..fixture.root_count].ptr,
        @intCast(fixture.root_count),
        fixture.keys[0..fixture.key_count].ptr,
        @intCast(fixture.key_count),
    );
}

fn applyRootTieCatalog(session: *session_module.Session, fixture: *RootTieCatalog) ResultCode {
    var input = rawReplacementInput(&session.config, 1, 1, 2, 2, 2, fixture.key_count, 0, 0, 0);
    return session.replace(
        &input,
        &fixture.entries,
        &fixture.aliases,
        &fixture.roots,
        fixture.keys[0..fixture.key_count],
    );
}

fn applyIdentityTieCatalog(session: *session_module.Session, fixture: *IdentityTieCatalog) ResultCode {
    var input = rawReplacementInput(&session.config, 1, 1, 2, 2, 0, fixture.key_count, 0, 0, 0);
    return session.replace(
        &input,
        &fixture.entries,
        &fixture.aliases,
        &.{},
        fixture.keys[0..fixture.key_count],
    );
}

fn runQuery(session: *session_module.Session, query_epoch: u64, fixture: *QueryFixture) MatchCall {
    var output = emptyMatchOutput();
    var input = queryInput(query_epoch, fixture, session.config.generation, session.catalog_generation);
    const code = session.match(
        &input,
        fixture.facts[0..fixture.fact_count],
        fixture.keys[0..fixture.key_count],
        &output,
    );
    return .{ .code = code, .output = output };
}

fn runKnownQuery(session: *session_module.Session, query_epoch: u64, fixture: *KnownQueryFixture) KnownMatchCall {
    var output = emptyKnownOutput();
    const code = matchKnownWithOutput(session, query_epoch, fixture, &output);
    return .{ .code = code, .output = output };
}

fn matchKnownWithOutput(
    session: *session_module.Session,
    query_epoch: u64,
    fixture: *KnownQueryFixture,
    output: *protocol.KnownMatchOutput,
) ResultCode {
    var input = knownQueryInput(query_epoch, fixture, session.config.generation, session.catalog_generation);
    return session.matchKnown(
        &input,
        fixture.signals[0..fixture.signal_count],
        fixture.keys[0..fixture.key_count],
        output,
    );
}

fn snapshot(session: *session_module.Session) protocol.CatalogSummary {
    var output = emptySummary();
    const result = session.snapshot(&output);
    std.debug.assert(result == .ok);
    return output;
}

fn expectExactMatched(
    output: protocol.MatchOutput,
    entry_handle: u64,
    confidence: protocol.MatchConfidence,
) !void {
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.matched), output.status);
    try std.testing.expectEqual(@intFromEnum(confidence), output.confidence);
    try std.testing.expectEqual(entry_handle, output.entry_handle);
    try std.testing.expect(output.attribution_id_handle != 0);
    try std.testing.expect(output.display_name_handle != 0);
}

fn expectKnownMatched(
    output: protocol.KnownMatchOutput,
    entry_handle: u64,
    mode: protocol.KnownMatchMode,
) !void {
    try std.testing.expectEqual(@intFromEnum(protocol.MatchStatus.matched), output.status);
    try std.testing.expectEqual(@intFromEnum(mode), output.mode);
    try std.testing.expectEqual(entry_handle, output.entry_handle);
    try std.testing.expect(output.attribution_id_handle != 0);
    try std.testing.expect(output.display_name_handle != 0);
}

const CatalogFixture = struct {
    entries: [3]protocol.EntryInput,
    aliases: [12]protocol.AliasInput,
    roots: [3]protocol.RootInput,
    keys: [512]u8,
    entry_count: usize,
    alias_count: usize,
    root_count: usize,
    key_count: usize,
    prohibited_alias_count: u32,
    launcher_token_count: u32,
    managed_child_rule_count: u32,

    fn init() CatalogFixture {
        var fixture = CatalogFixture{
            .entries = std.mem.zeroes([3]protocol.EntryInput),
            .aliases = std.mem.zeroes([12]protocol.AliasInput),
            .roots = std.mem.zeroes([3]protocol.RootInput),
            .keys = std.mem.zeroes([512]u8),
            .entry_count = 3,
            .alias_count = 12,
            .root_count = 3,
            .key_count = 0,
            .prohibited_alias_count = 1,
            .launcher_token_count = 1,
            .managed_child_rule_count = 1,
        };
        fixture.entries[0] = fixture.appendEntry(10, 110, 210, 1, 1, "theelderscrollsvskyrimspecialedition");
        fixture.entries[1] = fixture.appendEntry(20, 120, 220, 1, 2, "vlcmediaplayer");
        fixture.entries[2] = fixture.appendEntry(30, 130, 230, 1, 3, "steamlauncher");
        fixture.roots[0] = fixture.appendRoot(1_001, 10, "c:/games/skyrim");
        fixture.roots[1] = fixture.appendRoot(1_002, 20, "c:/apps/vlc");
        fixture.roots[2] = fixture.appendRoot(1_003, 30, "c:/steam");
        fixture.aliases[0] = numericAlias(.steam_app_id, 10, 489_830);
        fixture.aliases[1] = fixture.appendAlias(.package_identifier, 20, "org.videolan.vlc");
        fixture.aliases[2] = fixture.appendAlias(.installed_name, 30, "steam launcher");
        fixture.aliases[3] = fixture.appendAlias(.installed_name, 10, "the elder scrolls v: skyrim special edition");
        fixture.aliases[4] = fixture.appendProhibitedAlias("launcher");
        fixture.aliases[5] = fixture.appendAlias(.executable_name, 10, "skyrimse");
        fixture.aliases[6] = fixture.appendAlias(.executable_name, 30, "steam");
        fixture.aliases[7] = fixture.appendAlias(.executable_name, 20, "vlc");
        fixture.aliases[8] = fixture.appendAlias(.product_name, 10, "the elder scrolls v: skyrim special edition");
        fixture.aliases[9] = fixture.appendAlias(.product_name, 20, "vlc media player");
        fixture.aliases[10] = fixture.appendRule(.launcher_token, "launcher");
        fixture.aliases[11] = fixture.appendRule(.managed_child_segment, "games");
        return fixture;
    }

    fn appendBytes(self: *CatalogFixture, value: []const u8) struct { offset: u32, length: u32 } {
        const offset = self.key_count;
        @memcpy(self.keys[offset .. offset + value.len], value);
        self.key_count += value.len;
        return .{ .offset = @intCast(offset), .length = @intCast(value.len) };
    }

    fn appendEntry(
        self: *CatalogFixture,
        handle: u64,
        attribution: u64,
        display: u64,
        source: u32,
        kind: u32,
        primary_name: []const u8,
    ) protocol.EntryInput {
        const key = self.appendBytes(primary_name);
        return entry(handle, attribution, display, source, kind, key.offset, key.length);
    }

    fn appendRoot(self: *CatalogFixture, root_handle: u64, entry_handle: u64, value: []const u8) protocol.RootInput {
        const key = self.appendBytes(value);
        return root(root_handle, entry_handle, key.offset, key.length);
    }

    fn appendAlias(self: *CatalogFixture, kind: protocol.AliasKind, entry_handle: u64, value: []const u8) protocol.AliasInput {
        const key = self.appendBytes(value);
        return keyAlias(kind, entry_handle, key.offset, key.length);
    }

    fn appendProhibitedAlias(self: *CatalogFixture, value: []const u8) protocol.AliasInput {
        const key = self.appendBytes(value);
        return prohibitedAlias(.executable_name, key.offset, key.length);
    }

    fn appendRule(self: *CatalogFixture, kind: protocol.AliasKind, value: []const u8) protocol.AliasInput {
        const key = self.appendBytes(value);
        return keyAlias(kind, 0, key.offset, key.length);
    }
};

const RootTieCatalog = struct {
    entries: [2]protocol.EntryInput,
    aliases: [2]protocol.AliasInput,
    roots: [2]protocol.RootInput,
    keys: [64]u8,
    key_count: usize,

    fn init() RootTieCatalog {
        var keys = std.mem.zeroes([64]u8);
        const bytes = "alphabetac:/sharedc:/sharedalphabeta";
        @memcpy(keys[0..bytes.len], bytes);
        return .{
            .entries = .{
                entry(10, 110, 210, 1, 1, 0, 5),
                entry(20, 120, 220, 1, 1, 5, 4),
            },
            .roots = .{
                root(1, 10, 9, 9),
                root(2, 20, 18, 9),
            },
            .aliases = .{
                keyAlias(.executable_name, 10, 27, 5),
                keyAlias(.executable_name, 20, 32, 4),
            },
            .keys = keys,
            .key_count = bytes.len,
        };
    }
};

const IdentityTieCatalog = struct {
    entries: [2]protocol.EntryInput,
    aliases: [2]protocol.AliasInput,
    keys: [64]u8,
    key_count: usize,

    fn init() IdentityTieCatalog {
        var keys = std.mem.zeroes([64]u8);
        const bytes = "sameappsameappalphabeta";
        @memcpy(keys[0..bytes.len], bytes);
        return .{
            .entries = .{
                entry(10, 110, 210, 1, 1, 0, 7),
                entry(20, 120, 220, 1, 1, 7, 7),
            },
            .aliases = .{
                keyAlias(.executable_name, 10, 14, 5),
                keyAlias(.executable_name, 20, 19, 4),
            },
            .keys = keys,
            .key_count = bytes.len,
        };
    }
};

const QueryFixture = struct {
    mode: protocol.MatchMode,
    facts: [4]protocol.FactInput,
    keys: [192]u8,
    fact_count: usize,
    key_count: usize,

    fn init(mode: protocol.MatchMode) QueryFixture {
        return .{
            .mode = mode,
            .facts = std.mem.zeroes([4]protocol.FactInput),
            .keys = std.mem.zeroes([192]u8),
            .fact_count = 0,
            .key_count = 0,
        };
    }

    fn appendNumeric(self: *QueryFixture, kind: protocol.AliasKind, value: u64) void {
        self.facts[self.fact_count] = numericFact(kind, value);
        self.fact_count += 1;
    }

    fn appendKey(self: *QueryFixture, kind: protocol.AliasKind, key: []const u8) void {
        const offset = self.key_count;
        @memcpy(self.keys[offset .. offset + key.len], key);
        self.key_count += key.len;
        self.facts[self.fact_count] = keyFact(kind, @intCast(offset), @intCast(key.len));
        self.fact_count += 1;
    }
};

const KnownQueryFixture = struct {
    signals: [protocol.maximum_signal_count]protocol.KnownSignalInput,
    keys: [512]u8,
    signal_count: usize,
    key_count: usize,
    path_length: u32,

    fn init(path: ?[]const u8) KnownQueryFixture {
        var fixture = KnownQueryFixture{
            .signals = std.mem.zeroes([protocol.maximum_signal_count]protocol.KnownSignalInput),
            .keys = std.mem.zeroes([512]u8),
            .signal_count = 0,
            .key_count = 0,
            .path_length = 0,
        };
        if (path) |value| {
            @memcpy(fixture.keys[0..value.len], value);
            fixture.key_count = value.len;
            fixture.path_length = @intCast(value.len);
        }
        return fixture;
    }

    fn appendSignal(self: *KnownQueryFixture, kind: protocol.SignalKind, key: []const u8) void {
        const offset = self.key_count;
        @memcpy(self.keys[offset .. offset + key.len], key);
        self.key_count += key.len;
        self.signals[self.signal_count] = knownSignal(kind, @intCast(offset), @intCast(key.len));
        self.signal_count += 1;
    }
};

fn entry(
    handle: u64,
    attribution: u64,
    display: u64,
    source: u32,
    kind: u32,
    primary_name_offset: u32,
    primary_name_length: u32,
) protocol.EntryInput {
    return .{
        .struct_size = @sizeOf(protocol.EntryInput),
        .flags = 0,
        .entry_handle = handle,
        .attribution_id_handle = attribution,
        .display_name_handle = display,
        .source = source,
        .software_kind = kind,
        .primary_name_offset = primary_name_offset,
        .primary_name_length = primary_name_length,
        .reserved = .{ 0, 0, 0 },
    };
}

fn root(root_handle: u64, entry_handle: u64, offset: u32, length: u32) protocol.RootInput {
    return .{
        .struct_size = @sizeOf(protocol.RootInput),
        .flags = 0,
        .root_handle = root_handle,
        .entry_handle = entry_handle,
        .key_offset = offset,
        .key_length = length,
        .reserved = .{ 0, 0, 0 },
    };
}

fn numericAlias(kind: protocol.AliasKind, entry_handle: u64, value: u64) protocol.AliasInput {
    return .{
        .struct_size = @sizeOf(protocol.AliasInput),
        .kind = @intFromEnum(kind),
        .entry_handle = entry_handle,
        .key_offset = 0,
        .key_length = 0,
        .numeric_value = value,
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn keyAlias(kind: protocol.AliasKind, entry_handle: u64, offset: u32, length: u32) protocol.AliasInput {
    var result = numericAlias(kind, entry_handle, 0);
    result.key_offset = offset;
    result.key_length = length;
    return result;
}

fn prohibitedAlias(kind: protocol.AliasKind, offset: u32, length: u32) protocol.AliasInput {
    var result = keyAlias(kind, 0, offset, length);
    result.flags = protocol.AliasFlags.prohibited;
    return result;
}

fn numericFact(kind: protocol.AliasKind, value: u64) protocol.FactInput {
    return .{
        .struct_size = @sizeOf(protocol.FactInput),
        .kind = @intFromEnum(kind),
        .key_offset = 0,
        .key_length = 0,
        .numeric_value = value,
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn keyFact(kind: protocol.AliasKind, offset: u32, length: u32) protocol.FactInput {
    var result = numericFact(kind, 0);
    result.key_offset = offset;
    result.key_length = length;
    return result;
}

fn knownSignal(kind: protocol.SignalKind, offset: u32, length: u32) protocol.KnownSignalInput {
    return .{
        .struct_size = @sizeOf(protocol.KnownSignalInput),
        .kind = @intFromEnum(kind),
        .key_offset = offset,
        .key_length = length,
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn replacementInput(
    catalog_generation: u64,
    operation_epoch: u64,
    fixture: *const CatalogFixture,
    configuration_generation: u64,
) protocol.ReplaceInput {
    var config = testConfigAtGeneration(configuration_generation);
    return rawReplacementInput(
        &config,
        catalog_generation,
        operation_epoch,
        fixture.entry_count,
        fixture.alias_count,
        fixture.root_count,
        fixture.key_count,
        fixture.prohibited_alias_count,
        fixture.launcher_token_count,
        fixture.managed_child_rule_count,
    );
}

fn rawReplacementInput(
    config: *const protocol.Config,
    catalog_generation: u64,
    operation_epoch: u64,
    entry_count: usize,
    alias_count: usize,
    root_count: usize,
    key_byte_count: usize,
    prohibited_alias_count: u32,
    launcher_token_count: u32,
    managed_child_rule_count: u32,
) protocol.ReplaceInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ReplaceInput),
        .configuration_generation = config.generation,
        .catalog_generation = catalog_generation,
        .operation_epoch = operation_epoch,
        .entry_count = @intCast(entry_count),
        .alias_count = @intCast(alias_count),
        .root_count = @intCast(root_count),
        .key_byte_count = @intCast(key_byte_count),
        .prohibited_alias_count = prohibited_alias_count,
        .launcher_token_count = launcher_token_count,
        .managed_child_rule_count = managed_child_rule_count,
        .reserved_u32 = 0,
        .valid_mask = protocol.ReplaceValid.required,
        .reserved = .{ 0, 0 },
    };
}

fn queryInput(
    query_epoch: u64,
    fixture: *const QueryFixture,
    configuration_generation: u64,
    catalog_generation: u64,
) protocol.QueryInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.QueryInput),
        .configuration_generation = configuration_generation,
        .catalog_generation = catalog_generation,
        .query_epoch = query_epoch,
        .mode = @intFromEnum(fixture.mode),
        .fact_count = @intCast(fixture.fact_count),
        .key_byte_count = @intCast(fixture.key_count),
        .flags = 0,
        .valid_mask = protocol.QueryValid.required,
        .reserved = .{ 0, 0, 0 },
    };
}

fn knownQueryInput(
    query_epoch: u64,
    fixture: *const KnownQueryFixture,
    configuration_generation: u64,
    catalog_generation: u64,
) protocol.KnownQueryInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.KnownQueryInput),
        .configuration_generation = configuration_generation,
        .catalog_generation = catalog_generation,
        .query_epoch = query_epoch,
        .signal_count = @intCast(fixture.signal_count),
        .key_byte_count = @intCast(fixture.key_count),
        .executable_path_offset = 0,
        .executable_path_length = fixture.path_length,
        .valid_mask = protocol.KnownQueryValid.required |
            (if (fixture.path_length != 0) protocol.KnownQueryValid.executable_path else 0),
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
}

fn emptyMatchOutput() protocol.MatchOutput {
    var output = std.mem.zeroes(protocol.MatchOutput);
    output.abi_version = protocol.abi_version;
    output.struct_size = @sizeOf(protocol.MatchOutput);
    return output;
}

fn emptyKnownOutput() protocol.KnownMatchOutput {
    var output = std.mem.zeroes(protocol.KnownMatchOutput);
    output.abi_version = protocol.abi_version;
    output.struct_size = @sizeOf(protocol.KnownMatchOutput);
    return output;
}

fn emptySummary() protocol.CatalogSummary {
    var output = std.mem.zeroes(protocol.CatalogSummary);
    output.abi_version = protocol.abi_version;
    output.struct_size = @sizeOf(protocol.CatalogSummary);
    return output;
}

fn testConfig() protocol.Config {
    return testConfigAtGeneration(1);
}

fn testConfigAtGeneration(generation: u64) protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = generation,
        .maximum_entry_count = 8,
        .maximum_alias_count = 32,
        .maximum_root_count = 16,
        .maximum_catalog_key_byte_count = 1_024,
        .maximum_query_fact_count = 8,
        .maximum_query_signal_count = protocol.maximum_signal_count,
        .maximum_query_key_byte_count = 512,
        .entry_index_capacity = 16,
        .alias_index_capacity = 64,
        .identity_index_capacity = 16,
        .root_index_capacity = 32,
        .required_prohibited_alias_count = 1,
        .required_launcher_token_count = 1,
        .required_managed_child_rule_count = 1,
        .minimum_contains_key_length = 3,
        .reserved_capacity = 0,
        .resident_byte_budget = 1 << 24,
        .exact_text_score = 120,
        .contains_text_score = 50,
        .identity_minimum_score = 90,
        .strong_evidence_minimum_score = 90,
        .root_hit_bonus = 20,
        .query_launcher_match_bonus = 60,
        .query_launcher_nonmatch_penalty = 80,
        .root_launcher_match_bonus = 80,
        .root_launcher_nonmatch_penalty = 120,
        .root_reject_score = -100,
        .signal_weights = .{ 45, 40, 30, 30, 25, 10 },
        .root_signal_weights = .{ 0, 0, 0, 0, 0, 0 },
        .strong_signal_mask = @intCast(protocol.SignalEvidence.process_name |
            protocol.SignalEvidence.product_name |
            protocol.SignalEvidence.file_description |
            protocol.SignalEvidence.window_application_user_model_id |
            protocol.SignalEvidence.application_user_model_id),
        .root_signal_mask = @intCast(protocol.SignalEvidence.process_name |
            protocol.SignalEvidence.product_name |
            protocol.SignalEvidence.file_description),
        .flags = protocol.ConfigFlags.launcher_non_entry_requires_root,
        .reserved = .{ 0, 0, 0, 0 },
    };
}
