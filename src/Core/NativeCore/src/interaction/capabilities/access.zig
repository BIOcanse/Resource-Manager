const std = @import("std");

// The sole registration owner supplies the requirement. This leaf has no route,
// selector, notification-kind or data-source table and performs no OS queries.
pub const Requirement = enum(u32) {
    anonymous = 0,
    normal_read = 1,
    normal_action = 2,
    administrator = 3,
    per_selector = 4,
    missing_policy = 5,
};

pub const CallerLevel = enum(u32) { unidentified = 0, normal = 1, administrator = 2 };
pub const Disposition = enum(u32) {
    rejected = 0,
    allowed = 1,
    check_selectors = 2,
    // Entry admission is not evidence of this caller's Windows right to read
    // a target instance/field. This is NOT a delivery or mapping permission.
    check_process_permission = 3,
};
pub const Reason = enum(u32) {
    none = 0,
    loopback_origin_required = 1,
    local_windows_caller_required = 2,
    administrator_required = 3,
    access_policy_missing = 4,
    source_unavailable = 5,
};

// Borrowed from the existing platform proof, never decoded from request data.
// The platform verifies PID/creation/SID/session/token together. Administrator
// means its actual unfiltered Administrators result (including SYSTEM), not
// merely an elevated token or a process's claimed role.
pub const Caller = struct {
    process_id: i32,
    process_created_utc_ticks: i64,
    windows_sid: []const u16,
    windows_session_id: i32,
    is_administrator: bool,

    fn complete(self: Caller) bool {
        return self.process_id > 0 and self.process_created_utc_ticks > 0 and
            self.windows_session_id >= 0 and self.windows_sid.len != 0;
    }
};

pub const Input = struct {
    requirement: Requirement,
    // HTTP supplies its real connection/Host/Origin results; a local pipe
    // supplies its transport's real local-origin proof. No HttpContext crosses.
    local_connection: bool,
    origin_allowed: bool,
    caller: ?Caller,
    // Already resolved by the settings owner, including upgrade initialization.
    // No implicit default or setting fetch is part of this call.
    normal_actions_allowed: bool,
};

pub const Decision = extern struct {
    disposition: Disposition,
    reason: Reason,
    caller_level: CallerLevel,
};

pub fn reject(reason: Reason, level: CallerLevel) Decision {
    return .{ .disposition = .rejected, .reason = reason, .caller_level = level };
}

// Entry admission only: an allowed route must still use evaluateRead for its
// actual sources. Neither normal_read nor administrator proves detail rights.
pub fn evaluate(input: Input) Decision {
    if (!input.local_connection or !input.origin_allowed)
        return reject(.loopback_origin_required, .unidentified);

    switch (input.requirement) {
        .anonymous => return .{ .disposition = .allowed, .reason = .none, .caller_level = .unidentified },
        .missing_policy => return reject(.access_policy_missing, .unidentified),
        // This is a dispatch instruction, never a final admission or region
        // grant. Each actual selector must enter evaluate with its registration.
        .per_selector => return .{ .disposition = .check_selectors, .reason = .none, .caller_level = .unidentified },
        .normal_read, .normal_action, .administrator => {},
    }

    const caller = input.caller orelse return reject(.local_windows_caller_required, .unidentified);
    if (!caller.complete()) return reject(.local_windows_caller_required, .unidentified);
    const level: CallerLevel = if (caller.is_administrator) .administrator else .normal;
    if (level == .administrator or input.requirement == .normal_read or
        (input.requirement == .normal_action and input.normal_actions_allowed))
        return .{ .disposition = .allowed, .reason = .none, .caller_level = level };
    return reject(.administrator_required, level);
}

// Supplied by the existing source owner, never inferred from GET/normal_read.
// ordinary_data excludes caller/target-dependent process details. Enumeration
// here means the globally visible name/PID/session facts, independent of an
// OpenProcess detail read; it is not an OS-rights classification of counters.
// Only desktop facts/derivations use the service-verified source login session.
// Never fill it from a payload, a SID comparison or the active-session list.
pub const ReadKind = enum(u32) {
    ordinary_data = 0,
    desktop_fact = 1,
    process_enumeration = 2,
    process_detail = 3,
};
pub const ReadSource = struct {
    kind: ReadKind,
    windows_session_id: ?i32,
};

