<script lang="ts">
  import { onMount } from 'svelte';
  import { fetchQualityProgress, type QualityProgress } from '$lib/api-client';

  const POLL_INTERVAL_MS = 15_000;

  let lastError = $state<QualityProgress['lastError']>(null);

  // The server suppresses `lastError` when grading is disabled or unconfigured, so a non-null
  // value here always means "grading is on and erroring". A failed fetch leaves it null → nothing shows.
  const outOfCredits = $derived(lastError?.code === 'out_of_credits');

  async function refresh(): Promise<void> {
    try {
      const p = await fetchQualityProgress();
      lastError = p.lastError ?? null;
    } catch {
      lastError = null;
    }
  }

  onMount(() => {
    void refresh();
    const handle = setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => clearInterval(handle);
  });
</script>

{#if lastError}
  <div
    role="status"
    aria-live="polite"
    class="flex items-center gap-2 border-b border-red-500/40 bg-red-500/10 px-4 py-2 text-sm text-red-700 dark:text-red-400"
  >
    <span class="size-2 shrink-0 rounded-full bg-red-500" aria-hidden="true"></span>
    {#if outOfCredits}
      <span>
        AI quality grading is paused — your OpenRouter account is out of credits. Add credits at
        <a
          href="https://openrouter.ai/settings/credits"
          target="_blank"
          rel="noopener noreferrer"
          class="font-medium underline">openrouter.ai/settings/credits</a
        >
        and grading resumes automatically.
      </span>
    {:else}
      <span>AI quality grading is failing: {lastError.message ?? 'unknown error'}.</span>
    {/if}
  </div>
{/if}
