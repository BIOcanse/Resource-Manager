import type {
  SourceFamily,
  SourceLease
} from "../frontendRuntime/source/SourceDescriptor.ts";
import type { SourceSnapshot } from
  "../frontendRuntime/source/SourceSnapshot.ts";
import {
  normalizeSoftwareMetadataQuery,
  softwareMetadataQueryKey,
  type SoftwareMetadataQuery
} from "./softwareMetadataApi.ts";
import type { SoftwareMetadataLookupResult } from
  "./softwareMetadataDecoder.ts";

interface OwnedSoftwareMetadataLease {
  readonly queryKey: string;
  readonly query: SoftwareMetadataQuery;
  readonly lease: SourceLease<SoftwareMetadataLookupResult>;
  unsubscribe: () => void;
}

export class SoftwareMetadataLeaseBinding {
  private readonly source: SourceFamily<
    SoftwareMetadataQuery,
    SoftwareMetadataLookupResult
  >;
  private readonly publish: (
    query: SoftwareMetadataQuery,
    snapshot: SourceSnapshot<SoftwareMetadataLookupResult>
  ) => void;
  private current: OwnedSoftwareMetadataLease | null = null;

  constructor(
    source: SourceFamily<SoftwareMetadataQuery, SoftwareMetadataLookupResult>,
    publish: (
      query: SoftwareMetadataQuery,
      snapshot: SourceSnapshot<SoftwareMetadataLookupResult>
    ) => void)
  {
    this.source = source;
    this.publish = publish;
  }

  switch(query: SoftwareMetadataQuery): void {
    const normalized = normalizeSoftwareMetadataQuery(query);
    const queryKey = softwareMetadataQueryKey(normalized);
    if (this.current?.queryKey === queryKey) {
      this.publish(normalized, this.current.lease.snapshot);
      return;
    }

    const lease = this.source.acquire(normalized, {
      active: true,
      refreshIntervalMs: null
    });
    const owned: OwnedSoftwareMetadataLease = {
      queryKey,
      query: normalized,
      lease,
      unsubscribe: () => undefined
    };
    try {
      owned.unsubscribe = lease.subscribe((snapshot) => {
        if (this.current === owned) {
          this.publish(owned.query, snapshot);
        }
      });
    } catch (error) {
      lease.release();
      throw error;
    }

    const previous = this.current;
    this.current = owned;
    try {
      this.publish(owned.query, lease.snapshot);
    } catch (error) {
      this.current = previous;
      this.releaseOwned(owned);
      throw error;
    }
    this.releaseOwned(previous);
  }

  release(): void {
    const current = this.current;
    this.current = null;
    this.releaseOwned(current);
  }

  private releaseOwned(owned: OwnedSoftwareMetadataLease | null): void {
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
