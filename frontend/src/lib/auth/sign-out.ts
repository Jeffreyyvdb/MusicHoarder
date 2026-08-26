/**
 * Single sign-out entry point shared by every "log out" affordance
 * (sidebar button, settings). It must clear all user-scoped client state
 * *before* navigating to `/login`, because the `(app)` route group runs with
 * SSR off — its stores are module singletons that survive a logout → login in
 * the same tab. Leaving them populated lets the next user briefly see the
 * previous session's data (e.g. the demo account's albums after switching to a
 * real account). `invalidateAll` only re-runs server `load`s; it does not touch
 * these client stores, so we reset them by hand here.
 */

import { goto } from '$app/navigation';
import { resetGrantors, signOut } from '$lib/api-client';
import { APP_HOME } from '$lib/app-home';
import { songsStore } from '$lib/stores/songs.svelte';
import { playerStore } from '$lib/stores/player.svelte';

export async function signOutAndReset(allSessions = false): Promise<void> {
  const { fallback } = await signOut(allSessions);
  if (fallback) {
    // The server promoted a parked account. A hard reload (not `goto`) resets every module
    // singleton for the new identity — same reasoning as `switch-account.ts`.
    location.assign(APP_HOME);
    return;
  }
  // Drop cached user data so it can't leak into the next session — including who shared what,
  // or the next account would briefly see the previous one's "Shared by …" attribution.
  songsStore.reset();
  playerStore.stop();
  resetGrantors();
  await goto('/login', { invalidateAll: true });
}
