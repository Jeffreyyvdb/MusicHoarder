<script lang="ts">
  import { Check, Loader2 } from '@lucide/svelte';
  import { providerColor, type ReviewCandidate } from '$lib/review-helpers';
  import { cn } from '$lib/utils';

  type Props = {
    candidates: ReviewCandidate[];
    pickedKey: string | null;
    loading?: boolean;
    onpick: (c: ReviewCandidate) => void;
    /** Single-column layout (mobile / narrow). Defaults to a responsive 2-col grid. */
    single?: boolean;
  };

  const { candidates, pickedKey, loading = false, onpick, single = false }: Props = $props();
</script>

<div
  class="border-border bg-surface-sunken/40 rounded-lg border p-3"
  class:border-primary={pickedKey != null && candidates.length > 0}
>
  <div
    class="text-muted-foreground mb-2.5 flex items-center justify-between text-[10.5px] font-semibold tracking-[0.06em] uppercase"
  >
    <span>Candidate matches · <span class="normal-case">pick one to project into the final values</span></span>
    <span class="font-mono">{candidates.length}</span>
  </div>

  {#if loading && candidates.length === 0}
    <div class="text-muted-foreground flex items-center gap-2 py-2 text-sm">
      <Loader2 class="size-4 animate-spin" /> Loading candidates…
    </div>
  {:else if candidates.length === 0}
    <div class="text-muted-foreground bg-background rounded-md px-3.5 py-4 text-[12.5px]">
      No fingerprint matches. Enter metadata manually below or skip this file.
    </div>
  {:else}
    <div class={cn('grid gap-2', single ? 'grid-cols-1' : 'sm:grid-cols-2')}>
      {#each candidates as c (c.key)}
        {@const picked = pickedKey === c.key}
        {@const pct = c.score != null ? Math.round(c.score * 100) : null}
        <button
          type="button"
          onclick={() => onpick(c)}
          class={cn(
            'flex items-start gap-3 rounded-md border p-3 text-left transition-colors',
            picked ? 'border-primary bg-primary/10' : 'border-border bg-background hover:bg-accent'
          )}
        >
          <div class="w-12 shrink-0">
            <div class={cn('font-mono text-[15px] font-semibold', picked && 'text-primary')}>
              {c.score != null ? c.score.toFixed(2) : '—'}
            </div>
            <div class="bg-border mt-1 h-[3px] overflow-hidden rounded-full">
              <div class="bg-primary h-full" style="width: {pct ?? 0}%"></div>
            </div>
          </div>
          <div class="min-w-0 flex-1">
            <div class="truncate text-[13px] font-medium">{c.title}</div>
            <div class="text-muted-foreground mt-0.5 truncate text-[11.5px]">
              {c.artist} · <em class="text-foreground/70 italic">{c.album}</em>{#if c.year} · {c.year}{/if}
            </div>
            <div
              class="border-border mt-1.5 inline-flex items-center gap-1.5 rounded-md border px-1.5 py-0.5"
            >
              <span class="size-1.5 rounded-full" style="background: {providerColor(c.source)}"></span>
              <span class="text-[10.5px]">{c.source}</span>
              {#if pct != null}
                <span class="text-muted-foreground border-border ml-0.5 border-l pl-1.5 font-mono text-[10.5px]"
                  >{pct}</span
                >
              {/if}
            </div>
          </div>
          {#if picked}
            <div class="text-primary flex shrink-0 items-center gap-1 self-center font-mono text-[9px] font-bold tracking-[0.08em]">
              <Check class="size-3" strokeWidth={2.5} /> PICKED
            </div>
          {/if}
        </button>
      {/each}
    </div>
  {/if}
</div>
