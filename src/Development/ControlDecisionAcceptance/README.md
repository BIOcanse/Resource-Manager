# Control decision acceptance probe

Development-only executable. It references the production backend but owns its host entry point, private IPv4 listener, private data root and unique trusted-access pipe. It never invokes `src/Core/Program.cs`, Native UI, service installation or frontend hosting.

The full production composition and compiled native control tree remain active. All listed effect adapters in `EffectBoundaries.Overrides` must be reviewed before launch. Real Smart/report/HMP owners create their subscriptions. Actual Windows CPU topology is used for the release/resubscribe measurement; no controller facts or sampling publications are fabricated. Disabled effect paths mean this cannot certify a fully unmodified deployed backend.

The main task's accepted build recipe supplies the paired Ledger output, NativeCore with names, C# generated inputs and native dependency locations. Build with `ResourceManagerStateNames=true`, using the registered `real-copy/build` output. Do not use this project to rerun unreviewed product build targets or frontend generation. Run long build/launch jobs through the established foreground JobQueue PowerShell 7 route.

`Prepare-ArtifactCopy.ps1` takes the accepted build output and its accepted source-input manifest and creates a new bounded copy beneath the registered F ledger real-copy task. It excludes EXEs, Dependencies, GpuPlacementShim and wwwroot; copies only ordinary files; verifies every source/copy length and SHA-256; and binds the source manifest hash into the artifact manifest. This script does not prove the source manifest belongs to a build: the caller must provide an accepted build manifest.

After source review, invoke the copied DLL using dotnet:

```text
dotnet <real-copy>/copy/ResourceManager.ControlDecisionAcceptance.dll --registered-root <real-copy> --runtime-root <real-copy>/data/new-run --artifact-manifest <real-copy>/copy/artifact-manifest.json --source-manifest <real-copy>/copy/source-input-manifest.json
```

The executable rejects source-tree execution, old runtime roots and output mutation. It maps one read-only development HTTP route and produces controller, HMP, settings and real topology evidence beneath the private run root. The process is bounded by a two-minute work cancellation and a twenty-second host shutdown deadline; its supervising JobQueue remains responsible for process ownership.

The subscription owner diagnostic is integrated: active publication requires exactly one source and nonzero real output scratch; after release, acceptance requires the role stopped and all role-owned lease/scratch/output pointers cleared. This proves the role borrow returned; the shared sampling owner remains available for Smart/report/HMP roles. Code completion is not runtime evidence. See `.local/no-demand/realcopy-acceptance/report.md` for current state.