// One per-source decision for HTTP, pipe, subscription projection and D-grant.
// D-grant may grant a physical mapping only when EVERY source it exposes is
// admitted; this function does not split mappings or create filtered replicas.
pub fn evaluateRead(input: Input, source: ReadSource) Decision {
    const admission = evaluate(input);
    if (admission.disposition != .allowed or source.kind == .ordinary_data) return admission;

    // Even an erroneously anonymous registration cannot expose these sources
    // without an actual caller. The registration still supplies the data level.
    const actual = input.caller orelse return reject(.local_windows_caller_required, .unidentified);
    if (!actual.complete()) return reject(.local_windows_caller_required, .unidentified);
    const level: CallerLevel = if (actual.is_administrator) .administrator else .normal;
    if (source.kind == .process_enumeration)
        return .{ .disposition = .allowed, .reason = .none, .caller_level = level };
    if (source.kind == .process_detail)
        // Dispatch to evaluateProcessRead with a live platform borrow and the
        // original source's fact identity. Route/role/session is not that proof.
        return .{ .disposition = .check_process_permission, .reason = .none, .caller_level = level };

    const source_session = source.windows_session_id orelse return reject(.source_unavailable, level);
    if (source_session < 0) return reject(.source_unavailable, level);
    if (level != .administrator and actual.windows_session_id != source_session)
        return reject(.administrator_required, level);
    return .{ .disposition = .allowed, .reason = .none, .caller_level = level };
}

// Permission metadata only, borrowed from the ORIGINAL owners for this call.
// These references are never client-supplied handles or new identity registries.
// The platform must validate/borrow its real capability in the actual caller
// scope immediately before invoking this leaf; copying a past descriptor is not
// a substitute. This leaf does not own, renew or close Windows capabilities.
pub const ProcessInstance = extern struct {
    process_id: i32,
    // Existing ProcessInstanceKey.StartKey units, not caller UTC ticks.
    creation_file_time_100ns: i64,
};
pub const ProcessFact = extern struct {
    target: ProcessInstance,
    // Original versioned source binding (including epoch/layout identity).
    // Field identity denotes the exact API/group AND reading policy, not mask.
    // Dependency identity denotes the original token/file object+version when
    // required. Image path has no extra object beyond the target instance.
    source_identity: ?*const anyopaque,
    field_identity: ?*const anyopaque,
    dependency_identity: ?*const anyopaque,
};
pub const ProcessReadUse = enum(u32) { single_output = 0, live_mapping = 1 };
pub const EvidenceExtent = enum(u32) { single_query = 0, held_object = 1 };
pub const NativeErrorKind = enum(u32) { none = 0, win32 = 1, ntstatus = 2 };
// Internal typed results, not new HTTP/wire reason values. ALL proof-stage
// failures affect only this permission view, never source Current/parts/revision.
pub const ProcessPermissionStatus = enum(u32) {
    not_proven = 0,
    granted = 1,
    caller_unverified = 2,
    caller_changed = 3,
    scope_ended = 4,
    impersonation_unavailable = 5,
    access_denied = 6,
    target_changed = 7,
    api_unsupported = 8,
    query_failed = 9,
    partial_copy = 10,
    invalid_result = 11,
    dependency_unavailable = 12,
    source_mismatch = 13,
    lifetime_unproven = 14,
};
pub const ProcessReadEvidence = struct {
    status: ProcessPermissionStatus,
    extent: EvidenceExtent,
    caller_process_id: i32,
    caller_process_created_utc_ticks: i64,
    // Original currently borrowed caller scope and platform capability object.
    // OpenUnderCaller and freshly duplicated caller-held handles both enter
    // here. No route/mask/SID cache or fresh-Open-only restriction is introduced.
    caller_scope: ?*const anyopaque,
    capability: ?*const anyopaque,
    fact: ProcessFact,
    error_kind: NativeErrorKind,
    native_error: u32,
};
pub const ProcessReadDecision = extern struct {
    access: Decision,
    status: ProcessPermissionStatus,
    error_kind: NativeErrorKind,
    native_error: u32,
};

