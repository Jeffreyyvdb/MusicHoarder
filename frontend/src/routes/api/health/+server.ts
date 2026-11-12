import type { RequestHandler } from './$types';

// Lightweight liveness probe used by Docker/Dokploy healthchecks.
// No upstream fetches so it stays green even when the backend API is unreachable.
export const prerender = false;

export const GET: RequestHandler = () => Response.json({ status: 'ok' });
