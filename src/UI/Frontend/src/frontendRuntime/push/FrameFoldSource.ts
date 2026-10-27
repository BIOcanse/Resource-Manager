import type { SourceSnapshot, SourceStatus } from "../source/SourceSnapshot.ts";
import type { CurrentValueSource } from "./PushValueSourceFamily.ts";

export interface FrameFoldSourceOptions<TFrame, TState, TValue> {
  readonly key: string;
  /** The one transport owner: a current-value source whose values are the decoded frames of one subscription. */
  readonly frames: CurrentValueSource<TFrame>;
  readonly initial: TState;
  /** Applies one frame; pure. */
  readonly apply: (state: TState, frame: TFrame) => TState;
  /** The complete current value, or null while frames the value needs have not arrived. */
  readonly view: (state: TState) => TValue | null;
  /** The frame that starts the backend's replay to a new subscription; it ends an earlier failure. */
  readonly startsReplay: (frame: TFrame) => boolean;
}

interface Failure { readonly status: SourceStatus; readonly error: unknown; readonly unavailableReason: string | null; }

/**
 * A current value folded from the frames of one subscription (a replay on subscribe, then increments). Consumers
 * read the folded value like any CurrentValueSource and never merge frames. A transport, decode or session failure
 * drops the fold, because increments after a missed frame cannot be trusted; it stays failed until the replay that a
 * new subscription receives. A new backend session starts from the initial state.
 */
export class FrameFoldSource<TFrame, TState, TValue> implements CurrentValueSource<TValue> {
  readonly key: string;
  private readonly options: FrameFoldSourceOptions<TFrame, TState, TValue>;
  private readonly listeners = new Map<number, (value: SourceSnapshot<TValue>) => void>();
  private nextListenerId = 0;
  private release: (() => void) | null = null;
  private state: TState;
  private failure: Failure | null = null;
  private frameRevision = -1;
  private epoch: string | null = null;
  private revision = 0;
  private snapshot: SourceSnapshot<TValue>;

  constructor(options: FrameFoldSourceOptions<TFrame, TState, TValue>) {
    this.key = options.key;
    this.options = options;
    this.state = options.initial;
    this.snapshot = this.compose(options.frames.readSnapshot().backendEpoch);
  }

  readSnapshot(): SourceSnapshot<TValue> {
    this.accept(this.options.frames.readSnapshot());
    return this.snapshot;
  }

  subscribeSnapshot(listener: (value: SourceSnapshot<TValue>) => void): () => void {
    const id = ++this.nextListenerId;
    this.listeners.set(id, listener);
    if (this.release === null) {
      // The frame source publishes its current frame synchronously; accept() skips a frame it already folded.
      this.release = this.options.frames.subscribeSnapshot(frame => {
        if (this.accept(frame)) this.notify();
      });
    }
    try { listener(this.readSnapshot()); } catch {
      // A consumer failure does not change the folded value.
    }
    let disposed = false;
    return () => {
      if (disposed) return;
      disposed = true;
      this.listeners.delete(id);
      if (this.listeners.size === 0) {
        this.release?.();
        this.release = null;
      }
    };
  }

  /** Folds one frame snapshot; returns whether the published snapshot changed. */
  private accept(frame: SourceSnapshot<TFrame>): boolean {
    if (frame.revision === this.frameRevision && frame.backendEpoch === this.epoch) return false;
    this.frameRevision = frame.revision;
    if (frame.backendEpoch !== this.epoch) {
      this.epoch = frame.backendEpoch;
      this.state = this.options.initial;
      this.failure = null;
    }
    if (frame.status === "ready" && frame.data !== null) {
      if (this.options.startsReplay(frame.data)) this.failure = null;
      this.state = this.options.apply(this.state, frame.data);
    } else if (frame.status !== "loading" || this.failure === null) {
      this.state = this.options.initial;
      this.failure = frame.status === "loading" ? null
        : { status: frame.status, error: frame.error, unavailableReason: frame.unavailableReason ?? null };
    }
    this.snapshot = this.compose(frame.backendEpoch);
    return true;
  }

  private compose(backendEpoch: string | null): SourceSnapshot<TValue> {
    const view = this.failure === null ? this.options.view(this.state) : null;
    return Object.freeze({
      key: this.key,
      status: this.failure?.status ?? (view === null ? "loading" : "ready"),
      data: view,
      error: this.failure?.error ?? null,
      unavailableReason: this.failure?.unavailableReason ?? null,
      backendEpoch,
      revision: ++this.revision,
      acceptedAttempt: null,
      domainRevision: null,
      capturedAt: null
    });
  }

  private notify(): void {
    for (const listener of [...this.listeners.values()]) {
      try { listener(this.snapshot); } catch {
        // A consumer cannot interrupt sibling consumers or the transport owner.
      }
    }
  }
}
