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
import { signOut } from '$lib/api-client';
import { songsStore } from '$lib/stores/songs.svelte';
import { playerStore } from '$lib/stores/player.svelte';

export async function signOutAndReset(allSessions = false): Promise<void> {
  await signOut(allSessions);
  // Drop cached user data so it can't leak into the next session.
  songsStore.reset();
  playerStore.stop();
  await goto('/login', { invalidateAll: true });
}
