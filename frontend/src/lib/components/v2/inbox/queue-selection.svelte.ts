import { untrack } from 'svelte';
import { goto } from '$app/navigation';
import { page } from '$app/state';
import { replaceUrl } from '$lib/navigation/replace-url';
import { neighbours, parseQueueId, queueHref, resolveSelection } from './queue-selection';

type Options = {
  /** The queue's `?tab=` value. */
  tab: string;
  /** The URL param that carries the selection. */
  param: 'song' | 'group';
  /** The queue's ids in display order. */
  ids: () => readonly number[];
  /** Below md: list first, a push to the detail. */
  compact: () => boolean;
  /** False while the queue is (re)loading — an unknown id is only "gone" once the list is in. */
  ready: () => boolean;
  /**
   * A requested id turned out not to be in the queue (decided elsewhere, a stale link, a track
   * that never needed review). Say so: dropping it silently reads as a broken link.
   */
  onmissing?: (id: number) => void;
};

/**
 * An Inbox queue's selection, kept in the URL:
 * - phone: the list shows until you pick an item; picking it is a push (Back returns to the
 *   list); a decision advancing to the next item, and the up/down chevrons, replace it;
 * - desktop: the first item is selected on arrival (without writing the URL) and every other
 *   selection replaces the URL, so the history does not fill with every row you clicked.
 * An id that is not in the queue (decided elsewhere, or a stale link) is dropped from the URL.
 *
 * Construct it during component initialisation: it registers effects.
 */
export class QueueSelection {
  // Bridges the tick between choosing an item and the router reporting the new URL, so the
  // detail swaps at once instead of flashing the list (or the old item) in between.
  #pending = $state<number | null | undefined>(undefined);
  #opts: Options;

  constructor(opts: Options) {
    this.#opts = opts;

    // The URL caught up (or changed under us — Back, a link): it is the truth again.
    $effect(() => {
      void this.urlId;
      untrack(() => (this.#pending = undefined));
    });

    // A selected id the queue does not have: back to the list (phone) / the first item (desktop).
    $effect(() => {
      const id = this.urlId;
      if (id == null || !this.#opts.ready()) return;
      const known = this.#opts.ids().includes(id);
      untrack(() => {
        if (!known && this.#pending === undefined) {
          void replaceUrl(this.href(null), { noScroll: false });
          this.#opts.onmissing?.(id);
        }
      });
    });

    // A param that is not an id at all (`?song=abc`): the queue already shows the list, but the
    // URL still names an item, so the bar would offer Back to that same list. Clean it at once.
    $effect(() => {
      const raw = page.url.searchParams.get(this.#opts.param);
      if (raw == null || parseQueueId(raw) != null) return;
      untrack(() => void replaceUrl(this.href(null), { noScroll: false }));
    });
  }

  /** The id the URL selects (no validation against the queue). */
  get urlId(): number | null {
    return parseQueueId(page.url.searchParams.get(this.#opts.param));
  }

  /** What the queue shows: an item, or null for the list (phone only). */
  get selectedId(): number | null {
    const requested = this.#pending !== undefined ? this.#pending : this.urlId;
    return resolveSelection(requested, this.#opts.ids(), this.#opts.compact());
  }

  /** "3 of 57" and the chevrons' targets. */
  get position(): { position: number; total: number; prev: number | null; next: number | null } {
    const ids = this.#opts.ids();
    return { ...neighbours(this.selectedId, ids), total: ids.length };
  }

  href(id: number | null): string {
    return queueHref(page.url.pathname, this.#opts.tab, this.#opts.param, id);
  }

  /**
   * Show `id` (null: the list). `push` is for opening a detail from the phone's list; everything
   * else — advancing after a decision, the chevrons, any desktop selection — replaces.
   */
  select(id: number | null, mode: 'push' | 'replace' = 'replace'): void {
    this.#pending = id;
    const url = this.href(id);
    if (mode === 'push') void goto(url);
    else void replaceUrl(url);
  }

  /**
   * A row was activated. On a phone its link does the push; on a desktop the click becomes a
   * replace. Modified clicks (new tab / window) are left to the browser at every width.
   */
  onRowClick(event: MouseEvent, id: number): void {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }
    if (this.#opts.compact()) {
      // Let the <a> navigate (a push); show the detail now rather than when the router lands.
      this.#pending = id;
      return;
    }
    event.preventDefault();
    this.select(id);
  }
}
