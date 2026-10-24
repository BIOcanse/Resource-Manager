/** Normal sessions use dozens of queries; retain 256 unused entries per owner.
 * Active entries never count toward this bound. Retention has no time window. */
export const MAX_UNSUBSCRIBED_SOURCE_ENTRIES = 256;
