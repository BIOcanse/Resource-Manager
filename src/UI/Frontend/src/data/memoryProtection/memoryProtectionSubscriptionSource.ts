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

/** Frames received so far; undefined means that frame has not arrived. */
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

/** Replace state and record replays; upsert records by action and process identity. Drop records before replay because the replay includes them. Capacity never evicts records. */
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

/** Complete after status, records, the nullable point and saved points arrive. */
export function memoryProtectionViewOf(state: MemoryProtectionFrameState): HostMemoryProtectionView | null {
  return state.status !== undefined && state.records !== undefined && state.recordCapacity !== undefined
    && state.unrecordedRecordCount !== undefined
    && state.normalRunningPoint !== undefined && state.normalRunningPoints !== undefined
    ? Object.freeze({ status: state.status, records: [...state.records], recordCapacity: state.recordCapacity,
      unrecordedRecordCount: state.unrecordedRecordCount,
      normalRunningPoint: state.normalRunningPoint, normalRunningPoints: state.normalRunningPoints })
    : null;
}

/** Fold decoded subscription frames into one value; records starts each backend replay. */
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
