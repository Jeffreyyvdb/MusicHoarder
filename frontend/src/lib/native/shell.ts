/**
 * The native shell (`mobile/` at the repo root, built for iOS and Android): the same pages, loaded
 * from this server into a Capacitor web view, with a native audio player behind the transport.
 *
 * Capacitor injects `window.Capacitor` into every page the shell loads, so nothing is imported
 * here and a browser pays nothing for it. Only the two entry points the injected bridge always has
 * are used (`nativePromise`, `addListener`), which keeps this free of a version-matched
 * `@capacitor/core` in the web bundle.
 */

interface CapacitorBridge {
  isNativePlatform?: () => boolean;
  getPlatform?: () => string;
  PluginHeaders?: { name: string }[];
  nativePromise?: (plugin: string, method: string, options?: unknown) => Promise<unknown>;
  addListener?: (plugin: string, event: string, callback: (data: unknown) => void) => unknown;
}

function bridge(): CapacitorBridge | null {
  if (typeof window === 'undefined') return null;
  const cap = (window as Window & { Capacitor?: CapacitorBridge }).Capacitor;
  return cap?.isNativePlatform?.() === true ? cap : null;
}

/** True when the page runs inside the native shell rather than a browser or a Home Screen app. */
export function isNativeShell(): boolean {
  return bridge() !== null;
}

export type NativePlatform = 'ios' | 'android';

/** Which system the shell runs on, or null outside it. */
export function nativePlatform(): NativePlatform | null {
  const platform = bridge()?.getPlatform?.();
  return platform === 'ios' || platform === 'android' ? platform : null;
}

/** One native plugin, as the calls and events it takes. */
export interface NativePlugin {
  call(method: string, options?: Record<string, unknown>): Promise<unknown>;
  listen(event: string, handler: (data: unknown) => void): void;
}

/** The shell's plugin of that name, or null outside the shell or in a build without it. */
export function nativePlugin(name: string): NativePlugin | null {
  const cap = bridge();
  const { nativePromise, addListener } = cap ?? {};
  if (!cap || !nativePromise || !addListener) return null;
  if (!cap.PluginHeaders?.some((header) => header.name === name)) return null;
  return {
    call: (method, options) => nativePromise(name, method, options ?? {}),
    listen: (event, handler) => void addListener(name, event, handler)
  };
}
