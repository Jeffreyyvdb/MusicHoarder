<script lang="ts">
  import type { Snippet } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { getBackgroundWork } from '#lib/api-client.ts';
  import type { BackgroundJobId, BackgroundWorkSummary } from '#lib/background-work/types.ts';
  import { replaceUrl } from '#lib/navigation/replace-url.ts';
  import { poll } from '#lib/poll.ts';
  import BackgroundJobSheet from './BackgroundJobSheet.svelte';
  import BackgroundWorkSection from './BackgroundWorkSection.svelte';

  type Props = { header: Snippet };
  let { header }: Props = $props();

  const REFRESH_MS = 30_000;
  const JOB_PARAM = 'job';

  let summary = $state<BackgroundWorkSummary | null>(null);
  let error = $state<string | null>(null);
  let now = $state(Date.now());

  $effect(() => {
    let controller: AbortController | null = null;
    const load = async () => {
      controller?.abort();
      const mine = new AbortController();
      controller = mine;
      try {
        const next = await getBackgroundWork(mine.signal);
        if (mine.signal.aborted) return;
        summary = next;
        error = null;
        now = Date.now();
      } catch {
        if (mine.signal.aborted) return;
        if (summary === null) error = "Couldn't load background work.";
      }
    };
    const stop = poll(() => void load(), REFRESH_MS, { immediate: true });
    return () => {
      stop();
      controller?.abort();
    };
  });

  const requested = $derived(page.url.searchParams.get(JOB_PARAM));
  const jobId = $derived(summary?.jobs.find((job) => job.id === requested)?.id ?? null);
  const openJob = $derived(summary?.jobs.find((job) => job.id === jobId) ?? null);

  let pushed = false;
  $effect(() => {
    if (requested === null) pushed = false;
  });

  function hrefFor(id: BackgroundJobId | null): string {
    const url = new URL(page.url.href);
    if (id) url.searchParams.set(JOB_PARAM, id);
    else url.searchParams.delete(JOB_PARAM);
    return url.pathname + url.search;
  }

  function open(id: BackgroundJobId) {
    pushed = true;
    void goto(hrefFor(id), { reset: false });
  }

  function close() {
    if (pushed) {
      pushed = false;
      history.back();
    } else {
      void replaceUrl(hrefFor(null));
    }
  }
</script>

<BackgroundWorkSection {summary} {error} {header} {now} onopen={open} />
<BackgroundJobSheet {jobId} summary={openJob} onclose={close} />
