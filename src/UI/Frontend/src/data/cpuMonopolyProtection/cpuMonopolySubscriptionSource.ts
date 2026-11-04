import {
  BackendCurrentValueSource, type CurrentValueSource
} from "../../frontendRuntime/push/PushValueSourceFamily.ts";
import { FrameFoldSource } from "../../frontendRuntime/push/FrameFoldSource.ts";
import type { BackendSubscriptionChannel } from "../../frontendRuntime/push/BackendSubscriptionChannel.ts";
import {
  buildCpuMonopolySubscriptionUrl, cpuMonopolyFrameDecoder,
  type CpuMonopolyActionRecord, type CpuMonopolyFrame, type CpuMonopolyLiveStatus, type CpuMonopolyView
} from "./cpuMonopolyProtection.ts";

/** Frames received so far; undefined means that frame has not arrived. */
export interface CpuMonopolyFrameState {
  readonly status?: CpuMonopolyLiveStatus;
  readonly records?: readonly CpuMonopolyActionRecord[];
  readonly recordCapacity?: number;
  readonly unrecordedRecordCount?: number;
}

const sameRecord = (left: CpuMonopolyActionRecord, right: CpuMonopolyActionRecord) => left.actionId === right.actionId
  && left.processId === right.processId && left.processStartKey === right.processStartKey;

/** Replace status and record replays, upsert records by action and process identity, and ignore events. Drop records before replay; the replay includes them. Capacity never evicts records. */
export function applyCpuMonopolyFrame(state: CpuMonopolyFrameState, frame: CpuMonopolyFrame): CpuMonopolyFrameState {
  switch (frame.type) {
    case "status":
      return { ...state, status: frame.status };
    case "records":
      return { ...state, records: frame.records, recordCapacity: frame.limit, unrecordedRecordCount: frame.unrecordedRecordCount };
    case "record": {
      if (state.records === undefined) return state;
      const index = state.records.findIndex(record => sameRecord(record, frame.record));
      const records = index >= 0
        ? state.records.map((record, position) => position === index ? frame.record : record)
        : [frame.record, ...state.records];
      return { ...state, records };
    }
    case "event":
      return state;
  }
}

/** Both status and records are required for a complete value. */
export function cpuMonopolyViewOf(state: CpuMonopolyFrameState): CpuMonopolyView | null {
  return state.status !== undefined && state.records !== undefined && state.recordCapacity !== undefined
    && state.unrecordedRecordCount !== undefined
    ? Object.freeze({ status: state.status, records: [...state.records], recordCapacity: state.recordCapacity,
      unrecordedRecordCount: state.unrecordedRecordCount })
    : null;
}

/** Fold decoded subscription frames into one value; records starts each backend replay. */
export function createCpuMonopolySubscriptionSource(channel: BackendSubscriptionChannel): CurrentValueSource<CpuMonopolyView> {
  return new FrameFoldSource<CpuMonopolyFrame, CpuMonopolyFrameState, CpuMonopolyView>({
    key: "cpu-monopoly-protection.subscription",
    frames: new BackendCurrentValueSource({
      key: "cpu-monopoly-protection.subscription.frames",
      channel,
      buildUrl: buildCpuMonopolySubscriptionUrl,
      decoder: cpuMonopolyFrameDecoder
    }),
    initial: {},
    apply: applyCpuMonopolyFrame,
    view: cpuMonopolyViewOf,
    startsReplay: frame => frame.type === "records"
  });
}
