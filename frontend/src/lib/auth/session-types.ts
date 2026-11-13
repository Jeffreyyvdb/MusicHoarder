/**
 * Shape of `GET /api/auth/me`. Lives outside `#lib/server` so client-side code (landing-page CTAs,
 * components reading `page.data`) can use the types without pulling in a server-only module.
 */

/**
 * The account's role, as the API names it. For display: branch on `isAdmin` and `capabilities`
 * instead — see `#lib/auth/capabilities.ts`.
 */
export type SessionRole = 'Admin' | 'Demo' | 'Member';

/** What an admin can grant a person. Mirrors the server's `Capability` flags, by name. */
export type Capability = 'DownloadMusic' | 'TrackListening' | 'ManageOwnShares' | 'Administer';

export interface SessionUser {
  id: string;
  email: string;
  /** For display — see {@link SessionRole}. Branch on `isAdmin` instead. */
  role: SessionRole;
  displayName: string | null;
  /** True for an administrator. Authoritative; do not re-derive it from `role`. */
  isAdmin?: boolean;
  /**
   * EFFECTIVE capabilities. An admin lists every one regardless of what is stored, so a UI that
   * checks a capability does not also need to special-case admins.
   */
  capabilities?: Capability[];
}
