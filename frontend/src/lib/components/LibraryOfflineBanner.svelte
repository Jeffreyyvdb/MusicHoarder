<script lang="ts">
  import { onMount } from 'svelte';
  import { fetchLibraryAvailability, type LibraryAvailability } from '$lib/api-client';

  const POLL_INTERVAL_MS = 15_000;

  // The shell binds `visible` to suppress lower-priority banners (update notice)
  // while this one shows — at most one banner renders at a time.
  type Props = { visible?: boolean };
  let { visible = $bindable(false) }: Props = $props();

  let availability = $state<LibraryAvailability | null>(null);

  // Hidden until we've confirmed a directory is actually offline. A failed fetch
  // (e.g. API down in standalone dev) leaves `availability` null → nothing shows.
  const offline = $derived(
    availability !== null && (!availability.sourceAvailable || !availability.destinationAvailable)
  );

  $effect(() => {
    visible = offline;
  });

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

<!--
  Slim hairline strip, not a tinted wall: one amber dot carries the warning —
  the only amber in the shell chrome, so it stays legible as "something is
  actually wrong".
-->
{#if offline}
  <div
    aria-live="polite"
    class="border-border/70 text-muted-foreground flex min-h-9 shrink-0 items-center gap-2.5 border-b px-4 py-1.5 text-[13px] sm:px-7"
  >
    <span class="size-1.5 shrink-0 rounded-full bg-amber-500" aria-hidden="true"></span>
    <p class="min-w-0 flex-1">
      <span class="text-foreground font-medium">Library {offlineLabel} directory unreachable</span>
      <span class="mx-1" aria-hidden="true">—</span>
      the pipeline is paused and resumes automatically when you reconnect.
    </p>
  </div>
{/if}
