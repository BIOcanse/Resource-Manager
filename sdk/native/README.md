# Resource Manager Ledger Native

**Local development release:** `0.1.0-dev.1` is published in `artifacts/sdk`. ABI v1 and UID128/256 passed the installed-package consumer gate (fourteen cases per width), all seventeen exports and public record layouts, and the remaining domain checks. The supported delivery target is Windows x64. The header is `include/ledger_native.h`; see [the consumption guide](../../docs/ledger-sdk-consumption.md) and the formal release manifest for package status. Consumer migration and ABI10 retirement remain separate work; this does not freeze the production pin.

## Build and identify the library

The consumer supplies its canonical static table and fixed-resource declarations plus the selected FreedomPoint values. The build tool generates the corresponding Zig and C# shape files and one Native library for that shape and UID width. The generated Zig root instantiates `ledger_sdk.native.Binding` once and references `exportAll()`; tables and fixed declarations are build-time structure, with no runtime table registration.

Before creating an instance, call `rm_ledger_query_descriptor`. Compare the ABI version, UID width, C2 candidate, declaration counts, structure and policy fingerprints, state/workspace sizes and alignments, and every public record layout against the generated consumer metadata. Reject a mismatch before calling any other operation.

## Instance and scratch memory

The caller allocates a zeroed state block of `instance_size` bytes aligned to `instance_alignment`, then calls `rm_ledger_create`. Keep the block alive until successful destroy. Allocate three separate aligned caller-owned scratch blocks using the descriptor: operation, tick, and destroy workspace. An operation workspace is required for push, partition push, close, and force release; tick and destroy use their own blocks.

Keep the instance, scratch, and each input and output range used by a call disjoint. Reuse scratch only after the call returns, including synchronous callback delivery. The library validates known pointer-range overlap and alignment; it cannot verify that arbitrary pointers are mapped or readable.

## Resource and lease flow

`rm_ledger_push` returns an in-memory counted lease for a resident resource, or a newly allocated loading lease and zeroed region. Fill a new region, then end that lease with `RM_LEDGER_END_COMPLETE` to publish it or `RM_LEDGER_END_DISCARD` to discard the load. `rm_ledger_use` and numeric `rm_ledger_use_fixed` return counted leases only for resident resources. End every lease exactly once, including leases returned by a cache hit.

`rm_ledger_update_lease` verifies the supplied lease and does not acquire another count. When an allocation-size update succeeds for a resident resource, its output repeats the lease credential and returns the refreshed region pointer. If a page move is required while other leases exist, the owner returns `RM_LEDGER_IN_USE`; a same-page growth can preserve existing pointers. After a successful move, use the returned pointer and stop using every old view. Attribute-only updates return no borrow. `rm_ledger_update_uid` updates by opaque UID and does not return a region.

`rm_ledger_close` applies normal safety rules. Before `rm_ledger_force_release` or forced destroy, the caller must stop and join every access to the affected regions, including asynchronous operations. Force release invalidates the current allocation and its outstanding leases. While the instance remains alive, a stale lease can be ended later without affecting a replacement allocation. `rm_ledger_tick` applies the compiled per-table policy and reports released and scanned counts.

## Notifications and shutdown

Notifications run synchronously on the thread completing the operation that committed them, after owner locks are released. Do not call any ledger operation from a callback, including unsubscribe or destroy. A managed callback trampoline must catch exceptions before returning through the C ABI.

`push`, `push_partition`, `tick`, `close`, `force_release`, and normal or forced destroy can deliver notifications before returning. Do not call them while holding a lock that a callback needs. Likewise, do not hold such a lock while unsubscribing, because unsubscribe waits for entered callbacks. Loading discard and update movement do not send release notifications.

Keep the callback and context alive until `rm_ledger_unsubscribe` returns successfully, or through successful destroy if still subscribed. Unsubscribe prevents future entries and waits for callbacks already entered. Before destroy, stop new calls and drain in-flight API calls. Active subscriptions remain rooted while destroy commits and delivers notifications. If normal destroy fails, the instance and subscriptions remain active. After successful destroy, do not call the instance again, including to end a lease.
