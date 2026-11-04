/** Reads native SharedSection v1 using trusted layout and selection identities. Recreate on grant or mapping changes; mapped bytes never establish authority. Atomic reads require a separately verified mutable mapping. */
export const V1 = Object.freeze({
  magic: 0x53534d52, version: 1, none: 0xffffffff,
  headerSize: 32, sourceSize: 112, positionSize: 40,
  endedBit: 1n << 63n,
});

/** Stable native Failure numbers, including codes not emitted by this reader. */
export enum Failure {
  region_too_small = 1, region_misaligned = 2, bad_magic = 3, bad_version = 4,
  count_over_capacity = 5, data_capacity = 6, source_index = 7, position_index = 8,
  position_source = 9, position_kind = 10, position_width = 11, position_range = 12,
  position_identity = 13, position_strings = 14, position_overlap = 15, owner_name = 16,
  source_state = 17, length_over_capacity = 18, string_range = 19, rows_mismatch = 20,
  retry_limit = 21, copy_too_small = 22, not_owner = 23, wrong_kind = 24,
  element_width = 25, element_range = 26, write_in_progress = 27, not_writing = 28,
  ended = 29, invalid_argument = 30, not_selected = 31, identity_not_selected = 32,
  layout_changed = 33, selection_order = 34, selection_empty = 35, selection_too_large = 36,
}

export class SelectedReaderError extends Error {
  readonly failure: Failure;
  constructor(failure: Failure) {
    super(`SharedSection v1: ${Failure[failure]}`);
    this.name = 'SelectedReaderError';
    this.failure = failure;
  }
}

export interface HeaderV1 {
  readonly magic: number;
  readonly version: number;
  readonly data_kind: number;
  readonly source_capacity: number;
  readonly source_count: number;
  readonly position_capacity: number;
  readonly position_count: number;
  readonly data_capacity: number;
}
export interface PositionSpecV1 {
  readonly source: number;
  /** scalar=1, column=2, string_column=3, string_table=4. */
  readonly kind: number;
  readonly offset: number;
  readonly size: number;
  readonly element_width: number;
  /** Local v1 position index, or V1.none; never a cross-region reference. */
  readonly identity: number;
  readonly strings: number;
}
export interface TrustedLayoutV1 {
  readonly header: HeaderV1;
  readonly positions: readonly PositionSpecV1[];
}

type Failed = { readonly status: 'failed'; readonly failure: Failure };
type Unavailable = { readonly status: 'unavailable'; readonly reason: number };
type Ended = { readonly status: 'ended' };
type NotArrived = { readonly status: 'not_arrived' };
type HeldValue = { readonly status: 'value'; readonly sequence: bigint; readonly writeTimeUnixMs: bigint };
export type SourceRead = { readonly status: 'unchanged' } | HeldValue | Unavailable | Ended | Failed;
export type SourceState = HeldValue | (Unavailable & { readonly sequence: bigint; readonly writeTimeUnixMs: bigint })
  | (Ended & { readonly sequence: bigint }) | NotArrived | Failed;
export interface ValueV1 {
  /** Detached from the held copy so a consumer cannot corrupt later reads. */
  readonly bytes: Uint8Array;
  /** Compact selection-data offset, matching native Selection. */
  readonly offset: number;
  readonly length: number;
}
export type PositionValue = ({ readonly status: 'value' } & ValueV1) | NotArrived | Unavailable | Ended | Failed;

const headerKeys = ['magic', 'version', 'data_kind', 'source_capacity', 'source_count',
  'position_capacity', 'position_count', 'data_capacity'] as const;
const specKeys = ['source', 'kind', 'offset', 'size', 'element_width', 'identity', 'strings'] as const;
interface SelectedPosition {
  readonly index: number;
  readonly spec: PositionSpecV1;
  readonly offset: number;
  length: number;
  identitySequence: bigint;
}
interface Round { state: number; reason: number; writeTimeUnixMs: bigint; failure?: Failure }

