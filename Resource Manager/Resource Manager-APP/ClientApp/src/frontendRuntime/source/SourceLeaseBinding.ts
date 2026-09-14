import type {
  SourceDemand,
  SourceLease
} from "./SourceDescriptor.ts";
import type { SourceSnapshot } from "./SourceSnapshot.ts";

interface OwnedSourceLease<T> {
  readonly key: string;
  readonly lease: SourceLease<T>;
  unsubscribe: () => void;
}

/** Owns one query lease and publishes only that lease's current snapshot. */
export class SourceLeaseBinding<T> {
  private readonly publish: (snapshot: SourceSnapshot<T>) => void;
  private current: OwnedSourceLease<T> | null = null;

  constructor(
    publish: (snapshot: SourceSnapshot<T>) => void
  ) {
    this.publish = publish;
  }

  switch(key: string, acquire: () => SourceLease<T>): SourceLease<T> {
    if (this.current?.key === key) {
      return this.current.lease;
    }

    const previous = this.current;
    const next = this.own(key, acquire());
    this.current = next;
    try {
      this.publish(next.lease.snapshot);
    } catch (error) {
      this.current = previous;
      this.releaseOwned(next);
      throw error;
    }
    this.releaseOwned(previous);
    return next.lease;
  }

  setDemand(demand: SourceDemand): void {
    this.current?.lease.setDemand(demand);
  }

  release(): void {
    const current = this.current;
    this.current = null;
    this.releaseOwned(current);
  }

  private own(key: string, lease: SourceLease<T>): OwnedSourceLease<T> {
    const owned: OwnedSourceLease<T> = {
      key,
      lease,
      unsubscribe: () => undefined
    };
    try {
      owned.unsubscribe = lease.subscribe((snapshot) => {
        if (this.current === owned) {
          this.publish(snapshot);
        }
      });
    } catch (error) {
      lease.release();
      throw error;
    }
    return owned;
  }

  private releaseOwned(owned: OwnedSourceLease<T> | null): void {
    if (!owned) {
      return;
    }
    try {
      owned.unsubscribe();
    } finally {
      owned.lease.release();
    }
  }
}
