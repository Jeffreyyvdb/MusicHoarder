import { markReplace } from '$lib/stores/tab-memory.svelte';

type ReplaceUrlOptions = { keepFocus?: boolean; noScroll?: boolean };

/**
 * Rewrite the current URL without adding a history entry — a chip toggle, a selection that
 * advances, a one-shot param being stripped. Use this instead of `goto(url, { replaceState: true })`:
 * afterNavigate cannot tell a replace from a push, so tab-memory has to be told, or Back would
 * step through every filter change as if each were a page.
 *
 * Defaults to keeping focus and scroll, since the page is not changing; pass `noScroll: false` /
 * `keepFocus: false` for a replace that really is a new page (an invalid id bouncing to a list).
 */
export async function replaceUrl(url: string | URL, opts: ReplaceUrlOptions = {}): Promise<void> {
  // Imported on use so a unit test can import a module that uses replaceUrl without SvelteKit.
  const { goto } = await import('$app/navigation');
  markReplace(url);
  await goto(url, { replaceState: true, noScroll: true, keepFocus: true, ...opts });
}
