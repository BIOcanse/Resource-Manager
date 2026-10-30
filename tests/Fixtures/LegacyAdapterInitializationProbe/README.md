# Legacy Adapter contract initialization probe

This finite Windows x64 executable initializes and disposes the current production
managed `NativeResourceSchedulerSession`, `NativeAdapterResourceLedgerSession`
and `NativeLocalResourceManagerSession`.
Their real constructors perform the native version, structure size and layout
fingerprint checks: Scheduler12, Private3 and Local10 at present.

All capacities are one and every session owns private memory. The probe does not
register a capability/table, start a worker, submit a scheduler target/plan/action,
or call an external PID. It does not use hardware, SCM, installation, IFEO, the
Core host, or the independently migrated Ledger dev.2 owner. Local10 checks the
third export module only; it does not select a different product ledger.

The executable has a 20-second watchdog and must also be run inside the validation
batch's bounded ExecutionJob. Build and execution are the responsibility of that
batch. This fixture adds no process launcher or unbounded fallback.

## Explicit candidate and current managed projects

`AdapterNativeOutput` and `SkipAdapterNativeBuild=true` are mandatory build
properties. The fixture references the real
`sdk/csharp/ResourceManager.Adapter.SharedMemory` project, with its transitive real
`ResourceManager.Adapter.Abstractions` project. The native DLL is copied by that
project's existing output item. No shim, package replacement, search path change,
or manually reflected ABI result is used. The SDK's original resolver loads
`ResourceManager.Adapter.Native.dll` only from the isolated probe output directory.

Use new paths under the caller's already registered F batch. The following are
child-stage commands, not authorization to bypass its ExecutionJob/resource/input
gates. Use the batch's existing restore configuration and package cache as well.

```powershell
$project = 'tests/Fixtures/LegacyAdapterInitializationProbe/LegacyAdapterInitializationProbe.csproj'
# $adapterNativeOutput is the exact F-rebuilt DLL. $expectedSha256 comes from
# the successful stable-input native build/output manifest, not a guessed baseline.
# $probeArtifacts and $probeOutput are isolated paths within this same F batch.
& dotnet build $project -c Release --output $probeOutput `
    ('-p:ArtifactsPath=' + $probeArtifacts) `
    ('-p:AdapterNativeOutput=' + $adapterNativeOutput) `
    -p:SkipAdapterNativeBuild=true -p:BuildInParallel=false `
    -maxcpucount:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Legacy Adapter probe build failed.' }
& dotnet (Join-Path $probeOutput 'LegacyAdapterInitializationProbe.dll') `
    $adapterNativeOutput $expectedSha256
if ($LASTEXITCODE -ne 0) { throw 'Legacy Adapter initialization failed.' }
```

The runtime arguments are the absolute original `AdapterNativeOutput` path and
its expected SHA256. Both the original file and canonical probe-output copy must
match before any session is created and after all sessions are disposed. Read-only
file handles remain open through the checks and session lifetimes. On success,
stdout contains one JSON object with the native input/output paths and hash, the
actual managed assembly paths/hashes, and three initialized/disposed module records.
The reported protocol versions are explicitly managed expectations, verified by
the constructors against native contracts. Any mismatch or initialization/disposal
exception exits nonzero with the exception on stderr; a watchdog exit is 124.

Capture this fixture's source/project hashes, both managed ProjectReference source
inputs and build outputs, the exact legacy native rebuild input/output manifest,
command/exit/stdout/stderr, and the Job's normal drain/empty state in the F receipt.
The probe establishes ABI/layout plus owned initialization/disposal for those
captured inputs. It does not certify resource operations, scheduling effects or a
full product publish by itself.
