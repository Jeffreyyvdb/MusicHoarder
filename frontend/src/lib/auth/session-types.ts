/**
 * Shape of `GET /api/auth/me`. Lives outside `$lib/server` so client-side code (landing-page CTAs,
 * components reading `page.data`) can use the types without pulling in a server-only module.
 */

/**
 * The role string as it appears ON THE WIRE, which is deliberately still the pre-rename
 * vocabulary. Server-side the roles are Admin / Demo / Member; the API keeps emitting the old
 * names because shipped Android builds branch on `role === 'Friend'` to pick their API routes.
 *
 * Prefer `isAdmin` and `capabilities` over this field — see `$lib/auth/capabilities`. This union
 * exists so the legacy value is still typed, not so new code branches on it.
 */
export type SessionRole = 'Owner' | 'Demo' | 'Friend';

/** What an admin can grant a person. Mirrors the server's `Capability` flags, by name. */
export type Capability = 'DownloadMusic' | 'TrackListening' | 'ManageOwnShares' | 'Administer';

export interface SessionUser {
  id: string;
  email: string;
  /** Legacy vocabulary — see {@link SessionRole}. Branch on `isAdmin` instead. */
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