function reject(failure: Failure): never { throw new SelectedReaderError(failure); }
function isU32(value: number): boolean { return Number.isInteger(value) && value >= 0 && value <= V1.none; }
function rows(kind: number): boolean { return kind === 2 || kind === 3; }
function align8(value: number): number { return Math.ceil(value / 8) * 8; }

function checkHeader(h: HeaderV1, regionLength: number): void {
  let i = 0;
  while (i < headerKeys.length) {
    if (!isU32(h[headerKeys[i]])) reject(Failure.invalid_argument);
    i += 1;
  }
  if (h.magic !== V1.magic) reject(Failure.bad_magic);
  if (h.version !== V1.version) reject(Failure.bad_version);
  if (h.source_count > h.source_capacity || h.position_count > h.position_capacity) reject(Failure.count_over_capacity);
  if (h.data_capacity % 8 !== 0) reject(Failure.data_capacity);
  if (V1.headerSize + h.source_capacity * V1.sourceSize + h.position_capacity * V1.positionSize + h.data_capacity > regionLength)
    reject(Failure.region_too_small);
}

function copyDescriptor(descriptor: TrustedLayoutV1, regionLength: number): TrustedLayoutV1 {
  const from = descriptor.header;
  const header: HeaderV1 = Object.freeze({ magic: from.magic, version: from.version, data_kind: from.data_kind,
    source_capacity: from.source_capacity, source_count: from.source_count,
    position_capacity: from.position_capacity, position_count: from.position_count, data_capacity: from.data_capacity });
  checkHeader(header, regionLength);
  if (descriptor.positions.length !== header.position_count) reject(Failure.count_over_capacity);
  const positions: PositionSpecV1[] = [];
  let index = 0;
  while (index < descriptor.positions.length) {
    const p = descriptor.positions[index];
    const spec = Object.freeze({ source: p.source, kind: p.kind, offset: p.offset, size: p.size,
      element_width: p.element_width, identity: p.identity, strings: p.strings });
    let field = 0;
    while (field < specKeys.length) {
      if (!isU32(spec[specKeys[field]])) reject(Failure.invalid_argument);
      field += 1;
    }
    positions.push(spec);
    index += 1;
  }
  index = 0;
  while (index < positions.length) {
    const p = positions[index];
    if (p.source >= header.source_count) reject(Failure.position_source);
    const width = p.element_width;
    if (p.kind === 1 || p.kind === 2) {
      if (width !== 1 && width !== 2 && width !== 4 && width !== 8) reject(Failure.position_width);
    } else if (p.kind === 3) {
      if (width !== 8) reject(Failure.position_width);
    } else if (p.kind === 4) {
      if (width !== 1) reject(Failure.position_width);
    } else reject(Failure.position_kind);
    if (p.offset % 8 !== 0 || p.size % width !== 0 || p.offset + p.size > header.data_capacity) reject(Failure.position_range);
    if (p.identity !== V1.none) {
      if (!rows(p.kind) || p.identity >= header.position_count || p.identity === index) reject(Failure.position_identity);
      const identity = positions[p.identity];
      if (!rows(identity.kind) || identity.identity !== V1.none) reject(Failure.position_identity);
    }
    if (p.kind === 3) {
      if (p.strings >= header.position_count) reject(Failure.position_strings);
      const table = positions[p.strings];
      if (table.kind !== 4 || table.source !== p.source) reject(Failure.position_strings);
    } else if (p.strings !== V1.none) reject(Failure.position_strings);
    index += 1;
  }
  index = 0;
  while (index < positions.length) {
    const a = positions[index];
    let other = index + 1;
    while (other < positions.length) {
      const b = positions[other];
      if (a.size !== 0 && b.size !== 0 && a.offset < b.offset + b.size && b.offset < a.offset + a.size)
        reject(Failure.position_overlap);
      other += 1;
    }
    index += 1;
  }
  return Object.freeze({ header, positions: Object.freeze(positions) });
}

