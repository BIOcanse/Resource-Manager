# ResourceManager.Ledger

This is the independent ledger managed runtime; the formal package line is documented in the Resource Manager development notes (`ledger-sdk-consumption.md`). It pairs an explicitly loaded Native ledger library with generated `LedgerBuildIdentity` metadata and exposes typed owner, lease, region, workspace, and subscription lifetimes. It has no Resource Manager product dependency or external package dependency.

Historical dev.2 qualification passed both UID widths, fourteen cases each, on Windows x64/.NET 10. Opening each library verified all seventeen exports and the 616-byte descriptor plus every public record layout. The domain acceptance checks also passed. See the formal release manifest; packing is enabled explicitly by the SDK packaging command. Consumer migration and ABI10 retirement remain open.

## Use

### Source candidate: scoped leases and transparent observation (2026-10-29, round 2)

This source candidate does not update immutable published packages. Authority:
the ledger authority (`ledger-authority.md`), §§3,7,10 and appendix G.

Keep leases local to the executing code scope. Use using/await using/finally/defer
through completion, cancellation and errors. Construction is not use. A business
block's long hold is local to its run method until stop. With a valid lease,
access the region directly; do not reacquire for each access or add markers.
Every epoch a lease covers is active. Final return stops that interval.

The same Use/EndUse syntax applies to short calls. Optional internal window
reuse preserves real logical pins across arbitrary ticks/awaits; it uses bounded
slot hints with full identity checks, never a grace-time assumption. Enabled
Native builds currently require x86_64+cx16 (-mcpu=baseline+cx16 with Zig 0.16).
The disabled path retains the prior target requirements. The measured default is off: single-reader UID paths improve, but contended fixed
direct paths regress and each slot costs 104 extra bytes. See the complete
matrix and workload limits in the main implementation record before enabling.

Await using waits for real memory pins. A failed return retains the local cleanup
obligation. RML001 detects fields, compiler-backed properties (including field-keyword and static interface auto-properties), positional record
storage, primary-constructor captures, retained closures and erased collections.
Storage writes include =, compound assignment (delegate +=), ??=, deconstruction,
event subscription, collection expressions, Interlocked.Exchange/CompareExchange,
and Add/AddRange/Enqueue/Push/TryAdd/Insert/GetOrAdd/AddOrUpdate on a retained
collection.
Interface/abstract signatures and async method locals remain allowed. An owning
IDisposable/IAsyncDisposable adapter or its disposing interface contract may assert [LedgerLeaseScope]; private
lifecycle credentials are allowed, but the adapter itself then has scoped-lease
storage restrictions. RML002 rejects an assertion without a disposal contract.
This is not a proof of arbitrary disposal bodies, reflection or cross-method
heap flow. Non-owning views must not claim an owning assertion.

Minimum compiler: the packaged analyzer targets netstandard2.0 and references
Microsoft.CodeAnalysis.CSharp 5.0.0, so RML001/RML002 run in C# compiler 5.0.0
or newer: the .NET 10 SDK, Visual Studio 2026 18.0 (including its .NET Framework
IDE host), or Microsoft.Net.Compilers.Toolset 5.0.0+. Every .NET 10 SDK, which
this net10.0 runtime needs, ships such a compiler. An older compiler only warns
CS9057 and skips both rules; add CS9057 to WarningsAsErrors to make that a build
failure.

```csharp
if (owner.Use(uid).Lease is { } lease)
{
    await using (lease)
    {
        Consume(lease.Span);
        await FinishOperationAsync();
    }
}
```

Development analysis defaults off. Select ledger.development_lease_analysis=1;
record capacity defaults to 256 (1..65536), and automatic write-observation
scratch defaults to 16384 pages (1..1048576). No caller markers exist. Windows
MEM_WRITE_WATCH reports dirty-page samples at lifecycle/Tick boundaries. New
observable pages are materialized by the SDK before establishing the baseline,
so cold demand-zero touches and SDK resize copies do not masquerade as user
writes. Oversized regions are explicitly unavailable, not materialized anyway.
Pure reads are unobservable; zero writes never means zero use. Overlapping
leases share allocation-level attribution. Page samples are not instruction
counts, and a page may reappear after reset. Snapshot flags expose these limits.

