import type { FrontendWorkId } from "./frontendWorkIds";
import type { FrontendVisibilitySourceDemand } from "./frontendVisibilitySurface.ts";

export type FrontendWorkReferenceChange = (workId: FrontendWorkId, delta: 1 | -1) => void;

export class FrontendVisibilityDemandController {
  readonly demandId: string;
  readonly source: FrontendVisibilitySourceDemand | null;
  readonly workIds: readonly FrontendWorkId[];
  private activeValue = false;
  private readonly changeWorkReference: FrontendWorkReferenceChange;

  constructor(
    demand: string | FrontendVisibilitySourceDemand,
    workIds: readonly FrontendWorkId[],
    changeWorkReference: FrontendWorkReferenceChange
  ) {
    this.source = typeof demand === "string" ? null : demand;
    const normalizedDemandId = (typeof demand === "string" ? demand : demand.demandId).trim();
    if (!normalizedDemandId) {
      throw new Error("Frontend visibility demand requires a non-empty demand ID.");
    }

    this.demandId = normalizedDemandId;
    this.workIds = Array.from(new Set(workIds)).sort() as FrontendWorkId[];
    if (this.workIds.length === 0) {
      throw new Error("Frontend visibility demand requires at least one work ID.");
    }
    this.changeWorkReference = changeWorkReference;
  }

  get active() {
    return this.activeValue;
  }

  setActive(active: boolean) {
    if (active === this.activeValue) {
      return false;
    }

    this.activeValue = active;
    const delta = active ? 1 : -1;
    for (const workId of this.workIds) {
      this.changeWorkReference(workId, delta);
    }
    return true;
  }

  dispose() {
    this.setActive(false);
  }
}