export class SelectedReader {
  private readonly buffer: ArrayBufferLike;
  private readonly descriptor: TrustedLayoutV1;
  private readonly retryLimit: number;
  private readonly held = new Map<number, SourceState>();
  private readonly positions = new Map<number, SelectedPosition>();
  private readonly positionTable: number;
  private readonly dataOffset: number;
  private readonly data: Uint8Array;
  private readonly u8: Uint8Array;
  private readonly u16: Uint16Array;
  private readonly u32: Uint32Array;
  private readonly u64: BigUint64Array;
  private readonly i64: BigInt64Array;

  constructor(buffer: ArrayBufferLike, descriptor: TrustedLayoutV1, sources: readonly number[], retryLimit: number) {
    if (!isU32(retryLimit)) reject(Failure.invalid_argument);
    // The native format is little endian, including typed element loads.
    if (new Uint8Array(new Uint32Array([V1.magic]).buffer)[0] !== 0x52) reject(Failure.invalid_argument);
    this.buffer = buffer;
    this.retryLimit = retryLimit;
    this.descriptor = copyDescriptor(descriptor, buffer.byteLength);
    const h = this.descriptor.header;
    this.positionTable = V1.headerSize + h.source_capacity * V1.sourceSize;
    this.dataOffset = this.positionTable + h.position_capacity * V1.positionSize;
    let index = 0;
    while (index < sources.length) {
      const source = sources[index];
      if (!isU32(source) || source >= h.source_count) reject(Failure.source_index);
      if (index > 0 && sources[index - 1] >= source) reject(Failure.selection_order);
      this.held.set(source, { status: 'not_arrived' });
      index += 1;
    }
    let size = 0;
    index = 0;
    while (index < this.descriptor.positions.length) {
      const spec = this.descriptor.positions[index];
      if (this.held.has(spec.source)) {
        if (spec.identity !== V1.none && !this.held.has(this.descriptor.positions[spec.identity].source))
          reject(Failure.identity_not_selected);
        this.positions.set(index, { index, spec, offset: size, length: 0, identitySequence: 0n });
        size = align8(size + spec.size);
      }
      index += 1;
    }
    this.data = new Uint8Array(size);
    this.u8 = new Uint8Array(buffer);
    this.u16 = new Uint16Array(buffer, 0, Math.floor(buffer.byteLength / 2));
    this.u32 = new Uint32Array(buffer, 0, Math.floor(buffer.byteLength / 4));
    this.u64 = new BigUint64Array(buffer, 0, Math.floor(buffer.byteLength / 8));
    this.i64 = new BigInt64Array(buffer, 0, Math.floor(buffer.byteLength / 8));
  }

  /** A snapshot of the held result, for the caller's region-set lifecycle. */
  sourceState(source: number): SourceState {
    const held = this.held.get(source);
    return held ? { ...held } : { status: 'failed', failure: Failure.not_selected };
  }

  /** Trusted lifecycle loss: discard this source until its owner permits reads. */
  invalidate(source: number): void {
    if (!this.held.has(source)) return;
    this.clearPositions(source);
    this.held.set(source, { status: 'not_arrived' });
  }

  read(source: number): SourceRead {
    if (!this.held.has(source)) return { status: 'failed', failure: Failure.not_selected };
    try {
      checkHeader(this.descriptor.header, this.buffer.byteLength);
      this.checkMappedLayout(source);
      const sequenceOffset = V1.headerSize + source * V1.sourceSize;
      let sequence = Atomics.load(this.u64, sequenceOffset / 8);
      const held = this.held.get(source)!;
      if ((held.status === 'value' || held.status === 'unavailable' || held.status === 'ended') && held.sequence === sequence)
        return { status: 'unchanged' };
      this.held.set(source, { status: 'not_arrived' });
      let attempt = 0;
      while (true) {
        if ((sequence & V1.endedBit) !== 0n) return this.settleEnded(source, sequence);
        if ((sequence & 1n) === 0n) {
          const round = this.copyRound(source);
          if (Atomics.load(this.u64, sequenceOffset / 8) === sequence) return this.settle(source, sequence, round);
        }
        // Native retry_limit counts retries AFTER the first attempt (zero = one attempt).
        if (attempt === this.retryLimit) return this.fail(source, Failure.retry_limit);
        attempt += 1;
        sequence = Atomics.load(this.u64, sequenceOffset / 8);
      }
    } catch (error) {
      if (error instanceof SelectedReaderError) return this.fail(source, error.failure);
      // Detached/resized buffers must invalidate the held result too.
      if (error instanceof RangeError || error instanceof TypeError) return this.fail(source, Failure.region_too_small);
      throw error;
    }
  }

