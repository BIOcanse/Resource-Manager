# Desktop freedom points

`desktop.points.json` declares the independent desktop tree's eleven build-time
choices and their defaults. Fixed state-item limits remain structural facts in
their owning modules. This tool uses the published FreedomPoint dev.48
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

The window wrapper owns `window_stop_deadline_ms` (milliseconds) and
`window_stop_timeout_policy`, a generated native enum with the formal choices
`terminate` (force termination, default) and `wait` (continue waiting).
The deadline bounds cooperative stop, including pipe worker joins. A separate
program-stop token cancels that wait regardless of the selected timeout policy;
closing the non-inherited Job kills the child, and the wrapper still joins it.
Defaults live only in the declaration; the host passes selected SDK values to
the wrapper. The measured basis and remaining real NativeUi acceptance are in
`.local/reports/final-report-2.md`.
