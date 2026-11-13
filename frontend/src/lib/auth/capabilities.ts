import type { Capability, SessionUser } from './session-types';

/**
 * The one place the UI asks "may this person do X".
 *
 * <p>Two rules worth knowing before you add a check:</p>
 *
 * 1. Never branch on `user.role` for what someone may do: it is for display. Use
 *    {@link isAdmin} or {@link can}.
 * 2. These are display decisions only. Every one of them is enforced server-side as well —
 *    hiding a button is a courtesy, not a control.
 */

/** True for an administrator. Falls back to the role for a payload that lacks `isAdmin`. */
export function isAdmin(user: Pick<SessionUser, 'role' | 'isAdmin'> | null | undefined): boolean {
  if (!user) return false;
  return user.isAdmin ?? user.role === 'Admin';
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

/** What to CALL an account in the UI: the wire role, or 'Anonymous' for none. */
export function roleLabel(role: string | null | undefined): string {
  switch (role) {
    case 'Admin':
      return 'Admin';
    case 'Member':
      return 'Member';
    case 'Demo':
      return 'Demo';
    default:
      return 'Anonymous';
  }
}
