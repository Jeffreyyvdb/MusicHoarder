<script lang="ts">
  import { Check, Loader2, RefreshCw, Sparkles, ChevronRight, Copy } from '@lucide/svelte';
  import {
    fetchQualityOverview,
    copyQualitySongDossier,
    type QualityWorstOffender,
    type QualityVerdict
  } from '$lib/api-client';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Button } from '$lib/components/ui/button';
  import { toast } from 'svelte-sonner';
  import { cn } from '$lib/utils';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  let offenders = $state<QualityWorstOffender[]>([]);
  let selectedId = $state<number | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let configured = $state(true);

  $effect(() => {
    oncount?.(loading ? null : offenders.length);
  });

  async function load() {
    try {
      loading = true;
      error = null;
      const ov = await fetchQualityOverview();
      // "AI flagged" = worst offenders the grader marked Wrong / Questionable.
      offenders = (ov.worstOffenders ?? []).filter(
        (o) => o.verdict === 'Wrong' || o.verdict === 'Questionable'
      );
      configured = (ov.library?.graded ?? 0) > 0 || offenders.length > 0;
      selectedId = offenders[0]?.songId ?? null;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load AI grades';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  const selected = $derived(offenders.find((o) => o.songId === selectedId) ?? null);

  function verdictTint(v: QualityVerdict | undefined): string {
    switch (v) {
      case 'Wrong':
        return 'bg-red-500/15 text-red-600 dark:text-red-400 border-red-500/30';
      case 'Questionable':
        return 'bg-amber-500/15 text-amber-600 dark:text-amber-400 border-amber-500/30';
      default:
        return 'bg-muted text-muted-foreground border-border';
    }
  }

  async function onCopyDossier(songId: number) {
    try {
      await copyQualitySongDossier(songId);
      toast.success('Copied dossier to clipboard — paste into Claude Code');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Copy failed');
    }
  }

  function reviewHref(songId: number): string {
    return `/inbox?tab=review&song=${songId}`;
  }
</script>

{#if loading}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="text-muted-foreground flex items-center gap-2 text-sm">
      <Loader2 class="size-5 animate-spin" /> Loading AI grades…
    </div>
  </div>
{:else if error}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="max-w-md text-center">
      <p class="text-destructive mb-3 text-sm">{error}</p>
      <Button onclick={load}>Retry</Button>
    </div>
  </div>
{:else if offenders.length === 0}
  <div class="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
    <span class="bg-primary/10 text-primary grid size-12 place-items-center rounded-full">
      {#if configured}<Check class="size-6" />{:else}<Sparkles class="size-6" />{/if}
    </span>
    <div class="text-[15px] font-semibold">{configured ? 'Nothing flagged by AI' : 'AI grading not run yet'}</div>
    <p class="text-muted-foreground max-w-sm text-[12.5px]">
      {#if configured}
        The quality grader hasn't marked any built tracks Wrong or Questionable.
      {:else}
        Run AI quality grading from the
        <a href="/quality" class="text-primary hover:underline">AI quality</a> tab to surface enrichments that look wrong.
      {/if}
    </p>
  </div>
{:else}
  <div class="grid min-h-0 flex-1 grid-cols-[320px_1fr] overflow-hidden">
    <!-- List -->
    <aside class="border-border bg-surface-sunken flex min-h-0 flex-col border-r">
      <div class="border-border flex items-center justify-between gap-2 border-b px-4 py-2.5">
        <span class="text-muted-foreground text-[11px]">{offenders.length} flagged by AI</span>
        <button
          type="button"
          onclick={load}
          title="Refresh"
          class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-md transition-colors"
        >
          <RefreshCw class="size-3.5" />
        </button>
      </div>
      <div class="min-h-0 flex-1 overflow-y-auto p-1.5">
        {#each offenders as o (o.songId)}
          <button
            type="button"
            onclick={() => (selectedId = o.songId)}
            class={cn(
              'mb-0.5 flex w-full items-center gap-2.5 rounded-md border-l-2 border-transparent py-2 pr-2.5 pl-2 text-left transition-colors',
              selectedId === o.songId ? 'border-l-primary bg-card' : 'hover:bg-accent'
            )}
          >
            <Cover artist={o.artist ?? 'Unknown'} title={o.title ?? o.fileName} size={40} corner={6} caption={false} />
            <div class="min-w-0 flex-1">
              <div class="truncate text-[13px] font-medium">{o.title ?? o.fileName}</div>
              <div class="text-muted-foreground truncate text-[11.5px]">{o.artist ?? '—'}</div>
            </div>
            <span class="text-muted-foreground shrink-0 font-mono text-[11px] tabular-nums">{o.score}</span>
          </button>
        {/each}
      </div>
    </aside>

    <!-- Detail: LLM verdict -->
    {#if selected}
      <div class="flex min-w-0 flex-col overflow-hidden">
        <div class="border-border flex items-start gap-3 border-b bg-red-500/5 px-6 py-3">
          <Sparkles class="mt-0.5 size-5 shrink-0 text-red-600 dark:text-red-400" />
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <span class="text-[14px] font-semibold">AI flagged</span>
              <span class={cn('rounded-md border px-1.5 py-0.5 text-[10.5px] font-semibold', verdictTint(selected.verdict))}>
                {selected.verdict} · {selected.score} / 100
              </span>
            </div>
            <div class="text-muted-foreground truncate text-[12px]">{selected.title ?? selected.fileName}{selected.artist ? ` — ${selected.artist}` : ''}</div>
          </div>
        </div>

        <div class="min-h-0 flex-1 space-y-4 overflow-y-auto px-6 py-4">
          <!-- Verdict card -->
          <div class="border-border bg-card rounded-lg border">
            <div class="border-border flex items-center justify-between border-b px-4 py-2.5">
              <span class="text-[12.5px] font-semibold">Quality LLM verdict</span>
              <span class="text-muted-foreground font-mono text-[11px]">graded {selected.gradedAtUtc.slice(0, 10)}</span>
            </div>
            <div class="px-4 py-3">
              {#if selected.summary}
                <p class="text-foreground/80 text-[13px] leading-relaxed">{selected.summary}</p>
              {:else}
                <p class="text-muted-foreground text-[13px]">No summary provided by the grader.</p>
              {/if}
              {#if selected.issues.length > 0}
                <div class="mt-3 flex flex-wrap gap-1.5">
                  {#each selected.issues as issue (issue.code)}
                    <span
                      class="border-border bg-muted/40 inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5"
                      title={issue.detail ?? ''}
                    >
                      <code class="font-mono text-[10.5px]">{issue.code}</code>
                      <span class="text-muted-foreground text-[10px] uppercase">{issue.severity}</span>
                    </span>
                  {/each}
                </div>
              {/if}
            </div>
          </div>

          <!-- What the algorithm did -->
          <div class="border-border overflow-hidden rounded-lg border">
            <div class="bg-surface-sunken/60 text-muted-foreground border-border grid grid-cols-[120px_1fr] gap-3 border-b px-4 py-2 font-mono text-[10px] tracking-[0.06em]">
              <div>FIELD</div>
              <div>VALUE</div>
            </div>
            {#each [{ l: 'Title', v: selected.title ?? '—' }, { l: 'Artist', v: selected.artist ?? '—' }, { l: 'Album', v: selected.album ?? '—' }, { l: 'Source', v: selected.sourcePath, mono: true }, { l: 'Destination', v: selected.destinationPathPreview ?? '(not written)', mono: true }, { l: 'Status at grade', v: selected.enrichmentStatusAtGrade ?? '—' }] as row (row.l)}
              <div class="border-border grid grid-cols-[120px_1fr] items-center gap-3 border-b px-4 py-2 last:border-b-0">
                <div class="text-[12.5px] font-medium">{row.l}</div>
                <div class={cn('min-w-0 break-words text-[12.5px]', row.mono && 'font-mono text-[11px]')}>{row.v}</div>
              </div>
            {/each}
          </div>
        </div>

        <!-- Action bar -->
        <div class="border-border bg-background flex items-center gap-3 border-t px-6 py-3">
          <div class="flex-1"></div>
          <Button variant="outline" onclick={() => onCopyDossier(selected.songId)} class="gap-1.5">
            <Copy class="size-3.5" /> Copy dossier
          </Button>
          <Button href={reviewHref(selected.songId)} class="gap-1.5">
            Open in review <ChevronRight class="size-3.5" />
          </Button>
        </div>
      </div>
    {/if}
  </div>
{/if}
