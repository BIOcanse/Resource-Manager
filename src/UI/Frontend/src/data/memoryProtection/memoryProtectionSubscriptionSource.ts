import {
  BackendCurrentValueSource, type CurrentValueSource
} from "../../frontendRuntime/push/PushValueSourceFamily.ts";
import { FrameFoldSource } from "../../frontendRuntime/push/FrameFoldSource.ts";
import type { BackendSubscriptionChannel } from "../../frontendRuntime/push/BackendSubscriptionChannel.ts";
import { buildMemoryProtectionSubscriptionUrl } from "./memoryProtectionApi.ts";
import { memoryProtectionFrameDecoder } from "./memoryProtectionDecoders.ts";
import type {
  HostMemoryActionRecord, HostMemoryProtectionFrame, HostMemoryProtectionLiveStatus,
  HostMemoryProtectionView, NormalRunningPoint, NormalRunningPointList
} from "./memoryProtectionTypes.ts";

/** What the frames of one subscription have delivered so far; undefined means that frame has not arrived. */
export interface MemoryProtectionFrameState {
  readonly status?: HostMemoryProtectionLiveStatus;
  readonly records?: readonly HostMemoryActionRecord[];
  readonly recordCapacity?: number;
  readonly unrecordedRecordCount?: number;
  readonly normalRunningPoint?: NormalRunningPoint | null;
  readonly normalRunningPoints?: NormalRunningPointList;
}

const sameRecord = (left: HostMemoryActionRecord, right: HostMemoryActionRecord) => left.actionId === right.actionId
  && left.processId === right.processId && left.processStartKey === right.processStartKey;

/**
 * Applies one frame. status, normalRunningPoint and normalRunningPoints replace their part; records replaces the list,
 * the capacity and the unrecorded count (sent on subscribe, after history is cleared and when the count changes);
 * record upserts by actionId plus process identity (in place, or as the newest). Nothing is truncated: the backend
 * writes no record past its capacity (docs/report_lifecycle_2026-10-28.md, full lists do not evict).
 * A record before the records replay has no list to join and is dropped; the replay that follows carries it.
 */
export function applyMemoryProtectionFrame(state: MemoryProtectionFrameState, frame: HostMemoryProtectionFrame): MemoryProtectionFrameState {
  switch (frame.type) {
    case "status":
      return { ...state, status: frame.status };
    case "records":
      return { ...state, records: frame.records, recordCapacity: frame.limit,
        unrecordedRecordCount: frame.unrecordedRecordCount };
    case "record": {
      if (state.records === undefined) return state;
      const index = state.records.findIndex(record => sameRecord(record, frame.record));
      const records = index >= 0
        ? state.records.map((record, position) => position === index ? frame.record : record)
        : [frame.record, ...state.records];
      return { ...state, records };
    }
    case "normalRunningPoint":
      return { ...state, normalRunningPoint: frame.normalRunningPoint };
    case "normalRunningPoints":
      return { ...state, normalRunningPoints: frame.normalRunningPoints };
  }
}

/** The view is complete once status, records, the point (possibly null) and the saved points have all arrived. */
export function memoryProtectionViewOf(state: MemoryProtectionFrameState): HostMemoryProtectionView | null {
  return state.status !== undefined && state.records !== undefined && state.recordCapacity !== undefined
    && state.unrecordedRecordCount !== undefined
    && state.normalRunningPoint !== undefined && state.normalRunningPoints !== undefined
    ? Object.freeze({ status: state.status, records: [...state.records], recordCapacity: state.recordCapacity,
      unrecordedRecordCount: state.unrecordedRecordCount,
      normalRunningPoint: state.normalRunningPoint, normalRunningPoints: state.normalRunningPoints })
    : null;
}

/**
 * The single owner of /api/memory-protection/subscribe: one BackendCurrentValueSource of decoded frames (transport,
 * lease and session fencing) folded into one current value. The backend replays status, records, the point and the
 * saved points on subscribe, so the records frame starts a replay.
 */
export function createMemoryProtectionSubscriptionSource(channel: BackendSubscriptionChannel): CurrentValueSource<HostMemoryProtectionView> {
  return new FrameFoldSource<HostMemoryProtectionFrame, MemoryProtectionFrameState, HostMemoryProtectionView>({
    key: "memory-protection.subscription",
    frames: new BackendCurrentValueSource({
      key: "memory-protection.subscription.frames",
      channel,
      buildUrl: buildMemoryProtectionSubscriptionUrl,
      decoder: memoryProtectionFrameDecoder
    }),
    initial: {},
    apply: applyMemoryProtectionFrame,
    view: memoryProtectionViewOf,
    startsReplay: frame => frame.type === "records"
  });
}
