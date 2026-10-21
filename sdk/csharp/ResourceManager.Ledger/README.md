# ResourceManager.Ledger

This is the independent ledger managed runtime, delivered in local SDK `0.1.0-dev.2`. It pairs an explicitly loaded Native ledger library with generated `LedgerBuildIdentity` metadata and exposes typed owner, lease, region, workspace, and subscription lifetimes. It has no Resource Manager product dependency or external package dependency.

Both UID widths passed Native compilation and installed-package consumer runs, fourteen cases each, on Windows x64/.NET 10. Opening each library verified all seventeen exports and the 616-byte descriptor plus every public record layout. The domain acceptance checks also passed. See [the consumption guide](../../../docs/ledger-sdk-consumption.md) and the formal release manifest; packing is enabled explicitly by the SDK packaging command. Consumer migration and ABI10 retirement remain open.

## Use

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
