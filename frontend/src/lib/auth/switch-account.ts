/**
 * Switches the active account and hard-reloads into the app. A full page load (not `goto`) is
 * deliberate: the `(app)` group runs with SSR off, so its stores — `songsStore`, `playerStore`,
 * and the grantor lookup behind the "Shared by …" attribution — are module state that survives
 * soft navigations. Switching the user identity in place is exactly the leak class documented in
 * `sign-out.ts`; the reload resets every singleton and re-runs the server guard, which bounces a
 * non-admin off any administration path.
 */

import { switchAccount } from '$lib/api-client';
import { APP_HOME } from '$lib/app-home';

export async function switchAccountAndReload(userId: string): Promise<void> {
  const switched = await switchAccount(userId);
  if (switched) {
    location.assign(APP_HOME);
  } else {
    // The parked session died since the list was rendered; the server pruned it. Reload so the
    // account list self-heals.
    location.reload();
  }
}
