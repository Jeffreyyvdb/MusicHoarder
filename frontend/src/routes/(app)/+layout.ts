// The (app) shell uses module-scoped $state that reads browser-only APIs
// (HTMLAudioElement, demo-mode boolean evaluated at module load).
// Disable SSR so those reads don't crash on the server.
export const ssr = false;
export const prerender = false;