LeaseAnalysisAvailable and GetLeaseAnalysis() provide a bounded structured
snapshot without borrowing or updating activity. Native analysis-off builds omit
storage, analysis locks/clocks, write observation and the optional query export.
Generated build props are selected-build metadata; there are no conditional
per-access API calls to insert or erase. The SDK contains no HTTP integration;
RM's owner wires the snapshot into its existing /api/debug/native-memory route.
Take snapshots before destruction, and do not register diagnostic storage as
payload. PAGE_GUARD is not used because it can change valid system-service
pointer accesses into failures.
`LedgerOwner<TUid>.Open` takes an explicit Native library path and generated build identity. It validates the Native descriptor and record layouts before creating the caller-owned Native state block.

```csharp
using ResourceManager.Ledger;
using ResourceManager.Ledger.Identity;

using LedgerOwner<LedgerUid128> owner = LedgerOwner<LedgerUid128>.Open(nativePath, generatedIdentity);

LedgerUseResult<LedgerUid128> result = owner.Use(uid);
if (result.Lease is { } lease)
{
    using (lease)
    {
        lease.Span[0] = 42;
    }
}
```

Newly allocated loading leases default to discard. Call `PublishOnDispose()` only after successful loading. `Memory<byte>` chunks pin the same Native lease until final unpin; use span/memory chunks for regions larger than `Int32.MaxValue`. Normal owner destruction keeps a blocked owner open, while `ForceDispose` is explicit and requires callers to stop all region access. Callbacks are synchronous, exceptions are contained at the unmanaged boundary, and callback handlers must not reenter the ledger.

`ForceRelease(uid)` invalidates every lease for that UID. Because the owner does not keep a lease registry, callers must stop access through all corresponding regions and release their memory pins before invoking it.

A successful moving `lease.Update(...)` refreshes that lease's region. Stop using spans or pointers obtained before the update; get fresh views from the lease. An outstanding memory pin prevents a move through the same managed lease.

`Push`, `PushPartition`, `Tick`, `Close`, `ForceRelease`, and normal or forced owner destruction can deliver synchronous notifications. Do not hold a lock required by a callback while calling them or disposing a subscription. Subscription disposal waits for callbacks that have already entered. Loading discard and update movement do not send release notifications.

Structural operations allocate descriptor-sized aligned scratch per call by default. Advanced callers can reuse a matching `LedgerWorkspace`; concurrent reuse fails immediately. Tick and destroy have separate workspace kinds.

The source-consumer gate covers loading/publication, counted leases, moving updates, notifications and unsubscribe overlap, workspace disposal, forced destruction, and real named-pipe asynchronous I/O with pinned Native memory. Package consumption is a separate gate.

## Exact scoped-end outcomes

`EndUseAsync` returns a confirmed `LedgerEndResult`. Completed, ForceReleased,
EndedCleanupFailed and Abandoned are terminal: the exact use has ended. Failed
unpublished-load cleanup retains zero-holder SDK pages/index/accounting; it does
not retain a caller lease or publish invalid content. Tick or Close(uid) retries
cleanup, charging each attempt to the table Tick action limit. AbandonAsync ends
only this use, without a fallible page/index action or force-releasing others.

Use `HasEnded` / `EndResult` to inspect the confirmed outcome. An adapter may call
`TryTakeEndResult(out result)` in its finally to settle its Active count once.
Repeated end/abandon/dispose never repeats a confirmed Native end. DisposeAsync
waits for Memory.Pin drain and throws LedgerEndException for terminal cleanup
failure, with HasEnded=true. A precommit failure instead has HasEnded=false and
keeps the original acquisition for same-scope retry/abandon. Unsupported memory
corruption or process faults must not be presented as ordinary end outcomes.

See the consumption guide and complete contract 6.10 for the local finally pattern;
no business owner or HTTP integration is implemented inside this package.