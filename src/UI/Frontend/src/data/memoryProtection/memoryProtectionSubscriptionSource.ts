import {
  BackendCurrentValueSource, type CurrentValueSource
} from "../../frontendRuntime/push/PushValueSourceFamily.ts";
import { FrameFoldSource } from "../../frontendRuntime/push/FrameFoldSource.ts";
import type { BackendSubscriptionChannel } from "../../frontendRuntime/push/BackendSubscriptionChannel.ts";
import { buildMemoryProtectionSubscriptionUrl } from "./memoryProtectionApi.ts";
import { memoryProtectionFrameDecoder } from "./memoryProtectionDecoders.ts";
import type {
  HostMemoryActionRecord, HostMemoryProtectionFrame, HostMemoryProtectionLiveStatus,
  HostMemoryProtectionView, NormalRunningPoint
} from "./memoryProtectionTypes.ts";

/** What the frames of one subscription have delivered so far; undefined means that frame has not arrived. */
export interface MemoryProtectionFrameState {
  readonly status?: HostMemoryProtectionLiveStatus;
  readonly records?: readonly HostMemoryActionRecord[];
  readonly recordLimit?: number;
  readonly normalRunningPoint?: NormalRunningPoint | null;
}

const sameRecord = (left: HostMemoryActionRecord, right: HostMemoryActionRecord) => left.actionId === right.actionId
  && left.processId === right.processId && left.processStartKey === right.processStartKey;

/**
 * Applies one frame. status and normalRunningPoint replace their part; records replaces the list and limit;
 * record upserts by actionId plus process identity (in place, or as the newest), then keeps the newest limit.
 * A record before the records replay has no list to join and is dropped; the replay that follows carries it.
 */
export function applyMemoryProtectionFrame(state: MemoryProtectionFrameState, frame: HostMemoryProtectionFrame): MemoryProtectionFrameState {
  switch (frame.type) {
    case "status":
      return { ...state, status: frame.status };
    case "records":
      return { ...state, records: frame.records.slice(0, frame.limit), recordLimit: frame.limit };
    case "record": {
      if (state.records === undefined || state.recordLimit === undefined) return state;
      const index = state.records.findIndex(record => sameRecord(record, frame.record));
      const records = index >= 0
        ? state.records.map((record, position) => position === index ? frame.record : record)
        : [frame.record, ...state.records];
      return { ...state, records: records.slice(0, state.recordLimit) };
    }
    case "normalRunningPoint":
      return { ...state, normalRunningPoint: frame.normalRunningPoint };
  }
}

/** The view is complete once status, records and the point (possibly null) have all arrived. */
export function memoryProtectionViewOf(state: MemoryProtectionFrameState): HostMemoryProtectionView | null {
  return state.status !== undefined && state.records !== undefined && state.recordLimit !== undefined
    && state.normalRunningPoint !== undefined
    ? Object.freeze({ status: state.status, records: [...state.records], recordLimit: state.recordLimit,
      normalRunningPoint: state.normalRunningPoint })
    : null;
}

/**
 * The single owner of /api/memory-protection/subscribe: one BackendCurrentValueSource of decoded frames (transport,
 * lease and session fencing) folded into one current value. The backend replays status, records and point on
 * subscribe, so the records frame starts a replay.
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
