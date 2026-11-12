import { markReplace } from '#lib/stores/tab-memory.svelte.ts';

type ReplaceUrlOptions = { reset?: boolean };

/**
 * Rewrite the current URL without adding a history entry — a chip toggle, a selection that
 * advances, a one-shot param being stripped. Use this instead of `goto(url, { replace: true })`:
 * afterNavigate cannot tell a replace from a push, so tab-memory has to be told, or Back would
 * step through every filter change as if each were a page.
 *
 * Defaults to keeping focus and scroll, since the page is not changing; pass `reset: true` for a
 * replace that really is a new page (an invalid id bouncing to a list), which scrolls to the top
 * and moves focus like any navigation (SvelteKit 3 merged `noScroll` and `keepFocus` into it).
 */
export async function replaceUrl(url: string | URL, opts: ReplaceUrlOptions = {}): Promise<void> {
  // Imported on use so a unit test can import a module that uses replaceUrl without SvelteKit.
  const { goto } = await import('$app/navigation');
  markReplace(url);
  await goto(url, { replace: true, reset: opts.reset ?? false });
}
