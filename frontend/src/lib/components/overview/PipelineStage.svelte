<script lang="ts">
  import type { Component } from 'svelte';
  import type { StepSnapshot } from '$lib/api-client';
  import { Progress } from '$lib/components/ui/progress';
  import { Badge } from '$lib/components/ui/badge';
  import { CheckCircle2, Zap } from '@lucide/svelte';
  import StepControl, { type PipelineMode, type StepAction } from './StepControl.svelte';

  type Props = {
    icon: Component<{ class?: string }>;
    label: string;
    count: number;
    total: number;
    unit: string;
    progress: number;
    step?: StepSnapshot;
    triggering: string | null;
    stepKey: string;
    onAction: (step: string, action: StepAction) => void;
    subtitle?: string;
    mode?: PipelineMode;
  };

  const {
    icon: Icon,
    label,
    count,
    total,
    unit,
    progress,
    step,
    triggering,
    stepKey,
    onAction,
    subtitle,
    mode = 'trigger'
  }: Props = $props();

  const running = $derived(step?.status === 'Running');
  const paused = $derived(step?.isPaused ?? false);
  const done = $derived(!running && total > 0 && count >= total);
  const isContinuous = $derived(mode === 'continuous');
</script>

<div class="space-y-2">
  <div class="flex items-center justify-between gap-2 text-sm">
    <span
      class="flex min-w-0 items-center gap-2 font-medium {running
        ? 'text-foreground'
        : done
          ? 'text-green-400'
          : 'text-muted-foreground'}"
    >
      <Icon
        class="size-4 shrink-0 {running ? 'text-primary' : done ? 'text-green-400' : ''}"
      />
      <span class="truncate">{label}</span>
      {#if running}
        <span class="bg-primary size-1.5 shrink-0 animate-pulse rounded-full"></span>
      {/if}
      {#if isContinuous && !paused && !running}
        <Badge
          variant="outline"
          class="border-primary/30 text-primary/70 shrink-0 px-1.5 py-0 text-[10px]"
        >
          <Zap class="mr-0.5 size-2.5" />
          Active
        </Badge>
      {/if}
      {#if paused && !running}
        <Badge
          variant="outline"
          class="shrink-0 border-amber-500/40 px-1.5 py-0 text-[10px] text-amber-400"
        >
          Paused
        </Badge>
      {/if}
      {#if done && !running && !isContinuous}
        <CheckCircle2 class="size-3.5 shrink-0 text-green-400" />
      {/if}
    </span>
    <div class="flex shrink-0 items-center gap-2">
      <span class="text-muted-foreground tabular-nums">
        {count.toLocaleString()}
        {unit}
        {#if total > 0}
          <span class="ml-1 text-xs">/ {total.toLocaleString()}</span>
        {/if}
      </span>
      <StepControl {stepKey} {running} {paused} {triggering} {onAction} {mode} />
    </div>
  </div>
  <Progress value={progress} class="h-2" />
  {#if subtitle}
    <p class="text-muted-foreground text-xs">{subtitle}</p>
  {/if}
</div>
