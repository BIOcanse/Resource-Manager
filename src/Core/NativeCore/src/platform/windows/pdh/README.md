# PDH GPU platform interface

This slice provides frame projection and one demand-scoped shared-memory query.
It does not create a sampler, publication stream, clock or background thread.

`gpu_abi.zig` exports the private version-1 binding in
`ReadWriteCore/Native/Pdh/NativePdhGpuOwner.cs`:

- `rm_pdh_gpu_workspace` / `rm_pdh_gpu_project`: project borrowed engine,
  window, memory and process-identity columns into the caller's workspace.
  Input flags select scheduling (1), breakdown (2), device engines (4), dedicated
  memory (8), and validated breakdown attribution (16).
- `rm_pdh_gpu_shared_query_windows`: query Windows PDH once for a subscribed
  request. `QueryRequest.Process=1` selects the requested LUID span and returns
  process IDs. `Process=0` selects the raw `Adapter` span, owns LUID/display-index
  ambiguity decisions, and returns display indices in `SharedRow.Pid`.
  `SharedRow.Luid` always retains the original Windows adapter identity.
- `rm_pdh_gpu_shared_query`: the same query owner with injected OS capabilities
  for equivalence tests. It uses the same query and aggregation implementation.
- `rm_pdh_gpu_parse`, `rm_pdh_gpu_decode`, `rm_pdh_gpu_shared`: bounded borrowed
  array operations used by the query owner and fixed-input tests.

Query results retain the existing `Ok=0`, `NoData=4`, `PdhError=8` meanings.
`SharedResult.Flags & 1` denotes no adapter requested; otherwise a nonempty row
span is available, including valid zero bytes. Error status and failed-counter
count are distinct. The query is closed before result consumption; a close error
discards any otherwise-good rows. Every input/output pointer is synchronous and
borrowed. Neither `Output.Rows` nor `SharedResult.Rows` may escape consumption.

`PdhGpuProjectionOwner.Project(in Input, nuint maximum, Consume)` and
`QueryShared(in QueryHost, in QueryRequest, bool windows, ConsumeShared)` are the
compiled host adapter. They share one stable Ledger scratch identity, serialize
its use, and acquire/pin/release it locally per operation. Diagnostics distinguish
native bytes used (`PeakWorkspaceBytes`) from owned region size (`WorkspaceBytes`,
`PeakRegionBytes`). Input references must belong to the original source and stay
valid throughout the call; they must not point into this mutable scratch region.

## Joining step: PDH-source-joining

Maintenance must connect this interface inside the existing PDH source and its
subscription chain. Supply the original committed frame and validated process
identities as borrowed Ledger columns, retaining the producer's observation,
generation and LastGood rules. Preserve the purpose-specific payload rules for
scheduling, breakdown and device reads. Do not add a second PDH session/sampler
or copy committed rows into a second managed authority.

Resolve workspace/array/item/name limits once from the owning FreedomPoints or
settings and pass them explicitly; this platform module supplies no defaults.
Timestamp publication outside the core through the existing injected clock.
Consume native rows while the local lease is valid. For process shared memory,
the original source must still validate process creation identity after the read.
Wire the adapter's disposal to that source's lifetime.

The legacy C# production readers remain active until that joining step passes the
same product tests and end-to-end CPU/allocation/region comparison. Delete the
replaced C# in a separate commit after the cutover. The present fixed-input tests
are equivalence evidence for this interface, not evidence of a product cutover.

## Remaining managed bridge: PDH-native-host

`PdhGpuProjectionOwner.cs` (123 lines) supplies the current managed Ledger and
execution capabilities. `NativePdhGpuOwner.cs` (98 lines) is the managed ABI
binding. Both belong to the `PDH-native-host` handoff batch: replace their host
composition and Ledger capability plumbing in Zig, then delete the bindings.
They are temporary bridges, not exceptions to removing the .NET dependency.

These batch names identify the handoffs; they do not claim that maintenance has
already dispatched them. `PdhCounterMethods.cs` also serves the live CPU/IO
readers; any methods still used after GPU joining belong to `PDH-CPU-IO-platform`
and must not be deleted with the GPU readers.
