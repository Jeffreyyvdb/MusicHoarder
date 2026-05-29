<script lang="ts">
  import { onMount } from 'svelte';
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
  import { fetchLibraryAvailability, type LibraryAvailability } from '$lib/api-client';
  import * as Alert from '$lib/components/ui/alert/index.js';

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
  <Alert.Root
    aria-live="polite"
    class="mx-4 my-2 border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-400"
  >
    <TriangleAlert class="size-4" />
    <Alert.Description class="text-amber-700 dark:text-amber-400">
      Music library {offlineLabel} directory is unreachable — the processing pipeline is paused.
      Settings and other pages still work; it resumes automatically when you reconnect.
    </Alert.Description>
  </Alert.Root>
{/if}
