<script lang="ts">
  import { onMount } from 'svelte';
  import { fetchLibraryAvailability, type LibraryAvailability } from '$lib/api-client';

  const POLL_INTERVAL_MS = 15_000;

  let availability = $state<LibraryAvailability | null>(null);

  // Hidden until we've confirmed a directory is actually offline. A failed fetch
  // (e.g. API down in standalone dev) leaves `availability` null → nothing shows.
  const offline = $derived(
    availability !== null && (!availability.sourceAvailable || !availability.destinationAvailable)
  );

  const offlineLabel = $derived.by(() => {
    if (!availability) return '';
    const parts: string[] = [];
    if (!availability.sourceAvailable) parts.push('source');
    if (!availability.destinationAvailable) parts.push('destination');
    return parts.join(' and ');
  });

  async function refresh(): Promise<void> {
    try {
      availability = await fetchLibraryAvailability();
    } catch {
      availability = null;
    }
  }

  onMount(() => {
    void refresh();
    const handle = setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => clearInterval(handle);
  });
</script>

{#if offline}
  <div
    role="status"
    aria-live="polite"
    class="flex items-center gap-2 border-b border-amber-500/40 bg-amber-500/10 px-4 py-2 text-sm text-amber-700 dark:text-amber-400"
  >
    <span class="size-2 shrink-0 rounded-full bg-amber-500" aria-hidden="true"></span>
    <span>
      Music library {offlineLabel} directory is unreachable — the processing pipeline is paused.
      Settings and other pages still work; it resumes automatically when you reconnect.
    </span>
  </div>
{/if}
