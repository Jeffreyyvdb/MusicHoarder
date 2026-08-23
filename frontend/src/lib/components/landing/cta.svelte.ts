import { goto } from '$app/navigation';
import { page } from '$app/state';
import { signInAsDemo } from '$lib/api-client';
import { APP_HOME, FRIEND_HOME } from '$lib/app-home';
import type { SessionRole } from '$lib/auth/session-types';

export interface PrimaryCtaOptions {
  /** Label shown to signed-out visitors, e.g. "Try the live demo". */
  signedOutLabel: string;
  /** Optional shorter label for narrow viewports (nav bar). Defaults to `signedOutLabel`. */
  shortSignedOutLabel?: string;
}

/**
 * The landing page's primary call-to-action.
 *
 * The important behaviour is the guard: when the visitor already has a session we navigate into
 * the app instead of calling `signInAsDemo()`. That call issues a fresh Demo session and the API
 * overwrites the `mh_session` cookie with it — so one click on the home page used to silently
 * demote a signed-in owner to the read-only demo account, and the next visit looked like a
 * logged-out state.
 *
 * `page.data.sessionRole` is populated by `src/routes/+page.server.ts`.
 */
export function createPrimaryCta(options: PrimaryCtaOptions) {
  let launching = $state(false);

  const role = $derived((page.data.sessionRole ?? null) as SessionRole | null);
  const signedInLabel = $derived(
    role === 'Owner'
      ? 'Open your library'
      : role === 'Friend'
        ? 'Open shared music'
        : 'Continue the demo'
  );
  const label = $derived(role ? signedInLabel : options.signedOutLabel);
  const shortLabel = $derived(
    role ? signedInLabel : (options.shortSignedOutLabel ?? options.signedOutLabel)
  );

  async function activate() {
    if (launching) return;
    launching = true;
    try {
      // Already signed in — go straight in rather than replacing the session with a demo one.
      if (role) {
        await goto(role === 'Friend' ? FRIEND_HOME : APP_HOME);
        return;
      }
      await signInAsDemo();
      await goto(APP_HOME);
    } catch {
      await goto('/login');
    } finally {
      launching = false;
    }
  }

  return {
    get label() {
      return launching ? 'Starting…' : label;
    },
    get shortLabel() {
      return launching ? 'Starting…' : shortLabel;
    },
    get busy() {
      return launching;
    },
    activate
  };
}
