<script lang="ts">
  import type { Component } from 'svelte';
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
  };

  const { icon: Icon, label, status, isPaused, count, total, perSec }: Props = $props();

  const active = $derived(status === 'Running');
  const pct = $derived(total > 0 ? Math.min(100, (count / total) * 100) : 0);
  const rateLabel = $derived(perSec >= 10 ? Math.round(perSec).toString() : perSec.toFixed(1));
</script>

<div
  class={cn(
    'rounded-md border bg-muted/30 p-2.5 transition-all',
    active ? 'border-primary/30 bg-primary/[0.04] opacity-100' : 'opacity-70'
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
    <div class="font-mono text-[11px] font-semibold tabular-nums">
      {rateLabel}<span class="text-muted-foreground font-normal"> files/s</span>
    </div>
  </div>
  <Progress value={pct} class="h-[3px]" />
  <div class="text-muted-foreground mt-1.5 flex justify-between font-mono text-[10px] tabular-nums">
    <span>in: {count.toLocaleString()}</span>
    <span>out: {Math.max(0, count - Math.round(perSec * 2)).toLocaleString()}</span>
  </div>
  {#if isPaused}
    <div class="mt-1.5 text-[10px] font-semibold tracking-wide text-amber-400 uppercase">
      Paused
    </div>
  {/if}
</div>
