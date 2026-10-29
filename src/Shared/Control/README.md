# Shared Zig controller

`rm_control` owns block states and run numbers, node permits and drained summaries, condition values and marks, item publications, reference read functions, named runtime-reader registrations and fixed histories. The host owns run entries, cancellation signals, diagnostics and its mapping from reader registration IDs to reader functions. Host-valued objects remain with their owner; the controller holds only their presence and events.

A node computes marked conditions in a fixed bottom-up order. Changed values mark their readers. A changed top value or structural change makes the node align its direct children. A branch is stopped or restarted only after its permit has been revoked and its node has acknowledged that the whole branch is stopped. The root always acts and shutdown makes it want no children; other nodes act while their block is Running.

Boolean reads return a value or `not_published`, `owner_stopped`, or `read_failed`. Host items expose `Presence.present` or the same absence reasons. Owner reads never sample, allocate, block or acquire an owner lock. Conditions must choose their no-value policy. An undeclared read is an error; conditions reading host-valued items fail compilation. Compute failure retains the previous value and last error without propagating upward or itself causing an action. Structural revocation still drains a broken branch.

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

## Development tree export

The exporter reads declarations only; it never creates a controller, calls a
compute function, launches the product or inspects runtime state. Each process
provides `process_name`, its development `registry`, and `profiles` (a slice of
`rm_control.export_tree.Profile`, each with `name` and `tree`). Profiles may
contain subsets whose union covers the registry. Blocks merge by UID and must have the same
parent in every profile; a conflict reports the block's name. A release table
without block, item or condition names is rejected with a development-table
diagnostic. `tree.Block.launches_process` defaults to null and is export metadata
only; the controller ignores it.

JSON schema version 1 contains `process`, `registryVersion`, `profiles`,
`program` (UID), `conditions` and `blocks`. Every block has `uid`, `name`,
`parent` (UID or null), `children` (UIDs), `profiles`, and `declarations`.
Each declaration has `profile`, `inputs` (UID/name pairs), `decidingCondition`
(null for the process root), and `launchesProcess` (name or null). Keeping the
declarations per profile preserves differing inputs, conditions and launch
metadata without duplicating the block. Conditions contain `uid`, `name`,
`perBlock`, named `items` (including effective block keys), `conditions` (named
lower condition references) and `children` (named state reads). The top-level
condition table also describes lower conditions, including their own reads.

The allocation-free API is
`export_tree.write(comptime registry, process, profiles, *json.Writer, *?[]const u8)`.
The diagnostic borrows the offending name. Pass an empty writer buffer to
measure, then allocate exactly `writer.len` bytes and write again. Errors do not
write partial JSON. All walks are bounded by the declaration slices; there is no
recursive traversal.

In a process's `build.zig`, reuse the ReleaseSafe `control` module above and add:

```zig
const process_tree = b.createModule(.{
    .root_source_file = b.path("src/export_tree.zig"),
    .target = target,
    .optimize = .ReleaseSafe,
    .imports = &.{.{ .name = "rm_control", .module = control }},
});
const exporter = b.addExecutable(.{
    .name = "export-control-tree",
    .use_llvm = false, // build-only utility; reduces compile memory on x86_64
    .root_module = b.createModule(.{
        .root_source_file = b.path("../../Shared/Control/src/export_tree_main.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{
            .{ .name = "rm_control", .module = control },
            .{ .name = "process_tree", .module = process_tree },
        },
    }),
});
const run_exporter = b.addRunArtifact(exporter);
// Inherit stdout: zig build export-tree writes only the per-process JSON there.
b.step("export-tree", "Export development control declarations").dependOn(&run_exporter.step);
```

`src/export_tree.zig` is a small adapter over the process's existing tree:

```zig
const control = @import("rm_control");
const source = @import("tree.zig");
pub const process_name = "desktop";
pub const registry = source.registry; // generated development table, with names
pub const profiles: []const control.export_tree.Profile = &.{
    .{ .name = "normal", .tree = source.normal_tree },
    .{ .name = "minimal", .tree = source.minimal_tree },
};
```

