import type { SourceHandle } from "./SourceDescriptor.ts";
import type { SourceSnapshot } from "./SourceSnapshot.ts";

/** Keep command acceptance alive through consumer disposal; undefined skips acceptance. */
export async function acceptSourceCommand<TValue, TResult>(
  source: SourceHandle<TValue>,
  command: () => Promise<TResult>,
  selectValue: (result: TResult, current: SourceSnapshot<TValue>) => TValue | undefined
): Promise<TResult> {
  const lease = source.acquire({ active: false });
  try {
    const result = await command();
    const value = selectValue(result, lease.snapshot);
    if (value !== undefined) lease.acceptAuthoritative(value);
    return result;
  } finally {
    lease.release();
  }
}