  private checkMappedLayout(source: number): void {
    if (this.buffer.byteLength < V1.headerSize) reject(Failure.region_too_small);
    const magic = Atomics.load(this.u32, 0);
    if (magic !== V1.magic) reject(Failure.bad_magic);
    const observed: HeaderV1 = { magic, version: Atomics.load(this.u32, 1), data_kind: Atomics.load(this.u32, 2),
      source_capacity: Atomics.load(this.u32, 3), source_count: Atomics.load(this.u32, 4),
      position_capacity: Atomics.load(this.u32, 5), position_count: Atomics.load(this.u32, 6), data_capacity: Atomics.load(this.u32, 7) };
    // Validate the observed values, but never derive an access from them.
    checkHeader(observed, this.buffer.byteLength);
    let index = 0;
    while (index < headerKeys.length) {
      const key = headerKeys[index];
      if (observed[key] !== this.descriptor.header[key]) reject(Failure.layout_changed);
      index += 1;
    }
    const iterator = this.positions.values();
    let next = iterator.next();
    while (!next.done) {
      const selected = next.value;
      if (selected.spec.source === source) {
        const offset = this.positionTable + selected.index * V1.positionSize;
        index = 0;
        while (index < specKeys.length) {
          if (Atomics.load(this.u32, offset / 4 + index) !== selected.spec[specKeys[index]]) reject(Failure.layout_changed);
          index += 1;
        }
      }
      next = iterator.next();
    }
  }

  private copyRound(source: number): Round {
    const offset = V1.headerSize + source * V1.sourceSize;
    const round: Round = { state: Atomics.load(this.u32, (offset + 16) / 4),
      reason: Atomics.load(this.u32, (offset + 20) / 4), writeTimeUnixMs: Atomics.load(this.i64, (offset + 8) / 8) };
    if (round.state !== 1 && round.state !== 2 && round.state !== 3) return { ...round, failure: Failure.source_state };
    if (round.state !== 1) return round;
    const iterator = this.positions.values();
    let next = iterator.next();
    while (!next.done) {
      const selected = next.value;
      const spec = selected.spec;
      if (spec.source === source) {
        if (spec.offset + spec.size > this.descriptor.header.data_capacity || selected.offset + spec.size > this.data.length)
          return { ...round, failure: Failure.copy_too_small };
        const width = spec.element_width;
        const entry = this.positionTable + selected.index * V1.positionSize;
        const length = spec.kind === 1 ? spec.size / width : Atomics.load(this.u32, (entry + 28) / 4);
        if (length > spec.size / width) return { ...round, failure: Failure.length_over_capacity };
        selected.length = length;
        selected.identitySequence = Atomics.load(this.u64, (entry + 32) / 8);
        const from = this.dataOffset + spec.offset;
        const target = new DataView(this.data.buffer);
        let element = 0;
        while (element < length) {
          const at = from + element * width;
          const to = selected.offset + element * width;
          if (width === 1) target.setUint8(to, Atomics.load(this.u8, at));
          else if (width === 2) target.setUint16(to, Atomics.load(this.u16, at / 2), true);
          else if (width === 4) target.setUint32(to, Atomics.load(this.u32, at / 4), true);
          else target.setBigUint64(to, Atomics.load(this.u64, at / 8), true);
          element += 1;
        }
      }
      next = iterator.next();
    }
    return round;
  }

