/** "3:07" from seconds, as a segment display prints it. Non-finite or negative reads 0:00. */
export function clock(seconds: number): string {
  const s = Number.isFinite(seconds) && seconds > 0 ? seconds : 0;
  return `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
}
