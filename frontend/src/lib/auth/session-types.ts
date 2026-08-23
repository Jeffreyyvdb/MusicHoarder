/**
 * Shape of `GET /api/auth/me`. Lives outside `$lib/server` so client-side code (landing-page CTAs,
 * components reading `page.data`) can use the types without pulling in a server-only module.
 */

export type SessionRole = 'Owner' | 'Demo' | 'Friend';

export interface SessionUser {
  id: string;
  email: string;
  role: SessionRole;
  displayName: string | null;
}