  private settle(source: number, sequence: bigint, round: Round): SourceRead {
    if (round.failure !== undefined) return this.fail(source, round.failure);
    if (round.state === 3) return this.settleEnded(source, sequence);
    if (round.state === 2) {
      this.held.set(source, { status: 'unavailable', sequence, writeTimeUnixMs: round.writeTimeUnixMs, reason: round.reason });
      return { status: 'unavailable', reason: round.reason };
    }
    const failure = this.checkStrings(source);
    if (failure !== undefined) return this.fail(source, failure);
    const value: HeldValue = { status: 'value', sequence, writeTimeUnixMs: round.writeTimeUnixMs };
    this.held.set(source, value);
    return { ...value };
  }

  private checkStrings(source: number): Failure | undefined {
    const iterator = this.positions.values();
    let next = iterator.next();
    const data = new DataView(this.data.buffer);
    while (!next.done) {
      const column = next.value;
      if (column.spec.source === source && column.spec.kind === 3) {
        const table = this.positions.get(column.spec.strings);
        if (!table) return Failure.position_strings;
        let row = 0;
        while (row < column.length) {
          const at = column.offset + row * 8;
          if (data.getUint32(at, true) + data.getUint32(at + 4, true) > table.length) return Failure.string_range;
          row += 1;
        }
      }
      next = iterator.next();
    }
    return undefined;
  }

  private settleEnded(source: number, sequence: bigint): Ended {
    this.clearPositions(source);
    this.held.set(source, { status: 'ended', sequence });
    return { status: 'ended' };
  }

  private clearPositions(source: number): void {
    const iterator = this.positions.values();
    let next = iterator.next();
    while (!next.done) {
      const selected = next.value;
      if (selected.spec.source === source) {
        this.data.fill(0, selected.offset, selected.offset + selected.spec.size);
        selected.length = 0;
        selected.identitySequence = 0n;
      }
      next = iterator.next();
    }
  }

  private fail(source: number, failure: Failure): Failed {
    this.held.set(source, { status: 'failed', failure });
    return { status: 'failed', failure };
  }

  positionValue(position: number): PositionValue {
    const selected = this.positions.get(position);
    if (!selected) return { status: 'failed', failure: Failure.not_selected };
    const owner = this.held.get(selected.spec.source);
    if (!owner) return { status: 'failed', failure: Failure.not_selected };
    if (owner.status === 'unavailable') return { status: 'unavailable', reason: owner.reason };
    if (owner.status === 'ended') return { status: 'ended' };
    if (owner.status === 'failed') return { status: 'failed', failure: owner.failure };
    if (owner.status === 'not_arrived') return { status: 'not_arrived' };
    if (selected.spec.identity !== V1.none) {
      const rows = this.positions.get(selected.spec.identity);
      if (!rows) return { status: 'failed', failure: Failure.identity_not_selected };
      const rowsSource = this.held.get(rows.spec.source);
      if (!rowsSource) return { status: 'failed', failure: Failure.identity_not_selected };
      if (rowsSource.status !== 'value' || rowsSource.sequence !== selected.identitySequence) return { status: 'not_arrived' };
      if (rows.length !== selected.length) return { status: 'failed', failure: Failure.rows_mismatch };
    }
    const span = selected.length * selected.spec.element_width;
    if (selected.offset + span > this.data.length) return { status: 'failed', failure: Failure.copy_too_small };
    return { status: 'value', bytes: this.data.slice(selected.offset, selected.offset + span), offset: selected.offset, length: selected.length };
  }

  /** Raw UTF-8 bytes; the application owns text decoding. */
  stringAt(column: number, row: number): Uint8Array | null {
    const rows = this.positionValue(column);
    if (rows.status !== 'value') return null;
    const spec = this.positions.get(column)!.spec;
    if (spec.kind !== 3 || !isU32(row) || row >= rows.length) return null;
    const table = this.positionValue(spec.strings);
    if (table.status !== 'value') return null;
    const data = new DataView(rows.bytes.buffer, rows.bytes.byteOffset, rows.bytes.byteLength);
    const offset = data.getUint32(row * 8, true);
    const length = data.getUint32(row * 8 + 4, true);
    if (offset + length > table.bytes.length) return null;
    return table.bytes.slice(offset, offset + length);
  }
}
