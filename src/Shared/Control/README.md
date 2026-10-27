# Shared Zig controller

`rm_control` owns block states and run numbers, node permits and drained summaries, condition values and marks, item publications, demands, named runtime-reader registrations and fixed histories. The host owns run entries, cancellation signals, diagnostics and its mapping from reader registration IDs to reader functions. Host-valued objects remain with their owner; the controller holds only their presence and events.

A node computes marked conditions in a fixed bottom-up order. Changed values mark their readers. A changed top value or structural change makes the node align its direct children. A branch is stopped or restarted only after its permit has been revoked and its node has acknowledged that the whole branch is stopped. The root always acts and shutdown makes it want no children; other nodes act while their block is Running.

Boolean reads return a value or `not_published`, `owner_stopped`, or `read_failed`. Host items expose `Presence.present` or the same absence reasons. Reads never sample, allocate or acquire a lock. Conditions must choose their no-value policy. An undeclared read is an error; conditions reading host-valued items fail compilation. Compute failure retains the previous value and last error without propagating upward or itself causing an action. Structural revocation still drains a broken branch.

## Import and declarations

From `src/UI/Desktop/build.zig` or `src/Core/NativeCore/build.zig`:

```zig
const control = b.createModule(.{
    .root_source_file = b.path("../../Shared/Control/src/root.zig"),
    .target = target,
    .optimize = .ReleaseSafe,
});
host_module.addImport("rm_control", control);
```

Use `native_core_module` for `host_module` in NativeCore; the frontend supplies its own executable module. A compile-time guard rejects other controller optimization modes. The standalone build also exports `rm_control` in ReleaseSafe. The tests compile a ReleaseFast consumer importing this module ReleaseSafe.

`declarations.zig` is the verbatim supplied contract. A process supplies its generated `declarations.Registry` and one `tree.Tree`:

- `program` is the program block UID.
- `blocks` includes every block once; the root has null parent. Other blocks have one parent. `inputs` describes data flow without creating control demand.
- `uses` lists each per-block condition instance as `{node, condition, child}`.
- `computes` supplies one `{condition, function}` per registered condition. A function takes `*Input` and returns `anyerror!bool`.

`Input.read(key)`, `condition(uid)` and `childState(uid)` enforce the declared reads. A round captures its item publications once, so repeated reads use the same value. `input.block` identifies a per-block instance. An `ItemKey` is `{item, block}`, with null block for a single value. Within a per-block condition, null block on a per-block item binds to the instance block.

`Controller(registry, tree)` checks the complete tree at compile time, using bounded loops rather than recursion. Inputs may cross branches and form data cycles.

## Host calls

```zig
const Engine = rm_control.Controller(registry, tree);
const controller = try Engine.create(allocator, config, callbacks);
host.controller = controller;
controller.activate();

const writer = try controller.claimWrite(.{ .item = OwnItemUid });
try controller.publish(writer, .{ .value = true }, reason, token);
const cell = try controller.claimCell(.{ .item = ReferenceItemUid });
try controller.publish(cell, .{ .value = true }, reason, null);
try controller.unbind(cell, reason, null);
try controller.bind(cell, reason, null);

const host_cell = try controller.claimCell(.{ .item = HostObjectItemUid });
try controller.publishPresence(host_cell, .present, reason, null);

const reader_id = try controller.registerReader(.{ .item = HostObjectItemUid }, "settings-stream");
// The host maps reader_id to its reader function.
try controller.unregisterReader(reader_id);
try controller.unregisterReader(reader_id); // harmless

try controller.setDemand("subscription-key", BlockUid, reason, null);
try controller.clearDemand("subscription-key", reason, null);
try controller.reportRunning(BlockUid, run_number);
try controller.reportReturned(BlockUid, run_number);
try controller.shutdown(reason, token);
// After all run entries and host callers have finished:
try controller.destroy();
```

Attach the returned handle before `activate()` dispatches initial rounds. Tokens before activation return `NotActive`. Each start receives a new run number; reports for old runs are ignored, and Running cannot reverse Stopping. Claims are opaque handles tied to their authority, issued once per item key.

