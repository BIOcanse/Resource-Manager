/** Elapsed H:MM:SS, including hours above 23; discard subsecond fractions in every language. */
export function formatElapsedDuration(seconds: number): string {
  const total = Math.floor(seconds);
  const pad = (number: number) => String(number).padStart(2, "0");
  return `${Math.floor(total / 3600)}:${pad(Math.floor(total / 60) % 60)}:${pad(total % 60)}`;
}
