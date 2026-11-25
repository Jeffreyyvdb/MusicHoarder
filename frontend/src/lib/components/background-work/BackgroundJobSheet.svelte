<script lang="ts">
  import { untrack } from 'svelte';
  import { getBackgroundJob, isPermissionError } from '#lib/api-client.ts';
  import type {
    BackgroundJobDetail,
    BackgroundJobId,
    BackgroundJobSummary
  } from '#lib/background-work/types.ts';
  import * as BottomSheet from '#lib/components/ui/bottom-sheet/index.ts';
  import { poll } from '#lib/poll.ts';
  import BackgroundJobDetailBody from './BackgroundJobDetailBody.svelte';

  type Props = {
    jobId: BackgroundJobId | null;
    summary: BackgroundJobSummary | null;
    onclose: () => void;
  };
  let { jobId, summary, onclose }: Props = $props();

  const REFRESH_MS = 30_000;

  let detailHeldWhileClosing = $state<BackgroundJobDetail | null>(null);
  let error = $state<string | null>(null);
  let now = $state(Date.now());

  $effect(() => {
    const id = jobId;
    if (id === null) return;
    untrack(() => {
      if (detailHeldWhileClosing?.summary.id !== id) detailHeldWhileClosing = null;
      error = null;
    });

    let controller: AbortController | null = null;
    const load = async () => {
      controller?.abort();
      const mine = new AbortController();
      controller = mine;
      try {
        const next = await getBackgroundJob(id, mine.signal);
        if (mine.signal.aborted) return;
        detailHeldWhileClosing = next;
        error = null;
        now = Date.now();
      } catch (err) {
        if (mine.signal.aborted) return;
        if (detailHeldWhileClosing === null) {
          error = isPermissionError(err)
            ? 'Only administrators can see background work.'
            : "Couldn't load this job. It will try again in a moment.";
        }
      }
    };
    const stop = poll(() => void load(), REFRESH_MS, { immediate: true });
    return () => {
      stop();
      controller?.abort();
    };
  });

  const title = $derived(
    detailHeldWhileClosing?.summary.title ?? summary?.title ?? 'Background work'
  );
</script>

<BottomSheet.Root
  open={jobId !== null}
  onOpenChange={(open) => {
    if (!open) onclose();
  }}
  {title}
  class="md:max-w-3xl"
>
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={onclose}>Done</BottomSheet.Action>
  {/snippet}
  <BackgroundJobDetailBody {summary} detail={detailHeldWhileClosing} {error} {now} />
</BottomSheet.Root>
