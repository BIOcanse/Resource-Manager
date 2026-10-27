# Desktop freedom points

`desktop.points.json` declares the independent desktop tree's nine build-time
choices and their defaults. Fixed state-item limits remain structural facts in
their owning modules. This tool uses the published FreedomPoint dev.45
`TypedProjection.Emit` Zig backend; it does not implement a second emitter.

Run `scripts/build/Generate-DesktopPoints.ps1 -WorkRoot <running registered workspace>`
before building the desktop host. The generator builds through JobQueue with
BelowNormal priority, disabled build servers, and owned artifacts/package caches.
The only declared profile is `default`, whose difference list is empty.

The configuration-independent declaration output is committed at
`src/UI/Desktop/src/generated/points.structure.zig`. Selected values and the SDK
bindings/manifest are generated under `<WorkRoot>/desktop-points`.

The desktop build must provide `desktop_points_values` from `points.values.zig`
and bind its `desktop_points_structure` import to the committed structure file.
Production code reads `@import("desktop_points_values").Bound` directly; there
is no runtime SDK, configuration lookup, fallback value, or hand-written copy of
the defaults. Build wiring and production readers belong to the desktop host.

Generation receipts establish generation only. Full development-time source
tracking, reverse lookup, and debug-mode checking require the host's explicit
production source inventory and are not claimed by this generator.