fn processUnavailable(level: CallerLevel, status: ProcessPermissionStatus) ProcessReadDecision {
    return .{ .access = reject(.source_unavailable, level), .status = status, .error_kind = .none, .native_error = 0 };
}

// Per-original-source view for GET, subscriptions and H/R's physical set. It
// contains no source value and cannot publish, sample, clear or retire anything.
// Single output permits one stable authorized result, NOT a live mapping.
// A live mapping additionally requires the platform's proved held-object
// lifetime and H's retirement/publication transaction; Allowed is not an OS
// handle and does not itself implement that transaction or authorize a section
// containing other facts. Every exposed fact/dependency must be covered.
pub fn evaluateProcessRead(input: Input, caller_scope: ?*const anyopaque, source: ProcessFact, use: ProcessReadUse, evidence: ?ProcessReadEvidence) ProcessReadDecision {
    const admission = evaluateRead(input, .{ .kind = .process_detail, .windows_session_id = null });
    if (admission.disposition != .check_process_permission)
        return .{ .access = admission, .status = .not_proven, .error_kind = .none, .native_error = 0 };
    const level = admission.caller_level;
    const scope = caller_scope orelse return processUnavailable(level, .caller_unverified);
    if (source.target.process_id <= 0 or source.target.creation_file_time_100ns <= 0)
        // Enumeration does not call this function and retains special PID and
        // unknown-creation rows. Never synthesize an actionable startKey=0.
        return processUnavailable(level, .target_changed);
    const proof = evidence orelse return processUnavailable(level, .not_proven);
    if (proof.caller_scope != scope or proof.caller_process_id != input.caller.?.process_id or
        proof.caller_process_created_utc_ticks != input.caller.?.process_created_utc_ticks)
        return processUnavailable(level, .caller_changed);
    if (proof.status != .granted) return .{
        .access = reject(.source_unavailable, level),
        .status = proof.status,
        .error_kind = proof.error_kind,
        .native_error = proof.native_error,
    };
    if (proof.capability == null or proof.error_kind != .none or proof.native_error != 0)
        return processUnavailable(level, .invalid_result);
    if (source.target.process_id != proof.fact.target.process_id or
        source.target.creation_file_time_100ns != proof.fact.target.creation_file_time_100ns)
        return processUnavailable(level, .target_changed);
    if (source.source_identity == null or source.field_identity == null or
        source.source_identity != proof.fact.source_identity or source.field_identity != proof.fact.field_identity)
        return processUnavailable(level, .source_mismatch);
    if (source.dependency_identity != proof.fact.dependency_identity)
        return processUnavailable(level, .dependency_unavailable);
    if (use == .live_mapping and proof.extent != .held_object)
        return processUnavailable(level, .lifetime_unproven);
    return .{
        .access = .{ .disposition = .allowed, .reason = .none, .caller_level = level },
        .status = .granted,
        .error_kind = .none,
        .native_error = 0,
    };
}

// Operation/length validation only. The unique binding supplies normal_action
// for the existing GPU operations; this function does not grant access or parse
// a wire envelope. Payload limits remain explicit protocol-owner inputs.
pub const GpuOperation = enum(u32) { invalid = 0, resolve = 1, report = 2 };

pub fn gpuOperation(operation: []const u16, payload_length: usize, maximum_payload: usize) GpuOperation {
    if (payload_length > maximum_payload) return .invalid;
    if (std.mem.eql(u16, operation, std.unicode.utf8ToUtf16LeStringLiteral("gpu.resolve"))) return .resolve;
    if (std.mem.eql(u16, operation, std.unicode.utf8ToUtf16LeStringLiteral("gpu.report"))) return .report;
    return .invalid;
}
