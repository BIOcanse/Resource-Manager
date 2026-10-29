# Shared section layout

`rm_shared_section` defines the memory layout of a shared region and the per-source sequence rules for writing and reading it (IPC design `ipc_shared_memory_2026-10-29.md`, step 1). It works only on a caller-provided byte region. Creating or mapping sections, duplicating handles, HTTP, WebView and the meaning of any data kind belong to the callers.

## Layout

All fields are little-endian at fixed offsets. A region starts 8-byte aligned. Every field is naturally aligned: 64-bit fields on 8-byte offsets and 32-bit fields on 4-byte offsets. A page can read a plain ArrayBuffer with typed arrays, reading each source sequence with `Atomics.load` on a `BigInt64Array`. Compile-time asserts pin the offsets.

| Part | Offset | Size |
|---|---|---|
| Header | 0 | 32 |
| Source table | 32 | 112 × source capacity |
| Position table | 32 + 112 × source capacity | 40 × position capacity |
| Data area | after the position table | data capacity (multiple of 8) |

`requiredSize(source_capacity, position_capacity, data_capacity)` gives the total size.

Header: `magic` u32 `0x53534D52` ("RMSS") at offset 0, `version` u32 1 at 4, `data_kind` u32 at 8 (chosen by the creator), `source_capacity` at 12, `source_count` at 16, `position_capacity` at 20, `position_count` at 24, `data_capacity` at 28.

Source entry:

| Offset | Field | Written by |
|---|---|---|
| 0 | `sequence` u64: even when stable, odd inside a round, bit 63 = ended | the source |
| 8 | `write_time_unix_ms` i64: UTC milliseconds since the Unix epoch | the source |
| 16 | `state` u32: 1 has value, 2 unavailable, 3 ended | the source |
| 20 | `reason` u32: the source's own code while unavailable | the source |
| 24 | `kind` u32: source kind | creator |
| 28 | `process_id` u32: cross-process sources only, else 0 | creator |
| 32 | `process_start_time` u64: the creation time the creator verified (FILETIME on Windows), else 0 | creator |
| 40 | `owner_name_length` u32 (≤ 64) and `owner_name` [64] bytes at 44 | creator |

Position entry:

| Offset | Field | Written by |
|---|---|---|
| 0 | `source` u32: owning source index | creator |
| 4 | `kind` u32: 1 scalar, 2 column, 3 string column, 4 string table | creator |
| 8 | `offset` u32: in the data area, multiple of 8 | creator |
| 12 | `size` u32: reserved bytes, multiple of the width | creator |
| 16 | `element_width` u32: 1/2/4/8; 8 for a string column, 1 for a string table | creator |
| 20 | `identity` u32: for a column or string column, its identity position, or `0xFFFFFFFF` | creator |
| 24 | `strings` u32: for a string column, its string table, or `0xFFFFFFFF` | creator |
| 28 | `length` u32: rows (column, string column) or bytes (string table) | the source |
| 32 | `identity_sequence` u64: the identity source sequence this column was written against | the source |

A scalar is always whole. A string-column row is one u64: byte offset in its string table (low 32 bits) and byte length (high 32 bits). Each string table belongs to the same source as its string columns, so no two sources share any position. `initialize` rejects overlapping positions, references to missing or wrong positions, and identity chains: an identity position has no identity itself.

## Rules

