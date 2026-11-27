import { searchCatalogTracks, type CatalogTrack } from '#lib/api-client.ts';

export type CatalogSearchStatus = 'idle' | 'loading' | 'ready' | 'error';

const MIN_CHARS = 2;
const DELAY_MS = 300;

/**
 * Typing in the Find music box. A query waits out the pause between keystrokes, and a newer one
 * abandons the request an older one started, so a slow answer can never overwrite a fast one.
 */
export function createCatalogSearch() {
  let status = $state<CatalogSearchStatus>('idle');
  let results = $state.raw<CatalogTrack[]>([]);
  let error = $state<string | null>(null);

  let timer: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | null = null;
  let current = '';

  function cancel(): void {
    clearTimeout(timer);
    controller?.abort();
    controller = null;
  }

  async function fetchNow(query: string): Promise<void> {
    const mine = new AbortController();
    controller = mine;
    try {
      const found = await searchCatalogTracks(query, mine.signal);
      if (controller !== mine) return;
      results = found;
      error = null;
      status = 'ready';
    } catch (err) {
      if (controller !== mine) return;
      error = err instanceof Error && err.message ? err.message : 'Search is unavailable.';
      status = 'error';
    }
  }

  function set(query: string): void {
    cancel();
    current = query.trim();
    if (current.length < MIN_CHARS) {
      results = [];
      error = null;
      status = 'idle';
      return;
    }
    status = 'loading';
    const q = current;
    timer = setTimeout(() => void fetchNow(q), DELAY_MS);
  }

  function retry(): void {
    cancel();
    if (current.length < MIN_CHARS) return;
    status = 'loading';
    void fetchNow(current);
  }

  return {
    get status() {
      return status;
    },
    get results() {
      return results;
    },
    get error() {
      return error;
    },
    set,
    retry,
    cancel
  };
}
