<script lang="ts">
  // NOTE: this card is consumed by ImportPipelineDrawer.svelte (the in-app live-import drawer).
  // It stays prop-driven with no browser access. It sits on the drawer's card, so its fill is the
  // sunken-well token rather than a translucent fill with an opacity modifier; an idle card is no
  // longer dimmed with opacity (that sank its muted text below 4.5:1) — the tint tile, border and
  // bar carry "active" instead.
  import type { Component } from 'svelte';
  import { Loader2, Pause, Play } from '@lucide/svelte';
  import { Progress } from '$lib/components/ui/progress';
  import { cn } from '$lib/utils';

  type Props = {
    icon: Component<{ class?: string }>;
    label: string;
    status: string | undefined;
    isPaused: boolean;
    count: number;
    total: number;
    perSec: number;
    /** Renders a Pause/Resume control. Omitted on the landing page, which wraps the whole card
     *  in a button (a nested button is invalid there). */
    onTogglePause?: () => void;
    /** A pause/resume request is in flight. */
    pauseBusy?: boolean;
  };

  const { icon: Icon, label, status, isPaused, count, total, perSec, onTogglePause, pauseBusy = false }: Props =
    $props();

  // A paused Enrich step keeps reporting Running (its workers hold the queue), and a paused
  // build is Running until its job winds down — neither is flowing, so neither lights up.
  const active = $derived(status === 'Running' && !isPaused);
  const pct = $derived(total > 0 ? Math.min(100, (count / total) * 100) : 0);
  const rateLabel = $derived(perSec >= 10 ? Math.round(perSec).toString() : perSec.toFixed(1));
  // Pause is offered while the step runs; Resume whenever it is paused, running or not (a paused
  // idle step is one auto-triggers skip).
  const showControl = $derived(onTogglePause != null && (status === 'Running' || isPaused));
</script>

<div
  class={cn(
    'bg-surface-sunken rounded-md border p-2.5 transition-[border-color,background-color] duration-200',
    active && 'border-primary/30 bg-primary/[0.04]'
  )}
>
  <div class="mb-2 flex items-center gap-2">
    <div
      class={cn(
        'flex size-[22px] shrink-0 items-center justify-center rounded',
        active ? 'bg-primary text-primary-foreground' : 'bg-background text-muted-foreground'
      )}
    >
      <Icon class="size-3.5" />
    </div>
    <div class="min-w-0 flex-1 truncate text-[12px] font-medium">{label}</div>
    <div class="text-[11px] font-semibold tabular-nums">
      {rateLabel}<span class="text-muted-foreground font-normal"> files/s</span>
    </div>
  </div>
  <!-- A progressbar needs a name of its own (axe: aria-progressbar-name); the card's label is the
       one on screen. -->
  <Progress value={pct} class="h-[3px]" aria-label="{label} progress" />
  <div class="text-muted-foreground mt-1.5 flex justify-between text-[11px] tabular-nums">
    <span>in: {count.toLocaleString()}</span>
    <span>out: {Math.max(0, count - Math.round(perSec * 2)).toLocaleString()}</span>
  </div>
  {#if isPaused || showControl}
    <div class="mt-1.5 flex min-h-7 items-center justify-between gap-2">
      {#if isPaused}
        <span class="text-warning-text inline-flex items-center gap-1 text-[11px] font-semibold">
          <Pause class="size-3" aria-hidden="true" /> Paused
        </span>
      {:else}
        <span></span>
      {/if}
      {#if showControl}
        <button
          type="button"
          onclick={onTogglePause}
          disabled={pauseBusy}
          aria-label={isPaused ? `Resume ${label.toLowerCase()}` : `Pause ${label.toLowerCase()}`}
          class="border-border bg-background hover:bg-muted text-foreground inline-flex h-7 items-center gap-1 rounded-md border px-2 text-[11px] font-medium transition-colors disabled:opacity-60"
        >
          {#if pauseBusy}
            <Loader2 class="size-3 animate-spin" />
          {:else if isPaused}
            <Play class="size-3" />
          {:else}
            <Pause class="size-3" />
          {/if}
          {isPaused ? 'Resume' : 'Pause'}
        </button>
      {/if}
    </div>
  {/if}
</div>
