<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { Pause, Play, RefreshCw } from '@lucide/svelte';

  export type PipelineMode = 'trigger' | 'auto' | 'continuous';
  export type StepAction = 'start' | 'pause' | 'resume';

  type Props = {
    stepKey: string;
    running: boolean;
    paused: boolean;
    triggering: string | null;
    onAction: (step: string, action: StepAction) => void;
    mode?: PipelineMode;
  };

  const { stepKey, running, paused, triggering, onAction, mode = 'trigger' }: Props = $props();
  const isTriggering = $derived(triggering === stepKey);
  const isContinuous = $derived(mode === 'continuous');
</script>

{#if running}
  <Button
    variant="ghost"
    size="icon"
    class="size-6"
    disabled={isTriggering}
    onclick={() => onAction(stepKey, 'pause')}
    title={`Pause ${stepKey}`}
  >
    <Pause class="size-3.5" />
  </Button>
{:else if paused}
  <Button
    variant="ghost"
    size="icon"
    class="size-6 text-amber-400 hover:text-amber-300"
    disabled={isTriggering}
    onclick={() => onAction(stepKey, 'resume')}
    title={`Resume ${stepKey}`}
  >
    <Play class="size-3.5" />
  </Button>
{:else if isContinuous}
  <Button
    variant="ghost"
    size="icon"
    class="size-6"
    disabled={isTriggering}
    onclick={() => onAction(stepKey, 'pause')}
    title={`Pause ${stepKey}`}
  >
    <Pause class="size-3.5" />
  </Button>
{:else if mode === 'auto'}
  <Button
    variant="ghost"
    size="icon"
    class="size-6"
    disabled={isTriggering}
    onclick={() => onAction(stepKey, 'start')}
    title={`Trigger ${stepKey}`}
  >
    <RefreshCw class="size-3.5" />
  </Button>
{:else}
  <Button
    variant="ghost"
    size="icon"
    class="size-6"
    disabled={isTriggering}
    onclick={() => onAction(stepKey, 'start')}
    title={`Start ${stepKey}`}
  >
    <Play class="size-3.5" />
  </Button>
{/if}