- **Creator.** `initialize(region, data_kind, capacities…, sources, positions)` fixes each position's source and writes the identities. Every source starts unavailable with `not_written_reason` (0) at sequence 0. The data area is zeroed. The magic is stored last with release.
- **Write.** A source writes only its own positions, and only when its data changes. `Writer.begin` moves its sequence to odd. `store`, `storeRaw`, `appendString` and `setLength` write positions. `end(.has_value | .unavailable = reason | .ended, time)` writes the state and time and moves the sequence to even. `.ended` is terminal and zeroes the source's positions.
- **Rows.** A row-defining source owns an identity position (which rows, in what order). Any other column records, with `setLength`, the identity source sequence it was written against. Use `Writer.roundSequence()` when the identity belongs to the same source. `positionValue` uses a column only when that sequence equals the sequence of the identity source's held copy. Otherwise it returns `not_arrived`. An arrived column with another row count is `failed = rows_mismatch`. Any write by the identity source bumps its sequence, so its other columns and the other sources' columns wait for their next write.
- **Read.** `read(region, copy, source, retry_limit)` loads the sequence. If it is equal to the held copy's, the read returns `unchanged` and copies nothing. Otherwise it checks the position table, copies the source's state and positions into `copy` at their data-area offsets, then loads the sequence again. If the sequence changed or was odd, it retries. After `retry_limit` retries (default `default_retry_limit` = 1000, each after a spin-loop hint) the source is `failed = retry_limit` for this round. A read never hangs.
- **Untrusted content.** Every count, offset, size, length, state and string row is checked against the region and the copy before use. A malformed region yields `failed` with a `Failure` reason. It never panics or reads out of bounds. Only header changes are seen by an unchanged read. A table change is seen at the source's next change.
- **End ("无数据").** `end(region, time)` sets the ended bit on every source, marks every source ended, and zeroes all lengths and the data area. Call it before unmapping, after stopping your own writers. A round still open elsewhere fails at its end with `error.Ended` and zeroes its own positions again. A reader zeroes its copy of an ended source.
- **Memory order.** This is a fence-free seqlock: data stores in a round are release stores, data loads in a read are acquire loads, and the sequence uses acquire/release (`seqlock.zig` explains why). On x86-64 these are plain moves.

## Import

```zig
const shared_section = b.createModule(.{
    .root_source_file = b.path("../../Shared/SharedSection/src/root.zig"),
    .target = target,
    .optimize = .ReleaseSafe,
});
host_module.addImport("rm_shared_section", shared_section);
```

## C ABI

Exported from `src/root.zig` with fixed-width arguments. The functions allocate nothing and keep no state, and every result goes to caller memory. A function returns 0 or a `Failure` code, except `read` (`ReadStatus`) and `position_value` (`PositionStatus`).

- Layout: `rm_shared_section_required_size`, `rm_shared_section_initialize` (`LayoutSpec`), and the queries `rm_shared_section_describe`, `rm_shared_section_source` and `rm_shared_section_position`.
- Write: `rm_shared_section_write_begin` (fills a caller `Writer`), `rm_shared_section_write_elements` (whole little-endian elements of the position's width), `rm_shared_section_write_length`, and `rm_shared_section_write_end` (`state` 1/2/3, `reason`, time).
- Read: `rm_shared_section_read(region, length, copy, source, retry_limit)` and `rm_shared_section_position_value(copy, position, ValueView)`. The value's offset and byte length index the copy's data. Callers pass the retry limit; the Zig default is 1000. A `Copy` holds pointers to caller arrays: data of at least the data capacity, zeroed `SourceCopy[source count]` and zeroed `PositionEntry[position count]`.
- End: `rm_shared_section_end`.

## Tests

Run from this directory under pwsh 7 through the foreground JobQueue, with caches in a registered ledger:

```powershell
$env:ZIG_GLOBAL_CACHE_DIR = '<ledger>\zig-global-cache'
& '<tools>\JobQueue\JobQueue.ps1' -Action Run -Label 'shared-section tests' -FilePath zig -ArgumentList @('build','test','--summary','all','--cache-dir','<ledger>\zig-cache') -WorkingDirectory '<checkout>\src\Shared\SharedSection' -TimeoutMinutes 30
```

The suite covers:

- concurrent writers and readers: no torn round, and a column never paired with other rows;
- unchanged reads, row versioning, string tables, source-written unavailable, and the retry limit (in-thread and cross-thread);
- writer ownership and capacity, and the alignment rule;
- malformed headers, tables and dynamic content, rejected layouts, and a 20,000-iteration corruption fuzz;
- end (region and per source, including a round open during end);
- a round trip through every C ABI function.