Every publishing operation, bind, unbind and shutdown takes an explicit `Reason {kind,owner,cause,plan_version}`. It is forwarded unchanged to runtime readers and retained when needed for decisions. Empty owner/cause or a reason exceeding the configured bound fails before mutation. Boolean access to host items, and presence access to Boolean items, return `TypeMismatch`. Host item values never cross the boundary.

The C-compatible `Host` has a persistent context and these `.c` callbacks:

- `start(context, block, run)` and `stop(context, block, run)`.
- `complete(context, token)`.
- `log(context, level, pointer, length)`; levels 2/4 mean Information/Error.
- `item_changed(context, registration_id, ItemKeyView, *const ReasonView)`.

`item_changed` is authority §3c's sixth callback kind. Polling remains deferred to step 6, so no poll callback was added here. `ItemKeyView` contains two u32 fields; block zero denotes a single-valued key. `ReasonView` contains kind, owner/cause pointer-length pairs and plan version. Its buffers are borrowed for the callback only; `toReason()` exposes borrowed Zig slices.

No controller lock crosses a host callback. Callbacks may reenter, but must queue work or set a flag and return, rather than waiting for controller rounds or destroying the controller from that callback.

## Runtime readers and completion

`registerReader(key, holder)` copies the holder into bounded storage and returns a controller-local u64 ID. The same holder on the same full key fails with `DuplicateHolder`; it may be used on another key. Registration IDs are never reused within the controller, even when storage slots are reused. `unregisterReader(id)` is idempotent, so an old ID cannot remove its replacement.

Each visible event first marks declared condition readers. On the publishing thread, without a controller lock, it then invokes `item_changed` once for each matching runtime registration. Registrations added during delivery start with subsequent events. Registrations removed before the event are not called. The host owns the thread-safe ID-to-reader map; there is no reader context or reader function pointer stored in the controller. The host can discard an already-dispatched ID if its mapping has been removed.

A completion token covers only the declared conditions' required rounds. It may return while a runtime callback is still executing; the controller does not await runtime readers' work. The host queues that work and waits for it itself when needed. Step 5c's C# wrapper will combine those reader tasks with the native round task. `waitIdle()` waits for controller rounds and callback invocations, not asynchronous work queued by the host. There is no per-reader-context drain requirement.

## Configuration, snapshot and memory

Config explicitly supplies worker count, history capacity, demand entry/text capacities, reader entry/holder-text capacities (`reader_capacity`, `reader_bytes`), completion capacity, worker arena bytes, optional demand item UID, shutdown item UID and the exact non-root block UIDs the host can run. `reason_text_capacity` has the visible default 256 combined owner/cause bytes. Invalid capacity, exhausted bounded storage and overlong reasons return errors without growing or partial mutation.

`snapshot(buffer)` returns the complete needed byte count, even for an empty or short buffer; it writes no trailing NUL. Retry with enough space if concurrent changes enlarge the result. The JSON includes registry version, blocks and inputs, transitions, demands, items, readers, node conditions and decisions. Host-item rows expose `hasValue` and `noValueReason`, with no object payload. Registry names are omitted when absent. Reader holders are caller-supplied ownership labels and remain available independently of registry names. Demand and reader pools each expose current, peak and capacity.

Tables and histories are allocated once with fixed sizes. Demand keys/reasons and reader holders use bounded copied-text pools. Each worker resets its fixed arena every round. Snapshot output is caller-owned. Destroy requires shutdown and all runs returned; it waits for current rounds/callbacks, joins workers and releases creation allocations. The host finishes its callers and queued reader work, retains the Host context until destroy returns, and makes no calls afterward.

## Tests

Run from this directory under pwsh 7 through the foreground JobQueue:

```powershell
$env:ZIG_GLOBAL_CACHE_DIR = 'F:\code\Resource-Manager-Development\ledger-state-items-5b\zig-global-cache'
& 'D:\Code\CommonAssets\Tools\JobQueue\JobQueue.ps1' -Action Run -Label 'claude: zig-control' -FilePath zig -ArgumentList @('build','test','--summary','all') -WorkingDirectory 'F:\code\Resource-Manager-Worktrees\zig-control\src\Shared\Control' -TimeoutMinutes 30
```

The test step runs controller scenarios, conditions, item and reader storage, host presence, runtime-reader delivery, JSON tests and diagnostic-matching compile fixtures. Tests use `std.testing.allocator`. Product hosts and C ABI exports belong to 5c/5d.
