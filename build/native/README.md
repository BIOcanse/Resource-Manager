# Declaration bootstrap

Zig 0.17 builds this host tool without dotnet, MSBuild, NuGet or PowerShell:

```
zig build --build-file build/native/build.zig -j1 --cache-dir <running-ledger>/zig-cache --prefix <running-ledger>/native
zig build --build-file build/native/build.zig generate -j1 --cache-dir <running-ledger>/zig-cache --prefix <running-ledger>/native
```

Set `ZIG_GLOBAL_CACHE_DIR` to `<running-ledger>/zig-global` in the invoking
environment before dispatching. Zig 0.17's build runner accepts the local
`--cache-dir` option; the global cache is selected through the environment,
not a `--global-cache-dir` build argument.

RM workers dispatch compilation through JobQueue, with the same arguments and
the cache environment inherited by the queue host.
`check` installs compiled focused tests without running them; `test` runs those
tests. Generation always reads the authoritative registry/preferences file;
the checked-in state projection is not a bootstrap input. `generate` emits four
process tables, with and without diagnostic names, plus frontend notification
defaults. `-Ddeclaration-max-bytes=<bytes>` sets the build-tool input bound.

The installed `rm-declarations` accepts:

```
rm-declarations state-items <registry.json> <output.zig> <max-input-bytes> <backend|ui|desktop|window> <names|no-names>
rm-declarations frontend-defaults <preferences.points.json> <output.zig> <max-input-bytes>
```

It validates before atomically replacing output, preserves identical files,
and reports entry/field errors to stderr. Generated table bytes retain the
historical provenance comment to allow exact old/new comparisons.
Native callers keep a `Diagnostic` in the read scope and call
`Diagnostic.deinit` with the same allocator used by `read`. Failed-read field
text remains valid after parser cleanup until diagnostic cleanup or reuse.

`declarations/build_support.zig` provides host-tool and generation steps for
the existing product roots. Those roots have a separate integrating writer;
the B-generate report contains their patch. The bootstrap is not a second
product library. Native product cutover is pending that patch and acceptance.

`declarations/dependencies.json` records current callers, lifetimes, missing
managed-tool replacements and deletion ownership. The native host-type label
check does not reproduce Roslyn's full grammar. Existing managed generators
and validation remain until this and the other recorded contracts are closed.

The temporary old/native oracle is `tests/Native/Generation/Run-Equivalence.ps1`.
It runs only precompiled focused tests and the old/new tools; it does not build
or start any product process. X-tests/X-tools retire that oracle after the
independent native cases cover the accepted contract.
