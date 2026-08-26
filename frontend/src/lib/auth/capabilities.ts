import type { Capability, SessionUser } from './session-types';

/**
 * The one place the UI asks "may this person do X".
 *
 * <p>Two rules worth knowing before you add a check:</p>
 *
 * 1. Never branch on `user.role`. It carries the legacy wire vocabulary ('Owner' | 'Demo' |
 *    'Friend'), which will change under you. Use {@link isAdmin} or {@link can}.
 * 2. These are display decisions only. Every one of them is enforced server-side as well —
 *    hiding a button is a courtesy, not a control.
 */

/** True for an administrator. Falls back to the legacy role for a payload from an older API. */
export function isAdmin(user: Pick<SessionUser, 'role' | 'isAdmin'> | null | undefined): boolean {
  if (!user) return false;
  return user.isAdmin ?? user.role === 'Owner';
}

/** True for the shared demo login, which is read-only and shared by every visitor. */
export function isDemo(user: Pick<SessionUser, 'role'> | null | undefined): boolean {
  return user?.role === 'Demo';
}

/**
 * True when the person holds `capability`. Admins hold everything, which the server already
 * folds into the effective set — the explicit {@link isAdmin} check here is only a fallback for
 * a payload from an API that predates capabilities.
 */
export function can(
  user: Pick<SessionUser, 'role' | 'isAdmin' | 'capabilities'> | null | undefined,
  capability: Capability
): boolean {
  if (!user) return false;
  if (isAdmin(user)) return true;
  return (user.capabilities ?? []).includes(capability);
}

/**
 * What to CALL an account in the UI.
 *
 * The wire still says 'Owner' and 'Friend' for compatibility with shipped mobile builds, but
 * neither word belongs on screen: an invited person is a full account, not somebody's "friend",
 * and "Owner" describes who owns the rows rather than who runs the instance. This maps the wire
 * vocabulary to the words people should read, and accepts the post-rename names too so it keeps
 * working when the wire flips.
 */
export function roleLabel(role: string | null | undefined): string {
  switch (role) {
    case 'Owner':
    case 'Admin':
      return 'Admin';
    case 'Friend':
    case 'Member':
      return 'Member';
    case 'Demo':
      return 'Demo';
    default:
      return 'Anonymous';
  }
}
