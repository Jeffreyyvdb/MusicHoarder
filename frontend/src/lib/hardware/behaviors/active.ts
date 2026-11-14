// MusicHoarder: the behaviours below listen at the document and cannot be uninstalled, so each one
// checks this before it buzzes or sounds. The hardware skin is an opt-in easter egg
// (#lib/hardware/hardware-mode.svelte.ts); with it off the app must feel exactly as before.
export function skinActive(): boolean {
  return typeof document !== "undefined" && document.documentElement.dataset.mhSkin === "hardware"
}
