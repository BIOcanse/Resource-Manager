# Ledger FreedomPoint configuration

This project defines three global build facts and thirteen configuration points
for every structural table. For `tableCount = N`, the registry has exactly
`3 + 13N` active declarations. Table and partition capacities, allocation
sizes, and fixed resource identities remain structural declarations supplied
by the adopter.

It consumes the formal FreedomPoint `0.1.0-dev.39` Core and Generators packages
from the repository copy in `sdk/freedom-point/packages`. `FactResolver` resolves each selected
build input and `TypedProjection` produces its C# and Zig output. The project
does not implement a second resolver or source emitter.

## Points and defaults

| Scope | Point | Default | Contract |
| --- | --- | ---: | --- |
| Global | `ledger.uid_width_bits` | 128 | Exactly 128 or 256 through unsigned minimum, maximum and step |
| Global | `ledger.activity_bitmap_width` | 8 | Exactly 8, 16, 32 or 64 |
| Global | `ledger.development_allocation_statistics` | 0 (off) | Build-time unsigned 0/1; disabled Native builds omit statistic storage, collection and the query export |
| Per table | `activity_score_epochs` | 8 | K, from 1 through the global bitmap width |
| Per table | `idle_epochs` | 8 | N, from 0 through the global bitmap width; zero disables idle unload |
| Per table | `capacity_protection_method` | `concentrated` | `concentrated` or `smooth` |
| Per table | `concentrated_trigger_percent` | 10 | Zero disables concentrated protection |
| Per table | `concentrated_target_percent` | 20 | Concentrated trigger must be lower than target |
| Per table | `smooth_trigger_percent` | 15 | Smooth emergency threshold must be lower than trigger |
| Per table | `smooth_emergency_percent` | 5 | Inclusive smooth emergency threshold |
| Per table | `smooth_interval_epochs` | 1 | Positive |
| Per table | `smooth_release_count` | 1 | Positive |
| Per table | `tick_maximum_actions` | 64 | Positive upper bound shared by the three Tick stages |
| Per table | `release_check_enabled` | `false` | Single-choice `false`/`true` value |
| Per table | `release_tolerance_parts_per_million` | 0 | Unsigned 32-bit |
| Per table | `release_tolerance_bytes` | 0 | Unsigned 64-bit |

Per-table point names and source pointers use the canonical form
`ledger.table.N.field`. Their `PointPath` owner2 is distinct per table, and
generated value members are named `table_N_field`. The global bitmap point is
the single dynamic maximum for every table's K and N. Each scope uses dense
zero-based point indexes: global bitmap 0, UID width 1 and development statistics 2; each table's thirteen
points use 0 through 12, with capacity method at 12. The removed pending A/B
points do not leave holes in the catalog.

The SDK point catalog records the ranges, steps, choices and K/N dynamic maxima
that FreedomPoint can express. Two ledger rules are narrower than that catalog
can encode: the bitmap width is the discrete set 8/16/32/64 (a range and step
would also admit unsupported widths), and each threshold pair has a strict
cross-point `<` relation. The development-only `check` command runs the SDK's
full point-contract validation and then checks those ledger rules independently
for every table. It adds no resolver and no runtime validator. The two methods'
thresholds do not constrain each other.

The structural `tableCount` determines the registry and generated member set,
so values generation requires the same count as the checked-in structure. The
CLI uses one table when `--table-count` is omitted; adopters should pass their
actual structural table count.

## Generate checked-in structure

Run this when point declarations, generated bindings, or structural table count
change. The checked-in structure must use the table count selected by the
adopter; this example generates the two-table owner fixture:

```powershell
dotnet <configuration-tool.dll> structure <repository>/sdk/zig/src/local_resource_manager/generated --table-count 2
```

The command writes only configuration-independent artifacts:

- `ledger_structure.zig`
- `LedgerDefaults.g.cs`
- `ledger_registry.json`
- `ledger_zig_bindings.json`

These files come directly from `TypedProjection.StructureSource` and registry
metadata. The generator does not write selected defaults into the source tree.

## Check a build's configuration

Run the development check with the exact table count and choices that the
build will use:

```powershell
dotnet <configuration-tool.dll> check --table-count 2 --choices D:/build/ledger-choices.json
```

The command merges inputs through FreedomPoint, validates all SDK point
contracts, and checks the discrete bitmap set and strict within-method
threshold pairs. Run it before value generation using the same choices file.

## Generate one build's values

Every build supplies its selected table count, a distinct absolute output
directory, the checked-in structure directory, and optional choices:

```powershell
dotnet <configuration-tool.dll> values D:/build/ledger-values <repository>/sdk/zig/src/local_resource_manager/generated --table-count 2 --choices D:/build/ledger-choices.json
```

Override keys are exact canonical point names. Global examples are
`ledger.uid_width_bits` and `ledger.activity_bitmap_width`; per-table examples
include `ledger.table.0.activity_score_epochs` and
`ledger.table.1.capacity_protection_method`. Unsigned values are JSON numbers,
the protection method is a string, and `release_check_enabled` is a JSON
boolean. For example:

```json
{
  "ledger.uid_width_bits": 256,
  "ledger.activity_bitmap_width": 16,
  "ledger.table.0.activity_score_epochs": 12,
  "ledger.table.0.idle_epochs": 5,
  "ledger.table.0.capacity_protection_method": "concentrated",
  "ledger.table.0.tick_maximum_actions": 64,
  "ledger.table.1.activity_score_epochs": 8,
  "ledger.table.1.idle_epochs": 0,
  "ledger.table.1.capacity_protection_method": "smooth",
  "ledger.table.1.tick_maximum_actions": 32,
  "ledger.table.1.release_check_enabled": true
}
```

The command merges inputs through FreedomPoint, verifies the checked-in
structure byte-for-byte before writing, then writes only these selected
build artifacts. It does not run numeric point-contract checks; the build
workflow must run `check` first with the same input file:

- `ledger_values.zig`
- `LedgerDefaults.Values.g.cs`
- `ledger_facts.json`

`ledger_values.zig` imports the checked-in module `ledger_structure` and exposes
the selected constants through `Bound`, including the global
`Bound.uid_width_bits` / `Bound.activity_bitmap_width` and table-specific
`Bound.table_0_*`, `Bound.table_1_*`, and later members. Zig builds add the named
modules `ledger_structure` (checked in) and `ledger_values` (the build's output).
The base activity-window and release-check fixtures read table 0; owners compile
their already-resolved per-table `Policy` values for each table. The C# structure
and matching `LedgerDefaults.Values.g.cs` must both be included in a consuming
build.

Configurations are resolved independently for each build. Selected values are
build inputs and are never checked in as defaults.