The build executable imports this adapter under the fixed name `process_tree`.
It has no product entry points. This change does not wire steps into NativeCore
or Desktop: add them after 5c and desktop-resident land; window follows 5d.

From the repository root under pwsh 7:

```powershell
scripts/tools/Export-ControlTree.ps1 -TreeJson backend.json,desktop.json,window.json
scripts/tools/Export-ControlTree.ps1 -TreeJson desktop.json,window.json -From window:MainWindow
scripts/tools/Export-ControlTree.ps1 -TreeJson desktop.json,window.json -From desktop:42 -Profile normal -Format json
scripts/tools/Export-ControlTree.ps1 -TreeJson backend.json,desktop.json,window.json -CompareWith target.md
# Automatic discovery; builds go through foreground JobQueue, caches to ledger:
scripts/tools/Export-ControlTree.ps1 -WorkRoot <workset-root>/ledger-control-tree-export
```

`-TreeJson` accepts 1–64 per-process files. Omit it to run the `export-tree`
step of backend (NativeCore) and desktop (Desktop), listed in one table at the
top of the script. A missing build/step prints `no Zig declaration yet` to stderr
and is not an error. Other build/export failures fail normally. Automatic builds
use foreground JobQueue with `-j1`, a 4 GiB ceiling and a registered validation
batch; `-WorkRoot` defaults to `.local`.
Set it to the ledger for big caches. Successful and failed batches are completed
after use; existing batches and processes are not closed or stopped.

`-From` defaults to the software `Program` root and accepts `process:Name`, a
globally unique name, or `process:UID`. The process program blocks are retained
in per-process metadata but replaced in the stitched structure: unlaunched
processes contribute their top-level blocks to Program; launched ones contribute
them to their launching block. Duplicate launchers, unknown processes/parents
and cycles fail with names. `-Profile` selects that profile's declarations;
removing a parent or a launch removes the entire branch. Unknown profiles fail.

`-Format text` uses exactly three columns per indent, `├─ ` / `└─ ` / `│  `,
`process:Name`, then two spaces and the deciding condition name. If unfiltered
profiles use different conditions, the comment joins their names with ` / `;
JSON retains each declaration. `-Format json` emits a flat subtree, named parent
and child references and per-process condition tables. The selected root has
null parent. A process program UID/name also selects Program, which it represents.
The `processes` metadata retains the original `programBlock` declaration and
the complete condition table. No recursive serialization is needed for the tree.

`-CompareWith` reads the first `control-tree` fenced block in Markdown (or a plain
text tree). It reports target-only blocks, code-only blocks and different
parents, ignoring comments and sibling order. Differences exit successfully;
malformed input still fails. Use `-Format json` for `missingInCode`, `extraInCode`
and `parentDiffers` arrays. Comparison applies to the selected subtree/profile.
Input files are capped at 64 MiB, trees at 100,000 blocks and depth at 4096.

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

