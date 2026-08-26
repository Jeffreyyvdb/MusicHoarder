// See https://svelte.dev/docs/kit/types#app.d.ts
declare global {
  namespace App {
    // interface Error {}
    // interface Locals {}
    /**
     * `user` is present for every route in the `(app)` group (its layout load resolves the
     * session or redirects). Typing it here is what lets components read `page.data.user`
     * directly instead of casting it inline — those casts had drifted, and two of them claimed
     * the role could only be 'Owner' | 'Demo', silently mishandling every invited account.
     */
    interface PageData {
      user?: import('$lib/auth/session-types').SessionUser;
    }
    // interface PageState {}
    // interface Platform {}
  }
}

export {};
