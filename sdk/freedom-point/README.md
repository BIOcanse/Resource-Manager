# FreedomPoint candidate packages

This host consumes the independent SDK from `D:/Code/FreedomPoint`. The local
NuGet feed contains the exact candidate version selected for this integration,
so a checkout restores without an absolute project reference to that workspace.

`FreedomPoint.Core` is the product dependency. `FreedomPoint.SourceTracking` is
only used by the development checker; its compiler libraries are not copied into
the Resource Manager product through this tool.

Run `scripts/validation/Test-FreedomPointSources.ps1` to build the actual managed
consumer, obtain its compiler context, and export both views and real source
bindings. A missing production consumer or stale generated position returns an
error. Custom grouping is saved separately in `backend.groups.json`.

The original compiler address strings remain host aliases until their existing
consumers are replaced. SDK identity, bindings and group references use PointPath.
This is an internal candidate; complete SDK acceptance and public packaging are
tracked in the independent workspace progress document.
