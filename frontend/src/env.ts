import { defineEnvVars } from '@sveltejs/kit/env';

/**
 * Every variable here is optional and read when the server starts. An unset one is `undefined`
 * (not `''`), exactly as `$env/dynamic/*` returned it before SvelteKit 3, so each caller's own
 * fallback (`PUBLIC_SITE_URL ?? 'https://musichoarder.app'`, `?? ''`) still applies.
 */
const optional = (input: string | undefined) => input;

export const variables = defineEnvVars({
  SPOTIFY_OAUTH_STATE_SIGNING_KEY: { schema: optional },
  SPOTIFY_RETURN_ORIGIN_ALLOWLIST: { schema: optional },
  PUBLIC_UMAMI_WEBSITE_ID: { public: true, schema: optional },
  PUBLIC_UMAMI_SRC: { public: true, schema: optional },
  PUBLIC_UMAMI_RECORDER_SRC: { public: true, schema: optional },
  PUBLIC_SITE_URL: { public: true, schema: optional }
});
