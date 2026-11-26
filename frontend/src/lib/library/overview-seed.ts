/**
 * Deterministic daily seed for the Overview's shuffled shelves ("New to you",
 * "Discover — never played", "Artists to revisit", "From the shelves").
 *
 * Derived from the account id and today's local calendar date (`YYYY-MM-DD`), so the shelves stay
 * put as you drill into albums and come back — and match across tabs, reloads and devices for the
 * whole day — then rotate automatically the next day with no storage or server state.
 */
export function overviewDailySeed(userId?: string | null, now: Date = new Date()): string {
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  const day = `${y}-${m}-${d}`;
  return userId ? `${userId}:${day}` : day;
}
