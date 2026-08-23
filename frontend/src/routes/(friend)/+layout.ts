// Same reason as the (app) group: the player store is module-scoped $state owning an
// HTMLAudioElement, which must never be evaluated on the server.
export const ssr = false;
export const prerender = false;
