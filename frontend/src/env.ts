import { defineEnvVars } from '@sveltejs/kit/env';

// @migration-task Review usage of dynamic environment variables. They fall back to the empty string if not present, which may not be what you want.
export const variables = defineEnvVars({
	SPOTIFY_OAUTH_STATE_SIGNING_KEY: { schema: (input) => input ?? '' },
	SPOTIFY_RETURN_ORIGIN_ALLOWLIST: { schema: (input) => input ?? '' },
	ANDROID_ASSETLINKS_FINGERPRINTS: { schema: (input) => input ?? '' },
	PUBLIC_UMAMI_WEBSITE_ID: { public: true, schema: (input) => input ?? '' },
	PUBLIC_UMAMI_SRC: { public: true, schema: (input) => input ?? '' },
	PUBLIC_UMAMI_RECORDER_SRC: { public: true, schema: (input) => input ?? '' },
	PUBLIC_SITE_URL: { public: true, schema: (input) => input ?? '' }
});
