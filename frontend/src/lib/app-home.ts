/**
 * Where every sign-in path lands — the Listen group's landing route.
 *
 * Kept in its own module (no icon imports) so `routes/auth/callback/+server.ts` can import it
 * without pulling `@lucide/svelte` components into the server bundle, and so the four sign-in
 * paths (magic-link callback, the already-signed-in /login redirect, the landing CTA, and the
 * demo button) can't disagree about the app's front door again — they used to, three saying
 * /pipeline and one saying /library.
 *
 * Overview rather than the album grid: it opens on what you've been playing and what's new to you,
 * which is a better answer to "what now?" than an A-Z wall of covers.
 */
export const APP_HOME = '/overview';
