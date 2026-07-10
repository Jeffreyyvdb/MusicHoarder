<script lang="ts">
  import { onMount } from 'svelte';
  import { CircleAlert } from '@lucide/svelte';
  import { fetchQualityProgress, type QualityProgress } from '$lib/api-client';
  import * as Alert from '$lib/components/ui/alert/index.js';

  const POLL_INTERVAL_MS = 15_000;

  // Shell banner policy: `visible` reports the underlying error state upward,
  // `suppressed` hides this banner while a higher-priority one (offline) shows.
  type Props = { visible?: boolean; suppressed?: boolean };
  let { visible = $bindable(false), suppressed = false }: Props = $props();

  let lastError = $state<QualityProgress['lastError']>(null);

  // The server suppresses `lastError` when grading is disabled or unconfigured, so a non-null
  // value here always means "grading is on and erroring". A failed fetch leaves it null → nothing shows.
  const outOfCredits = $derived(lastError?.code === 'out_of_credits');

  $effect(() => {
    visible = lastError !== null;
  });

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

{#if lastError && !suppressed}
  <Alert.Root
    variant="destructive"
    aria-live="polite"
    class="mx-4 my-2 w-auto border-destructive/40 bg-destructive/10"
  >
    <CircleAlert class="size-4" />
    <Alert.Description class="min-w-0 break-words">
      {#if outOfCredits}
        AI quality grading is paused — your OpenRouter account is out of credits. Add credits at
        <a
          href="https://openrouter.ai/settings/credits"
          target="_blank"
          rel="noopener noreferrer"
          class="font-medium underline">openrouter.ai/settings/credits</a
        >
        and grading resumes automatically.
      {:else}
        AI quality grading is failing: {lastError.message ?? 'unknown error'}.
      {/if}
    </Alert.Description>
  </Alert.Root>
{/if}
