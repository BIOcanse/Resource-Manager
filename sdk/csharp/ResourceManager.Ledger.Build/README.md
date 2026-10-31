# ResourceManager.Ledger.Build

This independent .NET 10 command-line project generates consumer-specific static owner shape. It is separate from the FreedomPoint resolver and the managed runtime, with no package or product references. It is delivered as the `ledger-build` tool; packing is enabled by the SDK packaging entry point. The current formal version is `0.1.0-dev.2`.

The command accepts one canonical JSON declaration and an output directory:

```text
dotnet run --project sdk/csharp/ResourceManager.Ledger.Build -- shape <owner.json> <consumer-obj-directory> --uid-width=128|256
```

The consumer build must pass the UID width selected by that build's FreedomPoint values. `LedgerOwnerShape.zig` checks the supplied width against `ledger_values.uid_width_bits` at Zig compile time. The generator does not resolve FreedomPoint, copy table policy values, choose an index candidate, or choose an ABI.

The owner shape emits each table's policy using explicit members such as
`ledger_values.table_0_activity_score_epochs`. These are references to the
selected values module, not a second set of values. Each field keeps the same
typed cast or enum mapping. There is no generated generic `policy(table)` or
computed `@field` name. This lets static source tracking resolve every real
Compiler reference without executing Zig comptime code. This representation
change is in the dev.4 generator; immutable dev.3 used computed field names.

For the selected C2 Native ABI v1 candidate, generate the paired entry and managed build identity with:

```text
ResourceManager.Ledger.Build native <owner.json> <ledger_values.zig> <consumer-obj-directory> --uid-width=128|256
```

This also generates the two shape files. `LedgerNativeEntry.zig` imports modules named `ledger_sdk` and `ledger_owner_shape` and instantiates the SDK's single Native binding. `LedgerOwnerBuild.g.cs` supplies the corresponding immutable `LedgerBuildIdentity`. The structure fingerprint is SHA-256 of the exact owner JSON bytes; the policy fingerprint is SHA-256 of the exact selected `ledger_values.zig` bytes. `ledger-native-build.json` records those inputs, fingerprints and output hashes for the consumer build to verify before compilation. Both languages receive identical fingerprints; the Native descriptor validates their pairing at load time. Generated input paths are build provenance, not runtime configuration.

Before compiling, run `ResourceManager.Ledger.Build verify <ledger-native-build.json>`. It requires both original inputs and all five generated outputs (including `LedgerOwnerBuild.props`) to retain their recorded hashes. It also recomputes the props and managed companion from the selected input, so changing an output's recorded hash cannot approve a mismatched switch. Module wiring must use the manifest's selected values file. Changing the owner declaration, values, or generated output requires generation again. Older manifests without the props output require regeneration.

The selected `ledger_values.zig` must be the unsigned scalar `Bound` initializer
emitted by the FreedomPoint configuration tool. The Native command reads the
two lease analysis selections from those exact hashed bytes. Missing, repeated,
out-of-range or noncanonical values are rejected; no separate input or default
is used. Native config uses `ledger_values.development_lease_analysis != 0` and
`@intCast(ledger_values.development_lease_analysis_capacity)` directly. Run the
configuration tool's `check` with the same choices first to validate the complete
point catalog, including the flag's 0/1 and capacity's 1..65536 contracts.

`LedgerOwnerBuild.g.cs` and LedgerOwnerBuild.props retain selected-build metadata
and are covered by verify. The props define LEDGER_DEVELOPMENT_LEASE_ANALYSIS
only for the enabled selection. Both files use deterministic UTF-8/LF bytes on
every host, because verify regenerates them and compares bytes. This is
build metadata, not an instruction to add caller access markers. Current analysis
is automatic in the selected Native build; consumers do not write RecordRead,
RecordWrite or coverage declarations, and need no per-access conditional symbol.
Include the managed identity used to pair the Native descriptor as usual.

The owner shape also projects use_window_cache, use_window_cache_slots and
development_lease_analysis_write_watch_pages directly from selected values.
The enabled window-cache candidate requires x86_64+cx16; compile it with
-mcpu=baseline+cx16 on Zig 0.17. Unsupported enabled targets fail compilation
instead of silently using a process-global atomic lock fallback. The default
disabled path keeps prior target support. No C#/use syntax changes are needed.
The Native command only generates source. It does not build a library, resolve configuration or publish a package. The delivered C2/ABI v1 passed compiled layout and installed-package consumer acceptance at UID128 and UID256 on Windows x64. Build-time module wiring must supply the same values file named in the manifest.

## Canonical JSON schema, version 1

The root is an object with exactly these fields:

```json
{
  "schemaVersion": 1,
  "indexBucketCount": 64,
  "tables": [
    {
      "capacity": 32,
      "partitionCapacity": 8,
      "reservedStart": 0,
      "reservedCount": 4,
      "attributes": {
        "recoverability": "recoverable",
        "recoveryCostCoefficient": 2,
        "accessLossImpact": "unobservable_now"
      }
    }
  ],
  "fixedResources": [
    {
      "table": 0,
      "parentUid": null,
      "ordinal": 6,
      "uid": "00000000000000000000000000000000",
      "allocationBytes": 128,
      "attributes": null
    }
  ]
}
```

`tables` and `fixedResources` are ordered arrays. Their zero-based positions are the generated table and fixed-resource identifiers; there are no user-defined names or name conversion rules. A fixed resource with `parentUid: null` targets its `table`; a non-null `parentUid` identifies its parent partition. `uid` and non-null `parentUid` are exactly 32 hexadecimal characters for 128-bit builds or 64 for 256-bit builds. Hexadecimal letter case is accepted. All bytes, including an all-zero UID, are preserved.

`attributes` has exactly `recoverability`, `recoveryCostCoefficient`, and `accessLossImpact`. The enum spellings are `not_recoverable` or `recoverable`, and `fatal`, `observable_now`, or `unobservable_now`. A fixed resource's `attributes` may be `null` to inherit its table or parent defaults.

All object fields are required, unknown or repeated fields are rejected, and JSON numbers must be exact non-negative integer tokens within the target field's unsigned width. Comments and trailing commas are rejected. The generator checks schema and representation only. The owner performs its structural checks when compiled or initialized. UID uniqueness and business ownership remain the software's responsibility; neither this generator nor the ledger adds those checks.

## Outputs

The output directory receives:

- `LedgerOwnerShape.zig`: `types.Table` and `types.Fixed` declarations, `types.Config`, the selected UID type, and a per-table policy projection from `ledger_values.Bound`.
- `LedgerOwnerShape.g.cs`: ordered numeric table/fixed identifiers and fixed UID values using the immutable `ResourceManager.Ledger.Identity.LedgerUid128` or `LedgerUid256` types. It contains no mutable UID byte arrays, live table, UID index, leases, or owner state.

Both outputs embed the same lowercase SHA-256 hash of the exact canonical JSON file bytes. They are generated into the consumer's `obj` directory and are not checked-in FreedomPoint structure products. The checked-in FreedomPoint declarations remain configuration-independent; selected values remain per-build inputs.

The `native` command additionally writes the Native entry, managed identity, selected-values build props and build manifest described above. It does not introduce per-resource exports, a fixed-declaration switch, a UID map or an ABI10 compatibility route.
