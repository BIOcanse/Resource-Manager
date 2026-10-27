/** 经过的时长，写成 时:分:秒，例如 25:04:09；不足一秒的部分舍去。各语言写法相同。 */
export function formatElapsedDuration(seconds: number): string {
  const total = Math.floor(seconds);
  const pad = (number: number) => String(number).padStart(2, "0");
  return `${Math.floor(total / 3600)}:${pad(Math.floor(total / 60) % 60)}:${pad(total % 60)}`;
}