const read_function = try controller.claimReadFunction(.{ .item = ActiveSubscriptionsUid }, .{ .context = tracker, .read = readActiveSubscriptions });
try controller.notifyChanged(read_function, reason, null);
try controller.unbind(read_function, reason, null);
try controller.bind(read_function, reason, null);
try controller.reportRunning(BlockUid, run_number);
try controller.reportReturned(BlockUid, run_number);
try controller.shutdown(reason, token);
// After all run entries and host callers have finished:
try controller.destroy();
```

Attach the returned handle before `activate()` dispatches initial rounds. Tokens before activation return `NotActive`. Each start receives a new run number; reports for old runs are ignored, and Running cannot reverse Stopping. Claims are opaque handles tied to their authority, issued once per item key.

Every publishing operation, change notification, bind, unbind and shutdown takes an explicit `Reason {kind,owner,cause,plan_version}`. It is forwarded unchanged to runtime readers and retained when needed for decisions. Empty owner/cause or a reason exceeding the configured bound fails before mutation. Boolean access to host items, and presence access to Boolean items, return `TypeMismatch`. Host item values never cross the boundary.

A reference bool item may be claimed with `claimReadFunction(key, ReadFunction)` instead of a publication cell. The sole-owner claim excludes both other function registrations and cell claims. The callback is `read(context, *ReadView) callconv(.c) void`; `ReadView` contains u32 `kind` and u32 `value`. Kinds 0/1/2 mean not published, owner stopped and read failed; kind 3 contains a bool encoded as 0/1. Invalid outputs read as failure. The host translates callback exceptions to read failure and logs them. A read function reads the owner's current published state synchronously, excluding expired sources by the current time; it must not sample, allocate, block or take an owner lock. Each node round captures its binding and reads it once outside the controller lock, then all of that round's conditions reuse the result. `notifyChanged` carries only a wake-up and reason. Binding starts enabled; unbinding returns owner stopped and rebinding reads the latest owner state. The function and context remain valid through controller destruction, including after unbinding, because an earlier captured round may still be finishing. Direct reads and snapshots also invoke the function outside the controller lock.

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

Config explicitly supplies worker count, history capacity, reader entry/holder-text capacities (`reader_capacity`, `reader_bytes`), completion capacity, worker arena bytes, shutdown item UID and the exact non-root block UIDs the host can run. `reason_text_capacity` has the visible default 256 combined owner/cause bytes. Invalid capacity, exhausted bounded storage and overlong reasons return errors without growing or partial mutation.

`snapshot(buffer)` returns the complete needed byte count, even for an empty or short buffer; it writes no trailing NUL. Retry with enough space if concurrent changes enlarge the result. The JSON includes registry version, blocks and inputs, transitions, items, readers, node conditions, bounded `decisions` history, and `currentDecisions` containing the latest alignment for each node that has aligned. Current decisions remain available when `history_capacity` is zero. Host-item rows expose `hasValue` and `noValueReason`, with no object payload. Registry names are omitted when absent. Reader holders are caller-supplied ownership labels and remain available independently of registry names. The reader pool exposes current, peak and capacity.

Tables and histories are allocated once with fixed sizes. Decision reasons and reader holders use bounded copied-text storage. Each worker resets its fixed arena every round. Snapshot output is caller-owned. Destroy requires shutdown and all runs returned; it waits for current rounds/callbacks, joins workers and releases creation allocations. The host finishes its callers and queued reader work, retains all callback contexts until destroy returns, and makes no calls afterward.

## Tests

Run from this directory under pwsh 7 through the foreground JobQueue:

```powershell
$env:ZIG_GLOBAL_CACHE_DIR = '<workset-root>\<registered ledger>\zig-global-cache'
& '<tools>\JobQueue\JobQueue.ps1' -Action Run -Label 'zig-control' -FilePath zig -ArgumentList @('build','test','-j1','--summary','all') -WorkingDirectory '<checkout>\src\Shared\Control' -MemoryLimitGiB 4 -TimeoutMinutes 30
```

The test step runs controller scenarios, conditions, item and reader storage, host presence, runtime-reader delivery, JSON tests and diagnostic-matching compile fixtures. Tests use `std.testing.allocator`. Product hosts and C ABI exports belong to 5c/5d.

The export suite and build-only entry fixture have their own build file under
`tests/Fixtures/ControlTreeExport` (relative to the repository root). Run it
through the same foreground JobQueue with `@('build','test','export-tree','-j1',
'--summary','all','--cache-dir', '<registered ledger scratch>/fixture-cache')`
and `-MemoryLimitGiB 4`. Set `ZIG_GLOBAL_CACHE_DIR` to that batch's global cache.
The fixture uses the Zig native backend (`use_llvm = false`) to fit the 4 GiB
ceiling; it still compiles ReleaseSafe and exercises the actual exporter and
stdout entry. It does not change the product build's compiler selection.

The plain pwsh suite is `tests/ControlTreeExport.Tests.ps1 -WorkRoot <ledger>`.
`-GeneratedJson <fixture-stdout.json>` additionally checks the actual Zig output
in the stitching script. `-TestDiscovery` runs the default process table, so run
that mode through JobQueue with a 4 GiB ceiling too. Use the ledger as that outer
job's working directory to avoid claiming the product directories needed by the
inner export jobs. Without `-TestDiscovery` the suite only reads/writes small
fixtures and does not build Zig. It registers and completes its own workset.
