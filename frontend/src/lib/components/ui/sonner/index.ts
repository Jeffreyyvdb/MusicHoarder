export { default as Toaster } from './sonner.svelte';

/**
 * `onInteractOutside` for an overlay a toast can sit on (Now Playing and the sheets nested in it).
 * Sonner mounts at the body, outside every dialog, so to bits-ui a tap on a toast — its X, a swipe
 * to dismiss — is a tap outside, which would close the overlay under it. A toast is not the page
 * behind, so the overlay stays open. (The toasts take taps over Now Playing at all only because
 * app.css hands them pointer events back; see `[data-sonner-toast]` there.)
 */
export function keepOpenForToasts(event: PointerEvent): void {
  if (event.target instanceof Element && event.target.closest('[data-sonner-toaster]')) {
    event.preventDefault();
  }
}
