<script lang="ts">
  import { Check } from '@lucide/svelte';
  import { Spinner } from '$lib/components/ui/spinner/index.js';
  import { providerColor, type ReviewCandidate } from '$lib/review-helpers';
  import { cn } from '$lib/utils';

  type Props = {
    candidates: ReviewCandidate[];
    pickedKey: string | null;
    loading?: boolean;
    onpick: (c: ReviewCandidate) => void;
    /** Single-column layout (mobile / narrow). Defaults to a responsive 2-col grid. */
    single?: boolean;
    /** Audit/overview mode: cards are non-interactive. */
    readonly?: boolean;
  };

  const { candidates, pickedKey, loading = false, onpick, single = false, readonly = false }: Props =
    $props();
</script>

{#if loading && candidates.length === 0}
  <div class="text-muted-foreground flex items-center gap-2 py-2 text-sm">
    <Spinner class="size-4" /> Loading candidates…
  </div>
{:else if candidates.length === 0}
  <div class="text-muted-foreground bg-surface-sunken/40 rounded-md px-3.5 py-4 text-[12.5px]">
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
        disabled={readonly}
        class={cn(
          'flex items-center gap-3 rounded-lg border p-3 text-left transition-[color,background-color,border-color,transform] duration-100 ease-out',
          !readonly && 'active:scale-[0.99]',
          picked
            ? 'border-primary bg-primary/10'
            : readonly
              ? 'border-border bg-background cursor-default'
              : 'border-border bg-background hover:bg-accent'
        )}
      >
        <div class="min-w-0 flex-1">
          <div class="truncate text-[13px] font-medium">{c.title}</div>
          <div class="text-muted-foreground mt-0.5 truncate text-[11.5px]">
            {c.artist} · <em class="text-foreground/70 italic">{c.album}</em>{#if c.year} · {c.year}{/if}
          </div>
          <div class="text-muted-foreground mt-1.5 flex items-center gap-1.5 text-[11px]">
            <span class="size-1.5 shrink-0 rounded-full" style="background: {providerColor(c.source)}"></span>
            {c.source}
          </div>
        </div>
        <div class="flex shrink-0 flex-col items-end gap-0.5">
          {#if pct != null}
            <span class={cn('text-[14px] font-semibold tabular-nums', picked ? 'text-primary' : 'text-foreground/80')}>{pct}%</span>
          {/if}
          {#if picked}
            <span class="text-primary flex items-center gap-1 text-[11px] font-medium">
              <Check class="size-3" strokeWidth={2.5} /> Picked
            </span>
          {/if}
        </div>
      </button>
    {/each}
  </div>
{/if}
